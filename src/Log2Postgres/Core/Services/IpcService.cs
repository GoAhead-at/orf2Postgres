using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic; // Added for List<string>
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Log2Postgres.Core.Models;
using Newtonsoft.Json;
using System.Security.Principal;

namespace Log2Postgres.Core.Services
{
    // Moved from MainWindow.xaml.cs
    public class IpcMessage<T>
    {
        public string Type { get; set; } = string.Empty;
        public T? Payload { get; set; }
    }

    // Moved from MainWindow.xaml.cs and made public
    public class PipeServiceStatus
    {
        public string ServiceOperationalState { get; set; } = string.Empty;
        public bool IsProcessing { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public long CurrentPosition { get; set; }
        public long TotalLinesProcessedSinceStart { get; set; }
        public string LastErrorMessage { get; set; } = string.Empty;
        public string Type { get; set; } = "ServiceStatus"; // Matches IpcMessageTypes.StatusUpdate now
    }

    // Placeholder for a more detailed LogEntry if needed.
    // For now, LogEntriesReceived uses List<string>.
    // public class LogEntry { public string Message { get; set; } }

    // Placeholder for ServiceStateSnapshot
    public class ServiceStateSnapshot
    {
        public string SomeStateProperty { get; set; } = string.Empty;
        // Add other relevant properties
    }


    public interface IIpcService
    {
        bool IsConnected { get; }
        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync();
        Task SendCommandAsync<T>(string type, T payload, CancellationToken cancellationToken = default);
        Task SendServiceStatusRequestAsync(CancellationToken cancellationToken = default);
        Task SendSettingsAsync(LogMonitorSettings settings, CancellationToken cancellationToken = default);

        event Func<PipeServiceStatus, Task>? ServiceStatusReceived;
        event Func<Task>? PipeConnected;
        event Func<Task>? PipeDisconnected;
        event Func<List<string>, Task>? LogEntriesReceived; // Expects a list of strings
        event Func<ServiceStateSnapshot, Task>? StateSnapshotReceived; // New event for state snapshots
    }

    public class IpcService : IIpcService, IAsyncDisposable
    {
        private readonly ILogger<IpcService> _logger;
        private NamedPipeClientStream? _pipeClient;
        private StreamReader? _pipeReader;
        private StreamWriter? _pipeWriter;
        private Task? _receiveTask;
        private CancellationTokenSource? _receiveCts;
        private bool _isConnected = false;
        private readonly string _pipeName;
        private readonly object _lock = new object();

        public event Func<PipeServiceStatus, Task>? ServiceStatusReceived;
        public event Func<Task>? PipeConnected;
        public event Func<Task>? PipeDisconnected;
        public event Func<List<string>, Task>? LogEntriesReceived;
        public event Func<ServiceStateSnapshot, Task>? StateSnapshotReceived;


        public bool IsConnected => _isConnected && (_pipeClient?.IsConnected ?? false);

        public IpcService(ILogger<IpcService> logger, string pipeName = "Log2PostgresServicePipe")
        {
            _logger = logger;
            _pipeName = pipeName;
            _logger.LogInformation("IpcService initialized for pipe: {PipeName}", _pipeName);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                _logger.LogInformation("Already connected.");
                return;
            }

            _logger.LogInformation("Attempting to connect to IPC pipe '{PipeName}'...", _pipeName);
            int attempt = 0;
            int maxAttempts = 3; 
            int delayMs = 1000;  
            int timeout = 5000; 

            while (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                attempt++;
                _logger.LogDebug("Connection attempt {Attempt} of {MaxAttempts}", attempt, maxAttempts);

                try
                {
                    lock (_lock) // Ensure exclusive access for disposing and creating pipe resources
                    {
                        // Dispose existing resources if this is a retry or a new attempt after a previous one
                        if (_pipeClient != null)
                        {
                            _logger.LogDebug("Disposing existing pipe resources before new connection attempt.");
                            _pipeWriter?.Dispose(); // Dispose writer first
                            _pipeReader?.Dispose(); // Then reader
                            _pipeClient.Dispose();  // Then client stream
                            _pipeWriter = null;
                            _pipeReader = null;
                            _pipeClient = null;
                        }

                        _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    }
                    
                    _logger.LogDebug("Attempting to connect with timeout {Timeout}ms...", timeout);
                    await _pipeClient.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Successfully connected to IPC pipe '{PipeName}'.", _pipeName);

                    // Initialize reader and writer immediately after successful connection, still under potential lock if needed by other parts
                    // However, the critical part is that _pipeClient is valid here.
                    // Adding a lock here to be consistent with _isConnected logic and StartListeningAsync
                    lock(_lock)
                    {
                        _pipeReader = new StreamReader(_pipeClient); 
                        _pipeWriter = new StreamWriter(_pipeClient) { AutoFlush = false }; // AutoFlush false, we flush manually
                        _isConnected = true; // Set connected only after streams are initialized
                    }

                    _receiveCts?.Dispose(); // Dispose previous CTS if any
                    _receiveCts = new CancellationTokenSource();
                    _receiveTask = StartListeningAsync(_receiveCts.Token);

                    _logger.LogInformation("IPC client connected and listener started.");
                    if (PipeConnected != null)
                    {
                        await PipeConnected.Invoke().ConfigureAwait(false);
                    }
                    return; 
                }
                catch (TimeoutException tex)
                {
                    _logger.LogWarning(tex, "Timeout connecting to IPC pipe '{PipeName}' on attempt {Attempt}.", _pipeName, attempt);
                    if (attempt >= maxAttempts)
                    {
                        _logger.LogError("Max connection attempts reached. Giving up.");
                        await DisconnectAsyncInternal().ConfigureAwait(false);
                        throw; 
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error connecting to IPC pipe '{PipeName}' on attempt {Attempt}.", _pipeName, attempt);
                    if (attempt >= maxAttempts)
                    {
                        _logger.LogError("Max connection attempts reached after other error. Giving up.");
                        await DisconnectAsyncInternal().ConfigureAwait(false);
                        throw; 
                    }
                }
                
                if (!cancellationToken.IsCancellationRequested)
                {
                     _logger.LogDebug("Waiting {DelayMs}ms before next connection attempt.", delayMs);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs *= 2; 
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Connection cancelled.");
            }
        }

        private async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("IPC listener task started. Waiting for messages...");
            try
            {
                while (!cancellationToken.IsCancellationRequested && _pipeReader != null && (_pipeClient?.IsConnected ?? false) )
                {
                    string? messageJson = await _pipeReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (messageJson == null)
                    {
                        _logger.LogWarning("IPC pipe connection closed by server (ReadLineAsync returned null).");
                        break; 
                    }

                    _logger.LogTrace("IPC message received: {MessageJson}", messageJson);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessMessageAsync(messageJson, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation("ProcessMessageAsync cancelled.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing IPC message in background task.");
                        }
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("IPC listener task was cancelled.");
            }
            catch (IOException ioEx)
            {
                _logger.LogWarning(ioEx, "IOException in IPC listener (e.g. pipe broken).");
            }
            catch (ObjectDisposedException odEx)
            {
                _logger.LogWarning(odEx, "ObjectDisposedException in IPC listener (e.g. pipe closed during read).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in IPC listener task.");
            }
            finally
            {
                _logger.LogInformation("IPC listener task finishing.");
                if (_isConnected) 
                {
                    await DisconnectAsyncInternal().ConfigureAwait(false);
                }
            }
        }

        private async Task ProcessMessageAsync(string messageJson, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                var baseMessage = JsonConvert.DeserializeObject<IpcMessage<object>>(messageJson);
                if (baseMessage == null || string.IsNullOrEmpty(baseMessage.Type))
                {
                    _logger.LogWarning("Failed to deserialize base IPC message, message was null, or type was empty.");
                    return;
                }

                _logger.LogDebug("Processing IPC message of type: {MessageType}", baseMessage.Type);

                switch (baseMessage.Type)
                {
                    case IpcMessageTypes.LogEntry:
                        // Assuming LogEntry messages carry a list of strings for LogEntriesReceived event
                        var logEntriesMessage = JsonConvert.DeserializeObject<IpcMessage<List<string>>>(messageJson);
                        if (logEntriesMessage?.Payload != null && LogEntriesReceived != null)
                        {
                            _logger.LogDebug("Invoking LogEntriesReceived event with {Count} entries.", logEntriesMessage.Payload.Count);
                            await LogEntriesReceived.Invoke(logEntriesMessage.Payload).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("Received LOG_ENTRY message but payload was null or no subscribers for LogEntriesReceived event.");
                        }
                        break;
                    case IpcMessageTypes.StatusUpdate:
                        // Assuming StatusUpdate messages carry a PipeServiceStatus object
                        var statusMessage = JsonConvert.DeserializeObject<IpcMessage<PipeServiceStatus>>(messageJson);
                        if (statusMessage?.Payload != null && ServiceStatusReceived != null)
                        {
                             _logger.LogDebug("Invoking ServiceStatusReceived event.");
                            await ServiceStatusReceived.Invoke(statusMessage.Payload).ConfigureAwait(false);
                        }
                         else
                        {
                            _logger.LogWarning("Received STATUS_UPDATE message but payload was null or no subscribers for ServiceStatusReceived event.");
                        }
                        break;
                    case IpcMessageTypes.StateSnapshot:
                        var stateSnapshotMessage = JsonConvert.DeserializeObject<IpcMessage<ServiceStateSnapshot>>(messageJson);
                        if (stateSnapshotMessage?.Payload != null && StateSnapshotReceived != null)
                        {
                            _logger.LogDebug("Invoking StateSnapshotReceived event.");
                            await StateSnapshotReceived.Invoke(stateSnapshotMessage.Payload).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("Received STATE_SNAPSHOT message but payload was null or no subscribers for StateSnapshotReceived event.");
                        }
                        break;
                    default:
                        _logger.LogWarning("Unknown IPC message type received: {MessageType}", baseMessage.Type);
                        break;
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error deserializing IPC message: {MessageJson}", messageJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing IPC message: {MessageJson}", messageJson);
            }
        }
        
        public async Task SendCommandAsync<T>(string type, T payload, CancellationToken cancellationToken = default)
        {
            if (!_isConnected || _pipeWriter == null)
            {
                _logger.LogWarning("SendCommandAsync: Not connected or writer is null. Cannot send command type {CommandType}.", type);
                return;
            }
            var message = new IpcMessage<T> { Type = type, Payload = payload };
            await SendRawMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendRawMessageAsync<T>(IpcMessage<T> message, CancellationToken cancellationToken)
        {
            if (!_isConnected || _pipeWriter == null || _pipeClient == null || !_pipeClient.IsConnected)
            {
                _logger.LogWarning("SendRawMessageAsync: IPC client not connected, writer is null, or pipe is not connected. Cannot send message type {MessageType}.", message?.Type);
                return;
            }

            try
            {
                string jsonMessage = JsonConvert.SerializeObject(message);
                _logger.LogDebug("Attempting to send IPC message: {JsonMessage}", jsonMessage);
                await _pipeWriter.WriteLineAsync(jsonMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Sent IPC message: {JsonMessage}", jsonMessage);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SendRawMessageAsync was cancelled during write/flush.");
            }
            catch (ObjectDisposedException odEx)
            {
                 _logger.LogError(odEx, "Exception during SendRawMessageAsync: Pipe was disposed. Attempting to disconnect.");
                 await DisconnectAsyncInternal().ConfigureAwait(false); 
            }
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx, "IOException during SendRawMessageAsync (e.g. pipe broken). Attempting to disconnect.");
                await DisconnectAsyncInternal().ConfigureAwait(false); 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during SendRawMessageAsync while writing/flushing to pipe.");
                throw;
            }
        }


        private async Task DisconnectAsyncInternal()
        {
            if (!_isConnected && _pipeClient == null && _receiveTask == null) 
            {
                _logger.LogDebug("DisconnectAsyncInternal called but already effectively disconnected or never connected.");
                return;
            }

            _logger.LogInformation("DisconnectAsyncInternal: Initiating disconnection from IPC pipe.");
            bool wasConnected = _isConnected; // Capture state for event firing
            _isConnected = false; 

            if (_receiveCts != null)
            {
                _logger.LogDebug("Cancelling receive task CTS.");
                _receiveCts.Cancel();
                _receiveCts.Dispose();
                _receiveCts = null;
            }

            if (_receiveTask != null)
            {
                _logger.LogDebug("Waiting for receive task to complete...");
                try
                {
                    await Task.WhenAny(_receiveTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false); 
                    if (!_receiveTask.IsCompleted)
                    {
                        _logger.LogWarning("Receive task did not complete in time.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Exception while waiting for receive task to complete during disconnect.");
                }
                _receiveTask = null;
            }
            
            lock (_lock) 
            {
                if (_pipeWriter != null)
                {
                    _logger.LogDebug("Disposing StreamWriter.");
                    try 
                    {
                        // If the pipe was connected, attempt to flush. Otherwise, just dispose.
                        // However, StreamWriter.Dispose() should call Flush.
                        // The InvalidOperationException "Pipe hasn't been connected yet" might occur if
                        // _pipeClient.IsConnected is false when Dispose() tries to Flush.
                        // Let's rely on the standard Dispose behavior for now.
                        _pipeWriter.Dispose(); 
                    } 
                    catch (ObjectDisposedException odEx)
                    {
                         _logger.LogWarning(odEx, "ObjectDisposedException during StreamWriter.Dispose(). Likely already disposed.");
                    }
                    catch (InvalidOperationException ioEx)
                    {
                        _logger.LogWarning(ioEx, "InvalidOperationException during StreamWriter.Dispose(). Pipe might not have been connected or already closed.");
                    }
                    catch (Exception ex) 
                    { _logger.LogWarning(ex, "Exception disposing StreamWriter."); }
                    _pipeWriter = null;
                }

                if (_pipeReader != null)
                {
                    _logger.LogDebug("Disposing StreamReader.");
                    try { _pipeReader.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Exception disposing StreamReader."); }
                    _pipeReader = null;
                }

                if (_pipeClient != null)
                {
                    _logger.LogDebug("Closing and disposing NamedPipeClientStream.");
                    try
                    {
                        _pipeClient.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Exception closing/disposing NamedPipeClientStream.");
                    }
                    _pipeClient = null;
                }
            }

            _logger.LogInformation("IPC client disconnected.");
            try
            {
                if (wasConnected && PipeDisconnected != null) // Fire event only if it was previously connected
                {
                    await PipeDisconnected.Invoke().ConfigureAwait(false);
                }
            }
            catch(Exception ex)
            {
                _logger.LogWarning(ex, "Exception during Disconnected event invocation.");
            }
        }

        public async Task DisconnectAsync()
        {
            await DisconnectAsyncInternal().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("IpcService DisposeAsync called.");
            await DisconnectAsyncInternal().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        
        public async Task SendServiceStatusRequestAsync(CancellationToken cancellationToken = default)
        {
            await SendCommandAsync(IpcMessageTypes.RequestState, "REQUEST_STATUS", cancellationToken).ConfigureAwait(false);
        }

        public async Task SendSettingsAsync(LogMonitorSettings settings, CancellationToken cancellationToken = default)
        {
            await SendCommandAsync(IpcMessageTypes.Command, new { CommandType = "UpdateSettings", settings }, cancellationToken).ConfigureAwait(false);
        }
    }

    public static class IpcMessageTypes
    {
        public const string LogEntry = "LOG_ENTRY"; // Handled to expect IpcMessage<List<string>>
        public const string StatusUpdate = "STATUS_UPDATE"; // Handled to expect IpcMessage<PipeServiceStatus>
        public const string Command = "COMMAND"; // Generic command
        public const string StateSnapshot = "STATE_SNAPSHOT"; // Handled to expect IpcMessage<ServiceStateSnapshot>
        public const string RequestState = "REQUEST_STATE";   // UI to request state from service
    }
} 
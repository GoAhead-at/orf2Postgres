using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Log2Postgres.Core.Models; // Assuming LogMonitorSettings is here
using Newtonsoft.Json;

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
        // For consistency with commands, let's add Type, though not strictly needed for status object itself
        public string Type { get; set; } = "ServiceStatus";
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
        event Func<List<string>, Task>? LogEntriesReceived;
    }

    public class IpcService : IIpcService, IAsyncDisposable
    {
        private const string PipeName = "Log2PostgresServicePipe";
        private readonly ILogger<IpcService> _logger;
        
        private NamedPipeClientStream? _pipeClient;
        private StreamReader? _pipeReader;
        private StreamWriter? _pipeWriter;
        private CancellationTokenSource? _listenerCts;
        private Task? _listenerTask;
        private readonly SemaphoreSlim _pipeLock = new SemaphoreSlim(1, 1); // Using SemaphoreSlim for async locking

        private bool _isConnected = false;
        public bool IsConnected => _isConnected;

        public event Func<PipeServiceStatus, Task>? ServiceStatusReceived;
        public event Func<Task>? PipeConnected;
        public event Func<Task>? PipeDisconnected;
        public event Func<List<string>, Task>? LogEntriesReceived;

        public IpcService(ILogger<IpcService> logger)
        {
            _logger = logger;
            _logger.LogInformation("IpcService created.");
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_isConnected)
            {
                _logger.LogInformation("Already connected to service pipe.");
                return;
            }

            await _pipeLock.WaitAsync(cancellationToken);
            try
            {
                if (_isConnected) return;

                _logger.LogInformation("Attempting to connect to service pipe '{PipeName}'...", PipeName);
                _pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                
                try
                {
                    await _pipeClient.ConnectAsync(5000, cancellationToken); // 5-second timeout
                    _logger.LogInformation("Successfully connected to service pipe '{PipeName}'.", PipeName);
                }
                catch (TimeoutException ex)
                {
                    _logger.LogWarning(ex, "Timeout connecting to service pipe '{PipeName}'. Service might not be running or available.", PipeName);
                    await CleanupPipeResourcesAsync(); // Clean up the partially opened client
                    throw; // Re-throw to indicate connection failure
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error connecting to service pipe '{PipeName}'.", PipeName);
                    await CleanupPipeResourcesAsync();
                    throw; // Re-throw
                }

                _pipeReader = new StreamReader(_pipeClient, Encoding.UTF8);
                _pipeWriter = new StreamWriter(_pipeClient, Encoding.UTF8) { AutoFlush = true };

                _listenerCts = new CancellationTokenSource();
                _listenerTask = Task.Run(() => StartListeningAsync(_listenerCts.Token), cancellationToken);
                
                _isConnected = true;
                PipeConnected?.Invoke();
                _logger.LogInformation("IPC Service Listener started.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish IPC connection.");
                await CleanupPipeResourcesAsync(); // Ensure cleanup on any connection setup failure
                throw; // Re-throw the exception to the caller
            }
            finally
            {
                _pipeLock.Release();
            }
        }
        
        private async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("IPC listener task started. Waiting for messages...");
            try
            {
                while (!cancellationToken.IsCancellationRequested && _pipeReader != null)
                {
                    string? messageJson = await _pipeReader.ReadLineAsync(cancellationToken);
                    if (messageJson == null)
                    {
                        _logger.LogWarning("Pipe stream ended (null received). Assuming disconnection.");
                        break; // Pipe closed or stream ended
                    }

                    _logger.LogDebug("Received IPC message: {MessageJson}", messageJson);
                    await ProcessMessageAsync(messageJson);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("IPC listener gracefully stopped due to cancellation request.");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IOException in IPC listener (e.g. pipe broken).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in IPC listener.");
            }
            finally
            {
                _logger.LogInformation("IPC listener task shutting down.");
                if (_isConnected) // If still marked as connected, initiate disconnect
                {
                    await DisconnectAsyncInternal(true); // Signal it's an unexpected disconnect
                }
            }
        }

        private async Task ProcessMessageAsync(string messageJson)
        {
            try
            {
                var baseMessage = JsonConvert.DeserializeObject<IpcMessage<object>>(messageJson);
                if (baseMessage == null || string.IsNullOrEmpty(baseMessage.Type))
                {
                    _logger.LogWarning("Received an invalid or untyped IPC message: {MessageJson}", messageJson);
                    return;
                }

                _logger.LogDebug("Processing message of type: {MessageType}", baseMessage.Type);

                switch (baseMessage.Type)
                {
                    case "ServiceStatus":
                        var statusMessage = JsonConvert.DeserializeObject<IpcMessage<PipeServiceStatus>>(messageJson);
                        if (statusMessage?.Payload != null && ServiceStatusReceived != null)
                        {
                            await ServiceStatusReceived.Invoke(statusMessage.Payload);
                        }
                        break;
                    case "LOG_ENTRIES":
                        var logEntriesMessage = JsonConvert.DeserializeObject<IpcMessage<List<string>>>(messageJson);
                        if (logEntriesMessage?.Payload != null && LogEntriesReceived != null)
                        {
                            _logger.LogDebug("Invoking LogEntriesReceived event with {Count} entries.", logEntriesMessage.Payload.Count);
                            await LogEntriesReceived.Invoke(logEntriesMessage.Payload);
                        }
                        else
                        {
                            _logger.LogWarning("Received LOG_ENTRIES message but payload was null or no subscribers for LogEntriesReceived event.");
                        }
                        break;
                    // Add other message types here if the service sends more than status
                    // case "SettingsUpdateRequested": 
                    //    // Example: if service can request settings from UI
                    //    break;
                    default:
                        _logger.LogWarning("Unknown IPC message type received: {MessageType}", baseMessage.Type);
                        break;
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON deserialization error in ProcessMessageAsync for message: {MessageJson}", messageJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing IPC message: {MessageJson}", messageJson);
            }
        }
        
        public async Task DisconnectAsync()
        {
            await DisconnectAsyncInternal(false); // User-initiated disconnect
        }

        private async Task DisconnectAsyncInternal(bool dueToErrorOrClosure)
        {
            if (!_isConnected && _pipeClient == null) // Already disconnected or never connected
            {
                _logger.LogInformation("Disconnect called but already disconnected or not initialized.");
                return;
            }

            await _pipeLock.WaitAsync(); // Ensure exclusive access for disconnection
            try
            {
                if (!_isConnected && _pipeClient == null) return; // Check again inside lock

                _logger.LogInformation("Disconnecting from service pipe... Error/Closure: {DueToError}", dueToErrorOrClosure);

                if (_listenerCts != null)
                {
                    _listenerCts.Cancel();
                    _listenerCts.Dispose();
                    _listenerCts = null;
                }

                if (_listenerTask != null)
                {
                    try
                    {
                         // Give it a moment to shut down gracefully
                        bool completed = await Task.WhenAny(_listenerTask, Task.Delay(TimeSpan.FromSeconds(2))) == _listenerTask;
                        if(!completed) _logger.LogWarning("Listener task did not complete in time during disconnect.");
                    }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "Exception while waiting for listener task to complete during disconnect.");
                    }
                    _listenerTask = null;
                }
                
                await CleanupPipeResourcesAsync();

                _isConnected = false;
                
                if (PipeDisconnected != null)
                {
                   await PipeDisconnected.Invoke();
                }
                _logger.LogInformation("Successfully disconnected from service pipe.");

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disconnection from service pipe.");
            }
            finally
            {
                _pipeLock.Release();
            }
        }
        
        private async Task CleanupPipeResourcesAsync()
        {
            // This method should not acquire the _pipeLock itself, as it's called from within locked sections.
            _logger.LogDebug("Cleaning up pipe resources.");
            if (_pipeWriter != null)
            {
                try { await _pipeWriter.DisposeAsync(); } catch (Exception ex) { _logger.LogTrace(ex, "Exception disposing StreamWriter."); }
                _pipeWriter = null;
            }
            if (_pipeReader != null)
            {
                // StreamReader.Dispose typically handles underlying stream, but do it explicitly if _pipeClient isn't null.
                // No async dispose for StreamReader directly, but its stream might be an issue if not closed.
                try { _pipeReader.Dispose(); } catch (Exception ex) { _logger.LogTrace(ex, "Exception disposing StreamReader."); }
                _pipeReader = null;
            }
            if (_pipeClient != null)
            {
                try { await _pipeClient.DisposeAsync(); } catch (Exception ex) { _logger.LogTrace(ex, "Exception disposing PipeClient."); }
                _pipeClient = null;
            }
        }

        public async Task SendCommandAsync<T>(string type, T payload, CancellationToken cancellationToken = default)
        {
            if (!_isConnected || _pipeWriter == null)
            {
                _logger.LogWarning("Cannot send command '{Type}': Not connected to service pipe.", type);
                throw new InvalidOperationException("Not connected to the service pipe.");
            }

            await _pipeLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isConnected || _pipeWriter == null) // Re-check inside lock
                {
                     _logger.LogWarning("Cannot send command '{Type}' (checked in lock): Not connected to service pipe.", type);
                     throw new InvalidOperationException("Not connected to the service pipe (checked in lock).");
                }

                var message = new IpcMessage<T> { Type = type, Payload = payload };
                string messageJson = JsonConvert.SerializeObject(message);
                _logger.LogDebug("Sending IPC command: {MessageJson}", messageJson);
                await _pipeWriter.WriteLineAsync(messageJson.AsMemory(), cancellationToken);
                // AutoFlush is true, so no need to call Flush explicitly.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending IPC command of type '{Type}'.", type);
                // Consider if this should trigger a disconnect
                await DisconnectAsyncInternal(true); // Assume pipe is broken
                throw; // Re-throw
            }
            finally
            {
                _pipeLock.Release();
            }
        }

        public async Task SendServiceStatusRequestAsync(CancellationToken cancellationToken = default)
        {
            await SendCommandAsync<object?>("RequestStatus", null, cancellationToken);
        }

        public async Task SendSettingsAsync(LogMonitorSettings settings, CancellationToken cancellationToken = default)
        {
            await SendCommandAsync("UpdateSettings", settings, cancellationToken);
        }
        
        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("Disposing IpcService...");
            await DisconnectAsyncInternal(false); // Ensure cleanup on dispose
            _pipeLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
} 
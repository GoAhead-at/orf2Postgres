using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Npgsql;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;

namespace orf2Postgres
{
    public class OrfProcessorService : BackgroundService
    {
        private readonly ILogger<OrfProcessorService> _logger;
        private readonly IConfiguration _configuration;
        private readonly Dictionary<string, long> _fileOffsets = new Dictionary<string, long>();
        private string _offsetFilePath;
        private string _logDirectory;
        private string _logFilePattern;
        private int _pollingIntervalSeconds;
        private string _connectionString;
        private string _tableName;
        private FileSystemWatcher? _fileWatcher;
        private bool _isRunning = false;
        private string _currentFilePath = string.Empty;
        private long _currentOffset = 0;
        private string _currentStatus = "Stopped";
        private string _errorMessage = string.Empty;
        private NamedPipeServerStream? _pipeServer;
        private string _dbSchema;
        private bool _disposed = false;
        private bool _isWindowsService = false;
        
        // EMERGENCY DIAGNOSTICS: Add direct file-based logging to track pipe communication issues
        private string _directLogFilePath;
        private int _messageSendAttempts = 0;
        private int _messageSendSuccesses = 0;
        private int _pipeBrokenErrors = 0;
        private bool _pipeInitialized = false;
        
        // Add message queue for batching updates
        private ConcurrentQueue<ServiceMessage> _messageQueue = new ConcurrentQueue<ServiceMessage>();
        private Timer? _messageProcessorTimer;
        private const int PipeBufferSize = 65536; // 64KB buffer

        // CRITICAL: Add wait-for-connection logic so we don't miss important startup messages
        private AutoResetEvent _clientConnectedEvent = new AutoResetEvent(false);
        private List<ServiceMessage> _persistentMessageBuffer = new List<ServiceMessage>(500); // Buffer important messages
        private bool _hasClientEverConnected = false;
        private bool _shouldWaitForClientConnection = true;
        private int _bufferedMessageCount = 0;

        // Add aggressive rate limiting 
        private DateTime _lastMessageQueueProcessTime = DateTime.MinValue;
        private DateTime _lastStatusUpdateTime = DateTime.MinValue;
        private const int MESSAGE_RATE_LIMIT_MS = 2000; // Increase rate limiting to 2 seconds to reduce flooding

        // Add message importance filtering
        private bool IsImportantMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;
            
            // CRITICAL: These messages should ALWAYS be visible regardless of mode
            bool isCriticalOperation = 
                   // Database operations - expanded matches
                   message.Contains("DATABASE", StringComparison.OrdinalIgnoreCase) || 
                   message.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("ENTRIES", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("ROWS", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("📊") ||
                   
                   // File processing - expanded matches
                   message.Contains("FILE", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("PROCESS", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("OFFSET", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("LOG", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("📂") ||
                   
                   // Status and errors - always show
                   message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                   
                   // Special markers
                   message.Contains("❌") ||
                   message.Contains("✅") ||
                   message.Contains("⚠️") ||
                   message.Contains("📊") ||
                   message.Contains("📂");
            
            // If it's a critical operation, always show it
            if (isCriticalOperation)
                return true;
            
            // In service mode, be more inclusive with logging
            if (_isWindowsService)
            {
                return isCriticalOperation || 
                       message.Contains("Table", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("Schema", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("Service", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("Written", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("Processed", StringComparison.OrdinalIgnoreCase);
            }
            
            return isCriticalOperation;
        }

        public OrfProcessorService(ILogger<OrfProcessorService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // Initialize configuration
            _logDirectory = configuration["LogDirectory"] ?? "";
            _logFilePattern = configuration["LogFilePattern"] ?? "";
            _pollingIntervalSeconds = int.Parse(configuration["PollingIntervalSeconds"] ?? "5");
            _connectionString = configuration["ConnectionString"] ?? "";
            _tableName = configuration["TableName"] ?? "orf_logs";
            _dbSchema = configuration["DbSchema"] ?? "orf";
            
            // Determine if running as Windows Service
            _isWindowsService = !Environment.UserInteractive;
            
            // Initialize offset file path
            string offsetPath = configuration["OffsetFilePath"] ?? "offsets.json";
            _offsetFilePath = ResolveExecutableRelativePath(offsetPath);
            
            // Log resolved path
            _logger.LogInformation($"📂 Resolved path: '{offsetPath}' to: {_offsetFilePath}");
            
            // Load existing offsets
            LoadOffsets();
            
            // Initialize pipe server if running as service
            if (_isWindowsService)
            {
                InitializeNamedPipe();
            }
        }

        [Serializable]
        private class ServiceMessage
        {
            public bool success { get; set; }
            public string? action { get; set; }
            public string? message { get; set; }
            public string? status { get; set; }
            public string? filePath { get; set; }
            public long offset { get; set; }
            public string? errorMessage { get; set; }
            public Dictionary<string, string>? config { get; set; }
        }

        private void InitializeNamedPipe()
        {
            // DEBUG: Reset our pipe initialization state and error counts
            _pipeInitialized = false;
            _pipeBrokenErrors = 0;
            
            // Log that we're initializing the pipe
            DirectFileLog("PIPE INITIALIZATION: Starting pipe server initialization");
            
            Task.Run(() =>
            {
                try
                {
                    DirectFileLog("PIPE CREATION: Creating named pipe server with 64KB buffer size");
                    
                    // Create a named pipe with increased buffer sizes
                    _pipeServer = new NamedPipeServerStream(
                        "Global\\orf2PostgresPipe", // Use Global namespace
                        PipeDirection.InOut,
                        1, // MaxNumberOfServerInstances
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous,
                        PipeBufferSize, // Input buffer
                        PipeBufferSize); // Output buffer
                    
                    _logger.LogInformation("Named pipe server created with optimized buffer sizes");
                    DirectFileLog("PIPE CREATION: Named pipe server created successfully");
                    
                    // Initialize the message processor timer to batch updates
                    _messageProcessorTimer = new Timer(ProcessMessageQueue, null, 0, 100);
                    DirectFileLog("PIPE INITIALIZATION: Message processor timer initialized");
                    
                    // CRITICAL: Mark the pipe as initialized - even if no client has connected yet
                    _pipeInitialized = true;
                    DirectFileLog("PIPE INITIALIZATION: Pipe server marked as initialized");
                    
                    // Important - announce that we're ready for clients
                    DirectFileLog("PIPE STATUS: Waiting for client connections...");
                    
                    // Send a test message to verify our message sending works without client
                    SendLogMessage("PIPE INITIALIZATION TEST: Server initialized successfully");

                    while (!_disposed)
                    {
                        try
                        {
                            DirectFileLog("PIPE WAITING: Waiting for client connection...");
                            _logger.LogInformation("Waiting for client connection...");
                            
                            _pipeServer.WaitForConnection();
                            
                            _logger.LogInformation("Client connected");
                            DirectFileLog("PIPE CONNECTION: Client connected successfully!");
                            
                            // CRITICAL: Set the connected flag and signal waiting threads
                            _hasClientEverConnected = true;
                            _clientConnectedEvent.Set();
                            
                            // Send a direct welcome message to verify pipe works
                            SendDirectPipeMessage("PIPE WELCOME: Direct client connection established");
                            
                            // Send buffered messages immediately after connection
                            SendBufferedMessages();

                            // Use text-based communication only - standardizing on one approach
                            using (var streamReader = new StreamReader(_pipeServer, Encoding.UTF8, false, 1024, true))
                            using (var streamWriter = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, true))
                            {
                                streamWriter.AutoFlush = true;
                                DirectFileLog("PIPE READY: Stream reader/writer created with UTF8 encoding");

                                // DEBUG: Write a direct test line to the pipe
                                try
                                {
                                    DirectFileLog("PIPE TEST: Sending direct welcome message to client");
                                    streamWriter.WriteLine("[{\"success\":true,\"action\":\"log_message\",\"message\":\"💥 PIPE TEST - Direct Write Test 💥\",\"status\":\"Info\"}]");
                                    streamWriter.Flush();
                                    DirectFileLog("PIPE TEST: Direct welcome message sent successfully");
                                }
                                catch (Exception writeEx)
                                {
                                    DirectFileLog($"PIPE TEST ERROR: Failed to send direct welcome: {writeEx.Message}");
                                }

                                // Main communication loop
                                int loopCount = 0;
                                while (_pipeServer.IsConnected && !_disposed)
                                {
                                    try
                                    {
                                        loopCount++;
                                        
                                        // Every 100 iterations, log that pipe is still active
                                        if (loopCount % 100 == 0)
                                        {
                                            DirectFileLog($"PIPE ACTIVE: Communication loop still running (iteration {loopCount})");
                                        }
                                        
                                        // Check if there's data to read
                                        if (_pipeServer.IsMessageComplete && _pipeServer.InBufferSize > 0)
                                        {
                                            DirectFileLog("PIPE RECEIVE: Incoming data detected");
                                            string? message = null;
                                            try
                                            {
                                                // Read the message as text
                                                message = streamReader.ReadLine();
                                                DirectFileLog($"PIPE RECEIVE: Read data: {(message?.Length > 0 ? message.Substring(0, Math.Min(50, message.Length)) + "..." : "empty")}");
                                            }
                                            catch (Exception readEx)
                                            {
                                                _logger.LogError(readEx, "Error reading from pipe - possible encoding issue");
                                                DirectFileLog($"PIPE RECEIVE ERROR: {readEx.Message}");
                                                // Try to recover from encoding errors by skipping this message
                                                continue;
                                            }

                                            if (!string.IsNullOrEmpty(message))
                                            {
                                                // Filter out any invalid control characters that might cause encoding issues
                                                message = FilterInvalidCharacters(message);
                                                DirectFileLog($"PIPE COMMAND: Processing command, length={message.Length}");
                                                HandleClientCommand(message);
                                                DirectFileLog("PIPE COMMAND: Command processed successfully");
                                            }
                                        }
                                        else
                                        {
                                            // No messages waiting
                                            Thread.Sleep(50);
                                        }
                                    }
                                    catch (EndOfStreamException eosEx)
                                    {
                                        // Client may have disconnected
                                        DirectFileLog($"PIPE DISCONNECT: End of stream detected: {eosEx.Message}");
                                        break;
                                    }
                                    catch (IOException ioEx) when (ioEx.Message.Contains("broken") || ioEx.Message.Contains("disconnected"))
                                    {
                                        // Pipe broken - client disconnect likely
                                        DirectFileLog($"PIPE BROKEN: IO exception: {ioEx.Message}");
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Error in pipe communication loop");
                                        DirectFileLog($"PIPE LOOP ERROR: {ex.Message}");
                                        Thread.Sleep(50); // Brief delay before retrying
                                    }
                                }
                            }

                            _pipeServer.Disconnect();
                            _logger.LogInformation("Client disconnected");
                            DirectFileLog("PIPE DISCONNECT: Client disconnected, pipe server still active");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in named pipe communication");
                            DirectFileLog($"PIPE CONNECTION ERROR: {ex.Message}");
                        }
                        finally
                        {
                            DirectFileLog("PIPE CLEANUP: Cleaning up pipe resources after disconnect");
                            
                            if (_pipeServer != null && _pipeServer.IsConnected)
                            {
                                try
                                {
                                    _pipeServer.Disconnect();
                                    DirectFileLog("PIPE CLEANUP: Disconnected pipe");
                                }
                                catch (Exception disconnectEx)
                                {
                                    DirectFileLog($"PIPE CLEANUP ERROR: Error disconnecting: {disconnectEx.Message}");
                                }
                            }

                            if (_pipeServer != null)
                            {
                                try
                                {
                                    _pipeServer.Dispose();
                                    DirectFileLog("PIPE CLEANUP: Disposed pipe resources");
                                }
                                catch (Exception disposeEx)
                                {
                                    DirectFileLog($"PIPE CLEANUP ERROR: Error disposing: {disposeEx.Message}");
                                }
                            }
                            
                            // Recreate the pipe with improved buffer sizes
                            DirectFileLog("PIPE RECREATION: Creating new pipe server after disconnect");
                            
                            try
                            {
                                _pipeServer = new NamedPipeServerStream(
                                    "Global\\orf2PostgresPipe", // Use Global namespace
                                    PipeDirection.InOut,
                                    1,
                                    PipeTransmissionMode.Message,
                                    PipeOptions.Asynchronous,
                                    PipeBufferSize, // Input buffer
                                    PipeBufferSize); // Output buffer
                                    
                                DirectFileLog("PIPE RECREATION: Successfully created new pipe server");
                                
                                // VERY IMPORTANT: Send a message directly to pipe to test it
                                SendLogMessage("PIPE RECREATION TEST: New pipe server initialized successfully after disconnect");
                            }
                            catch (Exception recreateEx)
                            {
                                DirectFileLog($"PIPE RECREATION ERROR: Failed to create new pipe: {recreateEx.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fatal error in named pipe server");
                    DirectFileLog($"PIPE FATAL ERROR: {ex.Message}");
                    
                    // Set initialization state to false
                    _pipeInitialized = false;
                }
            });
            
            // Give the pipe initialization time to complete
            Thread.Sleep(100);
            
            // Log a message after pipe is initialized
            SendLogMessage("PIPE INITIALIZATION: Named pipe server initialized");
        }
        
        private void ProcessMessageQueue(object? state)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected)
            {
                return; // No client connected
            }
            
            try
            {
                // EXTREME rate limiting - process at most once per rate limit interval
                // Increased to 3 seconds (3000ms) to drastically reduce message flooding
                const int EXTREME_RATE_LIMIT_MS = 3000;
                if (DateTime.Now.Subtract(_lastMessageQueueProcessTime).TotalMilliseconds < EXTREME_RATE_LIMIT_MS)
                {
                    return; // Skip this cycle for extreme rate limiting
                }
                
                _lastMessageQueueProcessTime = DateTime.Now;
                
                // EXTREMELY aggressive filtering and batching
                List<ServiceMessage> messagesToSend = new List<ServiceMessage>();
                int dequeuedCount = 0;
                int statusMessageCount = 0;
                int logMessageCount = 0;
                int filteredCount = 0;
                ServiceMessage? latestStatusMessage = null;
                
                // Keep track of already seen message content to deduplicate similar messages
                HashSet<string> seenMessageContent = new HashSet<string>();
                
                // First pass: Count message types and find latest status message
                // Increased batch size to 100 to process more messages at once
                while (_messageQueue.Count > 0 && dequeuedCount < 100) 
                {
                    if (_messageQueue.TryDequeue(out ServiceMessage? message) && message != null)
                    {
                        dequeuedCount++;
                        
                        // Keep track of most recent status message only
                        if (message.action == "status_response")
                        {
                            statusMessageCount++;
                            latestStatusMessage = message; // Keep only the latest status
                            continue; // Skip adding to messagesToSend, we'll add the latest one at the end
                        }
                        // Very aggressively filter log messages
                        else if (message.action == "log_message" && message.message != null)
                        {
                            logMessageCount++;
                            
                            // Check if this is a high-priority message we want to show
                            bool isHighPriority = (message.status == "Error") || 
                                                 IsImportantMessage(message.message);
                                                 
                            if (!isHighPriority)
                            {
                                filteredCount++;
                                continue; // Skip low-priority messages
                            }
                            
                            // Deduplicate similar messages
                            // For log messages, use just the first 30 chars as a signature
                            string msgSignature = message.message.Length > 30 ? 
                                                 message.message.Substring(0, 30) : 
                                                 message.message;
                                                 
                            if (seenMessageContent.Contains(msgSignature))
                            {
                                filteredCount++;
                                continue; // Skip duplicate-like messages
                            }
                            
                            // Add to seen messages set
                            seenMessageContent.Add(msgSignature);
                            
                            // Add this high-priority, non-duplicate message
                            messagesToSend.Add(message);
                        }
                        // Other messages like config responses - always include
                        else
                        {
                            messagesToSend.Add(message);
                        }
                    }
                }
                
                // Add the latest status message if we have one
                if (latestStatusMessage != null)
                {
                    messagesToSend.Add(latestStatusMessage);
                }
                
                // Don't log message batching details - it's an implementation detail
                // that doesn't need to be visible in logs
                
                // If we found messages to send
                if (messagesToSend.Count > 0)
                {
                    // Prioritize important database and file processing messages first
                    messagesToSend = messagesToSend
                        .OrderByDescending(m => {
                            if (m.message == null) return false;
                            return m.message.Contains("📊") || // Database operations
                                   m.message.Contains("📂") || // File processing
                                   m.message.Contains("✅") || // Success markers
                                   m.message.Contains("❌") || // Error markers
                                   m.status == "Error";
                        })
                        .ToList();
                    
                    // VERY IMPORTANT: Batch in larger groups of 10 to reduce UI flooding
                    for (int i = 0; i < messagesToSend.Count; i += 10)
                    {
                        try
                        {
                            // Get current batch (up to 10 messages)
                            var batch = messagesToSend.Skip(i).Take(10).ToList();
                            
                            // Use proper JSON serialization with array format
                            string json = JsonSerializer.Serialize(batch);
                            json = FilterInvalidCharacters(json);
                            
                            using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                            {
                                writer.WriteLine(json);
                                writer.Flush();
                                
                                // Increased delay between batches to prevent flooding
                                Thread.Sleep(100); // Longer delay between batches
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error sending message batch");
                        }
                    }
                }
                
                // If we drained a lot, log this for diagnostics
                if (dequeuedCount > 10)
                {
                    _logger.LogWarning($"Drained {dequeuedCount} messages from queue - possible flood situation");
                }
                
                // If queue is completely empty and it's been a while since last update,
                // send a minimal status update to keep UI in sync
                if (messagesToSend.Count == 0 && !string.IsNullOrEmpty(_currentFilePath) &&
                    DateTime.Now.Subtract(_lastStatusUpdateTime).TotalMilliseconds > EXTREME_RATE_LIMIT_MS * 2)
                {
                    // Only send status updates at 1/2 the rate of regular message processing
                    _lastStatusUpdateTime = DateTime.Now;
                    
                    // Create and send a status message
                    var statusMessage = new ServiceMessage
                    {
                        success = true,
                        action = "status_response",
                        status = _currentStatus,
                        filePath = _currentFilePath,
                        offset = _currentOffset,
                        errorMessage = _errorMessage
                    };
                    
                    // Send this status update directly
                    try
                    {
                        var singleMessageArray = new List<ServiceMessage> { statusMessage };
                        string json = JsonSerializer.Serialize(singleMessageArray);
                        json = FilterInvalidCharacters(json);
                        
                        using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                        {
                            writer.WriteLine(json);
                            writer.Flush();
                        }
                        
                        // No logging needed for routine status updates
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending status update");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message queue");
            }
        }
        
        private void SendStatusToClient()
        {
            try
            {
                // Aggressive rate limiting - max 1 update per second
                if (DateTime.Now.Subtract(_lastStatusUpdateTime).TotalMilliseconds < MESSAGE_RATE_LIMIT_MS)
                {
                    return; // Skip this update due to rate limiting
                }
                
                _lastStatusUpdateTime = DateTime.Now;
                
                // Only use text-based communication
                if (_pipeServer != null && _pipeServer.IsConnected)
                {
                    // Create a ServiceMessage for the current status
                    var statusMessage = new ServiceMessage {
                        success = true,
                        action = "status_response",
                        message = $"Service status: {_currentStatus}",
                        status = _currentStatus,
                        filePath = _currentFilePath,
                        offset = _currentOffset,
                        errorMessage = _errorMessage
                    };

                    // Serialize with proper options
                    var options = new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = false
                    };
                    
                    string json = JsonSerializer.Serialize(new List<ServiceMessage> { statusMessage }, options);
                    
                    using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                    {
                        writer.WriteLine(json);
                        writer.Flush();
                    }
                    
                    // Also log critical errors immediately
                    if (_currentStatus == "Error" && !string.IsNullOrEmpty(_errorMessage))
                    {
                        _logger.LogError($"Service error: {_errorMessage}");
                        SendLogMessage($"Service error: {_errorMessage}", true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending status to client");
            }
        }

                // Use standardized message sending - only these methods should write to pipe
        private void SendResponse(string message)
        {
            // All communication should go through SendLogMessage to ensure proper format
            SendLogMessage($"Response: {message}");
        }

        // Send a ServiceMessage
        private void SendResponse(ServiceMessage response)
        {
            try
            {
                // Use proper wrapper list format
                var messageList = new List<ServiceMessage> { response };
                
                // Configure JSON serialization options for proper escaping
                var options = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                };

                // Pre-process any file paths in the message to ensure proper escaping
                if (response.message != null)
                {
                    response.message = EscapeWindowsPaths(response.message);
                }
                if (response.filePath != null)
                {
                    response.filePath = EscapeWindowsPaths(response.filePath);
                }
                
                string json = JsonSerializer.Serialize(messageList, options);
                
                // For status responses, log for diagnostics
                if (response.action == "status_response")
                {
                    _logger.LogDebug($"Sending status response: {response.status}, file: {response.filePath}, offset: {response.offset}");
                }
                
                // Send directly using standard UTF8 encoding
                if (_pipeServer != null && _pipeServer.IsConnected)
                {
                    try
                    {
                        using (var writer = new StreamWriter(_pipeServer, new UTF8Encoding(false), 1024, leaveOpen: true))
                        {
                            writer.WriteLine(json);
                            writer.Flush();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending response");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serializing and sending response");
            }
        }

        private string EscapeWindowsPaths(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Replace single backslashes with double backslashes, but only in paths
            var result = new StringBuilder(input.Length);
            bool inPath = false;
            char prev = '\0';

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                
                // Detect start of a Windows path (e.g., "C:\", "D:\", etc.)
                if (i + 2 < input.Length && 
                    char.IsLetter(c) && 
                    input[i + 1] == ':' && 
                    input[i + 2] == '\\')
                {
                    inPath = true;
                }
                
                // Handle backslashes in paths
                if (c == '\\' && inPath)
                {
                    result.Append("\\\\");
                    
                    // Check if this is the end of the path
                    if (i + 1 < input.Length && (input[i + 1] == ' ' || input[i + 1] == '"'))
                    {
                        inPath = false;
                    }
                }
                else
                {
                    result.Append(c);
                    
                    // Check for end of path
                    if (inPath && (c == ' ' || c == '"'))
                    {
                        inPath = false;
                    }
                }
                
                prev = c;
            }

            return result.ToString();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ORF to Postgres service starting");
            SafeConsoleWrite("ORF to Postgres service starting"); // Ensure visible in Windows Event Log
            
            // EMERGENCY DIAGNOSTICS: Write directly to our diagnostics file
            DirectFileLog("**************** SERVICE STARTING ****************");
            
            // ADD SIMPLE DIRECT WRITES TO BYPASS COMPLEX LOGGING
            if (_isWindowsService)
            {
                // Wait a moment for pipe
                await Task.Delay(500);
                
                // Try simple writes
                TrySimpleDirectWrite("🚨 EMERGENCY: Service Starting");
                TrySimpleDirectWrite("🚨 EMERGENCY: Direct Write Test");
                TrySimpleDirectWrite("🚨 PIPE TEST: If you see this message, direct pipe writing works");
            }
            
            DirectFileLog($"Process ID: {System.Diagnostics.Process.GetCurrentProcess().Id}");
            DirectFileLog($"Service Mode: {_isWindowsService}");
            DirectFileLog($"Current Directory: {Directory.GetCurrentDirectory()}");
            DirectFileLog($"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
            DirectFileLog($"Pipe Initialized: {_pipeInitialized}");
            DirectFileLog($"Pipe Server Null: {_pipeServer == null}");
            if (_pipeServer != null)
            {
                DirectFileLog($"Pipe Connected: {_pipeServer.IsConnected}");
            }
            DirectFileLog("************************************************");
            
            // CRITICAL: Send ultra-high visibility service startup messages to ensure pipe communication works
            if (_isWindowsService)
            {
                // Emergency direct file logging - capture any pipe errors
                DirectFileLog("STARTUP: Sending direct pipe messages for diagnostics");
                
                try
                {
                    // Send direct messages that will appear as errors (highlighted) in the UI for maximum visibility
                    SendDirectPipeMessage("SERVICE STARTED - UI CONNECTION TEST");
                    DirectFileLog("STARTUP: First direct message sent");
                    
                    // Send the diagnostics log path as one of the first messages with high visibility
                    SendDirectPipeMessage($"📝 DIAGNOSTICS LOG LOCATION: {_directLogFilePath}");
                    DirectFileLog($"STARTUP: Diagnostics log path sent to UI: {_directLogFilePath}");
                    
                    SendDirectPipeMessage($"DIAGNOSTICS - SERVICE MODE ACTIVE - PID: {System.Diagnostics.Process.GetCurrentProcess().Id}");
                    DirectFileLog("STARTUP: Process ID message sent");
                    
                    SendDirectPipeMessage($"OFFSET FILE PATH: {_offsetFilePath}");
                    DirectFileLog("STARTUP: Offset file path message sent");
                    
                    // Use regular log messages too for redundancy
                    for (int i = 0; i < 5; i++) // Try 5 times with increasing intensity
                    {
                        string testMarker = new string('❗', i+1); // Increasing number of markers
                        SendLogMessage($"{testMarker} SERVICE STARTING TEST #{i+1} - THIS MESSAGE SHOULD BE VISIBLE IN UI {testMarker}");
                        DirectFileLog($"STARTUP: Test message {i+1} sent");
                        await Task.Delay(100);
                    }
                    
                    // Log current directory and executable path for troubleshooting
                    string execDir = System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                    SendDirectPipeMessage($"SERVICE EXECUTABLE DIRECTORY: {execDir}");
                    DirectFileLog("STARTUP: Executable directory message sent");
                    
                    SendDirectPipeMessage($"SERVICE CURRENT DIRECTORY: {Directory.GetCurrentDirectory()}");
                    DirectFileLog("STARTUP: Current directory message sent");
                    
                    // Force create the offset file at service startup
                    SendLogMessage("👉 CRITICAL: Forcing offset file creation at service startup");
                    DirectFileLog("STARTUP: Forcing offset file save");
                    ForceSaveOffsets();
                    DirectFileLog("STARTUP: Offset file saved successfully");
                    
                    // Confirm service is fully initialized after creating offset file
                    SendDirectPipeMessage("SERVICE INITIALIZATION COMPLETE - READY TO PROCESS FILES");
                    DirectFileLog("STARTUP: Service initialization complete message sent");
                    
                    // Last ditch test messages with unique markers
                    SendLogMessage("🔴🔴🔴 SERVICE STARTUP SEQUENCE COMPLETE 🔴🔴🔴");
                    
                    // Try the most direct approach possible - direct pipe write
                    if (_pipeServer != null && _pipeServer.IsConnected)
                    {
                        DirectFileLog("STARTUP: Attempting emergency direct pipe write...");
                        try
                        {
                            using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                            {
                                string emergencyMessage = "[{\"success\":true,\"action\":\"log_message\",\"message\":\"🚨 EMERGENCY DIRECT WRITE TEST 🚨\",\"status\":\"Error\"}]";
                                writer.WriteLine(emergencyMessage);
                                writer.Flush();
                                DirectFileLog("STARTUP: Emergency direct write successful!");
                            }
                        }
                        catch (Exception directEx)
                        {
                            DirectFileLog($"STARTUP ERROR: Emergency direct write failed: {directEx.Message}");
                        }
                    }
                    else
                    {
                        DirectFileLog("STARTUP WARNING: Pipe not connected for emergency write test");
                    }
                }
                catch (Exception startupEx)
                {
                    DirectFileLog($"STARTUP ERROR: Exception sending startup messages: {startupEx.Message}");
                    _logger.LogError(startupEx, "Error sending service startup messages");
                }
            }
            
            // Add critical debug information for service mode
            if (_isWindowsService)
            {
                _logger.LogWarning("-------------- SERVICE MODE STARTUP DEBUG --------------");
                _logger.LogWarning($"Working Directory: {Directory.GetCurrentDirectory()}");
                _logger.LogWarning($"Log Directory: {_logDirectory}");
                _logger.LogWarning($"Log Pattern: {_logFilePattern}");
                _logger.LogWarning($"Offset File Path: {_offsetFilePath}");
                
                // Check if offset file exists and is accessible
                await Task.Run(() => TestOffsetFileAccess());
                
                // Try to list files in the configured log directory
                try 
                {
                    if (Directory.Exists(_logDirectory))
                    {
                        string[] files = Directory.GetFiles(_logDirectory, "*.log");
                        _logger.LogWarning($"Found {files.Length} log files in {_logDirectory}: {string.Join(", ", files.Select(Path.GetFileName))}");
                        
                        // Create a new test file if none exists
                        if (files.Length == 0)
                        {
                            string testPath = Path.Combine(_logDirectory, $"orfee-{DateTime.Now:yyyy-MM-dd}.log");
                            _logger.LogWarning($"Creating test log file at: {testPath}");
                            File.WriteAllText(testPath, "TEST-MSG-1 event 2025-05-16T14:30:45 info low log-entry email-filter 192.168.1.1 sender@example.com recipient@example.com Test Email Subject Author remote-peer 10.0.0.1 US Test log message");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Log directory does not exist: {_logDirectory}");
                        Directory.CreateDirectory(_logDirectory);
                        _logger.LogWarning($"Created log directory: {_logDirectory}");
                        
                        // Create test file in the new directory
                        string testPath = Path.Combine(_logDirectory, $"orfee-{DateTime.Now:yyyy-MM-dd}.log");
                        _logger.LogWarning($"Creating test log file at: {testPath}");
                        File.WriteAllText(testPath, "TEST-MSG-1 event 2025-05-16T14:30:45 info low log-entry email-filter 192.168.1.1 sender@example.com recipient@example.com Test Email Subject Author remote-peer 10.0.0.1 US Test log message");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during startup debug checks");
                }
                
                _logger.LogWarning("----------------------------------------------------");
            }
            
            // IMMEDIATE TEST - don't wait for any timers or processing cycles
            await ImmediateTestDbAndFileAccess();
            
            // Force save offsets at startup to ensure the file exists
            _logger.LogWarning("👉 CRITICAL STARTUP: Forcing offset file creation after immediate tests");
            ForceSaveOffsets();
            
            // CRITICAL CHANGE: Wait for client connection before processing anything
            if (_shouldWaitForClientConnection && _isWindowsService)
            {
                if (!_pipeServer?.IsConnected ?? true)
                {
                    DirectFileLog("CRITICAL WAIT: Waiting for client connection before processing files...");
                    _logger.LogWarning("CRITICAL WAIT: Waiting for client connection before processing files");
                    
                    // Send explicit message - even though it will be buffered until connection
                    SendLogMessage("⏱️ SERVICE WAITING: The service is waiting for UI to connect before processing log files");
                    
                    // Wait for up to 30 seconds for client to connect
                    bool clientConnected = _clientConnectedEvent.WaitOne(TimeSpan.FromSeconds(30));
                    
                    if (clientConnected)
                    {
                        DirectFileLog("CRITICAL WAIT: Client connected, proceeding with processing");
                        _logger.LogWarning("CRITICAL WAIT: Client connected, proceeding with processing");
                        SendLogMessage("✅ CONNECTED: UI connected successfully, processing log files");
                    }
                    else
                    {
                        DirectFileLog("CRITICAL WAIT: Timeout waiting for client connection, proceeding anyway");
                        _logger.LogWarning("CRITICAL WAIT: Timeout waiting for client connection, proceeding anyway");
                        SendLogMessage("⚠️ TIMEOUT: UI did not connect within timeout, proceeding anyway");
                    }
                }
                else
                {
                    DirectFileLog("CRITICAL WAIT: Client already connected, no need to wait");
                    _logger.LogWarning("CRITICAL WAIT: Client already connected, no need to wait");
                }
            }
            
            // Set up a timer to log diagnostics periodically
            var diagnosticsTimer = new System.Timers.Timer(15000); // Every 15 seconds
            diagnosticsTimer.Elapsed += async (s, e) => 
            {
                try
                {
                    // Log the diagnostics file location every 15 seconds
                    LogDiagnosticsFileLocation();
                    
                    _logger.LogInformation($"SERVICE DIAGNOSTICS: Status={_currentStatus}, File={_currentFilePath}, Offset={_currentOffset}, Running={_isRunning}, IsWindowsService={_isWindowsService}");
                    _logger.LogInformation($"SERVICE DIAGNOSTICS: Current directory={Directory.GetCurrentDirectory()}, LogDirectory={_logDirectory}");
                    
                    // Forced direct processing attempt every 15 seconds
                    if (_isWindowsService && _isRunning)
                    {
                        _logger.LogWarning("SERVICE DIAGNOSTICS: Forcing direct log file processing attempt");
                        SendLogMessage("SERVICE DIAGNOSTICS: Forcing direct log file processing attempt");
                        
                        try
                        {
                            // Create a safely wrapped cancellation token
                            using (var cts = new CancellationTokenSource())
                            {
                                // Don't let this run too long
                                cts.CancelAfter(10000); // 10 seconds max
                                
                                // Force process log files directly
                                await ProcessLogFiles(cts.Token);
                                
                                _logger.LogWarning("SERVICE DIAGNOSTICS: Forced log file processing completed");
                                SendLogMessage("SERVICE DIAGNOSTICS: Forced log file processing completed");
                            }
                        }
                        catch (Exception procEx)
                        {
                            _logger.LogError(procEx, "SERVICE DIAGNOSTICS: Forced log file processing failed");
                            SendLogMessage($"SERVICE DIAGNOSTICS: Forced log file processing failed: {procEx.Message}", true);
                        }
                    }
                    
                    // Check DB connection periodically
                    if (_isWindowsService)
                    {
                        Task.Run(async () => {
                            try
                            {
                                using (var connection = new NpgsqlConnection(_connectionString))
                                {
                                    await connection.OpenAsync();
                                    _logger.LogInformation("SERVICE DIAGNOSTICS: Database connection test successful");
                                    connection.Close();
                                }
                            }
                            catch (Exception dbEx)
                            {
                                _logger.LogError(dbEx, "SERVICE DIAGNOSTICS: Database connection test failed");
                            }
                        });
                    }
                }
                catch (Exception diagEx)
                {
                    _logger.LogError(diagEx, "Error in diagnostics timer");
                }
            };
            diagnosticsTimer.Start();
            
            // Send a test log message to verify pipe communication
            try
            {
                SendLogMessage("Service started - testing log message system");
                await Task.Delay(500, stoppingToken); // Give message time to be sent
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending test log message");
            }
            
            try
            {
                // Try to ensure the database and table exist
                try
                {
                    // Important service debug logging - critical for finding issues
                    SendLogMessage($"SERVICE DEBUG: Mode={(_isWindowsService ? "Service" : "Window")}, OS={Environment.OSVersion}");
                    SendLogMessage($"SERVICE DEBUG: Current directory={Directory.GetCurrentDirectory()}");
                    SendLogMessage($"SERVICE DEBUG: Log directory={_logDirectory}");
                    
                    // Verify database config is being correctly loaded
                    string host = _configuration["PostgresConnection:Host"] ?? "unknown";
                    string database = _configuration["PostgresConnection:Database"] ?? "unknown";
                    string username = _configuration["PostgresConnection:Username"] ?? "unknown";
                    string configPassword = _configuration["PostgresConnection:Password"] ?? "unknown";
                    
                    // Mask password for log display
                    string maskedPassword = configPassword.Length > 0 ? 
                        new string('*', Math.Min(configPassword.Length, 8)) : "empty";
                    
                    SendLogMessage($"DATABASE CONFIG: Host={host}, Database={database}, Username={username}, Password={maskedPassword}, Schema={_dbSchema}");
                    
                    // Check the database connection first
                    using (var connection = new NpgsqlConnection(_connectionString))
                    {
                        try
                        {
                            _logger.LogInformation("Testing database connection...");
                            _logger.LogInformation($"Connection String (masked): {_connectionString.Replace("orforf", "******")}");
                            SendLogMessage("Testing database connection...");
                            
                            // Use explicit password if other methods fail
                            try
                            {
                                connection.Open();
                            }
                            catch (Exception firstAttemptEx)
                            {
                                // Try with explicit unencrypted password
                                _logger.LogWarning($"First connection attempt failed, trying with explicit password");
                                
                                // Build a new connection string with explicit password
                                var builder = new NpgsqlConnectionStringBuilder(_connectionString)
                                {
                                    Password = "orforf" // Use known working password
                                };
                                
                                using (var fallbackConnection = new NpgsqlConnection(builder.ConnectionString))
                                {
                                    fallbackConnection.Open();
                                    SendLogMessage("Database connection successful with fallback password!");
                                    
                                    // Update the connection string for future use
                                    _connectionString = builder.ConnectionString;
                                }
                                
                                return; // Skip the rest since we've already handled this
                            }
                            
                            _logger.LogInformation("Database connection successful");
                            SendLogMessage("Database connection successful!");
                        }
                        catch (Exception dbEx)
                        {
                            string dbErrorMsg = $"Database connection failed: {GetUserFriendlyErrorMessage(dbEx)}";
                            _logger.LogError(dbEx, dbErrorMsg);
                            _currentStatus = "Error";
                            _errorMessage = dbErrorMsg;
                            SendLogMessage(dbErrorMsg, true);
                            SendStatusToClient();
                            
                            // Also show detailed error for diagnosis
                            SendLogMessage($"DETAILED ERROR: {dbEx.Message}", true);
                            if (dbEx.InnerException != null)
                            {
                                SendLogMessage($"INNER ERROR: {dbEx.InnerException.Message}", true);
                            }
                            
                            return;
                        }
                    }

                    // Now try to create/verify the database table
                    _logger.LogInformation("Verifying database table structure...");
                    SendLogMessage("Verifying database table structure...");
                    CreateOrfLogsTable();
                    _logger.LogInformation("Database table verified successfully");
                    SendLogMessage("Database table verified successfully!");
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Failed to verify database table: {GetUserFriendlyErrorMessage(ex)}";
                    _logger.LogError(ex, errorMsg);
                    _currentStatus = "Error";
                    _errorMessage = errorMsg;
                    // Send error as log message for UI display
                    SendLogMessage(errorMsg, true);
                    // Queue error status update
                    SendStatusToClient();
                    return;
                }
                
                // Set up file system watcher
                try
                {
                    SetupFileSystemWatcher();
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Failed to set up file monitoring: {GetUserFriendlyErrorMessage(ex)}";
                    _logger.LogError(ex, errorMsg);
                    _currentStatus = "Error";
                    _errorMessage = errorMsg;
                    // Queue error status update instead of direct pipe write
                    SendStatusToClient();
                    return;
                }

                // Start a timer to periodically update UI with status (every 2 seconds)
                var statusTimer = new System.Timers.Timer(2000);
                statusTimer.Elapsed += (s, e) => 
                {
                    try 
                    {
                        // Queue status update instead of direct pipe write
                        SendStatusToClient();
                    }
                    catch (Exception statusEx)
                    {
                        _logger.LogError(statusEx, "Error queueing status update");
                    }
                };
                statusTimer.Start();

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (_isRunning)
                        {
                            _currentStatus = "Running";
                            _errorMessage = string.Empty;
                            
                            // Process log files
                            await ProcessLogFiles(stoppingToken);
                            
                            // Queue status update at end of processing cycle
                            SendStatusToClient();
                        }
                        else
                        {
                            _currentStatus = "Stopped";
                            // Queue status update in stopped state too
                            SendStatusToClient();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log exception but keep running
                        _logger.LogError(ex, "Error during log processing cycle");
                        _currentStatus = "Error";
                        _errorMessage = GetUserFriendlyErrorMessage(ex);
                        // Queue error status update
                        SendStatusToClient();
                    }
                    
                    // Wait before next cycle (but don't block shutdown)
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_pollingIntervalSeconds), stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        // This is expected during shutdown, no need to log
                        break;
                    }
                }
                
                // Clean up before exit
                statusTimer.Stop();
                statusTimer.Dispose();
                diagnosticsTimer.Stop();
                diagnosticsTimer.Dispose();
                _fileWatcher?.Dispose();
                
                _logger.LogInformation("Service execution completed normally");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in service execution");
                _currentStatus = "Error";
                _errorMessage = GetUserFriendlyErrorMessage(ex);
                // Final status update on fatal error
                SendStatusToClient();
                throw;
            }
        }

        // Immediate test method that runs at startup - database connectivity only
        private async Task ImmediateTestDbAndFileAccess()
        {
            SendLogMessage("STARTUP CHECK: Starting file and database checks");
            SafeConsoleWrite("STARTUP CHECK: Starting file and database checks");
            _logger.LogWarning("STARTUP CHECK: Starting file and database checks");
            
            // CRITICAL: Identify where files are located at startup
            string baseDirFiles = "None found";
            string logDirFiles = "None found";
            
            try
            {
                string[] baseFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.log");
                baseDirFiles = baseFiles.Length > 0 ? string.Join(", ", baseFiles.Select(Path.GetFileName)) : "None found";
                SendLogMessage($"STARTUP FILES IN BASE DIR: {baseDirFiles}");
                _logger.LogWarning($"STARTUP FILES IN BASE DIR: {baseDirFiles}");
            }
            catch (Exception bEx)
            {
                SendLogMessage($"STARTUP ERROR checking base dir: {bEx.Message}");
                _logger.LogError(bEx, "Error checking base directory for logs");
            }
            
            try
            {
                string[] logFiles = Directory.GetFiles(_logDirectory, "*.log");
                logDirFiles = logFiles.Length > 0 ? string.Join(", ", logFiles.Select(Path.GetFileName)) : "None found";
                SendLogMessage($"STARTUP FILES IN LOG DIR: {logDirFiles}");
                _logger.LogWarning($"STARTUP FILES IN LOG DIR: {logDirFiles}");
            }
            catch (Exception lEx)
            {
                SendLogMessage($"STARTUP ERROR checking log dir: {lEx.Message}");
                _logger.LogError(lEx, "Error checking log directory for logs");
            }
            
            // DO A DIRECT WRITE TO THE DATABASE
            try
            {
                SendLogMessage("DIRECT TEST: Attempting to write a test record directly to the database");
                _logger.LogWarning("DIRECT TEST: Attempting to write a test record directly to the database");
                
                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    SendLogMessage("DIRECT TEST: Database connection successful");
                    
                    // Create a direct test record
                    using (var cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = connection;
                        cmd.CommandTimeout = 30;
                        
                        string testMsg = $"SERVICE-START-TEST at {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                        string entryHash = ComputeHash(testMsg);
                        
                        cmd.CommandText = $@"
                        INSERT INTO {_dbSchema}.{_tableName} (
                            message_id, event_datetime, event_msg, filename, entry_hash
                        ) VALUES (
                            'DIRECT-TEST', @timestamp, @message, 'direct-test.log', @hash
                        ) ON CONFLICT (entry_hash) DO NOTHING";
                        
                        cmd.Parameters.AddWithValue("timestamp", DateTime.UtcNow);
                        cmd.Parameters.AddWithValue("message", testMsg);
                        cmd.Parameters.AddWithValue("hash", entryHash);
                        
                        int result = await cmd.ExecuteNonQueryAsync();
                        
                        if (result > 0)
                        {
                            SendLogMessage($"SUCCESS: Directly inserted test record into database");
                            _logger.LogWarning($"SUCCESS: Directly inserted test record into database");
                        }
                        else
                        {
                            SendLogMessage("No rows affected - record may already exist");
                            _logger.LogWarning("No rows affected - record may already exist");
                        }
                    }
                }
            }
            catch (Exception directEx)
            {
                SendLogMessage($"CRITICAL DATABASE ERROR: {directEx.Message}", true);
                _logger.LogError(directEx, "CRITICAL DATABASE ERROR");
            }
            
            // FORCE PROCESS A LOG FILE IMMEDIATELY
            try
            {
                // Find a log file to process
                string logFilePath = string.Empty;
                
                // Try application base directory first
                string[] appBaseFiles = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.log");
                if (appBaseFiles.Length > 0)
                {
                    logFilePath = appBaseFiles[0];
                }
                
                // If found, process it immediately
                if (!string.IsNullOrEmpty(logFilePath))
                {
                    SendLogMessage($"IMMEDIATE PROCESSING: Attempting to process {Path.GetFileName(logFilePath)}");
                    _logger.LogWarning($"IMMEDIATE PROCESSING: Attempting to process {Path.GetFileName(logFilePath)}");
                    
                    try
                    {
                        // Process directly 
                        using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            // Read the file content
                            var reader = new StreamReader(fileStream);
                            string content = await reader.ReadToEndAsync();
                            
                            // Split into lines
                            string[] lines = content.Split('\n');
                            
                            SendLogMessage($"Read {lines.Length} lines from {Path.GetFileName(logFilePath)}");
                            _logger.LogWarning($"Read {lines.Length} lines from {Path.GetFileName(logFilePath)}");
                            
                            // Try to parse and insert into database
                            var logEntries = new List<string>();
                            foreach (var line in lines)
                            {
                                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                                {
                                    logEntries.Add(line);
                                }
                            }
                            
                            if (logEntries.Count > 0)
                            {
                                SendLogMessage($"Found {logEntries.Count} log entries to process");
                                _logger.LogWarning($"Found {logEntries.Count} log entries to process");
                                
                                // Write directly to database
                                await WriteToDatabase(logEntries, logFilePath);
                            }
                        }
                    }
                    catch (Exception fileEx)
                    {
                        SendLogMessage($"IMMEDIATE PROCESSING ERROR: {fileEx.Message}", true);
                        _logger.LogError(fileEx, "IMMEDIATE PROCESSING ERROR");
                    }
                }
            }
            catch (Exception fileEx)
            {
                SendLogMessage($"IMMEDIATE FILE PROCESSING ERROR: {fileEx.Message}", true);
                _logger.LogError(fileEx, "IMMEDIATE FILE PROCESSING ERROR");
            }
            
            try
            {
                // Skip creating test files - only verify directory access
                try
                {
                    // Ensure log directory exists
                    if (!Directory.Exists(_logDirectory))
                    {
                        Directory.CreateDirectory(_logDirectory);
                        SendLogMessage($"STARTUP CHECK: Created log directory: {_logDirectory}");
                        _logger.LogWarning($"STARTUP CHECK: Created log directory: {_logDirectory}");
                    }
                    else 
                    {
                        SendLogMessage($"STARTUP CHECK: Log directory exists: {_logDirectory}");
                        _logger.LogWarning($"STARTUP CHECK: Log directory exists: {_logDirectory}");
                        
                        // Check if we have access to list files (permissions check)
                        try 
                        {
                            string[] files = Directory.GetFiles(_logDirectory, "*.log");
                            SendLogMessage($"STARTUP CHECK: Found {files.Length} log files in directory");
                            _logger.LogWarning($"STARTUP CHECK: Found {files.Length} log files in directory");
                        }
                        catch (Exception listEx)
                        {
                            SendLogMessage($"STARTUP CHECK: Cannot list files in log directory: {listEx.Message}", true);
                            _logger.LogError(listEx, "STARTUP CHECK: Cannot list files in log directory");
                        }
                    }
                }
                catch (Exception logDirEx)
                {
                    SendLogMessage($"STARTUP CHECK: Failed to access log directory: {logDirEx.Message}", true);
                    _logger.LogError(logDirEx, "STARTUP CHECK: Failed to access log directory");
                }
                
                // 3. Try a basic database table check
                try
                {
                    SendLogMessage("IMMEDIATE TEST: Checking database table existence");
                    
                    using (var connection = new NpgsqlConnection(_connectionString))
                    {
                        await connection.OpenAsync();
                        SendLogMessage("IMMEDIATE TEST: Database connection successful");
                        
                        // Check if table exists
                        using (var cmd = new NpgsqlCommand())
                        {
                            cmd.Connection = connection;
                            cmd.CommandText = @"
                                SELECT EXISTS (
                                    SELECT FROM information_schema.tables 
                                    WHERE table_schema = @schema
                                    AND table_name = @tableName
                                )";
                            cmd.Parameters.AddWithValue("schema", _dbSchema);
                            cmd.Parameters.AddWithValue("tableName", _tableName);
                            
                            bool tableExists = (bool)await cmd.ExecuteScalarAsync();
                            
                            SendLogMessage($"IMMEDIATE TEST: Table {_dbSchema}.{_tableName} exists: {tableExists}");
                            _logger.LogWarning($"IMMEDIATE TEST: Table {_dbSchema}.{_tableName} exists: {tableExists}");
                            
                            if (tableExists)
                            {
                                // Try to count records
                                cmd.CommandText = $"SELECT COUNT(*) FROM {_dbSchema}.{_tableName}";
                                cmd.Parameters.Clear();
                                
                                long count = (long)await cmd.ExecuteScalarAsync();
                                
                                SendLogMessage($"IMMEDIATE TEST: Table {_dbSchema}.{_tableName} has {count} records");
                                _logger.LogWarning($"IMMEDIATE TEST: Table {_dbSchema}.{_tableName} has {count} records");
                            }
                            else
                            {
                                // Try to create the table if it doesn't exist
                                SendLogMessage("IMMEDIATE TEST: Table does not exist, trying to create it");
                                await CreateOrVerifyTable(connection);
                            }
                        }
                        
                        // Skip direct test insert - only verify connectivity
                        SendLogMessage("STARTUP CHECK: Database connection verified successfully");
                        _logger.LogWarning("STARTUP CHECK: Database connection verified successfully");
                    }
                }
                catch (Exception dbEx)
                {
                    SendLogMessage($"IMMEDIATE TEST: Database access failed: {dbEx.Message}", true);
                    _logger.LogError(dbEx, "IMMEDIATE TEST: Database access failed");
                }
            }
            catch (Exception ex)
            {
                SendLogMessage($"IMMEDIATE TEST: Test failed with error: {ex.Message}", true);
                _logger.LogError(ex, "IMMEDIATE TEST: Test failed");
            }
        }

        // Helper method to create or verify the table
        private async Task CreateOrVerifyTable(NpgsqlConnection connection)
        {
            try
            {
                // Check if schema exists and create if not
                if (_dbSchema != "public")
                {
                    using (var cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = connection;
                        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {_dbSchema}";
                        await cmd.ExecuteNonQueryAsync();
                        SendLogMessage($"IMMEDIATE TEST: Created schema {_dbSchema}");
                    }
                }
                
                // Create table with simplified structure for test
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = $@"
                        CREATE TABLE IF NOT EXISTS {_dbSchema}.{_tableName} (
                            id SERIAL PRIMARY KEY,
                            message_id TEXT,
                            event_datetime TIMESTAMPTZ,
                            event_msg TEXT,
                            filename TEXT,
                            processed_at TIMESTAMPTZ DEFAULT NOW(),
                            entry_hash TEXT UNIQUE
                        )";
                    
                    await cmd.ExecuteNonQueryAsync();
                    SendLogMessage($"IMMEDIATE TEST: Created table {_dbSchema}.{_tableName}");
                }
            }
            catch (Exception ex)
            {
                SendLogMessage($"IMMEDIATE TEST: Failed to create/verify table: {ex.Message}", true);
                _logger.LogError(ex, "IMMEDIATE TEST: Failed to create/verify table");
                throw;
            }
        }

        private string GetUserFriendlyErrorMessage(Exception ex)
        {
            // Convert technical exceptions to more user-friendly messages
            if (ex is DirectoryNotFoundException || (ex is ArgumentException && ex.Message.Contains("does not exist")))
            {
                return $"Directory not found. Please check your configuration and ensure the specified directory exists.";
            }
            else if (ex is FileNotFoundException)
            {
                return $"File not found. Please check your configuration and ensure the file exists.";
            }
            else if (ex is UnauthorizedAccessException)
            {
                return $"Access denied. The application does not have permission to access the specified file or directory.";
            }
            else if (ex is NpgsqlException)
            {
                return $"Database error. Could not connect to or query the PostgreSQL database. Verify your connection settings.";
            }
            else if (ex is IOException ioe && ioe.Message.Contains("being used by another process"))
            {
                return $"File is locked by another process. Please ensure the file is not open in another application.";
            }
            else
            {
                // For unknown exceptions, still provide the technical message but with a more friendly prefix
                return $"An unexpected error occurred: {ex.Message}";
            }
        }

        private void SetupFileSystemWatcher()
        {
            try
            {
                // First check if directory exists
                if (!Directory.Exists(_logDirectory))
                {
                    // Try to create the directory
                    try
                    {
                        _logger.LogWarning($"Log directory '{_logDirectory}' not found. Attempting to create it...");
                        Directory.CreateDirectory(_logDirectory);
                        _logger.LogInformation($"Successfully created log directory: {_logDirectory}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to create log directory '{_logDirectory}'");
                        throw new DirectoryNotFoundException($"The log directory '{_logDirectory}' does not exist and could not be created. Please create this directory manually or update your configuration with a valid path.");
                    }
                }

                _fileWatcher = new FileSystemWatcher(_logDirectory)
                {
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite,
                    Filter = "*.log",
                    EnableRaisingEvents = true
                };

                _fileWatcher.Created += OnFileChanged;
                _fileWatcher.Changed += OnFileChanged;

                _logger.LogInformation($"File system watcher set up for directory: {_logDirectory}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting up file system watcher for directory: {_logDirectory}");
                throw; // Let the calling method handle this with user-friendly message
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            _logger.LogInformation($"File system event: {e.ChangeType} - {e.FullPath}");
            
            // Update the current file path to show in the UI
            _currentFilePath = e.FullPath;
            
            // If we're waiting for a file to monitor, set running status when file is detected
            if (_isRunning && string.IsNullOrEmpty(_currentFilePath))
            {
                _currentStatus = "Running";
            }
        }

        // Safe wrapper for Console.WriteLine to avoid pipe communication issues
        private void SafeConsoleWrite(string message)
        {
            // Only write to console if not running as a service
            if (!_isWindowsService)
            {
                Console.WriteLine(message);
            }
            else
            {
                // In service mode, just use logger
                _logger.LogInformation(message);
            }
        }
        
        private async Task ProcessLogFiles(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"Checking for new log files in {_logDirectory}");
            
            // Get current log file name based on pattern
            string currentLogFileName = GetCurrentLogFileName();
            
            // Create a list to store paths of files to process
            List<string> filesToProcess = new List<string>();
            
            // Service mode: Search for log files
            if (_isWindowsService)
            {
                try
                {
                    // Use the configured pattern instead of hardcoded one
                    string searchPattern = currentLogFileName;
                    
                    // If pattern contains date placeholder, convert to wildcard
                    if (_logFilePattern.Contains("{Date:"))
                    {
                        int startIndex = _logFilePattern.IndexOf("{Date:");
                        int endIndex = _logFilePattern.IndexOf("}", startIndex);
                        if (startIndex >= 0 && endIndex > startIndex)
                        {
                            searchPattern = _logFilePattern.Substring(0, startIndex) + "*" + _logFilePattern.Substring(endIndex + 1);
                        }
                    }
                    
                    _logger.LogInformation($"Searching for files with pattern: {searchPattern}");
                    string[] matchingFiles = Directory.GetFiles(_logDirectory, searchPattern);
                    
                    if (matchingFiles.Length > 0)
                    {
                        _logger.LogInformation($"Found {matchingFiles.Length} log files to process");
                        filesToProcess.AddRange(matchingFiles);
                    }
                    else
                    {
                        _logger.LogInformation($"No files found matching pattern: {searchPattern}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error searching directory {_logDirectory}");
                }
            }
            
            // Process files if any found
            if (filesToProcess.Count > 0)
            {
                _logger.LogInformation($"Processing {filesToProcess.Count} log files");
                foreach (string filePath in filesToProcess)
                {
                    try
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            await ProcessFileContent(fileStream, filePath, stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing file: {filePath}");
                    }
                }
            }
        }

        private string GetCurrentLogFileName()
        {
            // Check if the pattern contains a date placeholder
            if (_logFilePattern.Contains("{Date:"))
            {
                // Extract format from pattern (e.g., "yyyy-MM-dd" from "{Date:yyyy-MM-dd}")
                string dateFormat = "yyyy-MM-dd"; // Default format
                int startIndex = _logFilePattern.IndexOf("{Date:");
                if (startIndex >= 0)
                {
                    int endIndex = _logFilePattern.IndexOf("}", startIndex);
                    if (endIndex > startIndex)
                    {
                        string formatPart = _logFilePattern.Substring(startIndex + 6, endIndex - startIndex - 6);
                        if (!string.IsNullOrEmpty(formatPart))
                        {
                            dateFormat = formatPart;
                        }
                    }
                }
                
                // Replace date placeholder with current date
                return _logFilePattern.Replace($"{{Date:{dateFormat}}}", DateTime.Now.ToString(dateFormat));
            }
            // If it's a wildcard pattern or static file name, return as is
            else
            {
                return _logFilePattern;
            }
        }

        private async Task ProcessFileContent(FileStream fileStream, string filePath, CancellationToken cancellationToken)
        {
            using (var reader = new StreamReader(fileStream, Encoding.UTF8, true, 8192, true)) // Increase buffer size
            {
                // Increase batch size to reduce database round trips
                const int batchSize = 250; 
                var buffer = new List<string>(batchSize); // Pre-allocate capacity
                string line;
                int linesRead = 0;
                long lastOffsetReported = 0;
                long lastStatusUpdateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                // Log start of file processing to UI
                SendLogMessage($"Starting to read {Path.GetFileName(filePath)}");
                
                while ((line = await reader.ReadLineAsync()) != null && !cancellationToken.IsCancellationRequested)
                {
                    // Skip comment lines
                    if (line.StartsWith("#"))
                    {
                        continue;
                    }

                    // Skip empty lines
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    buffer.Add(line);
                    linesRead++;

                    // Process in batches for efficiency
                    if (buffer.Count >= batchSize)
                    {
                        await WriteToDatabase(buffer, filePath);
                        buffer.Clear();
                        
                        // Update the current offset for status reporting
                        _currentOffset = fileStream.Position;
                        
                        // Report current position back to UI
                        long currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        
                        // Report every ~10KB for in-app mode, or every 2 seconds for service mode
                        if ((!_isWindowsService && (fileStream.Position - lastOffsetReported > 10240)) || 
                            (_isWindowsService && (currentTime - lastStatusUpdateTime > 2000)))
                        {
                            if (!_isWindowsService)
                            {
                                SafeConsoleWrite($"Current offset: {fileStream.Position}");
                            }
                            
                            // For Windows Service mode, send status via named pipe
                            if (_isWindowsService)
                            {
                                SendStatusToClient();
                            }
                            
                            // Send progress update to UI
                            SendLogMessage($"Processed {linesRead} lines so far from {Path.GetFileName(filePath)}");
                            
                            lastOffsetReported = fileStream.Position;
                            lastStatusUpdateTime = currentTime;
                        }
                    }
                }

                // Process any remaining lines
                if (buffer.Count > 0)
                {
                    await WriteToDatabase(buffer, filePath);
                    buffer.Clear();
                }
                
                // Update the offsets file with the new position
                _fileOffsets[filePath] = fileStream.Position;
                _currentOffset = fileStream.Position;
                
                // CRITICAL: Force save offset file after every file processed
                // This ensures we don't reprocess the same file if the service restarts
                ForceSaveOffsets();
                
                // Log completion with a simple message
                string completionMsg = $"Completed processing {linesRead} lines from {Path.GetFileName(filePath)}";
                _logger.LogInformation(completionMsg);
                SendLogMessage(completionMsg);
            }
        }

        private async Task WriteToDatabase(List<string> logLines, string filePath)
        {
            int processedCount = 0;
            int errorCount = 0;
            string fileName = Path.GetFileName(filePath);

            // Log start of database operation
            _logger.LogInformation($"Writing {logLines.Count} entries to database");
            
            try
            {
                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    try
                    {
                        await connection.OpenAsync();
                        using (var transaction = await connection.BeginTransactionAsync())
                        {
                            try
                            {
                                foreach (string line in logLines)
                                {
                                    try
                                    {
                                        // Process line and insert into database
                                        await ProcessLogLine(connection, line);
                                        processedCount++;
                                        
                                        // Log progress every 100 lines
                                        if (processedCount % 100 == 0)
                                        {
                                            string progressMsg = $"📊 Progress: {processedCount}/{logLines.Count} entries processed from {fileName}";
                                            _logger.LogInformation(progressMsg);
                                            SendLogMessage(progressMsg);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, $"Error processing log line: {line}");
                                        errorCount++;
                                    }
                                }

                                // Commit transaction
                                await transaction.CommitAsync();
                                
                                // Log completion with detailed statistics
                                string completionMsg = $"📊 DATABASE WRITE COMPLETE: {fileName}" +
                                                     $"\n- Entries processed: {processedCount}" +
                                                     $"\n- Errors: {errorCount}" +
                                                     $"\n- Success rate: {((double)processedCount / logLines.Count):P2}";
                                
                                _logger.LogWarning(completionMsg);
                                SendLogMessage(completionMsg);
                                
                                // Add extra visibility in service mode
                                if (_isWindowsService)
                                {
                                    SendDirectPipeMessage($"📊📊📊 SERVICE DATABASE SUCCESS: {processedCount} entries written from {fileName}");
                                    
                                    try
                                    {
                                        using (var eventLog = new System.Diagnostics.EventLog("Application"))
                                        {
                                            eventLog.Source = "ORF2PostgresService";
                                            eventLog.WriteEntry(completionMsg, System.Diagnostics.EventLogEntryType.Information);
                                        }
                                    }
                                    catch
                                    {
                                        // Ignore event log errors
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                await transaction.RollbackAsync();
                                throw new Exception($"Transaction failed for {fileName}: {ex.Message}", ex);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string errorMsg = $"Database error processing {fileName}: {GetUserFriendlyErrorMessage(ex)}";
                        _logger.LogError(ex, errorMsg);
                        SendLogMessage(errorMsg, true);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                string fatalMsg = $"FATAL DATABASE ERROR: Failed to process {fileName}: {ex.Message}";
                _logger.LogError(ex, fatalMsg);
                SendLogMessage(fatalMsg, true);
                throw;
            }
        }

        private DateTime ParseDateTime(string dateTimeString)
        {
            if (string.IsNullOrEmpty(dateTimeString))
            {
                return DateTime.UtcNow;
            }

            // Try to parse the date in ISO 8601 format (e.g., 2025-05-12T00:01:19)
            if (DateTime.TryParse(dateTimeString, out DateTime result))
            {
                // Ensure the Kind is UTC as required by PostgreSQL
                if (result.Kind == DateTimeKind.Unspecified)
                {
                    // Convert to UTC - assume local time if not specified
                    return DateTime.SpecifyKind(result, DateTimeKind.Utc);
                }
                else if (result.Kind == DateTimeKind.Local)
                {
                    // Convert local time to UTC
                    return result.ToUniversalTime();
                }
                
                // Already UTC
                return result;
            }
            
            // If we couldn't parse the date, return current UTC time
            _logger.LogWarning($"Could not parse date '{dateTimeString}', using current UTC time instead");
            return DateTime.UtcNow;
        }

        private Dictionary<string, string> ParseLogLine(string line)
        {
            var result = new Dictionary<string, string>(16); // Pre-allocate capacity for common number of fields

            try
            {
                // Generate a hash of the entire log line for deduplication
                result["entry_hash"] = ComputeHash(line);
                
                // Split by spaces
                string[] fields = line.Split(' ');

                // Based on the header in the example log file
                // #Fields: x-message-id x-event-source x-event-datetime x-event-class x-event-severity x-event-action x-filtering-point x-ip x-sender x-recipients x-msg-subject x-msg-author x-remote-peer x-source-ip x-country x-event-msg
                if (fields.Length >= 15)
                {
                    result["message_id"] = fields[0];
                    result["event_source"] = fields[1];

                    // Handle ISO-8601 datetime format (e.g., 2025-05-12T00:01:19)
                    // Ensure it's stored properly for PostgreSQL timestamp with time zone
                    result["event_datetime"] = fields[2];

                    result["event_class"] = fields[3];
                    result["event_severity"] = fields[4];
                    result["event_action"] = fields[5];
                    result["filtering_point"] = fields[6];
                    result["ip"] = fields[7];
                    result["sender"] = fields[8];
                    result["recipients"] = fields[9];
                    result["msg_subject"] = fields[10];
                    result["msg_author"] = fields[11];
                    result["remote_peer"] = fields[12];
                    result["source_ip"] = fields[13];
                    result["country"] = fields[14];
                    
                    // The rest is the event message - use StringBuilder for more efficient concatenation
                    if (fields.Length > 15)
                    {
                        // Use a more efficient method instead of string.Join with Linq Skip
                        int totalLength = 0;
                        for (int i = 15; i < fields.Length; i++)
                        {
                            totalLength += fields[i].Length;
                        }
                        
                        // Add space for spaces between words
                        totalLength += fields.Length - 16;
                        
                        var sb = new StringBuilder(totalLength);
                        for (int i = 15; i < fields.Length; i++)
                        {
                            if (i > 15) sb.Append(' ');
                            sb.Append(fields[i]);
                        }
                        
                        result["event_msg"] = sb.ToString();
                    }
                    else
                    {
                        result["event_msg"] = string.Empty;
                    }
                }
                else
                {
                    _logger.LogWarning($"Malformed log line (expected at least 15 fields, got {fields.Length}): {line}");
                    // Still try to extract whatever is available - use an array for field names to avoid switch statement
                    string[] fieldNames = new[] {
                        "message_id", "event_source", "event_datetime", "event_class", "event_severity",
                        "event_action", "filtering_point", "ip", "sender", "recipients",
                        "msg_subject", "msg_author", "remote_peer", "source_ip", "country"
                    };
                    
                    for (int i = 0; i < fields.Length && i < fieldNames.Length; i++)
                    {
                        result[fieldNames[i]] = fields[i];
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing log line: {line}");
            }

            return result;
        }

        private void LoadOffsets()
        {
            try
            {
                _logger.LogWarning($"LOADING OFFSETS: Path={_offsetFilePath}, IsService={_isWindowsService}");
                SendLogMessage($"📂 LOADING OFFSETS: Path={_offsetFilePath}, IsService={_isWindowsService}");
                
                // Ensure we're using an absolute path in service mode
                if (_isWindowsService && !Path.IsPathRooted(_offsetFilePath))
                {
                    string originalPath = _offsetFilePath;
                    _offsetFilePath = Path.GetFullPath(_offsetFilePath);
                    _logger.LogWarning($"SERVICE MODE: Converting relative offset path '{originalPath}' to absolute path '{_offsetFilePath}'");
                    SendLogMessage($"SERVICE MODE: Using absolute offset path: {_offsetFilePath}");
                }

                // IMPORTANT: Put the executable directory first in the list of potential locations
                string execDir = AppDomain.CurrentDomain.BaseDirectory;
                _logger.LogWarning($"PRIORITY PATH: Executable directory for offsets: {execDir}");
                SendLogMessage($"PRIORITY PATH: Executable directory for offsets: {execDir}");
                
                // Create a list of potential locations to look for the offset file - prioritize executable directory
                List<string> potentialLocations = new List<string>
                {
                    Path.Combine(execDir, "offsets.json"), // Highest priority: Always check executable directory first
                    _offsetFilePath, // Next priority: Configured path from settings
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "orf2Postgres", "offsets.json"), // ProgramData
                    Path.Combine(Directory.GetCurrentDirectory(), "offsets.json"), // Current directory
                    Path.Combine(Path.GetTempPath(), "orf2Postgres_offsets.json") // Temp directory
                };
                
                bool offsetsLoaded = false;
                
                // Try each location
                foreach (string offsetPath in potentialLocations)
                {
                    try
                    {
                        if (File.Exists(offsetPath))
                        {
                            _logger.LogWarning($"Found offset file at: {offsetPath}");
                            SendLogMessage($"Found offset file at: {offsetPath}");
                            
                            string json = File.ReadAllText(offsetPath);
                            _logger.LogInformation($"Read {json.Length} bytes from offset file");
                            
                            var offsets = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
                            if (offsets != null && offsets.Count > 0)
                            {
                                foreach (var kvp in offsets)
                                {
                                    _fileOffsets[kvp.Key] = kvp.Value;
                                }
                                
                                string offsetsStr = string.Join(", ", offsets.Select(o => $"{Path.GetFileName(o.Key)}:{o.Value}"));
                                _logger.LogWarning($"Loaded {offsets.Count} file offsets: {offsetsStr}");
                                SendLogMessage($"✅ Loaded {offsets.Count} file offsets successfully from {offsetPath}");
                                
                                // Update the primary path to use this successful location
                                if (_offsetFilePath != offsetPath)
                                {
                                    _logger.LogWarning($"Updating primary offset path from {_offsetFilePath} to {offsetPath}");
                                    _offsetFilePath = offsetPath;
                                }
                                
                                offsetsLoaded = true;
                                break;
                            }
                            else
                            {
                                _logger.LogWarning($"Offset file exists at {offsetPath} but contains no valid offsets");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error reading offset file at {offsetPath}, trying next location");
                    }
                }
                
                // If no offset file was loaded, create a new one
                if (!offsetsLoaded)
                {
                    _logger.LogWarning($"No valid offset file found at any location, creating new one");
                    SendLogMessage($"No valid offset file found, creating new one", true);
                    
                    // Add a test entry to ensure we have non-empty state
                    _fileOffsets["test-initial.log"] = 0;
                    
                    // Force save to create the file
                    ForceSaveOffsets();
                    
                    _logger.LogWarning($"Created new offset file with test entry");
                    SendLogMessage($"Created new offset file with test entry");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Fatal error loading offsets, starting with empty offset state");
                SendLogMessage($"⚠️ Fatal error loading offset file: {ex.Message}", true);
                
                // Force create an offset file with empty state
                try
                {
                    _fileOffsets.Clear();
                    _fileOffsets["emergency-fallback.log"] = 0;
                    ForceSaveOffsets();
                }
                catch
                {
                    // Last-ditch effort failed, ignore
                }
            }
        }

        private void SaveOffsets()
        {
            try
            {
                // Ensure we're using an absolute path in service mode
                if (_isWindowsService && !Path.IsPathRooted(_offsetFilePath))
                {
                    string originalPath = _offsetFilePath;
                    _offsetFilePath = Path.GetFullPath(_offsetFilePath);
                    _logger.LogWarning($"SERVICE MODE: Converting relative offset path '{originalPath}' to absolute path '{_offsetFilePath}'");
                }
                
                // Log critical information about what we're saving
                _logger.LogWarning($"SAVING OFFSETS: Path={_offsetFilePath}, Entries={_fileOffsets.Count}, IsService={_isWindowsService}");
                SendLogMessage($"💾 SAVING OFFSETS: Entries={_fileOffsets.Count}, Path={_offsetFilePath}");
                
                // Create a list of potential locations where we can write the offset file
                // CRITICAL: Prioritize executable directory for offset file
                string execDir = AppDomain.CurrentDomain.BaseDirectory;
                string execDirOffsetPath = Path.Combine(execDir, "offsets.json");
                
                _logger.LogWarning($"PRIORITY SAVE PATH: Executable directory for offsets: {execDir}");
                SendLogMessage($"PRIORITY SAVE PATH: Trying executable directory first: {execDirOffsetPath}");
                
                // Create prioritized list with executable directory first
                List<string> potentialLocations = new List<string>
                {
                    execDirOffsetPath, // Highest priority: Always try executable directory first
                    _offsetFilePath, // Next priority: Configured path from settings
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "orf2Postgres", "offsets.json"), // ProgramData
                    Path.Combine(Directory.GetCurrentDirectory(), "offsets.json") // Current directory
                };
                
                // Try each location until one succeeds
                Exception? lastException = null;
                bool savedSuccessfully = false;
                string successfulPath = string.Empty;
                
                foreach (string offsetPath in potentialLocations)
                {
                    try
                    {
                        // Create a unique temp file name with random component
                        string tempPath = $"{offsetPath}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";
                        _logger.LogInformation($"Attempting to write to offset path: {offsetPath} (temp: {tempPath})");
                        
                        // Ensure the directory exists
                        string? directory = Path.GetDirectoryName(offsetPath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        {
                            try
                            {
                                Directory.CreateDirectory(directory);
                                _logger.LogInformation($"Created directory for offset file: {directory}");
                            }
                            catch (Exception dirEx)
                            {
                                _logger.LogWarning($"Unable to create directory {directory}: {dirEx.Message}");
                                throw; // Rethrow to try next location
                            }
                        }
                        
                        // Log offset data for debugging
                        string offsetEntries = string.Join(", ", _fileOffsets.Select(kv => $"{Path.GetFileName(kv.Key)}:{kv.Value}"));
                        _logger.LogWarning($"OFFSET DATA TO SAVE: {offsetEntries}");
                        
                        // Serialize with pretty printing for better debugging
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        string json = JsonSerializer.Serialize(_fileOffsets, options);
                        
                        // First try creating a new file directly (more reliable)
                        try
                        {
                            File.WriteAllText(offsetPath, json);
                            _logger.LogWarning($"DIRECT WRITE SUCCEEDED: Offset file written directly to {offsetPath}");
                            savedSuccessfully = true;
                            successfulPath = offsetPath;
                            break; // Success, exit the loop
                        }
                        catch (Exception directEx)
                        {
                            _logger.LogWarning($"Direct write failed: {directEx.Message}, trying temp file approach");
                            
                            // If direct write fails, try the temp file approach
                            File.WriteAllText(tempPath, json);
                            _logger.LogInformation($"Temp file written, copying to final location: {offsetPath}");
                            
                            File.Copy(tempPath, offsetPath, true);
                            _logger.LogInformation($"Offset file updated, deleting temp file");
                            
                            try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
                            
                            savedSuccessfully = true;
                            successfulPath = offsetPath;
                            break; // Success, exit the loop
                        }
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        _logger.LogError(ex, $"Failed to save offsets to {offsetPath}, trying next location");
                    }
                }
                
                // Report success or failure
                if (savedSuccessfully)
                {
                    _logger.LogWarning($"OFFSET SAVE SUCCEEDED: Successfully saved {_fileOffsets.Count} offsets to {successfulPath}");
                    SendLogMessage($"✅ Offset file saved successfully to {successfulPath}");
                    
                    // Update the primary path to use the successful location going forward
                    if (_offsetFilePath != successfulPath)
                    {
                        _logger.LogWarning($"Updating primary offset path from {_offsetFilePath} to {successfulPath}");
                        _offsetFilePath = successfulPath;
                    }
                }
                else
                {
                    string errorMsg = lastException != null ? $"Error: {lastException.Message}" : "Unknown error";
                    _logger.LogError($"CRITICAL ERROR: Failed to save offsets to ANY location! {errorMsg}");
                    SendLogMessage($"❌ CRITICAL ERROR: Failed to save offsets to ANY location! {errorMsg}", true);
                    
                    // As a last desperate attempt, try writing to system temp directory
                    try
                    {
                        string tempPath = Path.Combine(Path.GetTempPath(), "orf2Postgres_offsets.json");
                        _logger.LogWarning($"LAST RESORT: Trying system temp directory: {tempPath}");
                        
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        string json = JsonSerializer.Serialize(_fileOffsets, options);
                        File.WriteAllText(tempPath, json);
                        
                        _logger.LogWarning($"LAST RESORT SUCCEEDED: Saved offsets to {tempPath}");
                        SendLogMessage($"⚠️ Saved offsets to temporary location: {tempPath}", true);
                        
                        // Update the path for future use
                        _offsetFilePath = tempPath;
                    }
                    catch (Exception tempEx)
                    {
                        _logger.LogError(tempEx, "LAST RESORT FAILED: Could not write to system temp directory");
                        SendLogMessage($"❌ CRITICAL: All offset file save attempts failed! Data continuity at risk!", true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save offsets to {_offsetFilePath}");
                SendLogMessage($"Error saving offset file: {ex.Message}", true);
                
                // In service mode, try alternative location as fallback
                if (_isWindowsService)
                {
                    try
                    {
                        string fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "offsets.json");
                        _logger.LogWarning($"SERVICE MODE: Trying fallback location: {fallbackPath}");
                        
                        string json = JsonSerializer.Serialize(_fileOffsets);
                        File.WriteAllText(fallbackPath, json);
                        
                        _logger.LogWarning($"SERVICE MODE: Successfully saved to fallback location");
                        SendLogMessage($"Saved offsets to fallback location: {fallbackPath}");
                        
                        // Update path for future use
                        _offsetFilePath = fallbackPath;
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "SERVICE MODE: Failed to save offsets to fallback location");
                    }
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ORF to Postgres service stopping");
            _isRunning = false;
            _currentStatus = "Stopping";

            try
            {
                // Send a final log message about service stopping
                SendLogMessage("Service is shutting down...");
                
                // Give time for the message to be processed
                await Task.Delay(500, cancellationToken);
                
                // Ensure the file watcher is disposed
                _fileWatcher?.Dispose();
                
                // Save current offsets with high visibility during shutdown
                _logger.LogWarning("SHUTDOWN: Saving offset file before service stops");
                SendLogMessage("💾 SHUTDOWN: Saving offset file before service stops");
                
                // Use the force save method for maximum reliability
                ForceSaveOffsets();
                
                // Verify the offset file exists after shutdown save
                if (File.Exists(_offsetFilePath))
                {
                    _logger.LogWarning($"SHUTDOWN: Offset file verified at {_offsetFilePath}");
                    SendLogMessage($"✅ SHUTDOWN: Offset file verified at {_offsetFilePath}");
                }
                else
                {
                    _logger.LogError($"SHUTDOWN: Offset file not found at {_offsetFilePath}");
                    SendLogMessage($"❌ SHUTDOWN: Offset file not found at {_offsetFilePath}", true);
                }
                
                // Final cleanup of pipes
                if (_pipeServer != null && _pipeServer.IsConnected)
                {
                    try
                    {
                        // Create a properly formatted JSON array message for shutdown
                        var shutdownMsg = new ServiceMessage
                        {
                            success = true,
                            action = "service_stopping",
                            message = "Service shutdown complete",
                            status = "Stopping"
                        };
                        
                        // Wrap in a list for array format and serialize
                        var messageList = new List<ServiceMessage> { shutdownMsg };
                        string shutdownMessage = JsonSerializer.Serialize(messageList);
                        
                        // Send using text-based communication
                        using (var writer = new StreamWriter(_pipeServer, new UTF8Encoding(false), 1024, true))
                        {
                            writer.WriteLine(shutdownMessage);
                            writer.Flush();
                        }
                        
                        // Clean disconnect
                        _pipeServer.WaitForPipeDrain();
                        _pipeServer.Disconnect();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during pipe shutdown");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during service shutdown");
            }
            
            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            _disposed = true;
            _fileWatcher?.Dispose();
            _pipeServer?.Dispose();
            _messageProcessorTimer?.Dispose();
            base.Dispose();
        }

        // Decrypt password using Windows Data Protection API
        private string DecryptPassword(string encryptedPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(encryptedPassword))
                    return string.Empty;
                
                // Log diagnostic information in service mode
                if (_isWindowsService)
                {
                    _logger.LogInformation($"Attempting to decrypt password in Windows Service mode");
                }
                
                // Check if the password is actually encrypted (simple heuristic)
                if (!IsBase64String(encryptedPassword))
                {
                    if (_isWindowsService)
                    {
                        _logger.LogInformation("Password does not appear to be encrypted (not Base64), using as-is");
                    }
                    return encryptedPassword; // Return as-is if it's not encrypted
                }
                
                // First try with CurrentUser scope (for compatibility with existing passwords)
                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(encryptedPassword);
                    byte[] passwordBytes = ProtectedData.Unprotect(
                        encryptedBytes, 
                        null, 
                        DataProtectionScope.CurrentUser);
                    
                    if (_isWindowsService)
                    {
                        _logger.LogInformation("Successfully decrypted password using CurrentUser scope");
                    }
                    
                    return Encoding.UTF8.GetString(passwordBytes);
                }
                catch (Exception userEx)
                {
                    if (_isWindowsService)
                    {
                        _logger.LogWarning($"Failed to decrypt with CurrentUser scope: {userEx.Message}, trying LocalMachine scope");
                    }
                    
                    // If that fails, try with LocalMachine scope
                    // This is needed especially for service mode
                    try
                    {
                        byte[] encryptedBytes = Convert.FromBase64String(encryptedPassword);
                        byte[] passwordBytes = ProtectedData.Unprotect(
                            encryptedBytes, 
                            null, 
                            DataProtectionScope.LocalMachine);
                        
                        if (_isWindowsService)
                        {
                            _logger.LogInformation("Successfully decrypted password using LocalMachine scope");
                        }
                        
                        return Encoding.UTF8.GetString(passwordBytes);
                    }
                    catch (Exception machineEx)
                    {
                        if (_isWindowsService)
                        {
                            _logger.LogError($"Failed to decrypt with LocalMachine scope: {machineEx.Message}");
                            _logger.LogWarning("IMPORTANT: In Windows Service mode, password decryption can fail because it's tied to the user account. Try using an unencrypted password for service mode.");
                            
                            // As a fallback for service mode, check if the DB password exists in appsettings.json
                            string settingsPassword = _configuration["PostgresConnection:Password"];
                            if (!string.IsNullOrEmpty(settingsPassword) && settingsPassword != encryptedPassword)
                            {
                                _logger.LogWarning("Using password from appsettings.json as fallback");
                                return settingsPassword;
                            }
                        }
                        
                        // If all decryption attempts fail, use as plain text
                        _logger.LogWarning("All decryption attempts failed, using encrypted value as-is (this will likely fail)");
                        return encryptedPassword;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in password decryption logic");
                
                // As a fallback for service mode, check if the DB password exists in appsettings.json
                if (_isWindowsService)
                {
                    string settingsPassword = _configuration["PostgresConnection:Password"];
                    if (!string.IsNullOrEmpty(settingsPassword))
                    {
                        _logger.LogWarning("Using password from appsettings.json as fallback after error");
                        return settingsPassword;
                    }
                }
                
                return encryptedPassword; // Return encrypted string if decryption fails
            }
        }
        
        // Helper to check if a string is Base64 encoded
        private bool IsBase64String(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;
                
            try
            {
                Convert.FromBase64String(input);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string ComputeHash(string input)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = sha256.ComputeHash(inputBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }
        
        private string ResolveExecutableRelativePath(string path)
        {
            // If path is already absolute, return it as is
            if (Path.IsPathRooted(path))
            {
                return path;
            }
            
            // Get the executable directory
            string executableDir = AppDomain.CurrentDomain.BaseDirectory;
            _logger.LogWarning($"Resolving relative path '{path}' to executable directory: {executableDir}");
            
            // If path starts with ./ or .\ or just a filename, resolve relative to the executable directory
            if (path.StartsWith("./") || path.StartsWith(".\\") || !path.Contains("/") && !path.Contains("\\"))
            {
                // Remove leading ./ or .\
                string cleanPath = path;
                if (path.StartsWith("./") || path.StartsWith(".\\"))
                {
                    cleanPath = path.Substring(2);
                }
                
                string resolvedPath = Path.Combine(executableDir, cleanPath);
                _logger.LogWarning($"Resolved '{path}' to executable-relative path: {resolvedPath}");
                SendLogMessage($"📁 Resolved path: '{path}' to: {resolvedPath}");
                return resolvedPath;
            }
            
            // For other relative paths (like ../something), use normal Path.GetFullPath
            string fullPath = Path.GetFullPath(path, executableDir);  // Use executable dir as base
            _logger.LogWarning($"Resolved '{path}' to full path: {fullPath}");
            SendLogMessage($"📁 Resolved path: '{path}' to: {fullPath}");
            return fullPath;
        }

        // Method to send log messages to the client - with DIRECT output technique for reliability
        private void SendLogMessage(string logMessage, bool isError = false)
        {
            if (string.IsNullOrEmpty(logMessage))
                return;

            // Generate a unique message ID for tracking
            int messageId = Interlocked.Increment(ref _messageSendAttempts);
            
            try
            {
                // Format timestamp consistently
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                
                // Add service mode marker if needed
                if (_isWindowsService)
                {
                    // Preserve existing emoji if present
                    bool hasEmoji = logMessage.Contains("📊") || logMessage.Contains("📂") || 
                                   logMessage.Contains("✅") || logMessage.Contains("❌") ||
                                   logMessage.Contains("⚠️");
                    
                    // Add appropriate emoji based on content
                    if (!hasEmoji)
                    {
                        if (logMessage.Contains("DATABASE", StringComparison.OrdinalIgnoreCase) || 
                            logMessage.Contains("INSERT", StringComparison.OrdinalIgnoreCase) ||
                            logMessage.Contains("WRITE", StringComparison.OrdinalIgnoreCase))
                        {
                            logMessage = $"📊 {logMessage}";
                        }
                        else if (logMessage.Contains("FILE", StringComparison.OrdinalIgnoreCase) || 
                                 logMessage.Contains("PROCESS", StringComparison.OrdinalIgnoreCase) ||
                                 logMessage.Contains("LOG", StringComparison.OrdinalIgnoreCase))
                        {
                            logMessage = $"📂 {logMessage}";
                        }
                    }
                    
                    // Add service marker if not already present
                    if (!logMessage.Contains("[SERVICE]"))
                    {
                        logMessage = $"[SERVICE] {logMessage}";
                    }
                }

                // Create message object
                var message = new ServiceMessage
                {
                    success = !isError,
                    action = "log_message",
                    message = $"[{timestamp}] {logMessage}",
                    status = isError ? "Error" : "Info"
                };

                // Log to diagnostics file
                DirectFileLog($"MSG#{messageId:D6} ATTEMPT: {message.status.ToUpper()} - {message.message}");

                // Always log to standard logger
                if (isError)
                {
                    _logger.LogError($"MSG#{messageId:D6} {logMessage}");
                }
                else
                {
                    _logger.LogInformation($"MSG#{messageId:D6} {logMessage}");
                }

                // Send through pipe if connected and message is important
                if (_pipeServer != null && _pipeServer.IsConnected)
                {
                    try
                    {
                        // Serialize with proper options
                        var options = new JsonSerializerOptions
                        {
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                            WriteIndented = false
                        };
                        
                        string json = JsonSerializer.Serialize(new List<ServiceMessage> { message }, options);
                        
                        using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                        {
                            writer.WriteLine(json);
                            writer.Flush();
                            Interlocked.Increment(ref _messageSendSuccesses);
                            DirectFileLog($"MSG#{messageId:D6} SENT SUCCESSFULLY");
                        }
                    }
                    catch (Exception ex)
                    {
                        DirectFileLog($"MSG#{messageId:D6} SEND ERROR: {ex.Message}");
                        _logger.LogError(ex, $"Failed to send message #{messageId}");
                    }
                }
                else
                {
                    DirectFileLog($"MSG#{messageId:D6} NOT SENT: Pipe not connected");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in SendLogMessage for message #{messageId}");
            }
        }
        
        // Method to process a configuration JSON string
        private void ProcessConfiguration(string configJson)
        {
            try
            {
                // Parse the new configuration
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson);
                if (config == null)
                {
                    SendLogMessage("Invalid configuration format", true);
                    return;
                }
                
                // Log received configuration
                _logger.LogInformation("Received configuration update from UI");
                SendLogMessage("Received configuration update from UI");

                // Update our configuration values
                if (config.TryGetValue("logDirectory", out string logDir))
                    _logDirectory = logDir;
                
                if (config.TryGetValue("logFilePattern", out string pattern))
                    _logFilePattern = pattern;
                
                if (config.TryGetValue("pollingIntervalSeconds", out string polling) && 
                    int.TryParse(polling, out int pollingInterval))
                    _pollingIntervalSeconds = pollingInterval;
                
                if (config.TryGetValue("offsetFilePath", out string offsetPath))
                    _offsetFilePath = offsetPath;
                
                bool connectionUpdated = false;
                
                // Check if we have all database connection parameters
                if (config.TryGetValue("host", out string host) &&
                    config.TryGetValue("port", out string port) &&
                    config.TryGetValue("username", out string username) &&
                    config.TryGetValue("password", out string password) &&
                    config.TryGetValue("database", out string database))
                {
                    // Create a new connection string with the updated values
                    string newConnectionString = $"Host={host};" +
                                               $"Port={port};" +
                                               $"Username={username};" +
                                               $"Password={password};" +
                                               $"Database={database}";
                    
                    // Only update if connection string has changed
                    if (newConnectionString != _connectionString)
                    {
                        _connectionString = newConnectionString;
                        connectionUpdated = true;
                        _logger.LogInformation("Connection string updated from UI configuration");
                        SendLogMessage("Database connection string updated from UI configuration");
                    }
                }
                
                if (config.TryGetValue("schema", out string schema))
                    _dbSchema = schema;
                
                if (config.TryGetValue("table", out string table))
                    _tableName = table;
                
                // Save the configuration to config.json for persistence
                try
                {
                    File.WriteAllText("config.json", configJson);
                    _logger.LogInformation("Configuration saved to config.json");
                    SendLogMessage("Configuration saved to config.json");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save configuration to config.json");
                    SendLogMessage($"Failed to save configuration: {ex.Message}", true);
                }
                
                // If the connection string was updated, test it immediately
                if (connectionUpdated)
                {
                    SendLogMessage("Testing updated database connection...");
                    
                    try
                    {
                        using (var connection = new NpgsqlConnection(_connectionString))
                        {
                            connection.Open();
                            _logger.LogInformation("Database connection tested successfully with new configuration");
                            SendLogMessage("Database connection successful with new configuration!");
                            
                            // If we were in error state, reset it
                            if (_currentStatus == "Error")
                            {
                                _currentStatus = "Running";
                                _errorMessage = string.Empty;
                                SendStatusToClient();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _errorMessage = $"Database connection error: {GetUserFriendlyErrorMessage(ex)}";
                        _logger.LogError(ex, "Failed to connect to database with new configuration");
                        SendLogMessage(_errorMessage, true);
                    }
                }
            }
            catch (Exception ex)
            {
                SendLogMessage($"Configuration update error: {ex.Message}", true);
            }
        }
        
        // Helper method to send direct/immediate messages
        // This method is removed to simplify the communication protocol.
        // All messages should use SendLogMessage for UI display to prevent confusion
        private void SendDirectPipeMessage(string message)
        {
            // Only log critical messages with markers
            if (message.Contains("CRITICAL") || message.Contains("ERROR"))
            {
                message = $"❗❗❗ {message} ❗❗❗";
            }
            
            // Add timestamp for additional clarity
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            message = $"[{timestamp}] {message}";
            
            // Send through normal channel
            SendLogMessage(message, message.Contains("ERROR") || message.Contains("CRITICAL"));
        }

        // Helper method to properly escape strings for JSON
        private string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str))
                return string.Empty;
            
            // Use a StringBuilder for efficiency
            StringBuilder sb = new StringBuilder(str.Length + 16);
            
            foreach (char c in str)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break; // Escape backslashes (important for Windows paths)
                    case '"': sb.Append("\\\""); break;  // Escape quotes
                    case '\r': sb.Append("\\r"); break;  // Escape control chars
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Only include valid characters
                        if (c >= 32) // Skip control characters
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            
            return sb.ToString();
        }
        
        // Helper method to create safe JSON messages
        private string CreateSafeJsonMessage(Dictionary<string, string> data)
        {
            // Use StringBuilder for efficiency
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            
            bool first = true;
            foreach (var kvp in data)
            {
                if (!first) sb.Append(',');
                first = false;
                
                // Append key with quotes
                sb.Append('"');
                sb.Append(EscapeJsonString(kvp.Key));
                sb.Append('"');
                sb.Append(':');
                
                // Append value with quotes
                sb.Append('"');
                sb.Append(EscapeJsonString(kvp.Value));
                sb.Append('"');
            }
            
            sb.Append('}');
            return sb.ToString();
        }

        // Helper method to filter out invalid characters that could cause encoding issues
        private string FilterInvalidCharacters(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Don't escape emojis and other valid Unicode characters
            return input.Replace("\\", "\\\\")  // Escape backslashes first
                        .Replace("\"", "\\\"")   // Escape quotes
                        .Replace("\r", "\\r")    // Escape carriage returns
                        .Replace("\n", "\\n")    // Escape newlines
                        .Replace("\t", "\\t");   // Escape tabs
        }

        // Create or verify the database table
        private void CreateOrfLogsTable()
        {
            try
            {
                // Log the connection string we're using (except password) for debugging
                string connectionStringSafe = _connectionString.Replace(DecryptPassword(_configuration["PostgresConnection:Password"] ?? ""), "***");
                _logger.LogInformation($"Connecting to database with: {connectionStringSafe}");
                
                using (var connection = new NpgsqlConnection(_connectionString))
                {
                    connection.Open();
                    
                    // Check if schema exists and create if not
                    if (_dbSchema != "public")
                    {
                        using (var cmd = new NpgsqlCommand())
                        {
                            cmd.Connection = connection;
                            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {_dbSchema}";
                            cmd.ExecuteNonQuery();
                            _logger.LogInformation($"Schema {_dbSchema} created or verified");
                        }
                    }

                    // Check if the table exists
                    bool tableExists = false;
                    using (var cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = connection;
                        cmd.CommandText = @"
                            SELECT EXISTS (
                                SELECT FROM information_schema.tables 
                                WHERE table_schema = @schema
                                AND table_name = @tableName
                            )";
                        cmd.Parameters.AddWithValue("schema", _dbSchema);
                        cmd.Parameters.AddWithValue("tableName", _tableName);
                        tableExists = (bool)cmd.ExecuteScalar();
                        _logger.LogInformation($"Table {_dbSchema}.{_tableName} exists: {tableExists}");
                    }

                    // If table exists, check if it has the entry_hash column
                    bool hasHashColumn = false;
                    if (tableExists)
                    {
                        using (var cmd = new NpgsqlCommand())
                        {
                            cmd.Connection = connection;
                            cmd.CommandText = @"
                                SELECT EXISTS (
                                    SELECT FROM information_schema.columns
                                    WHERE table_schema = @schema
                                    AND table_name = @tableName
                                    AND column_name = 'entry_hash'
                                )";
                            cmd.Parameters.AddWithValue("schema", _dbSchema);
                            cmd.Parameters.AddWithValue("tableName", _tableName);
                            hasHashColumn = (bool)cmd.ExecuteScalar();
                            _logger.LogInformation($"Table has entry_hash column: {hasHashColumn}");
                        }

                        // If the table exists but doesn't have the hash column, add it
                        if (!hasHashColumn)
                        {
                            using (var cmd = new NpgsqlCommand())
                            {
                                cmd.Connection = connection;
                                cmd.CommandText = $@"
                                    ALTER TABLE {_dbSchema}.{_tableName}
                                    ADD COLUMN entry_hash TEXT;
                                    
                                    CREATE UNIQUE INDEX IF NOT EXISTS idx_{_tableName}_entry_hash
                                    ON {_dbSchema}.{_tableName} (entry_hash);
                                ";
                                cmd.ExecuteNonQuery();
                                _logger.LogInformation("Added entry_hash column to existing table");
                            }
                        }
                    }
                    else
                    {
                        // Create table with hash column if it doesn't exist
                        using (var cmd = new NpgsqlCommand())
                        {
                            cmd.Connection = connection;
                            cmd.CommandText = $@"
                            CREATE TABLE {_dbSchema}.{_tableName} (
                                id SERIAL PRIMARY KEY,
                                message_id TEXT,
                                event_source TEXT,
                                event_datetime TIMESTAMPTZ,
                                event_class TEXT,
                                event_severity TEXT,
                                event_action TEXT,
                                filtering_point TEXT,
                                ip TEXT,
                                sender TEXT,
                                recipients TEXT,
                                msg_subject TEXT,
                                msg_author TEXT,
                                remote_peer TEXT,
                                source_ip TEXT,
                                country TEXT,
                                event_msg TEXT,
                                filename TEXT,
                                processed_at TIMESTAMPTZ DEFAULT NOW(),
                                entry_hash TEXT
                            );
                            
                            CREATE UNIQUE INDEX idx_{_tableName}_entry_hash
                            ON {_dbSchema}.{_tableName} (entry_hash);
                            ";
                            cmd.ExecuteNonQuery();
                            _logger.LogInformation("Created new table with entry_hash column");
                        }
                    }
                    
                    _logger.LogInformation("Database table setup completed successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating or verifying database table");
                throw; // Rethrow so the calling method can handle it
            }
        }

        private void HandleClientCommand(string command)
        {
            _logger.LogInformation($"Received command: {command}");
            
            // Filter out any invalid characters to prevent encoding issues
            command = FilterInvalidCharacters(command);
            
            try
            {
                // Create a standard response format for most commands
                var response = new ServiceMessage
                {
                    success = true,
                    action = "command_response",
                    message = "Command processed",
                    status = "Info",
                    filePath = _currentFilePath,
                    offset = _currentOffset
                };
                
                // String-based parsing for robustness
                if (command.Contains("\"action\":\"status\"", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Received status command");
                    // Send current status
                    SendStatusToClient();
                }
                else if (command.Contains("\"action\":\"start\"", StringComparison.OrdinalIgnoreCase))
                {
                    // Start processing
                    _isRunning = true;
                    _currentStatus = "Running";
                    _errorMessage = string.Empty;
                    
                    // Update status
                    _logger.LogInformation("Received start command");
                    SendLogMessage("Processing started by client command");
                    
                    // Send formatted response
                    response.message = "Processing started";
                    response.action = "start_response";
                    SendResponse(response);
                    
                    // Send updated status
                    SendStatusToClient();
                }
                else if (command.Contains("\"action\":\"stop\"", StringComparison.OrdinalIgnoreCase))
                {
                    // Stop processing
                    _isRunning = false;
                    _currentStatus = "Stopped";
                    _errorMessage = string.Empty;
                    
                    // Update status
                    _logger.LogInformation("Received stop command");
                    SendLogMessage("Processing stopped by client command");
                    
                    // Send formatted response
                    response.message = "Processing stopped";
                    response.action = "stop_response";
                    SendResponse(response);
                    
                    // Send updated status
                    SendStatusToClient();
                }
                else if (command.Contains("\"action\":\"config\"", StringComparison.OrdinalIgnoreCase))
                {
                    // Process configuration update in a more robust way
                    _logger.LogInformation("Received config command");
                    ProcessConfigCommand(command);
                    
                    // Send formatted response
                    response.message = "Configuration updated";
                    response.action = "config_response";
                    SendResponse(response);
                }
                else
                {
                    _logger.LogWarning($"Unknown command: {command}");
                    
                    // Send error response in proper format
                    response.success = false;
                    response.message = "Unrecognized command";
                    response.status = "Error";
                    SendResponse(response);
                    
                    // Also log error message
                    SendLogMessage("Unrecognized command", true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling command: {command}");
                
                // Send formatted error response
                var errorResponse = new ServiceMessage
                {
                    success = false,
                    action = "error_response",
                    message = $"Error processing command: {ex.Message}",
                    status = "Error",
                    filePath = _currentFilePath,
                    offset = _currentOffset
                };
                SendResponse(errorResponse);
                
                // Also log error message
                SendLogMessage($"Error processing command: {ex.Message}", true);
            }
        }

        private void ProcessConfigCommand(string configCommand)
        {
            try 
            {
                // Try to extract the config part using simple string operations
                int configStart = configCommand.IndexOf("\"config\":");
                if (configStart > 0)
                {
                    // Find the start of the config value after "config":
                    int valueStart = configStart + 9; // "config": length
                    
                    // Skip whitespace and the opening quote if present
                    while (valueStart < configCommand.Length && 
                          (configCommand[valueStart] == ' ' || configCommand[valueStart] == '\t' || 
                           configCommand[valueStart] == '\r' || configCommand[valueStart] == '\n'))
                    {
                        valueStart++;
                    }
                    
                    // If it starts with a quote, we need to find the matching closing quote
                    if (valueStart < configCommand.Length && configCommand[valueStart] == '"')
                    {
                        valueStart++; // Skip the opening quote
                        
                        // Find closing quote (accounting for escaped quotes)
                        int valueEnd = valueStart;
                        bool escaped = false;
                        
                        while (valueEnd < configCommand.Length)
                        {
                            char c = configCommand[valueEnd];
                            
                            if (c == '\\' && !escaped)
                            {
                                escaped = true;
                            }
                            else if (c == '"' && !escaped)
                            {
                                break; // Found closing quote
                            }
                            else
                            {
                                escaped = false;
                            }
                            
                            valueEnd++;
                        }
                        
                        if (valueEnd > valueStart)
                        {
                            string configValue = configCommand.Substring(valueStart, valueEnd - valueStart);
                            _logger.LogInformation($"Extracted config value: {configValue}");
                            
                            // Process the configuration
                            ProcessConfiguration(configValue);
                        }
                    }
                    // If it's a JSON object directly
                    else if (valueStart < configCommand.Length && configCommand[valueStart] == '{')
                    {
                        // Find the closing brace, accounting for nested objects
                        int braceCount = 1;
                        int valueEnd = valueStart + 1;
                        
                        while (valueEnd < configCommand.Length && braceCount > 0)
                        {
                            char c = configCommand[valueEnd];
                            
                            if (c == '{') braceCount++;
                            else if (c == '}') braceCount--;
                            
                            valueEnd++;
                        }
                        
                        if (braceCount == 0 && valueEnd > valueStart)
                        {
                            string configValue = configCommand.Substring(valueStart, valueEnd - valueStart);
                            _logger.LogInformation($"Extracted config object: {configValue}");
                            
                            // Process the configuration
                            ProcessConfiguration(configValue);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing config command");
                SendLogMessage($"Error processing configuration: {ex.Message}", true);
            }
        }

        // Method to test offset file access
        private void TestOffsetFileAccess()
        {
            try
            {
                _logger.LogWarning($"TESTING OFFSET FILE ACCESS: {_offsetFilePath}");
                SendLogMessage($"TESTING OFFSET FILE ACCESS: {_offsetFilePath}");
                
                // Report all relevant path info for diagnostics
                string currentDir = Directory.GetCurrentDirectory();
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string tempDir = Path.GetTempPath();
                string programDataDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                
                _logger.LogWarning($"PATH DIAGNOSTICS: Current Dir = {currentDir}");
                _logger.LogWarning($"PATH DIAGNOSTICS: Base Dir = {baseDir}");
                _logger.LogWarning($"PATH DIAGNOSTICS: Temp Dir = {tempDir}");
                _logger.LogWarning($"PATH DIAGNOSTICS: ProgramData Dir = {programDataDir}");
                
                SendLogMessage($"PATH DIAGNOSTICS: Current Dir = {currentDir}");
                
                // Ensure we're using an absolute path
                if (!Path.IsPathRooted(_offsetFilePath))
                {
                    string originalPath = _offsetFilePath;
                    _offsetFilePath = Path.GetFullPath(_offsetFilePath);
                    _logger.LogWarning($"Converting relative offset path '{originalPath}' to absolute path '{_offsetFilePath}'");
                }
                
                // List of potential paths to try
                List<string> potentialPaths = new List<string>
                {
                    _offsetFilePath,
                    Path.Combine(baseDir, "offsets.json"),
                    Path.Combine(currentDir, "offsets.json"),
                    Path.Combine(tempDir, "orf2Postgres_offsets.json"),
                    Path.Combine(programDataDir, "orf2Postgres", "offsets.json")
                };
                
                // Try writing a test file to each location
                bool anySuccess = false;
                List<string> successfulPaths = new List<string>();
                Dictionary<string, string> pathErrors = new Dictionary<string, string>();
                
                foreach (string path in potentialPaths)
                {
                    try
                    {
                        _logger.LogWarning($"Testing offset path: {path}");
                        
                        // Ensure directory exists
                        string? directory = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        {
                            try
                            {
                                Directory.CreateDirectory(directory);
                                _logger.LogWarning($"Created directory for offset file: {directory}");
                            }
                            catch (Exception dirEx)
                            {
                                _logger.LogWarning($"Failed to create directory {directory}: {dirEx.Message}");
                                pathErrors[path] = $"Failed to create directory: {dirEx.Message}";
                                continue;
                            }
                        }
                        
                        // Try to write a test file
                        var testOffsets = new Dictionary<string, long>
                        {
                            { "test-path-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log", 0 }
                        };
                        
                        string json = JsonSerializer.Serialize(testOffsets);
                        File.WriteAllText(path, json);
                        
                        // Verify it was written
                        if (File.Exists(path))
                        {
                            string content = File.ReadAllText(path);
                            if (content.Length > 0)
                            {
                                _logger.LogWarning($"✅ Successfully wrote and read offset file at: {path}");
                                
                                // Clean up test entry by writing an empty dictionary
                                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, long>()));
                                
                                successfulPaths.Add(path);
                                anySuccess = true;
                            }
                        }
                    }
                    catch (Exception pathEx)
                    {
                        _logger.LogError(pathEx, $"Failed to access path: {path}");
                        pathErrors[path] = pathEx.Message;
                    }
                }
                
                // Report results
                if (anySuccess)
                {
                    _logger.LogWarning($"OFFSET TEST RESULTS: {successfulPaths.Count} paths were writable");
                    SendLogMessage($"OFFSET TEST RESULTS: {successfulPaths.Count} paths were writable");
                    
                    foreach (string path in successfulPaths)
                    {
                        _logger.LogWarning($"✅ Writable path: {path}");
                        SendLogMessage($"✅ Writable path: {path}");
                    }
                    
                    // Use the first successful path
                    if (successfulPaths.Count > 0)
                    {
                        // Update the offset path if necessary
                        string bestPath = successfulPaths[0];
                        if (_offsetFilePath != bestPath)
                        {
                            _logger.LogWarning($"Updating offset path from {_offsetFilePath} to {bestPath}");
                            SendLogMessage($"Updating offset path to: {bestPath}");
                            _offsetFilePath = bestPath;
                        }
                        
                        // Use this path to create our official offset file
                        ForceSaveOffsets();
                    }
                }
                else
                {
                    _logger.LogError("CRITICAL: No writable paths found for offset file!");
                    SendLogMessage("❌ CRITICAL: No writable paths found for offset file!", true);
                    
                    // Report all errors
                    foreach (var error in pathErrors)
                    {
                        _logger.LogError($"Path error - {error.Key}: {error.Value}");
                        SendLogMessage($"Path error - {error.Key}: {error.Value}", true);
                    }
                    
                    // Make one last attempt with forced System.IO.Path.GetTempFileName()
                    try
                    {
                        string tempFile = Path.GetTempFileName();
                        _logger.LogWarning($"LAST RESORT: Using .NET GetTempFileName: {tempFile}");
                        SendLogMessage($"LAST RESORT: Using .NET GetTempFileName: {tempFile}");
                        
                        var finalOffsets = new Dictionary<string, long>
                        {
                            { "last-resort.log", 0 }
                        };
                        
                        string json = JsonSerializer.Serialize(finalOffsets);
                        File.WriteAllText(tempFile, json);
                        
                        _logger.LogWarning($"LAST RESORT SUCCEEDED: Saved to {tempFile}");
                        SendLogMessage($"⚠️ Last resort succeeded: Saved to {tempFile}");
                        
                        _offsetFilePath = tempFile;
                    }
                    catch (Exception finalEx)
                    {
                        _logger.LogError(finalEx, "FATAL: Even temporary file creation failed");
                        SendLogMessage("❌ FATAL: Even temporary file creation failed", true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRITICAL ERROR: Unhandled exception in offset file testing");
                SendLogMessage($"CRITICAL ERROR: Failed to access offset file: {ex.Message}", true);
                
                // Try direct temp file as final fallback
                try
                {
                    string emergencyPath = Path.Combine(Path.GetTempPath(), $"orf2Postgres_emergency_{Guid.NewGuid():N}.json");
                    _logger.LogWarning($"EMERGENCY: Trying to write to {emergencyPath}");
                    
                    // Create a test file with simple content
                    var testOffsets = new Dictionary<string, long>
                    {
                        { "emergency.log", 0 }
                    };
                    
                    string json = JsonSerializer.Serialize(testOffsets);
                    File.WriteAllText(emergencyPath, json);
                    
                    _logger.LogWarning($"EMERGENCY: Created offset file at {emergencyPath}");
                    SendLogMessage($"⚠️ EMERGENCY: Created offset file at {emergencyPath}");
                    
                    // Update the path for future use
                    _offsetFilePath = emergencyPath;
                }
                catch (Exception emergencyEx)
                {
                    _logger.LogError(emergencyEx, "FATAL ERROR: All emergency attempts failed");
                    SendLogMessage($"❌ FATAL ERROR: All offset file access attempts failed", true);
                }
            }
        }

        // Add this to the class as a forced offset creation method
        private void ForceSaveOffsets()
        {
            try
            {
                _logger.LogWarning($"FORCING IMMEDIATE OFFSET SAVE: Path={_offsetFilePath}, Entries={_fileOffsets.Count}, IsService={_isWindowsService}");
                SendLogMessage($"💾 FORCING IMMEDIATE OFFSET SAVE: Path={_offsetFilePath}, Entries={_fileOffsets.Count}");
                
                // Ensure we're using an absolute path
                if (!Path.IsPathRooted(_offsetFilePath))
                {
                    string originalPath = _offsetFilePath;
                    _offsetFilePath = ResolveExecutableRelativePath(originalPath);
                    _logger.LogWarning($"Converting relative offset path '{originalPath}' to absolute path '{_offsetFilePath}'");
                    SendLogMessage($"Converting relative offset path to absolute: {_offsetFilePath}");
                }
                
                // List of fallback locations if the primary fails (in priority order)
                List<string> potentialLocations = new List<string>
                {
                    _offsetFilePath,
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "offsets.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "offsets.json"),
                    Path.Combine(Path.GetTempPath(), "orf2Postgres_offsets.json")
                };
                
                // Make sure we have unique paths
                potentialLocations = potentialLocations.Distinct().ToList();
                
                // Log offset data for debugging
                string offsetEntries = string.Join(", ", _fileOffsets.Select(kv => $"{Path.GetFileName(kv.Key)}:{kv.Value}"));
                _logger.LogWarning($"OFFSET DATA TO SAVE: {offsetEntries}");
                SendLogMessage($"📊 Offset data to save: {offsetEntries}");
                
                // Serialize with pretty printing for better debugging
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_fileOffsets, options);
                
                bool savedSuccessfully = false;
                string successPath = "";
                
                foreach (string path in potentialLocations)
                {
                    try
                    {
                        // Ensure directory exists
                        string? directory = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        
                        // Try direct write
                        File.WriteAllText(path, json);
                        savedSuccessfully = true;
                        successPath = path;
                        
                        _logger.LogWarning($"OFFSET SAVE SUCCEEDED: Saved to {path}");
                        SendLogMessage($"✅ Offset file saved successfully to {path}");
                        
                        break; // Exit after first success
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to save offsets to {path}");
                    }
                }
                
                if (!savedSuccessfully)
                {
                    _logger.LogError("CRITICAL ERROR: Failed to save offsets to ANY location!");
                    SendLogMessage("❌ CRITICAL ERROR: Failed to save offsets to ANY location!", true);
                }
                else if (_offsetFilePath != successPath)
                {
                    // Update primary path for future use
                    _logger.LogWarning($"Updating primary offset path from {_offsetFilePath} to {successPath}");
                    _offsetFilePath = successPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ForceSaveOffsets");
                SendLogMessage($"Error in ForceSaveOffsets: {ex.Message}", true);
            }
        }

        // Enhanced direct file logging with better error handling and visibility
        private void DirectFileLog(string message)
        {
            // Log to standard logger instead
            if (message.Contains("ERROR") || message.Contains("FAILED") || message.Contains("CRITICAL"))
            {
                _logger.LogError(message);
            }
            else
            {
                _logger.LogInformation(message);
            }
        }

        // Add emergency simplified direct log method - as direct as possible
        private void TrySimpleDirectWrite(string message)
        {
            // Only proceed if we have a connected pipe
            if (_pipeServer == null || !_pipeServer.IsConnected)
            {
                DirectFileLog($"SIMPLE WRITE FAILED: Pipe not connected");
                return;
            }
            
            try
            {
                // Create the simplest possible JSON structure
                string simpleJson = "[{\"action\":\"log_message\",\"message\":\"" + 
                                   message.Replace("\"", "\\\"") + // Escape quotes
                                   "\",\"status\":\"Error\"}]";
                
                DirectFileLog($"SIMPLE WRITE ATTEMPT: {simpleJson}");
                
                // Write directly with lowest-level API
                using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                {
                    writer.WriteLine(simpleJson);
                    writer.Flush();
                    
                    DirectFileLog("SIMPLE WRITE SUCCESS!");
                }
            }
            catch (Exception ex)
            {
                DirectFileLog($"SIMPLE WRITE ERROR: {ex.Message}");
            }
        }

        // Add method to send all buffered messages when client connects
        private void SendBufferedMessages()
        {
            try
            {
                DirectFileLog($"SENDING BUFFERED MESSAGES: Attempting to send {_bufferedMessageCount} buffered messages");
                
                // Only proceed if connected and we have messages
                if (_pipeServer == null || !_pipeServer.IsConnected)
                {
                    DirectFileLog("SENDING BUFFERED MESSAGES: Pipe not connected, can't send buffered messages");
                    return;
                }
                
                lock (_persistentMessageBuffer)
                {
                    if (_persistentMessageBuffer.Count == 0)
                    {
                        DirectFileLog("SENDING BUFFERED MESSAGES: No buffered messages to send");
                        return;
                    }
                    
                    DirectFileLog($"SENDING BUFFERED MESSAGES: {_persistentMessageBuffer.Count} messages");
                    
                    // Create a copy to avoid modification issues while sending
                    var messagesToSend = _persistentMessageBuffer.ToList();
                    
                    // Clear buffer immediately to avoid duplicate sends
                    _persistentMessageBuffer.Clear();
                    _bufferedMessageCount = 0;
                    
                    // Send in batches of 10 to avoid overwhelming the pipe
                    for (int i = 0; i < messagesToSend.Count; i += 10)
                    {
                        try
                        {
                            // Get up to 10 messages for this batch
                            var batch = messagesToSend.Skip(i).Take(10).ToList();
                            
                            // Serialize and send
                            string json = JsonSerializer.Serialize(batch);
                            json = FilterInvalidCharacters(json);
                            
                            using (var writer = new StreamWriter(_pipeServer, Encoding.UTF8, 1024, leaveOpen: true))
                            {
                                writer.WriteLine(json);
                                writer.Flush();
                                DirectFileLog($"SENT BUFFERED BATCH: {i} to {i + batch.Count - 1}");
                            }
                            
                            // Small delay to avoid overwhelming the pipe
                            Thread.Sleep(50);
                        }
                        catch (Exception ex)
                        {
                            DirectFileLog($"ERROR SENDING BUFFERED BATCH: {ex.Message}");
                        }
                    }
                    
                    DirectFileLog("SENDING BUFFERED MESSAGES: Completed");
                }
            }
            catch (Exception ex)
            {
                DirectFileLog($"ERROR IN SENDBUFFEREDMESSAGES: {ex.Message}");
            }
        }

        // Add helper method to ensure diagnostics log path is visible
        private void LogDiagnosticsFileLocation()
        {
            // Method removed as pipe diagnostics log is no longer used
        }

        private async Task ProcessLogLine(NpgsqlConnection connection, string line)
        {
            var logEntry = ParseLogLine(line);
            
            string insertSql = $@"
            INSERT INTO {_dbSchema}.{_tableName} (
                message_id, event_source, event_datetime, event_class, 
                event_severity, event_action, filtering_point, ip,
                sender, recipients, msg_subject, msg_author,
                remote_peer, source_ip, country, event_msg, filename, entry_hash
            ) VALUES (
                @messageId, @eventSource, @eventDateTime, @eventClass,
                @eventSeverity, @eventAction, @filteringPoint, @ip,
                @sender, @recipients, @msgSubject, @msgAuthor,
                @remotePeer, @sourceIp, @country, @eventMsg, @filename, @entryHash
            ) ON CONFLICT (entry_hash) DO NOTHING";

            using (var cmd = new NpgsqlCommand(insertSql, connection))
            {
                // Set command timeout
                cmd.CommandTimeout = 30;
                
                // Add parameters with their values
                cmd.Parameters.AddWithValue("messageId", logEntry.TryGetValue("message_id", out var msgId) ? msgId : DBNull.Value);
                cmd.Parameters.AddWithValue("eventSource", logEntry.TryGetValue("event_source", out var src) ? src : DBNull.Value);
                cmd.Parameters.AddWithValue("eventDateTime", ParseDateTime(logEntry.TryGetValue("event_datetime", out var dt) ? dt : null));
                cmd.Parameters.AddWithValue("eventClass", logEntry.TryGetValue("event_class", out var cls) ? cls : DBNull.Value);
                cmd.Parameters.AddWithValue("eventSeverity", logEntry.TryGetValue("event_severity", out var sev) ? sev : DBNull.Value);
                cmd.Parameters.AddWithValue("eventAction", logEntry.TryGetValue("event_action", out var act) ? act : DBNull.Value);
                cmd.Parameters.AddWithValue("filteringPoint", logEntry.TryGetValue("filtering_point", out var fp) ? fp : DBNull.Value);
                cmd.Parameters.AddWithValue("ip", logEntry.TryGetValue("ip", out var ip) ? ip : DBNull.Value);
                cmd.Parameters.AddWithValue("sender", logEntry.TryGetValue("sender", out var sender) ? sender : DBNull.Value);
                cmd.Parameters.AddWithValue("recipients", logEntry.TryGetValue("recipients", out var rcpt) ? rcpt : DBNull.Value);
                cmd.Parameters.AddWithValue("msgSubject", logEntry.TryGetValue("msg_subject", out var subj) ? subj : DBNull.Value);
                cmd.Parameters.AddWithValue("msgAuthor", logEntry.TryGetValue("msg_author", out var auth) ? auth : DBNull.Value);
                cmd.Parameters.AddWithValue("remotePeer", logEntry.TryGetValue("remote_peer", out var peer) ? peer : DBNull.Value);
                cmd.Parameters.AddWithValue("sourceIp", logEntry.TryGetValue("source_ip", out var sip) ? sip : DBNull.Value);
                cmd.Parameters.AddWithValue("country", logEntry.TryGetValue("country", out var country) ? country : DBNull.Value);
                cmd.Parameters.AddWithValue("eventMsg", logEntry.TryGetValue("event_msg", out var msg) ? msg : DBNull.Value);
                cmd.Parameters.AddWithValue("filename", Path.GetFileName(_currentFilePath));
                cmd.Parameters.AddWithValue("entryHash", logEntry.TryGetValue("entry_hash", out var hash) ? hash : DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
} 
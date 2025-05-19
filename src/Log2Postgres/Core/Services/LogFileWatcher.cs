using Log2Postgres.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;
using System.Security.Principal;
using System.Security.AccessControl;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Service that monitors log files, processes them, and forwards entries to the database
    /// </summary>
    public class LogFileWatcher : BackgroundService
    {
        private const string PipeName = "Log2PostgresServicePipe";
        private Task? _ipcServerTask;
        private CancellationTokenSource? _ipcServerCts;

        private readonly ILogger<LogFileWatcher> _logger;
        private readonly OrfLogParser _logParser;
        private readonly PositionManager _positionManager;
        private readonly PostgresService _postgresService;
        private readonly LogMonitorSettings _settings;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly bool _isRunningAsHostedService;
        
        // Event to notify the UI or other services about processing status
        public event Action<string, int, long>? ProcessingStatusChanged;
        
        // Event to notify when new log entries are processed
        public event Action<IEnumerable<OrfLogEntry>>? EntriesProcessed;
        
        // Event to notify the UI about errors
        public event Action<string, string>? ErrorOccurred;
        
        // Processing statistics
        public int TotalLinesProcessed { get; private set; }
        public string CurrentFile { get; private set; } = string.Empty;
        public long CurrentPosition { get; private set; }
        public DateTime LastProcessedTime { get; private set; }
        public int EntriesSavedToDb { get; private set; }
        
        // Expose current directory and pattern for comparison
        public string CurrentDirectory => _settings.BaseDirectory;
        public string CurrentPattern => _settings.LogFilePattern;
        
        private readonly CancellationTokenSource _stoppingCts = new();
        private FileSystemWatcher? _fileSystemWatcher;
        
        // We'll use this to track the processing state in a thread-safe way
        private volatile int _isProcessingFile = 0;
        
        // This is for the UI to show if we're in processing mode
        public bool IsProcessing { get; private set; } = false;
        private string? _lastProcessingError = null;
        
        private readonly List<string> _pendingFiles = new();
        
        public LogFileWatcher(
            ILogger<LogFileWatcher> logger,
            OrfLogParser logParser,
            PositionManager positionManager,
            PostgresService postgresService,
            IOptions<LogMonitorSettings> settings,
            IHostApplicationLifetime appLifetime,
            IOptions<WindowsServiceSettings> serviceSettingsOptions)
        {
            _logger = logger;
            _logParser = logParser;
            _positionManager = positionManager;
            _postgresService = postgresService;
            _settings = settings.Value;
            _appLifetime = appLifetime;

            // Log the received RunAsService value directly from IOptions<WindowsServiceSettings>
            if (serviceSettingsOptions != null && serviceSettingsOptions.Value != null)
            {
                _logger.LogInformation("LogFileWatcher Constructor: Received serviceSettingsOptions.Value.RunAsService = {RunAsServiceValue}", serviceSettingsOptions.Value.RunAsService);
            }
            else
            {
                _logger.LogWarning("LogFileWatcher Constructor: serviceSettingsOptions or serviceSettingsOptions.Value is null. Cannot determine RunAsService from options.");
            }

            _isRunningAsHostedService = serviceSettingsOptions?.Value?.RunAsService ?? false;
            _logger.LogInformation("LogFileWatcher Constructor: _isRunningAsHostedService initialized to {IsRunningAsHostedServiceValue}", _isRunningAsHostedService);
        }
        
        // Helper method to notify UI about errors
        private void NotifyError(string component, string message)
        {
            _logger.LogError("{Component} error: {Message}", component, message);
            _lastProcessingError = $"[{component}] {message}";
            ErrorOccurred?.Invoke(component, message);
        }
        
        /// <summary>
        /// Start watching log files and processing them
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LogFileWatcher ExecuteAsync ENTERED. IsRunningAsHostedService: {IsHosted}", _isRunningAsHostedService);

            stoppingToken.Register(() => {
                _logger.LogInformation("ExecuteAsync: Host's stoppingToken was cancelled. Current time: {Time}", DateTime.UtcNow);
                _logger.LogInformation("ExecuteAsync: Cancelling internal _stoppingCts. Current time: {Time}", DateTime.UtcNow);
                if (!_stoppingCts.IsCancellationRequested) _stoppingCts.Cancel(); // Ensure cancel only once
                _logger.LogInformation("ExecuteAsync: Internal _stoppingCts cancellation requested. IsCancellationRequested: {IsCancelled}. Current time: {Time}", _stoppingCts.IsCancellationRequested, DateTime.UtcNow);
            });

            await InitialSetupAsync(_stoppingCts.Token);

            if (_isRunningAsHostedService)
            {
                _logger.LogInformation("Running as a hosted service. Automatically starting processing via internal StartProcessingAsync().");
                // This will set IsProcessing = true and process existing files.
                await StartProcessingAsyncInternal();

                _logger.LogInformation("Starting IPC server task.");
                _ipcServerCts = new CancellationTokenSource();
                _ipcServerTask = Task.Run(() => RunIpcServerAsync(_ipcServerCts.Token), _ipcServerCts.Token);
            }
            
            _logger.LogDebug("Starting background polling task with _stoppingCts.Token. IsCancellationRequested initially: {IsCancelled}", _stoppingCts.Token.IsCancellationRequested);
            await PollingLoopAsync(_stoppingCts.Token);

            _logger.LogInformation("LogFileWatcher ExecuteAsync: PollingLoopAsync completed. Current time: {Time}", DateTime.UtcNow);
            _logger.LogInformation("LogFileWatcher ExecuteAsync finished, service stopping... Current time: {Time}", DateTime.UtcNow);
        }
        
        // New method to encapsulate initial setup
        private async Task InitialSetupAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Performing initial setup...");
            _lastProcessingError = null;
            SetupFileSystemWatcherAsync();

            // Optional: Check database connection/table during service startup
            try
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (!await _postgresService.TableExistsAsync())
                {
                    NotifyError("Database", "Required database table does not exist. Service will run but data won't be saved.");
                }
                else
                {
                    _logger.LogInformation("Database table verified successfully.");
                }
            }
            catch (Exception ex)
            {
                NotifyError("Database", $"Error checking database table: {ex.Message}. Service will run but data won't be saved.");
            }
            _logger.LogDebug("Initial setup complete.");
        }
        
        /// <summary>
        /// Setup the file system watcher with the current directory settings
        /// </summary>
        private void SetupFileSystemWatcherAsync()
        {
            if (string.IsNullOrWhiteSpace(_settings.BaseDirectory))
            {
                _logger.LogDebug("Skipping file system watcher setup - BaseDirectory not configured");
                return;
            }
            
            if (!Directory.Exists(_settings.BaseDirectory))
            {
                _logger.LogWarning("Directory {Directory} does not exist, cannot set up file watcher", _settings.BaseDirectory);
                return;
            }
            
            try
            {
                // Dispose old watcher if it exists
                if (_fileSystemWatcher != null)
                {
                    _fileSystemWatcher.EnableRaisingEvents = false;
                    _fileSystemWatcher.Changed -= OnFileChanged;
                    _fileSystemWatcher.Created -= OnFileCreated;
                    _fileSystemWatcher.Dispose();
                }
                
                _logger.LogDebug("Setting up file system watcher for directory {Directory}", _settings.BaseDirectory);
                _fileSystemWatcher = new FileSystemWatcher(_settings.BaseDirectory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    Filter = "*.log",
                    EnableRaisingEvents = true
                };
                
                _fileSystemWatcher.Changed += OnFileChanged;
                _fileSystemWatcher.Created += OnFileCreated;
                
                _logger.LogInformation("File watcher set up for directory {Directory}", _settings.BaseDirectory);
                _logger.LogDebug("File watcher configuration - Filter: *.log, NotifyFilter: LastWrite|Size|FileName");
            }
            catch (Exception ex)
            {
                NotifyError("File System", $"Error setting up file system watcher: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Process files that already exist in the directory
        /// </summary>
        private async Task ProcessExistingFilesAsync(CancellationToken cancellationToken)
        {
            try
            {
                string[] logFiles = GetMatchingLogFiles();
                _logger.LogInformation("Found {Count} existing log files to process", logFiles.Length);
                
                foreach (string filePath in logFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    await ProcessLogFileAsync(filePath, cancellationToken);
                }
                
                _logger.LogInformation("Finished processing existing log files");
            }
            catch (Exception ex)
            {
                NotifyError("File Processing", $"Error processing existing files: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Periodically check for new content in log files
        /// </summary>
        private async Task PollingLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting polling loop with interval of {Interval} seconds. IsProcessing: {IsProcessing}, IsRunningAsHostedService: {IsHosted}", 
                _settings.PollingIntervalSeconds, IsProcessing, _isRunningAsHostedService);
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Only process files if processing is active (started by user)
                    if (IsProcessing)
                    {
                        _logger.LogDebug("Polling loop - Processing is active, checking for changes. Pending files: {Count}, IsProcessing: {IsProcessing}", 
                            _pendingFiles.Count, IsProcessing);
                        
                        // Process any files that were queued by file system events
                        await ProcessPendingFilesAsync(cancellationToken);
                        
                        // Also check all matching files for updates
                        await ProcessCurrentLogFileAsync(cancellationToken);
                    }
                    else
                    {
                        _logger.LogDebug("Polling loop - Processing is not active, skipping file checks. IsProcessing: {IsProcessing}", IsProcessing);
                    }
                    
                    // Wait for the next polling interval
                    await Task.Delay(TimeSpan.FromSeconds(_settings.PollingIntervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in polling loop: {Message}", ex.Message);
                    NotifyError("PollingLoop", $"Error in polling loop: {ex.Message}");
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.PollingIntervalSeconds)), cancellationToken);
                    }
                }
            }
            
            _logger.LogInformation("Polling loop stopped");
        }
        
        /// <summary>
        /// Process any pending files that were queued by file system events
        /// </summary>
        private async Task ProcessPendingFilesAsync(CancellationToken cancellationToken)
        {
            List<string> filesToProcess;
            
            lock (_pendingFiles)
            {
                _logger.LogDebug("Processing pending files - Queue size: {Count}", _pendingFiles.Count);
                filesToProcess = new List<string>(_pendingFiles);
                _pendingFiles.Clear();
            }
            
            if (filesToProcess.Count > 0)
            {
                _logger.LogInformation("Processing {Count} pending files", filesToProcess.Count);
            }
            
            foreach (string filePath in filesToProcess.Distinct())
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                
                _logger.LogDebug("Processing pending file from queue: {FilePath}", filePath);
                
                // Check if this file matches our pattern before processing it
                if (IsMatchingLogFile(filePath))
                {
                    await ProcessLogFileAsync(filePath, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("Skipping file {FilePath} as it does not match the configured pattern: {Pattern}", 
                        filePath, _settings.LogFilePattern);
                }
            }
        }
        
        /// <summary>
        /// Process the current (today's) log file and any other matching log files
        /// </summary>
        private async Task ProcessCurrentLogFileAsync(CancellationToken cancellationToken)
        {
            try
            {
                // First try the exact current log file
                string currentLogFilePath = GetCurrentLogFilePath();
                _logger.LogDebug("Checking current log file: {FilePath}", currentLogFilePath);
                
                if (File.Exists(currentLogFilePath))
                {
                    var fileInfo = new FileInfo(currentLogFilePath);
                    _logger.LogDebug("Current log file exists - Size: {Size} bytes, Last modified: {LastModified}", 
                        fileInfo.Length, fileInfo.LastWriteTime);
                    
                    // Get last known position
                    long lastPosition = await _positionManager.GetPositionAsync(currentLogFilePath, cancellationToken);
                    _logger.LogDebug("Last known position for current log file: {Position}", lastPosition);
                    
                    // Only process if the file has changed since our last check
                    if (fileInfo.Length > lastPosition)
                    {
                        _logger.LogInformation("Current log file has new content - Old position: {OldPosition}, New size: {NewSize}", 
                            lastPosition, fileInfo.Length);
                        await ProcessLogFileAsync(currentLogFilePath, cancellationToken);
                    }
                    else
                    {
                        _logger.LogDebug("Current log file has no new content - Position: {Position}, Size: {Size}", 
                            lastPosition, fileInfo.Length);
                    }
                }
                else
                {
                    _logger.LogDebug("Current log file does not exist: {FilePath}", currentLogFilePath);
                    
                    // If the current day's file doesn't exist, check for other matching log files
                    string[] matchingFiles = GetMatchingLogFiles();
                    _logger.LogDebug("Found {Count} alternative matching log files to check", matchingFiles.Length);
                    
                    foreach (string filePath in matchingFiles)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        
                        var fileInfo = new FileInfo(filePath);
                        long lastPosition = await _positionManager.GetPositionAsync(filePath, cancellationToken);
                        
                        if (fileInfo.Length > lastPosition)
                        {
                            _logger.LogInformation("Log file {FilePath} has new content - Old position: {OldPosition}, New size: {NewSize}", 
                                filePath, lastPosition, fileInfo.Length);
                            await ProcessLogFileAsync(filePath, cancellationToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing current log file: {Message}", ex.Message);
                NotifyError("File Processing", $"Error processing current log file: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Process a specific log file, parsing and forwarding entries
        /// </summary>
        private async Task ProcessLogFileAsync(string filePath, CancellationToken cancellationToken)
        {
            // Skip processing if another operation is already in progress
            int previousValue = Interlocked.CompareExchange(ref _isProcessingFile, 1, 0);
            if (previousValue != 0)
            {
                _logger.LogWarning("Skipping processing of {FilePath} because another processing operation is in progress. Lock value: {LockValue}", filePath, previousValue);
                return;
            }
            
            try
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check at the beginning

                CurrentFile = Path.GetFileName(filePath);
                _logger.LogDebug("Processing log file {FilePath}", filePath);
                
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File {FilePath} no longer exists, skipping processing", filePath);
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    _logger.LogDebug("File {FilePath} size: {Size} bytes, last modified: {LastModified}", 
                        filePath, fileInfo.Length, fileInfo.LastWriteTime);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error getting file information: {Message}", ex.Message);
                }
                
                _logger.LogDebug("Retrieving last processing position for {FilePath}", filePath);
                long startPosition = await _positionManager.GetPositionAsync(filePath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                CurrentPosition = startPosition;
                _logger.LogDebug("Starting from position {Position} in file {FilePath}", startPosition, filePath);
                
                _logger.LogDebug("Parsing log file {FilePath} from position {Position}", filePath, startPosition);
                var (entries, newPosition) = _logParser.ParseLogFile(filePath, startPosition);
                cancellationToken.ThrowIfCancellationRequested(); // Check after potentially long synchronous operation
                _logger.LogDebug("Parsed {Count} entries from {FilePath}, new position: {NewPosition}", 
                    entries.Count, filePath, newPosition);
                
                if (entries.Count > 0)
                {
                    TotalLinesProcessed += entries.Count;
                    CurrentPosition = newPosition;
                    LastProcessedTime = DateTime.Now;
                    
                    _logger.LogDebug("Updating position tracker for {FilePath} to position {Position}", filePath, newPosition);
                    await _positionManager.UpdatePositionAsync(filePath, newPosition, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    _logger.LogDebug("Notifying UI of processing status update");
                    ProcessingStatusChanged?.Invoke(CurrentFile, entries.Count, newPosition);
                    EntriesProcessed?.Invoke(entries);
                    
                    try
                    {
                        _logger.LogDebug("Saving {Count} log entries to database", entries.Count);
                        int savedEntries = await _postgresService.SaveEntriesAsync(entries, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        EntriesSavedToDb += savedEntries;
                        
                        if (savedEntries != entries.Count)
                        {
                            _logger.LogWarning("Only {SavedCount} of {TotalCount} entries were saved to database (some may be duplicates)",
                                savedEntries, entries.Count);
                        }
                        else
                        {
                            _logger.LogDebug("Successfully saved all {Count} entries to database", savedEntries);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error saving entries to database: {Message}", ex.Message);
                        NotifyError("Database", $"Failed to save entries: {ex.Message}");
                    }
                    
                    _logger.LogInformation("Processed {Count} entries from {FilePath}", entries.Count, filePath);
                }
                else
                {
                    _logger.LogDebug("No new entries found in {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log file {FilePath}: {Message}", filePath, ex.Message);
                NotifyError("File Processing", $"Error processing {Path.GetFileName(filePath)}: {ex.Message}");
            }
            finally
            {
                // Note: We don't change IsProcessing here anymore
                
                // Release the processing lock using Interlocked for thread safety
                Interlocked.Exchange(ref _isProcessingFile, 0);
                _logger.LogDebug("Released processing lock for {FilePath}", filePath);
            }
        }
        
        /// <summary>
        /// Called when a file in the watched directory is modified
        /// </summary>
        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed || e.ChangeType == WatcherChangeTypes.Created)
            {
                string filePath = e.FullPath;
                string fileName = Path.GetFileName(filePath);
                bool isLogFile = Path.GetExtension(filePath).Equals(".log", StringComparison.OrdinalIgnoreCase);
                bool matchesPattern = isLogFile && IsMatchingLogFile(filePath);
                
                _logger.LogDebug("File changed: {FilePath}, FileName: {FileName}, ChangeType: {ChangeType}, IsLogFile: {IsLogFile}, MatchesPattern: {MatchesPattern}, IsProcessing: {IsProcessing}", 
                    filePath, fileName, e.ChangeType, isLogFile, matchesPattern, IsProcessing);
                
                // Queue the file regardless of whether processing is currently active
                // The polling loop will handle it when processing is active
                QueueFileForProcessing(filePath);
            }
        }
        
        /// <summary>
        /// Called when a new file is created in the watched directory
        /// </summary>
        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            _logger.LogInformation("New file created: {FilePath}", e.FullPath);
            QueueFileForProcessing(e.FullPath);
        }
        
        /// <summary>
        /// Queue a file for processing
        /// </summary>
        private void QueueFileForProcessing(string filePath)
        {
            // Only check if it's a log file (not necessarily if it matches our pattern)
            // This is to ensure we don't miss any potential log files
            if (!Path.GetExtension(filePath).Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping file with non-log extension: {FilePath}", filePath);
                return;
            }
            
            // Only queue the file if processing is active
            if (!IsProcessing)
            {
                _logger.LogDebug("File change detected for {FilePath}, but processing is not active. Ignoring.", filePath);
                return;
            }
                
            lock (_pendingFiles)
            {
                _pendingFiles.Add(filePath);
                _logger.LogInformation("File queued for processing: {FilePath}, Current queue size: {Count}", filePath, _pendingFiles.Count);
            }
        }
        
        /// <summary>
        /// Get all existing log files that match the configured pattern
        /// </summary>
        private string[] GetMatchingLogFiles()
        {
            try
            {
                return Directory.GetFiles(_settings.BaseDirectory, "*.log")
                    .Where(f => IsMatchingLogFile(f))
                    .OrderBy(f => f) // Sort by name (which should put oldest first for date-based names)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting matching log files: {Message}", ex.Message);
                return Array.Empty<string>();
            }
        }
        
        /// <summary>
        /// Check if a file matches the configured log file pattern
        /// </summary>
        private bool IsMatchingLogFile(string filePath)
        {
            string filename = Path.GetFileName(filePath);
            string pattern = _settings.LogFilePattern;
            
            // Replace {Date:format} with a regex pattern that matches dates
            // Create a pattern that matches digits and dashes in place of the date format
            string dateRegexPattern = @"\d{4}-\d{2}-\d{2}"; // yyyy-MM-dd format
            pattern = Regex.Replace(pattern, @"\{Date:[^}]+\}", dateRegexPattern);
            
            // Escape special regex characters except the ones we already handled
            string regexPattern = "^" + Regex.Escape(pattern).Replace(Regex.Escape(dateRegexPattern), dateRegexPattern) + "$";
            
            bool isMatch = Regex.IsMatch(filename, regexPattern);
            _logger.LogDebug("File pattern match check - Filename: {Filename}, Pattern: {Pattern}, RegexPattern: {RegexPattern}, IsMatch: {IsMatch}", 
                filename, _settings.LogFilePattern, regexPattern, isMatch);
            
            return isMatch;
        }
        
        /// <summary>
        /// Get the path for today's log file based on the configured pattern
        /// </summary>
        private string GetCurrentLogFilePath()
        {
            // Replace {Date:format} with the actual date
            string filename = Regex.Replace(
                _settings.LogFilePattern,
                @"\{Date:([^}]+)\}",
                match =>
                {
                    string format = match.Groups[1].Value;
                    return DateTime.Now.ToString(format);
                });
                
            return Path.Combine(_settings.BaseDirectory, filename);
        }
        
        /// <summary>
        /// Handles IPC requests from the UI.
        /// </summary>
        private async Task RunIpcServerAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("IPC Server starting. Pipe name: {PipeName}", PipeName);
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    _logger.LogDebug("IPC Server: Preparing PipeSecurity for Authenticated Users.");
                    // Define the pipe security
                    PipeSecurity pipeSecurity = new PipeSecurity();
                    // Grant access to "Authenticated Users"
                    SecurityIdentifier authenticatedUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                    PipeAccessRule accessRule = new PipeAccessRule(authenticatedUsersSid, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow);
                    pipeSecurity.AddAccessRule(accessRule);
                    _logger.LogInformation("IPC Server: PipeSecurity object created for 'Authenticated Users'.");

                    _logger.LogDebug("IPC Server: Attempting to create NamedPipeServerStream '{PipeName}' using NamedPipeServerStreamAcl.Create with PipeSecurity.", PipeName);
                    pipeServer = NamedPipeServerStreamAcl.Create(
                        PipeName, 
                        PipeDirection.InOut, 
                        1, // MaxNumberOfServerInstances
                        PipeTransmissionMode.Byte, 
                        PipeOptions.Asynchronous,
                        0, // inBufferSize (0 for default)
                        0, // outBufferSize (0 for default)
                        pipeSecurity,
                        HandleInheritability.None // Default inheritability
                        // additionalAccessRights defaults to 0, which is fine
                    );
                    _logger.LogInformation("IPC Server: NamedPipeServerStream object created for pipe '{PipeName}' via NamedPipeServerStreamAcl.Create.", PipeName);

                    _logger.LogInformation("IPC Server: Waiting for a client connection on pipe '{PipeName}'...", PipeName);
                    await pipeServer.WaitForConnectionAsync(cancellationToken);
                    _logger.LogInformation("IPC Server: Client connected to pipe '{PipeName}'.", PipeName);

                    try
                    {
                        // Read request from client
                        byte[] buffer = new byte[1024];
                        int bytesRead = await pipeServer.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        _logger.LogDebug("IPC Server: Received request: {Request}", request);

                        if (request == "GET_STATUS")
                        {
                            var statusResponse = new
                            {
                                ServiceOperationalState = GetOperationalStateString(),
                                IsProcessing = this.IsProcessing,
                                CurrentFile = this.CurrentFile,
                                CurrentPosition = this.CurrentPosition,
                                TotalLinesProcessedSinceStart = (long)this.TotalLinesProcessed,
                                LastErrorMessage = _lastProcessingError ?? string.Empty
                            };
                            _logger.LogDebug("IPC SERVER: Sending status. LastErrorMessage: '{ErrorMessage}', OperationalState: '{OpState}'", statusResponse.LastErrorMessage, statusResponse.ServiceOperationalState);
                            string response = JsonConvert.SerializeObject(statusResponse);
                            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                            await pipeServer.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
                            await pipeServer.FlushAsync(cancellationToken);
                            _logger.LogDebug("IPC Server: Sent status response: {JsonResponse}", response);
                        }
                        else if (request.StartsWith("UPDATE_SETTINGS:"))
                        {
                            _logger.LogInformation("IPC Server: Received UPDATE_SETTINGS request.");
                            try
                            {
                                string settingsJson = request.Substring("UPDATE_SETTINGS:".Length);
                                LogMonitorSettings? newSettings = JsonConvert.DeserializeObject<LogMonitorSettings>(settingsJson);
                                if (newSettings != null)
                                {
                                    _logger.LogInformation("IPC Server: Deserialized settings for update - BaseDir: {BaseDir}, Pattern: {Pattern}, Interval: {Interval}", 
                                        newSettings.BaseDirectory, newSettings.LogFilePattern, newSettings.PollingIntervalSeconds);
                                    await UpdateSettingsAsync(newSettings);
                                    // Send a simple ACK response
                                    byte[] ackBytes = Encoding.UTF8.GetBytes("ACK_UPDATE_SETTINGS");
                                    await pipeServer.WriteAsync(ackBytes, 0, ackBytes.Length, cancellationToken);
                                    await pipeServer.FlushAsync(cancellationToken);
                                    _logger.LogInformation("IPC Server: Sent ACK_UPDATE_SETTINGS to client.");
                                }
                                else
                                {
                                    _logger.LogError("IPC Server: Failed to deserialize settings from UPDATE_SETTINGS request. JSON: {Json}", settingsJson);
                                    byte[] nackBytes = Encoding.UTF8.GetBytes("NACK_UPDATE_SETTINGS_DESERIALIZATION_ERROR");
                                    await pipeServer.WriteAsync(nackBytes, 0, nackBytes.Length, cancellationToken);
                                    await pipeServer.FlushAsync(cancellationToken);
                                }
                            }
                            catch (JsonException jsonEx)
                            {
                                _logger.LogError(jsonEx, "IPC Server: JSON deserialization error for UPDATE_SETTINGS.");
                                byte[] nackBytes = Encoding.UTF8.GetBytes("NACK_UPDATE_SETTINGS_JSON_ERROR");
                                await pipeServer.WriteAsync(nackBytes, 0, nackBytes.Length, cancellationToken);
                                await pipeServer.FlushAsync(cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "IPC Server: Error processing UPDATE_SETTINGS request.");
                                byte[] nackBytes = Encoding.UTF8.GetBytes("NACK_UPDATE_SETTINGS_GENERAL_ERROR");
                                await pipeServer.WriteAsync(nackBytes, 0, nackBytes.Length, cancellationToken);
                                await pipeServer.FlushAsync(cancellationToken);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("IPC Server: Received unknown request: {Request}", request);
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "IPC Server: IOException during client communication on pipe '{PipeName}'. Client may have disconnected.", PipeName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "IPC Server: Exception during client communication on pipe '{PipeName}'.", PipeName);
                    }
                    finally
                    {
                        if (pipeServer.IsConnected)
                        {
                            _logger.LogDebug("IPC Server: Disconnecting client.");
                            pipeServer.Disconnect();
                        }
                        pipeServer.Dispose(); // Ensure pipe server is disposed
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("IPC Server: Operation cancelled (likely service stopping) for pipe '{PipeName}'. Shutting down IPC server loop.", PipeName);
                    pipeServer?.Dispose(); // Dispose if created before cancellation
                    break; // Exit loop if cancellation is requested
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC Server: Unhandled critical exception in server loop for pipe '{PipeName}'. This may affect pipe availability.", PipeName);
                    pipeServer?.Dispose(); // Dispose on error too
                    // Avoid fast spinning on repeated errors if pipe creation fails, by adding a small delay.
                    if (!cancellationToken.IsCancellationRequested)
                    {
                         await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                }
            }
            _logger.LogInformation("IPC Server stopped for pipe '{PipeName}'.", PipeName);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("LogFileWatcher Service StopAsync CALLED. Current time: {Time}", DateTime.UtcNow);

            _logger.LogInformation("StopAsync: Cancelling _applicationStopping (internal _stoppingCts). Current time: {Time}", DateTime.UtcNow);
            if (!_stoppingCts.IsCancellationRequested) _stoppingCts.Cancel(); // Ensure cancel only once
            _logger.LogInformation("StopAsync: _applicationStopping cancellation requested. IsCancellationRequested: {IsCancelled}. Current time: {Time}", _stoppingCts.IsCancellationRequested, DateTime.UtcNow);

            // Stop the IPC Server
            if (_ipcServerTask != null && !_ipcServerTask.IsCompleted)
            {
                _logger.LogDebug("StopAsync: Attempting to gracefully shutdown IPC server... Current time: {Time}", DateTime.UtcNow);
                if (_ipcServerCts != null && !_ipcServerCts.IsCancellationRequested)
                {
                    _logger.LogDebug("StopAsync: Requesting cancellation of _ipcServerCts. Current time: {Time}", DateTime.UtcNow);
                    _ipcServerCts.Cancel();
                }
                try
                {
                    // Give IPC server a short time to shutdown based on SCM's token for the delay itself
                    await Task.WhenAny(_ipcServerTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)); 
                    if (_ipcServerTask.IsCompleted)
                    {
                        _logger.LogInformation("StopAsync: IPC Server task completed. Current time: {Time}", DateTime.UtcNow);
                    }
                    else
                    {
                        _logger.LogWarning("StopAsync: IPC Server task did not complete within 5s. Current time: {Time}", DateTime.UtcNow);
                    }
                }
                catch(OperationCanceledException)
                {
                    _logger.LogInformation("StopAsync: IPC server shutdown delay was cancelled by SCM token. Current time: {Time}", DateTime.UtcNow);
                }
                 catch (Exception ex)
                {
                    _logger.LogError(ex, "StopAsync: Exception during IPC server shutdown. Current time: {Time}", DateTime.UtcNow);
                }
            }
            else
            {
                _logger.LogDebug("StopAsync: IPC server task is null or already completed. Current time: {Time}", DateTime.UtcNow);
            }
            
            _logger.LogInformation("StopAsync: Signalled internal components to stop. ExecuteAsync should be unwinding based on its token. Current time: {Time}", DateTime.UtcNow);
            // Removed problematic _processingTask wait. The host manages ExecuteAsync's lifetime via its stoppingToken.

            // Perform any other synchronous, quick cleanup needed by LogFileWatcher itself.
            // Example: if _fileSystemWatcher needs disposal (though it might be better in Dispose method if IDisposable)
            if (_fileSystemWatcher != null)
            {
                _logger.LogDebug("StopAsync: Disabling and disposing FileSystemWatcher. Current time: {Time}", DateTime.UtcNow);
                _fileSystemWatcher.EnableRaisingEvents = false;
                _fileSystemWatcher.Dispose(); // Dispose it here if it's managed by this class's lifecycle
                _fileSystemWatcher = null; // Avoid reuse
            }

            // Removed call to _positionManager.SaveAllPositionsAsync(); 
            // Position saving should ideally happen during processing or via PositionManager's own disposal if applicable.
            // If an explicit final save is critical and not handled elsewhere, a correct, quick method call would be needed.
            _logger.LogInformation("StopAsync: Pre-base.StopAsync cleanup finished. Current time: {Time}", DateTime.UtcNow);

            await base.StopAsync(cancellationToken); // Call base.StopAsync as required by the framework
            
            _logger.LogInformation("LogFileWatcher Service StopAsync COMPLETED. Current time: {Time}", DateTime.UtcNow);
        }
        
        /// <summary>
        /// Update the settings in use by this watcher
        /// </summary>
        /// <param name="newSettings">The new settings to use</param>
        public async Task UpdateSettingsAsync(LogMonitorSettings newSettings)
        {
            _logger.LogInformation("Updating LogFileWatcher settings. Current IsProcessing: {IsProcessingValue}", IsProcessing);
            _logger.LogInformation("UpdateSettingsAsync received newSettings - BaseDirectory: '{NewBaseDir}', Pattern: '{NewPattern}', Interval: {NewInterval}", 
                newSettings.BaseDirectory, newSettings.LogFilePattern, newSettings.PollingIntervalSeconds);
            
            // Store the state before updates for comparison
            bool wasProcessingBeforeUpdate = IsProcessing;
            string? oldBaseDirectory = _settings.BaseDirectory;

            // Update the internal settings object.
            _settings.BaseDirectory = newSettings.BaseDirectory;
            _settings.LogFilePattern = newSettings.LogFilePattern;
            _settings.PollingIntervalSeconds = newSettings.PollingIntervalSeconds;
            
            _logger.LogDebug("Settings updated - BaseDirectory: {BaseDirectory}, LogFilePattern: {LogFilePattern}, PollingInterval: {PollingInterval}s",
                _settings.BaseDirectory, _settings.LogFilePattern, _settings.PollingIntervalSeconds);

            bool directoryChanged = !string.Equals(oldBaseDirectory, _settings.BaseDirectory, StringComparison.OrdinalIgnoreCase);

            if (directoryChanged) // Re-setup watcher if directory changed
            {
                _logger.LogDebug("Directory changed, re-evaluating FileSystemWatcher.");
                SetupFileSystemWatcherAsync();
            }
            
            // Logic to handle starting or stopping processing based on directory validity
            if (directoryChanged)
            {
                bool newDirectoryIsValid = !string.IsNullOrWhiteSpace(_settings.BaseDirectory) && Directory.Exists(_settings.BaseDirectory);

                if (newDirectoryIsValid)
                {
                    if (!wasProcessingBeforeUpdate) // If processing was NOT active before this settings update
                    {
                        _logger.LogInformation("Valid log directory '{NewDir}' configured and processing was not active. Attempting to start processing.", _settings.BaseDirectory);
                        IsProcessing = true; // Set processing to active
                        _lastProcessingError = null; // Clear any previous config error
                        ErrorOccurred?.Invoke("Configuration", string.Empty); // CS8625 fix: Pass string.Empty instead of null

                        // Now process existing files and allow polling loop to take over
                        await InitializeAndProcessFilesOnStartAsync(_stoppingCts.Token);
                        _logger.LogInformation("Processing automatically started after configuration update. Polling loop will now be active.");
                    }
                    else // Processing was already active, and directory changed to another valid one
                    {
                         _logger.LogInformation("Directory changed to '{NewDir}' while processing is active. Re-processing existing files in new directory.", _settings.BaseDirectory);
                         await ProcessExistingFilesAsync(_stoppingCts.Token);
                    }
                }
                else // New directory is invalid (cleared or non-existent)
                {
                    _logger.LogWarning("Log directory configuration became invalid or was cleared ('{NewDir}').", _settings.BaseDirectory);
                    if (IsProcessing) // If it was processing, stop it
                    {
                        IsProcessing = false;
                        NotifyError("Configuration", "Log directory became invalid or was cleared. Processing stopped.");
                    }
                }
            }
        }
        
        /// <summary>
        /// Reset the current file and position information
        /// </summary>
        public void ResetPositionInfo()
        {
            CurrentFile = string.Empty;
            CurrentPosition = 0;
            TotalLinesProcessed = 0;
            LastProcessedTime = DateTime.MinValue;
            EntriesSavedToDb = 0;
            
            _logger.LogDebug("Position info has been reset");
        }
        
        /// <summary>
        /// Start processing log files (typically called by UI or for auto-start)
        /// </summary>
        /// <returns>Task representing the operation</returns>
        private async Task StartProcessingAsyncInternal()
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Processing is already active, internal start request ignored.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.BaseDirectory))
            {
                _logger.LogWarning("Cannot start processing - no directory configured");
                NotifyError("Configuration", "Log directory is not configured. Please configure a directory first.");
                return;
            }
            
            if (!Directory.Exists(_settings.BaseDirectory))
            {
                _logger.LogWarning("Cannot start processing - directory {Directory} does not exist", _settings.BaseDirectory);
                NotifyError("File System", $"Directory {_settings.BaseDirectory} does not exist. Please check the configuration.");
                return;
            }
            
            if (!_positionManager.PositionsFileExists())
            {
                _logger.LogWarning("Positions file doesn't exist at startup. Will create a new one with initial positions.");
            }
            
            IsProcessing = true;
            _logger.LogInformation("Log file processing has been set to ACTIVE (IsProcessing: {IsProcessing}) by StartProcessingAsyncInternal.", IsProcessing);
            
            await InitializeAndProcessFilesOnStartAsync(_stoppingCts.Token);
            
            _logger.LogInformation("Initial file processing complete (if any files were found). Polling loop will continue if service is running.");
        }

        // New method for UI-initiated start
        public async Task UIManagedStartProcessingAsync()
        {
            if (_isRunningAsHostedService)
            {
                _logger.LogWarning("Application is running as a hosted service. UI cannot directly start processing. Use IPC (not yet implemented).");
                NotifyError("Service Control", "Cannot start from UI when running as a dedicated service. Control via service management or IPC.");
                return;
            }

            if (IsProcessing)
            {
                _logger.LogWarning("Processing is already running (UI mode), ignoring start request.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.BaseDirectory))
            {
                _logger.LogWarning("Cannot start processing - no directory configured (UI mode)");
                NotifyError("Configuration", "Log directory is not configured. Please configure a directory first.");
                return;
            }
            
            if (!Directory.Exists(_settings.BaseDirectory))
            {
                _logger.LogWarning("Cannot start processing - directory {Directory} does not exist (UI mode)", _settings.BaseDirectory);
                NotifyError("File System", $"Directory {_settings.BaseDirectory} does not exist. Please check the configuration.");
                return;
            }

            if (!_positionManager.PositionsFileExists())
            {
                _logger.LogWarning("Positions file doesn't exist at UI start. Will create a new one.");
            }

            IsProcessing = true;
            _logger.LogInformation("Log file processing started by UI. IsProcessing: {IsProcessing}", IsProcessing);
            
            await InitializeAndProcessFilesOnStartAsync(_stoppingCts.Token);
            
            _logger.LogInformation("UI-initiated processing started and initial files processed.");
            if (!_isRunningAsHostedService)
            {
                _logger.LogInformation("UI Mode: Kicking off PollingLoopAsync as ExecuteAsync is not managing it.");
                 _ = Task.Run(() => PollingLoopAsync(_stoppingCts.Token), _stoppingCts.Token);
            }
        }
        
        // Helper for initializing files on start (used by both service auto-start and UI start)
        private async Task InitializeAndProcessFilesOnStartAsync(CancellationToken cancellationToken)
        {
             // Immediately get the current day's log file and notify the UI
            string currentLogFile = GetCurrentLogFilePath();
            if (File.Exists(currentLogFile))
            {
                CurrentFile = Path.GetFileName(currentLogFile);
                long position = await _positionManager.GetPositionAsync(currentLogFile);
                CurrentPosition = position;
                _logger.LogInformation("Initial state - Current file: {CurrentFile}, position: {CurrentPosition}", CurrentFile, CurrentPosition);
                ProcessingStatusChanged?.Invoke(CurrentFile, 0, CurrentPosition);
            }
            else
            {
                string[] matchingFiles = GetMatchingLogFiles();
                if (matchingFiles.Length > 0)
                {
                    string? mostRecentFile = matchingFiles
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .FirstOrDefault();
                        
                    if (mostRecentFile != null)
                    {
                        CurrentFile = Path.GetFileName(mostRecentFile);
                        long position = await _positionManager.GetPositionAsync(mostRecentFile);
                        CurrentPosition = position;
                        _logger.LogInformation("Initial state - Most recent file: {CurrentFile}, position: {CurrentPosition}", CurrentFile, CurrentPosition);
                        ProcessingStatusChanged?.Invoke(CurrentFile, 0, CurrentPosition);
                    }
                    else { ProcessingStatusChanged?.Invoke("Waiting for log files...", 0, 0); }
                }
                else { ProcessingStatusChanged?.Invoke("Waiting for log files...", 0, 0); }
            }
            
            // Process any existing files
            if (!cancellationToken.IsCancellationRequested)
            {
                await ProcessExistingFilesAsync(cancellationToken);
            }
        }

        // New method for UI-initiated stop
        public void UIManagedStopProcessing()
        {
            if (_isRunningAsHostedService)
            {
                _logger.LogWarning("Application is running as a hosted service. UI cannot directly stop processing. Use IPC (not yet implemented).");
                NotifyError("Service Control", "Cannot stop from UI when running as a dedicated service. Control via service management or IPC.");
                return;
            }

            if (!IsProcessing)
            {
                _logger.LogWarning("Processing is not currently active (UI mode), ignoring stop request.");
                return;
            }

            IsProcessing = false;
            _logger.LogInformation("Log file processing stopped by UI. IsProcessing: {IsProcessing}", IsProcessing);
            
            ResetPositionInfo();
        }

        private string GetOperationalStateString()
        {
            if (IsProcessing)
            {
                return "Processing";
            }
            else if (!string.IsNullOrEmpty(_lastProcessingError))
            {
                return "Error";
            }
            else
            {
                return "Idle";
            }
        }
    }
    
    /// <summary>
    /// Settings for the log monitor service
    /// </summary>
    public class LogMonitorSettings
    {
        public string BaseDirectory { get; set; } = string.Empty;
        public string LogFilePattern { get; set; } = "orfee-{Date:yyyy-MM-dd}.log";
        public int PollingIntervalSeconds { get; set; } = 5;
    }
} 
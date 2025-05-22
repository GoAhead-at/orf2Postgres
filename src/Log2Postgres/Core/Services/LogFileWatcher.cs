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
using System.Security.AccessControl;
using System.Security.Principal;

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
        private StreamWriter? _ipcClientStreamWriter;
        private readonly object _ipcClientWriterLock = new object();
        private readonly List<string> _bufferedLogEntries = new List<string>();
        private TaskCompletionSource<bool> _ipcServerReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly ILogger<LogFileWatcher> _logger;
        private readonly OrfLogParser _logParser;
        private readonly PositionManager _positionManager = null!;
        private readonly PostgresService _postgresService;
        private readonly IOptionsMonitor<LogMonitorSettings> _optionsMonitor;
        private LogMonitorSettings CurrentSettings => _optionsMonitor.CurrentValue;
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
        public string CurrentDirectory => CurrentSettings.BaseDirectory;
        public string CurrentPattern => CurrentSettings.LogFilePattern;
        
        private CancellationTokenSource _stoppingCts = new();
        private FileSystemWatcher? _fileSystemWatcher;
        
        // We'll use this to track the processing state in a thread-safe way
        private volatile int _isProcessingFile = 0;
        
        // This is for the UI to show if we're in processing mode
        public bool IsProcessing { get; private set; } = false;
        private string? _lastProcessingError = null;
        
        private readonly List<string> _pendingFiles = new();

        private PipeSecurity CreatePipeSecurity()
        {
            PipeSecurity pipeSecurity = new PipeSecurity();
            // Allow Authenticated Users to connect, read, and write.
            // This is more permissive than InteractiveSid and usually works better for UI <-> Service.
            SecurityIdentifier authenticatedUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            pipeSecurity.AddAccessRule(new PipeAccessRule(authenticatedUsersSid, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            
            // Set the owner to NetworkService if the service runs as NetworkService (often default for new pipes by NS)
            // Or set to Administrators for broader control during development/if UI runs elevated.
            // For simplicity and general use, Authenticated Users should suffice for connectivity.
            // If running service as NetworkService, it will be the owner.
            // SecurityIdentifier owner = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            // pipeSecurity.SetOwner(owner);

            _logger.LogDebug("PipeSecurity created for Authenticated Users (ReadWrite, CreateNewInstance).");
            return pipeSecurity;
        }
        
        public LogFileWatcher(
            ILogger<LogFileWatcher> logger,
            OrfLogParser logParser,
            PositionManager positionManager,
            PostgresService postgresService,
            IOptionsMonitor<LogMonitorSettings> optionsMonitor,
            IHostApplicationLifetime appLifetime,
            IOptions<WindowsServiceSettings> serviceSettingsOptions)
        {
            _logger = logger;
            _logParser = logParser;
            _positionManager = positionManager;
            _postgresService = postgresService;
            _optionsMonitor = optionsMonitor;
            _optionsMonitor.OnChange(newSettings => {
                _logger.LogInformation("IOptionsMonitor.OnChange detected new LogMonitorSettings: BaseDirectory='{NewBaseDir}', Pattern='{NewPattern}', Interval={NewInterval}",
                    newSettings.BaseDirectory, newSettings.LogFilePattern, newSettings.PollingIntervalSeconds);
                // Potentially re-evaluate FileSystemWatcher or other dependent components here
                // For now, just logging the change is sufficient for diagnostics.
                // If BaseDirectory changes, SetupFileSystemWatcherAsync should be called.
                // Let's check if the new settings differ from what CurrentSettings *was* (before this OnChange fired and updated CurrentValue implicitly)
                // This is a bit tricky because CurrentValue is already the new value when OnChange fires.
                // We need to compare with the state *before* this OnChange event.
                // A simple way is to see if the FileSystemWatcher needs resetting.
                if (_fileSystemWatcher == null || !Path.Equals(_fileSystemWatcher.Path, newSettings.BaseDirectory))
                { // Ensure case-insensitivity for paths if needed, Path.Equals might not be sufficient for all OS.
                    _logger.LogInformation("IOptionsMonitor.OnChange: BaseDirectory changed ('{OldPath}' -> '{NewPath}') or FileSystemWatcher not initialized. Re-initializing FileSystemWatcher.", _fileSystemWatcher?.Path ?? "null", newSettings.BaseDirectory);
                    SetupFileSystemWatcherAsync(); // Uses CurrentSettings which is now newSettings
                }
            });
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
            _logger.LogInformation("LogFileWatcher Constructor: PositionManager reports positions file path: {PositionsPath}", _positionManager?.PositionsFilePathForDiagnostics);
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
                _logger.LogInformation("Running as a a hosted service. Automatically starting processing via internal StartProcessingAsync().");
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
            if (string.IsNullOrWhiteSpace(CurrentSettings.BaseDirectory))
            {
                _logger.LogDebug("Skipping file system watcher setup - BaseDirectory not configured");
                return;
            }
            
            if (!Directory.Exists(CurrentSettings.BaseDirectory))
            {
                _logger.LogWarning("Directory {Directory} does not exist, cannot set up file watcher", CurrentSettings.BaseDirectory);
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
                
                _logger.LogDebug("Setting up file system watcher for directory {Directory}", CurrentSettings.BaseDirectory);
                _fileSystemWatcher = new FileSystemWatcher(CurrentSettings.BaseDirectory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    Filter = "*.log",
                    EnableRaisingEvents = true
                };
                
                _fileSystemWatcher.Changed += OnFileChanged;
                _fileSystemWatcher.Created += OnFileCreated;
                
                _logger.LogInformation("File watcher set up for directory {Directory}", CurrentSettings.BaseDirectory);
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
                CurrentSettings.PollingIntervalSeconds, IsProcessing, _isRunningAsHostedService);
            
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
                    await Task.Delay(TimeSpan.FromSeconds(CurrentSettings.PollingIntervalSeconds), cancellationToken);
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
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, CurrentSettings.PollingIntervalSeconds)), cancellationToken);
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
                        filePath, CurrentSettings.LogFilePattern);
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
                    
                    _logger.LogDebug("Notifying UI of processing status update (pre-DB save)");
                    ProcessingStatusChanged?.Invoke(CurrentFile, entries.Count, newPosition); 
                    EntriesProcessed?.Invoke(entries); 
                    await SendLogEntriesToIpcClientAsync(entries);

                    try
                    {
                        _logger.LogDebug("Saving {Count} log entries to database", entries.Count);
                        int savedEntries = await _postgresService.SaveEntriesAsync(entries, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        EntriesSavedToDb += savedEntries;
                        
                        if (savedEntries == entries.Count)
                        {
                            _logger.LogDebug("Successfully saved all {Count} entries to database. Updating position.", savedEntries);
                            // Update position only after successful save
                            CurrentPosition = newPosition;
                            LastProcessedTime = DateTime.Now;
                            await _positionManager.UpdatePositionAsync(filePath, newPosition, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            _logger.LogInformation("Processed and saved {Count} entries from {FilePath}", entries.Count, filePath);
                            await SendCurrentStatusToIpcClientAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else if (savedEntries > 0)
                        {
                             _logger.LogWarning("Only {SavedCount} of {TotalCount} entries were saved to database (some may be duplicates or other issues). Position NOT updated.",
                                savedEntries, entries.Count);
                             NotifyError("Database", $"Partial save: {savedEntries}/{entries.Count} entries from {Path.GetFileName(filePath)}. Log position not updated.");
                             // Decide if CurrentPosition and LastProcessedTime should be updated here
                             // For now, they are not, as the position file isn't updated.
                        }
                        else // savedEntries == 0 and no exception
                        {
                            _logger.LogWarning("No entries were saved to database from {FilePath} (count: {TotalCount}). Position NOT updated.", filePath, entries.Count);
                            NotifyError("Database", $"Zero entries saved from {Path.GetFileName(filePath)}. Log position not updated.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error saving entries to database: {Message}. Position NOT updated.", ex.Message);
                        NotifyError("Database", $"Failed to save entries from {Path.GetFileName(filePath)}: {ex.Message}. Log position not updated.");
                        // Position will not be updated due to the exception.
                    }
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
                await SendCurrentStatusToIpcClientAsync(CancellationToken.None).ConfigureAwait(false);
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
                return Directory.GetFiles(CurrentSettings.BaseDirectory, "*.log")
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
            string pattern = CurrentSettings.LogFilePattern;
            
            // Replace {Date:format} with a regex pattern that matches dates
            // Create a pattern that matches digits and dashes in place of the date format
            string dateRegexPattern = @"\d{4}-\d{2}-\d{2}"; // yyyy-MM-dd format
            pattern = Regex.Replace(pattern, @"\{Date:[^}]+\}", dateRegexPattern);
            
            // Escape special regex characters except the ones we already handled
            string regexPattern = "^" + Regex.Escape(pattern).Replace(Regex.Escape(dateRegexPattern), dateRegexPattern) + "$";
            
            bool isMatch = Regex.IsMatch(filename, regexPattern);
            _logger.LogDebug("File pattern match check - Filename: {Filename}, Pattern: {Pattern}, RegexPattern: {RegexPattern}, IsMatch: {IsMatch}", 
                filename, CurrentSettings.LogFilePattern, regexPattern, isMatch);
            
            return isMatch;
        }
        
        /// <summary>
        /// Get the path for today's log file based on the configured pattern
        /// </summary>
        private string GetCurrentLogFilePath()
        {
            // Replace {Date:format} with the actual date
            string filename = Regex.Replace(
                CurrentSettings.LogFilePattern,
                @"\{Date:([^}]+)\}",
                match =>
                {
                    string format = match.Groups[1].Value;
                    return DateTime.Now.ToString(format);
                });
                
            return Path.Combine(CurrentSettings.BaseDirectory, filename);
        }
        
        /// <summary>
        /// Handles IPC requests from the UI.
        /// </summary>
        private async Task RunIpcServerAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting IPC server on pipe '{PipeName}'...", PipeName);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Corrected NamedPipeServerStream creation using NamedPipeServerStreamAcl.Create
                    NamedPipeServerStream serverStream = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        0, // Default in buffer size (usually 4096)
                        0, // Default out buffer size (usually 4096)
                        CreatePipeSecurity() // Apply PipeSecurity
                    );

                    _logger.LogDebug("IPC Server: Pipe stream created via ACL method. Waiting for a client connection...");
                    await serverStream.WaitForConnectionAsync(cancellationToken);
                    _logger.LogInformation("IPC Server: Client connected.");
                    _ipcServerReadySignal.TrySetResult(true); // Signal that server is ready and client connected

                    // Client connected, handle communication
                    _ipcClientStreamWriter = new StreamWriter(serverStream, Encoding.UTF8) { AutoFlush = true };
                    var clientReader = new StreamReader(serverStream, Encoding.UTF8);

                    try
                    {
                        while (!cancellationToken.IsCancellationRequested && serverStream.IsConnected)
                        {
                            string? messageJson = await clientReader.ReadLineAsync(cancellationToken);
                            if (messageJson == null)
                            {
                                _logger.LogInformation("IPC Server: Client disconnected (null received).");
                                break; // Client disconnected
                            }
                            // Restored call to ProcessIpcMessageAsync
                            await ProcessIpcMessageAsync(messageJson, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("IPC Server: Operation cancelled during client communication.");
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "IPC Server: IOException during client communication (e.g., pipe broken).");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "IPC Server: Error during client communication.");
                    }
                    finally
                    {
                        _logger.LogInformation("IPC Server: Cleaning up client connection.");
                        lock (_ipcClientWriterLock)
                        {
                            _ipcClientStreamWriter?.Dispose();
                            _ipcClientStreamWriter = null;
                        }
                        clientReader.Dispose();
                        if (serverStream.IsConnected) // Should be false if broken, but good practice
                        {
                            try { serverStream.Disconnect(); } catch (Exception exDisc) { _logger.LogWarning(exDisc, "IPC Server: Exception during serverStream.Disconnect()."); }
                        }
                        serverStream.Dispose();
                        _logger.LogInformation("IPC Server: Client connection resources released.");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("IPC Server: Cancellation requested after client handling, breaking server loop.");
                        _ipcServerReadySignal.TrySetCanceled(cancellationToken); // Reflect cancellation if it occurs before connection
                        break;
                    }
                    // After client disconnects, loop to wait for a new connection
                    _logger.LogInformation("IPC Server: Client disconnected, looping to wait for new connection.");
                    // Reset the ready signal for the next connection if UIManagedStartProcessingAsync can be called multiple times
                    // or if it's intended to signal each client connection. For now, it signals first successful server setup.
                    // If _ipcServerReadySignal is awaited by UIManagedStartProcessingAsync only once per start, 
                    // then this TrySetResult(true) is for that initial setup. Subsequent connections won't re-trigger it unless reset.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("IPC server task cancelled.");
                _ipcServerReadySignal.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in IPC server task.");
                _ipcServerReadySignal.TrySetException(ex); 
            }
            finally
            {
                _logger.LogInformation("IPC server task shutting down.");
            }
        }

        // Restored ProcessIpcMessageAsync method
        private async Task ProcessIpcMessageAsync(string messageJson, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("IPC Server: Received message: {MessageJson}", messageJson);
                var baseMessage = JsonConvert.DeserializeObject<IpcMessage<object>>(messageJson);

                if (baseMessage == null || string.IsNullOrEmpty(baseMessage.Type))
                {
                    _logger.LogWarning("IPC Server: Received an invalid or untyped IPC message: {MessageJson}", messageJson);
                    return;
                }

                _logger.LogDebug("IPC Server: Processing command of Type: {CommandType}", baseMessage.Type);

                switch (baseMessage.Type)
                {
                    case IpcMessageTypes.RequestState:
                        _logger.LogDebug("IPC Server: Handling '{RequestStateType}' command.", IpcMessageTypes.RequestState);
                        var statusPayload = new PipeServiceStatus
                        {
                            ServiceOperationalState = GetOperationalStateString(),
                            IsProcessing = this.IsProcessing,
                            CurrentFile = this.CurrentFile,
                            CurrentPosition = this.CurrentPosition,
                            TotalLinesProcessedSinceStart = this.TotalLinesProcessed,
                            LastErrorMessage = _lastProcessingError ?? string.Empty
                        };
                        var responseMessage = new IpcMessage<PipeServiceStatus> { Type = IpcMessageTypes.StatusUpdate, Payload = statusPayload };
                        string jsonResponse = JsonConvert.SerializeObject(responseMessage);
                         if (_ipcClientStreamWriter != null)
                        {
                            await _ipcClientStreamWriter.WriteLineAsync(jsonResponse.AsMemory(), cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("IPC Server: Sent '{StatusUpdateType}' response to client: {JsonResponse}", IpcMessageTypes.StatusUpdate, jsonResponse);
                        }
                        else
                        {
                            _logger.LogWarning("IPC Server: _ipcClientStreamWriter is null, cannot send '{StatusUpdateType}' response.", IpcMessageTypes.StatusUpdate);
                        }
                        break;

                    case "UpdateSettings":
                        _logger.LogDebug("IPC Server: Handling 'UpdateSettings' command.");
                        if (baseMessage.Payload != null)
                        {
                            try
                            {
                                var settings = JsonConvert.DeserializeObject<LogMonitorSettings>(baseMessage.Payload.ToString() ?? string.Empty);
                                if (settings != null)
                                {
                                    _logger.LogInformation("IPC Server: Received UpdateSettings request. Applying new settings.");
                                    await UpdateSettingsAsync(settings); // Assuming this method exists and works
                                    // Acknowledge the settings update
                                     if (_ipcClientStreamWriter != null)
                                    {
                                        var ackPayload = new { Status = "OK", Message = "Settings updated successfully." };
                                        var ackResponseMessage = new IpcMessage<object> { Type = "SettingsUpdateAck", Payload = ackPayload };
                                        await _ipcClientStreamWriter.WriteLineAsync(JsonConvert.SerializeObject(ackResponseMessage).AsMemory(), cancellationToken);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("IPC Server: Failed to deserialize LogMonitorSettings from UpdateSettings command payload.");
                                     if (_ipcClientStreamWriter != null)
                                    {
                                        var errPayload = new { Status = "Error", Message = "Failed to deserialize settings." };
                                        var errResponseMessage = new IpcMessage<object> { Type = "SettingsUpdateNack", Payload = errPayload };
                                        await _ipcClientStreamWriter.WriteLineAsync(JsonConvert.SerializeObject(errResponseMessage).AsMemory(), cancellationToken);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "IPC Server: Error processing UpdateSettings command.");
                                if (_ipcClientStreamWriter != null)
                                {
                                    var errPayload = new { Status = "Error", Message = $"Error processing settings: {ex.Message}" };
                                    var errResponseMessage = new IpcMessage<object> { Type = "SettingsUpdateNack", Payload = errPayload };
                                    await _ipcClientStreamWriter.WriteLineAsync(JsonConvert.SerializeObject(errResponseMessage).AsMemory(), cancellationToken);
                                }
                            }
                        }
                        else
                        {
                             _logger.LogWarning("IPC Server: 'UpdateSettings' command received with null payload.");
                             if (_ipcClientStreamWriter != null)
                             {
                                var errPayload = new { Status = "Error", Message = "UpdateSettings payload was null." };
                                var errResponseMessage = new IpcMessage<object> { Type = "SettingsUpdateNack", Payload = errPayload };
                                await _ipcClientStreamWriter.WriteLineAsync(JsonConvert.SerializeObject(errResponseMessage).AsMemory(), cancellationToken);
                             }
                        }
                        break;
                    
                    default:
                        _logger.LogWarning("IPC Server: Unknown or unhandled command type received: {MessageType} from message: {RawMessage}", baseMessage.Type, messageJson);
                        break;
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "IPC Server: JSON deserialization error in ProcessIpcMessageAsync for message: {MessageJson}", messageJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC Server: Error processing IPC message: {MessageJson}", messageJson);
            }
        }

        private async Task SendCurrentStatusToIpcClientAsync(CancellationToken cancellationToken = default)
        {
            if (_ipcClientStreamWriter == null)
            {
                _logger.LogTrace("SendCurrentStatusToIpcClientAsync: No IPC client connected, cannot send status.");
                return;
            }

            // Use the shared PipeServiceStatus model from Log2Postgres.Core.Services
            var statusPayload = new PipeServiceStatus
            {
                ServiceOperationalState = GetOperationalStateString(),
                IsProcessing = this.IsProcessing,
                CurrentFile = this.CurrentFile,
                CurrentPosition = this.CurrentPosition,
                TotalLinesProcessedSinceStart = this.TotalLinesProcessed,
                LastErrorMessage = _lastProcessingError ?? string.Empty
            };

            var message = new IpcMessage<PipeServiceStatus> { Type = IpcMessageTypes.StatusUpdate, Payload = statusPayload };
            string jsonMessage = JsonConvert.SerializeObject(message);

            try
            {
                _logger.LogDebug("IPC Server: Proactively sending '{StatusUpdateType}' to client: {JsonMessage}", IpcMessageTypes.StatusUpdate, jsonMessage);
                await _ipcClientStreamWriter.WriteLineAsync(jsonMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ioEx)
            {
                _logger.LogWarning(ioEx, "IPC Server: IOException proactively sending status update. Client may have disconnected.");
                // Consider nulling out _ipcClientStreamWriter or other cleanup if pipe is broken.
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("IPC Server: Proactive status update send was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC Server: Error proactively sending status update.");
            }
        }

        private async Task SendLogEntriesToIpcClientAsync(IEnumerable<OrfLogEntry> entries)
        {
            var logMessages = entries.Select(e => e.EventMsg ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (!logMessages.Any())
            {
                _logger.LogDebug("IPC SendLogEntries: No valid log messages to send after filtering.");
                return;
            }

            StreamWriter? writer;
            bool clientConnectedThisCall;
            lock (_ipcClientWriterLock)
            {
                writer = _ipcClientStreamWriter;
                clientConnectedThisCall = writer != null;

                if (!clientConnectedThisCall)
                {
                    _logger.LogDebug("IPC SendLogEntries: No client connected, buffering {Count} log entries.", logMessages.Count);
                    _bufferedLogEntries.AddRange(logMessages);
                    // Optional: Limit buffer size if necessary
                    // if (_bufferedLogEntries.Count > MAX_BUFFER_SIZE) { /* trim buffer */ }
                    return; // Buffered, so exit
                }
            }
            _logger.LogDebug("IPC SendLogEntries: Entered method. Writer is {WriterStatus}.", writer == null ? "NULL" : "NOT NULL");

            // If we reach here, client was connected at the start of the call (writer != null)
            // No need to re-check writer == null directly, but operations might fail if it disconnects.
            
            List<string> messagesToSend = new List<string>(); // Declare messagesToSend here

            try
            {
                // Attempt to send buffered messages first, if any.
                // This part needs to be careful about re-acquiring lock or ensuring writer is still valid.
                // For simplicity, we'll assume if writer was good at start, it's good for this batch.
                // A more robust solution might involve a sending queue processed by a dedicated task.

                if (_bufferedLogEntries.Any())
                {
                    lock(_ipcClientWriterLock) // Lock when accessing shared buffer
                    {
                        if(_ipcClientStreamWriter == writer) // Ensure writer is still the same one we latched
                        {
                             messagesToSend.AddRange(_bufferedLogEntries);
                            _bufferedLogEntries.Clear();
                            _logger.LogInformation("IPC SendLogEntries: Sending {Count} previously buffered log entries.", messagesToSend.Count);
                        }
                        else // Writer changed or became null, re-buffer
                        {
                            _logger.LogWarning("IPC SendLogEntries: Client writer changed while trying to send buffered logs. Re-buffering all.");
                            _bufferedLogEntries.InsertRange(0, messagesToSend); // Put them back at the start
                            _bufferedLogEntries.AddRange(logMessages); // Add current messages too
                            return; // Exit, will try again later
                        }
                    }
                }
                messagesToSend.AddRange(logMessages); // Add current batch

                if (!messagesToSend.Any())
                {
                    _logger.LogDebug("IPC SendLogEntries: No messages to send (current or buffered).");
                    return;
                }

                var ipcMessage = new IpcMessage<List<string>> { Type = "LOG_ENTRIES", Payload = messagesToSend };
                string jsonMessage = JsonConvert.SerializeObject(ipcMessage);
                _logger.LogDebug("IPC SendLogEntries: Attempting to send JSON for {Count} total entries: {JsonMessage}", messagesToSend.Count, jsonMessage);
                
                // The 'writer' variable was captured outside the lock specific to buffered entries.
                // Re-evaluate or ensure its validity if sending takes long or involves yielding.
                // For now, assume it's still valid if we got this far.
                await writer!.WriteLineAsync(jsonMessage.AsMemory(), _stoppingCts.Token); 
                _logger.LogInformation("IPC: Sent {Count} total log entries to client. JSON: {JsonMessage}", messagesToSend.Count, jsonMessage);
            }
            catch (IOException ioEx) 
            {
                _logger.LogWarning(ioEx, "IPC SendLogEntries: IOException sending log entries. Nulling out writer and re-buffering if applicable.");
                // Re-buffer unsent messages if possible (messagesToSend still holds them)
                lock (_ipcClientWriterLock) 
                { 
                    if (_ipcClientStreamWriter == writer) _ipcClientStreamWriter = null; 
                    _bufferedLogEntries.InsertRange(0, messagesToSend); // Put them back
                }
            }
            catch (OperationCanceledException) 
            { 
                _logger.LogInformation("IPC SendLogEntries: Operation cancelled sending log entries."); 
                // Re-buffer unsent messages
                lock (_ipcClientWriterLock) { _bufferedLogEntries.InsertRange(0, messagesToSend); }
            }
            catch (Exception ex) 
            { 
                _logger.LogError(ex, "IPC SendLogEntries: Error sending log entries. Re-buffering. Writer state unchanged unless it was the cause."); 
                lock (_ipcClientWriterLock) { _bufferedLogEntries.InsertRange(0, messagesToSend); }
            }
        }

        // New method to flush buffered log entries
        private async Task FlushBufferedLogEntriesAsync(StreamWriter writer)
        {
            List<string> entriesToFlush = new List<string>();
            lock (_ipcClientWriterLock)
            {
                // Ensure the provided writer is still the active one
                if (_ipcClientStreamWriter == writer && _bufferedLogEntries.Any())
                {
                    entriesToFlush.AddRange(_bufferedLogEntries);
                    _bufferedLogEntries.Clear();
                    _logger.LogInformation("IPC FlushBuffered: Preparing to flush {Count} buffered log entries.", entriesToFlush.Count);
                }
                else if (_ipcClientStreamWriter != writer)
                {
                    _logger.LogWarning("IPC FlushBuffered: Active writer changed, cannot flush with stale writer. Entries remain buffered.");
                    return;
                }
                else
                {
                     _logger.LogDebug("IPC FlushBuffered: No buffered entries to flush.");
                    return;
                }
            }

            if (entriesToFlush.Any())
            {
                try
                {
                    var ipcMessage = new IpcMessage<List<string>> { Type = "LOG_ENTRIES", Payload = entriesToFlush };
                    string jsonMessage = JsonConvert.SerializeObject(ipcMessage);
                    _logger.LogDebug("IPC FlushBuffered: Attempting to send JSON for {Count} entries: {JsonMessage}", entriesToFlush.Count, jsonMessage);
                    await writer.WriteLineAsync(jsonMessage.AsMemory(), _stoppingCts.Token);
                    _logger.LogInformation("IPC FlushBuffered: Flushed {Count} buffered log entries to client.", entriesToFlush.Count);
                }
                catch (IOException ioEx)
                {
                    _logger.LogWarning(ioEx, "IPC FlushBuffered: IOException flushing entries. Re-buffering.");
                    lock (_ipcClientWriterLock) { _bufferedLogEntries.InsertRange(0, entriesToFlush); } // Put them back
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("IPC FlushBuffered: Operation cancelled. Re-buffering.");
                    lock (_ipcClientWriterLock) { _bufferedLogEntries.InsertRange(0, entriesToFlush); }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IPC FlushBuffered: Error flushing entries. Re-buffering.");
                    lock (_ipcClientWriterLock) { _bufferedLogEntries.InsertRange(0, entriesToFlush); }
                }
            }
        }

        private class IpcMessage<T>
        {
            public string Type { get; set; } = string.Empty;
            public T? Payload { get; set; }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("LogFileWatcher Service StopAsync CALLED. Current time: {Time}", DateTime.UtcNow);

            _logger.LogInformation("StopAsync: Cancelling _applicationStopping (internal _stoppingCts). Current time: {Time}", DateTime.UtcNow);
            if (!_stoppingCts.IsCancellationRequested) _stoppingCts.Cancel(); // Ensure cancel only once
            _logger.LogInformation("StopAsync: _applicationStopping cancellation requested. IsCancellationRequested: {IsCancelled}. Current time: {Time}", _stoppingCts.IsCancellationRequested, DateTime.UtcNow);

            // Set IsProcessing to false before sending final status
            IsProcessing = false; 
            await SendCurrentStatusToIpcClientAsync(CancellationToken.None).ConfigureAwait(false);

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
                _logger.LogDebug("StopAsync: Disposing FileSystemWatcher. Current time: {Time}", DateTime.UtcNow);
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
            _logger.LogInformation("LogFileWatcher.UpdateSettingsAsync called. This method should typically be used for IPC/programmatic updates.");
            _logger.LogInformation("New settings received - BaseDirectory: '{NewBaseDir}', Pattern: '{NewPattern}', Interval: {NewInterval}", 
                newSettings.BaseDirectory, newSettings.LogFilePattern, newSettings.PollingIntervalSeconds);

            string oldBaseDirectory = CurrentSettings.BaseDirectory;
            bool oldIsProcessing = IsProcessing; // Capture state before potential implicit changes by monitor

            // It's tricky to "apply" newSettings directly to the IOptionsMonitor flow.
            // The correct way for IOptionsMonitor to update is via changes to the underlying configuration source (appsettings.json).
            // If this UpdateSettingsAsync is called (e.g., from MainWindow's ReloadConfiguration after a save),
            // appsettings.json has ALREADY been updated. IOptionsMonitor should pick that up.
            // We just need to react to potential changes, especially for the FileSystemWatcher.
            // Let's log what the monitor currently sees.
            _logger.LogInformation("CurrentSettings via IOptionsMonitor before potential re-evaluation - BaseDir: '{MonitorBaseDir}', Pattern: '{MonitorPattern}', Interval: {MonitorInterval}",
                CurrentSettings.BaseDirectory, CurrentSettings.LogFilePattern, CurrentSettings.PollingIntervalSeconds);

            // The actual _settings instance backing CurrentSettings is managed by the IOptions system.
            // We should not directly assign to its properties here if we expect IOptionsMonitor to work correctly.
            // Instead, we ensure appsettings.json is the source of truth and IOptionsMonitor reflects it.

            // Check if the directory from the new explicit settings matches what the monitor now sees.
            // This helps understand if the monitor has already updated from the file save that likely preceded this call.
            if (!string.Equals(oldBaseDirectory, CurrentSettings.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("BaseDirectory has changed according to IOptionsMonitor. Old: '{OldDir}', New: '{NewDir}'. Re-evaluating FileSystemWatcher.", oldBaseDirectory, CurrentSettings.BaseDirectory);
                SetupFileSystemWatcherAsync(); // Uses CurrentSettings

                // Similar logic as before for starting/stopping processing if directory validity changed
                bool newDirectoryIsValid = !string.IsNullOrWhiteSpace(CurrentSettings.BaseDirectory) && Directory.Exists(CurrentSettings.BaseDirectory);
                if (newDirectoryIsValid)
                {
                    if (!oldIsProcessing) // If it wasn't processing before this entire settings update sequence began
                    {
                        _logger.LogInformation("Valid log directory '{NewDir}' configured via monitor and processing was not active. Attempting to start processing.", CurrentSettings.BaseDirectory);
                        IsProcessing = true; 
                        _lastProcessingError = null; 
                        ErrorOccurred?.Invoke("Configuration", string.Empty);
                        await InitializeAndProcessFilesOnStartAsync(_stoppingCts.Token); // Uses CurrentSettings
                    }
                    else
                    {
                         _logger.LogInformation("Directory changed to '{NewDir}' (via monitor) while processing is active. Re-processing existing files in new directory.", CurrentSettings.BaseDirectory);
                         await ProcessExistingFilesAsync(_stoppingCts.Token); // Uses CurrentSettings
                    }
                }
                else // New directory (from monitor) is invalid
                {
                    _logger.LogWarning("Log directory (from monitor) '{NewDir}' is invalid or cleared. Stopping processing if active.", CurrentSettings.BaseDirectory);
                    if (IsProcessing) 
                    {
                        IsProcessing = false;
                        NotifyError("Configuration", "Log directory became invalid or was cleared. Processing stopped.");
                    }
                }
            }
            else
            {
                _logger.LogInformation("BaseDirectory has NOT changed according to IOptionsMonitor compared to before UpdateSettingsAsync call. Current monitor dir: '{MonitorDir}'", CurrentSettings.BaseDirectory);
            }
             // Polling interval will be picked up by PollingLoopAsync naturally from CurrentSettings.PollingIntervalSeconds
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
            _logger.LogInformation("StartProcessingAsyncInternal called.");

            if (!_isRunningAsHostedService && IsProcessing) 
            {
                _logger.LogInformation("StartProcessingAsyncInternal: UI-Managed mode and already processing. No action needed.");
                return;
            }
            if (_isRunningAsHostedService && IsProcessing)
            {
                _logger.LogWarning("StartProcessingAsyncInternal: Hosted service mode and already processing. This might indicate a logic issue or re-entrant call. CurrentFile: {CurrentFile}", CurrentFile);
                // Depending on desired behavior, might return or reset some state.
                // For now, allow to proceed, which might re-process or continue existing.
            }

            // Ensure PositionManager is initialized and loads positions from file
            if (_positionManager != null)
            {
                _logger.LogInformation("StartProcessingAsyncInternal: Initializing PositionManager...");
                try
                {
                    await _positionManager.InitializeAsync(_stoppingCts.Token).ConfigureAwait(false);
                    _logger.LogInformation("StartProcessingAsyncInternal: PositionManager initialized successfully.");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("StartProcessingAsyncInternal: PositionManager initialization was cancelled.");
                    IsProcessing = false; // Stop if we can't initialize positions
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StartProcessingAsyncInternal: Error initializing PositionManager. File positions may not be loaded correctly.");
                    NotifyError("PositionManagerInit", $"Failed to initialize positions: {ex.Message}");
                    IsProcessing = false; // Stop if we can't initialize positions
                    return;
                }
            }
            else
            {
                _logger.LogError("StartProcessingAsyncInternal: _positionManager is null. Cannot initialize or use positions.");
                NotifyError("CriticalError", "PositionManager is not available. File processing cannot proceed correctly.");
                IsProcessing = false;
                return;
            }

            IsProcessing = true;
            _lastProcessingError = null;
            _logger.LogInformation("Processing started. IsProcessing: {IsProcessing}", IsProcessing);

            await InitializeAndProcessFilesOnStartAsync(_stoppingCts.Token);
            
            _logger.LogInformation("Initial file processing complete (if any files were found). Polling loop will continue if service is running.");
        }

        // New method for UI-initiated start
        public async Task UIManagedStartProcessingAsync()
        {
            _logger.LogInformation("UIManagedStartProcessingAsync called.");

            if (_isRunningAsHostedService)
            {
                _logger.LogWarning("UIManagedStartProcessingAsync called, but LogFileWatcher is configured to run as a hosted service. UI should not manage it directly.");
                NotifyError("Startup", "Service is configured to run as a Windows Service. UI cannot start local processing.");
                return;
            }

            if (IsProcessing)
            {
                _logger.LogWarning("UIManagedStartProcessingAsync: Processing is already active.");
                return;
            }

            // Reset the ready signal for this start attempt - this might still be useful internally for the IPC server
            _ipcServerReadySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _logger.LogInformation("UIManagedStartProcessingAsync: Performing initial setup...");
            // Ensure _stoppingCts is fresh if this can be called multiple times after stops
            if (_stoppingCts.IsCancellationRequested)
            {
                _logger.LogInformation("UIManagedStartProcessingAsync: Previous _stoppingCts was cancelled. Creating a new one.");
                _stoppingCts = new CancellationTokenSource();
            }
            await InitialSetupAsync(_stoppingCts.Token); 

            if (_stoppingCts.IsCancellationRequested)
            {
                _logger.LogWarning("UIManagedStartProcessingAsync: Cancellation requested during initial setup.");
                return;
            }

            _logger.LogInformation("UIManagedStartProcessingAsync: Starting internal processing logic...");
            // StartProcessingAsyncInternal also awaits InitializeAndProcessFilesOnStartAsync,
            // which can take time. Consider if this needs to be offloaded if it blocks UI too long.
            // For now, let's assume it's acceptable or that its internal awaits are well-behaved.
            await StartProcessingAsyncInternal(); 

            if (!IsProcessing)
            {
                _logger.LogError("UIManagedStartProcessingAsync: StartProcessingAsyncInternal did not result in IsProcessing being true. Aborting IPC server start.");
                NotifyError("Startup", "Failed to start internal processing components.");
                return;
            }

            _logger.LogInformation("UIManagedStartProcessingAsync: Starting IPC server task.");
            // Ensure _ipcServerCts is fresh
            _ipcServerCts?.Cancel(); // Cancel previous if any
            _ipcServerCts = new CancellationTokenSource();
            _ipcServerTask = Task.Run(() => RunIpcServerAsync(_ipcServerCts.Token), _ipcServerCts.Token);

            // DO NOT await _ipcServerReadySignal.Task here.
            // The UI will attempt to connect via IpcService and ServiceStatusTimer_Tick.
            // The IPC server will become "ready" when a client connects.
            _logger.LogInformation("UIManagedStartProcessingAsync: IPC server started in background. UI will connect separately. IsProcessing: {IsProcessingState}", IsProcessing);
            // The method now returns quickly. MainWindow should update its UI based on IsProcessing
            // and the IpcService connection status.
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
            await SendCurrentStatusToIpcClientAsync(cancellationToken).ConfigureAwait(false);
        }

        // New method for UI-initiated stop
        public void UIManagedStopProcessing()
        {
            _logger.LogInformation("UIManagedStopProcessing called.");
            if (_isRunningAsHostedService)
            {
                _logger.LogWarning("UIManagedStopProcessing called, but LogFileWatcher is configured to run as a hosted service. UI should not manage it directly.");
                return;
            }

            if (!IsProcessing && (_ipcServerTask == null || _ipcServerTask.IsCompleted))
            {
                _logger.LogWarning("UIManagedStopProcessing: Processing and IPC server are not active or already stopped.");
                // Ensure IsProcessing is false if we reach here unexpectedly
                if (IsProcessing) IsProcessing = false;
                return;
            }

            _logger.LogInformation("UIManagedStopProcessing: Stopping internal processing...");
            if (!_stoppingCts.IsCancellationRequested)
            {
                _stoppingCts.Cancel(); // Signal polling loop and other internal processes to stop
            }
            // Note: StartProcessingAsyncInternal uses _stoppingCts.Token, so this should halt its operations.
            // FileSystemWatcher is disposed by StopAsync if it's a full service stop, or should be handled if UIManaged stop needs it.
            // For now, FSW is managed by SetupFileSystemWatcherAsync and disposed/recreated there or during full StopAsync.

            if (_ipcServerCts != null && !_ipcServerCts.IsCancellationRequested)
            {
                _logger.LogInformation("UIManagedStopProcessing: Cancelling IPC server task...");
                _ipcServerCts.Cancel();
            }

            // It might be good to wait for _ipcServerTask to complete, with a timeout.
            // For now, just cancelling. Proper cleanup of the task should occur in RunIpcServerAsync's finally block.

            IsProcessing = false;
            CurrentFile = string.Empty;
            CurrentPosition = 0;
            // TotalLinesProcessed is not reset here, it's a cumulative count for the session/instance.

            _logger.LogInformation("UIManagedStopProcessing: Completed.");
            ProcessingStatusChanged?.Invoke(CurrentFile, TotalLinesProcessed, CurrentPosition); // Notify UI of stopped state
            // Task.Run needed because UIManagedStopProcessing is void
            _ = Task.Run(() => SendCurrentStatusToIpcClientAsync(CancellationToken.None).ConfigureAwait(false));
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
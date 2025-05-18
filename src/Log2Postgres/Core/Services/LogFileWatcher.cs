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

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Service that monitors log files, processes them, and forwards entries to the database
    /// </summary>
    public class LogFileWatcher : BackgroundService
    {
        private readonly ILogger<LogFileWatcher> _logger;
        private readonly OrfLogParser _logParser;
        private readonly PositionManager _positionManager;
        private readonly PostgresService _postgresService;
        private readonly LogMonitorSettings _settings;
        private readonly IHostApplicationLifetime _appLifetime;
        
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
        
        private readonly List<string> _pendingFiles = new();
        
        public LogFileWatcher(
            ILogger<LogFileWatcher> logger,
            OrfLogParser logParser,
            PositionManager positionManager,
            PostgresService postgresService,
            IOptions<LogMonitorSettings> settings,
            IHostApplicationLifetime appLifetime)
        {
            _logger = logger;
            _logParser = logParser;
            _positionManager = positionManager;
            _postgresService = postgresService;
            _settings = settings.Value;
            _appLifetime = appLifetime;
        }
        
        // Helper method to notify UI about errors
        private void NotifyError(string component, string message)
        {
            _logger.LogError("{Component} error: {Message}", component, message);
            ErrorOccurred?.Invoke(component, message);
        }
        
        /// <summary>
        /// Start watching log files and processing them
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LogFileWatcher service starting...");
            
            // Handle case when settings are not configured at startup
            if (string.IsNullOrWhiteSpace(_settings.BaseDirectory))
            {
                _logger.LogInformation("BaseDirectory is not configured yet. Service is waiting for configuration.");
                // Don't return here - just continue and wait for configuration to be set via UpdateSettingsAsync
            }
            else
            {
                // Ensure the directory exists
                if (!Directory.Exists(_settings.BaseDirectory))
                {
                    _logger.LogWarning("Base directory {BaseDirectory} does not exist.", _settings.BaseDirectory);
                }
                else
                {
                    // Initialize file system watcher since we have a valid directory
                    await SetupFileSystemWatcherAsync();
                    
                    // No longer automatically processing existing files on startup
                    // This will be done when StartProcessingAsync is called from the UI
                    _logger.LogInformation("File watching initialized, waiting for user to start processing");
                }
            }
            
            // Ensure the database table exists
            try 
            {
                _logger.LogDebug("Checking database table before starting service");
                bool tableExists = await _postgresService.EnsureTableExistsAsync();
                if (!tableExists)
                {
                    NotifyError("Database", "Failed to ensure database table exists. Service will run but data won't be saved.");
                }
                else
                {
                    _logger.LogDebug("Database table check completed successfully");
                }
            }
            catch (Exception ex)
            {
                NotifyError("Database", $"Error checking database table: {ex.Message}. Service will run but data won't be saved.");
            }
            
            // Start polling loop regardless of directory config - it will just skip processing if no directory is set
            // or if processing hasn't been started by the user
            _logger.LogDebug("Starting background polling task");
            _ = Task.Run(() => PollingLoopAsync(stoppingToken), stoppingToken);
            
            // Wait for the application to stop
            _logger.LogDebug("LogFileWatcher main task now waiting for cancellation");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            
            _logger.LogInformation("LogFileWatcher service stopping...");
        }
        
        /// <summary>
        /// Setup the file system watcher with the current directory settings
        /// </summary>
        private async Task SetupFileSystemWatcherAsync()
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
            _logger.LogInformation("Starting polling loop with interval of {Interval} seconds", 
                _settings.PollingIntervalSeconds);
            
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
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
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
                    long lastPosition = await _positionManager.GetPositionAsync(currentLogFilePath);
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
                        long lastPosition = await _positionManager.GetPositionAsync(filePath);
                        
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
                // Note: We don't set IsProcessing here anymore - that's controlled by StartProcessingAsync/StopProcessing
                CurrentFile = Path.GetFileName(filePath);
                _logger.LogDebug("Processing log file {FilePath}", filePath);
                
                // Verify the file exists and has proper read permissions
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File {FilePath} no longer exists, skipping processing", filePath);
                    return;
                }

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
                
                // Get the last position we read from for this file
                _logger.LogDebug("Retrieving last processing position for {FilePath}", filePath);
                long startPosition = await _positionManager.GetPositionAsync(filePath);
                CurrentPosition = startPosition;
                _logger.LogDebug("Starting from position {Position} in file {FilePath}", startPosition, filePath);
                
                // Parse the log file from that position
                _logger.LogDebug("Parsing log file {FilePath} from position {Position}", filePath, startPosition);
                var (entries, newPosition) = _logParser.ParseLogFile(filePath, startPosition);
                _logger.LogDebug("Parsed {Count} entries from {FilePath}, new position: {NewPosition}", 
                    entries.Count, filePath, newPosition);
                
                if (entries.Count > 0)
                {
                    // Update statistics
                    TotalLinesProcessed += entries.Count;
                    CurrentPosition = newPosition;
                    LastProcessedTime = DateTime.Now;
                    
                    // Save the new position
                    _logger.LogDebug("Updating position tracker for {FilePath} to position {Position}", filePath, newPosition);
                    await _positionManager.UpdatePositionAsync(filePath, newPosition);
                    
                    // Notify listeners
                    _logger.LogDebug("Notifying UI of processing status update");
                    ProcessingStatusChanged?.Invoke(CurrentFile, entries.Count, newPosition);
                    EntriesProcessed?.Invoke(entries);
                    
                    // Save entries to database
                    try
                    {
                        _logger.LogDebug("Saving {Count} log entries to database", entries.Count);
                        int savedEntries = await _postgresService.SaveEntriesAsync(entries);
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
        /// Stop the service gracefully
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping LogFileWatcher service...");
            
            if (_fileSystemWatcher != null)
            {
                _fileSystemWatcher.EnableRaisingEvents = false;
                _fileSystemWatcher.Changed -= OnFileChanged;
                _fileSystemWatcher.Created -= OnFileCreated;
                _fileSystemWatcher.Dispose();
            }
            
            _stoppingCts.Cancel();
            
            await base.StopAsync(cancellationToken);
            
            _logger.LogInformation("LogFileWatcher service stopped");
        }
        
        /// <summary>
        /// Update the settings in use by this watcher
        /// </summary>
        /// <param name="newSettings">The new settings to use</param>
        public async Task UpdateSettingsAsync(LogMonitorSettings newSettings)
        {
            _logger.LogInformation("Updating LogFileWatcher settings");
            
            // Compare with current settings to see what changed
            bool directoryChanged = !string.Equals(_settings.BaseDirectory, newSettings.BaseDirectory, StringComparison.OrdinalIgnoreCase);
            bool patternChanged = _settings.LogFilePattern != newSettings.LogFilePattern;
            bool pollingIntervalChanged = _settings.PollingIntervalSeconds != newSettings.PollingIntervalSeconds;
            
            // Since _settings is readonly, we need to update its properties
            // Rather than replacing the entire object
            typeof(LogMonitorSettings).GetProperty(nameof(LogMonitorSettings.BaseDirectory)).SetValue(_settings, newSettings.BaseDirectory);
            typeof(LogMonitorSettings).GetProperty(nameof(LogMonitorSettings.LogFilePattern)).SetValue(_settings, newSettings.LogFilePattern);
            typeof(LogMonitorSettings).GetProperty(nameof(LogMonitorSettings.PollingIntervalSeconds)).SetValue(_settings, newSettings.PollingIntervalSeconds);
            
            _logger.LogDebug("Settings updated - BaseDirectory: {BaseDirectory}, LogFilePattern: {LogFilePattern}, PollingInterval: {PollingInterval}s",
                _settings.BaseDirectory, _settings.LogFilePattern, _settings.PollingIntervalSeconds);
            
            // If directory or pattern changed, we need to update file system watcher
            if (directoryChanged || patternChanged)
            {
                // Use our centralized setup method
                await SetupFileSystemWatcherAsync();
            }
            
            // Only process existing files if processing is already active
            if (IsProcessing && directoryChanged && !string.IsNullOrWhiteSpace(_settings.BaseDirectory) && Directory.Exists(_settings.BaseDirectory))
            {
                _logger.LogInformation("Directory changed while processing is active, processing existing files in new directory");
                await ProcessExistingFilesAsync(_stoppingCts.Token);
            }
            else if (directoryChanged && !string.IsNullOrWhiteSpace(_settings.BaseDirectory) && Directory.Exists(_settings.BaseDirectory))
            {
                _logger.LogInformation("Directory changed, but processing is not active (IsProcessing: {IsProcessing}). Files will be processed when processing is started.", IsProcessing);
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
        /// Start processing log files
        /// </summary>
        /// <returns>Task representing the operation</returns>
        public async Task StartProcessingAsync()
        {
            if (IsProcessing)
            {
                _logger.LogWarning("Processing is already running, ignoring start request");
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
            
            // Check if positions file exists and handle if it doesn't
            if (!_positionManager.PositionsFileExists())
            {
                _logger.LogWarning("Positions file doesn't exist at startup. Will create a new one with initial positions.");
                // The PositionManager will create a new file with initial positions (0) when methods are called
            }
            
            // Set processing state to true
            IsProcessing = true;
            _logger.LogInformation("Starting log file processing...");
            
            // Immediately get the current day's log file and notify the UI
            string currentLogFile = GetCurrentLogFilePath();
            if (File.Exists(currentLogFile))
            {
                CurrentFile = Path.GetFileName(currentLogFile);
                
                // Get the current position (either 0 or from the position manager)
                long position = await _positionManager.GetPositionAsync(currentLogFile);
                CurrentPosition = position;
                
                // Notify UI immediately to update status display
                _logger.LogInformation("Processing started with current file: {CurrentFile}, position: {CurrentPosition}", CurrentFile, CurrentPosition);
                ProcessingStatusChanged?.Invoke(CurrentFile, 0, CurrentPosition);
            }
            else
            {
                // If today's file doesn't exist, try to find the most recent matching log file
                string[] matchingFiles = GetMatchingLogFiles();
                if (matchingFiles.Length > 0)
                {
                    // Sort by last modified date to get the most recent file
                    string mostRecentFile = matchingFiles
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .FirstOrDefault();
                        
                    if (mostRecentFile != null)
                    {
                        CurrentFile = Path.GetFileName(mostRecentFile);
                        
                        // Get the position for this file
                        long position = await _positionManager.GetPositionAsync(mostRecentFile);
                        CurrentPosition = position;
                        
                        // Notify UI immediately
                        _logger.LogInformation("Processing started with most recent file: {CurrentFile}, position: {CurrentPosition}", CurrentFile, CurrentPosition);
                        ProcessingStatusChanged?.Invoke(CurrentFile, 0, CurrentPosition);
                    }
                    else
                    {
                        // This shouldn't happen, but just in case
                        _logger.LogWarning("No valid log files found to process");
                        
                        // Notify UI with empty values
                        ProcessingStatusChanged?.Invoke("Waiting for log files...", 0, 0);
                    }
                }
                else
                {
                    _logger.LogInformation("No matching log files found. Waiting for new files.");
                    
                    // Notify UI that we're waiting for files
                    ProcessingStatusChanged?.Invoke("Waiting for log files...", 0, 0);
                }
            }
            
            // Process any existing files
            await ProcessExistingFilesAsync(_stoppingCts.Token);
            
            _logger.LogInformation("Processing started");
        }
        
        /// <summary>
        /// Stop processing log files
        /// </summary>
        public void StopProcessing()
        {
            if (!IsProcessing)
            {
                _logger.LogWarning("Processing is not running, ignoring stop request");
                return;
            }
            
            _logger.LogInformation("Stopping processing. Current _isProcessingFile lock value: {LockValue}", _isProcessingFile);
            
            // Set the flag to stop future processing, but let current processing complete
            IsProcessing = false;
            
            // If there's a file currently being processed, log that information
            if (_isProcessingFile == 1)
            {
                _logger.LogInformation("A file is currently being processed. It will complete before processing stops fully.");
            }
            else
            {
                // Force reset the processing lock flag only if no file is being processed
                Interlocked.Exchange(ref _isProcessingFile, 0);
            }
            
            // Clear any pending files since we're not processing anymore
            lock (_pendingFiles)
            {
                if (_pendingFiles.Count > 0)
                {
                    _logger.LogInformation("Clearing {Count} pending files from the queue as processing has stopped", _pendingFiles.Count);
                    _pendingFiles.Clear();
                }
            }
            
            _logger.LogInformation("Processing stopped");
        }
        
        /// <summary>
        /// Get the current processing state
        /// </summary>
        /// <returns>True if the watcher is currently processing, false otherwise</returns>
        public bool GetProcessingState()
        {
            _logger.LogDebug("Getting processing state: {IsProcessing}", IsProcessing);
            return IsProcessing;
        }
        
        /// <summary>
        /// Set the processing state directly (without starting or stopping processing)
        /// This is mainly for synchronizing UI state with actual processing state
        /// </summary>
        /// <param name="isProcessing">The desired processing state</param>
        public void SetProcessingState(bool isProcessing)
        {
            _logger.LogInformation("Setting processing state directly to {IsProcessing}", isProcessing);
            IsProcessing = isProcessing;
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
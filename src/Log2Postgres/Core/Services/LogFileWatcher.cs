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
        private readonly LogMonitorSettings _settings;
        private readonly IHostApplicationLifetime _appLifetime;
        
        // Event to notify the UI or other services about processing status
        public event Action<string, int, long>? ProcessingStatusChanged;
        
        // Event to notify when new log entries are processed
        public event Action<IEnumerable<OrfLogEntry>>? EntriesProcessed;
        
        // Processing statistics
        public int TotalLinesProcessed { get; private set; }
        public string CurrentFile { get; private set; } = string.Empty;
        public long CurrentPosition { get; private set; }
        public DateTime LastProcessedTime { get; private set; }
        
        private readonly CancellationTokenSource _stoppingCts = new();
        private FileSystemWatcher? _fileSystemWatcher;
        private bool _isProcessing;
        private readonly object _processingLock = new();
        private readonly List<string> _pendingFiles = new();
        
        // Property to check if the watcher is currently processing files
        public bool IsProcessing => _isProcessing;
        
        public LogFileWatcher(
            ILogger<LogFileWatcher> logger,
            OrfLogParser logParser,
            PositionManager positionManager,
            IOptions<LogMonitorSettings> settings,
            IHostApplicationLifetime appLifetime)
        {
            _logger = logger;
            _logParser = logParser;
            _positionManager = positionManager;
            _settings = settings.Value;
            _appLifetime = appLifetime;
        }
        
        /// <summary>
        /// Start watching log files and processing them
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LogFileWatcher service starting...");
            
            // Validate settings before starting
            if (string.IsNullOrWhiteSpace(_settings.BaseDirectory))
            {
                _logger.LogError("BaseDirectory is not configured. Service cannot start.");
                return;
            }
            
            if (string.IsNullOrWhiteSpace(_settings.LogFilePattern))
            {
                _logger.LogError("LogFilePattern is not configured. Service cannot start.");
                return;
            }
            
            // Ensure the directory exists
            if (!Directory.Exists(_settings.BaseDirectory))
            {
                _logger.LogError("Base directory {Directory} does not exist. Service cannot start.", _settings.BaseDirectory);
                return;
            }
            
            // Set up file system watcher
            try
            {
                _fileSystemWatcher = new FileSystemWatcher(_settings.BaseDirectory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    Filter = "*.log",
                    EnableRaisingEvents = true
                };
                
                _fileSystemWatcher.Changed += OnFileChanged;
                _fileSystemWatcher.Created += OnFileCreated;
                
                _logger.LogInformation("File watcher set up for directory {Directory}", _settings.BaseDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up file system watcher: {Message}", ex.Message);
                return;
            }
            
            // Process any existing files first
            await ProcessExistingFilesAsync(stoppingToken);
            
            // Start polling loop
            _ = Task.Run(() => PollingLoopAsync(stoppingToken), stoppingToken);
            
            // Wait for the application to stop
            await Task.Delay(Timeout.Infinite, stoppingToken);
            
            _logger.LogInformation("LogFileWatcher service stopping...");
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
                _logger.LogError(ex, "Error processing existing files: {Message}", ex.Message);
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
                    // Process any files that were queued by file system events
                    await ProcessPendingFilesAsync(cancellationToken);
                    
                    // Also check all matching files for updates
                    await ProcessCurrentLogFileAsync(cancellationToken);
                    
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
                filesToProcess = new List<string>(_pendingFiles);
                _pendingFiles.Clear();
            }
            
            foreach (string filePath in filesToProcess.Distinct())
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                
                await ProcessLogFileAsync(filePath, cancellationToken);
            }
        }
        
        /// <summary>
        /// Process the current (today's) log file
        /// </summary>
        private async Task ProcessCurrentLogFileAsync(CancellationToken cancellationToken)
        {
            try
            {
                string currentLogFilePath = GetCurrentLogFilePath();
                if (File.Exists(currentLogFilePath))
                {
                    await ProcessLogFileAsync(currentLogFilePath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing current log file: {Message}", ex.Message);
            }
        }
        
        /// <summary>
        /// Process a specific log file, parsing and forwarding entries
        /// </summary>
        private async Task ProcessLogFileAsync(string filePath, CancellationToken cancellationToken)
        {
            // Skip processing if another operation is already in progress
            if (!Monitor.TryEnter(_processingLock))
                return;
            
            try
            {
                _isProcessing = true;
                CurrentFile = Path.GetFileName(filePath);
                _logger.LogDebug("Processing log file {FilePath}", filePath);
                
                // Get the last position we read from for this file
                long startPosition = await _positionManager.GetPositionAsync(filePath);
                CurrentPosition = startPosition;
                
                // Parse the log file from that position
                var (entries, newPosition) = _logParser.ParseLogFile(filePath, startPosition);
                
                if (entries.Count > 0)
                {
                    // Update statistics
                    TotalLinesProcessed += entries.Count;
                    CurrentPosition = newPosition;
                    LastProcessedTime = DateTime.Now;
                    
                    // Save the new position
                    await _positionManager.UpdatePositionAsync(filePath, newPosition);
                    
                    // Notify listeners
                    ProcessingStatusChanged?.Invoke(CurrentFile, entries.Count, newPosition);
                    EntriesProcessed?.Invoke(entries);
                    
                    _logger.LogInformation("Processed {Count} entries from {FilePath}", entries.Count, filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log file {FilePath}: {Message}", filePath, ex.Message);
            }
            finally
            {
                _isProcessing = false;
                Monitor.Exit(_processingLock);
            }
        }
        
        /// <summary>
        /// Called when a file in the watched directory is modified
        /// </summary>
        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed || e.ChangeType == WatcherChangeTypes.Created)
            {
                _logger.LogDebug("File changed: {FilePath}", e.FullPath);
                QueueFileForProcessing(e.FullPath);
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
            if (!IsMatchingLogFile(filePath))
                return;
                
            lock (_pendingFiles)
            {
                _pendingFiles.Add(filePath);
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
            
            // Replace {Date:format} with a regex pattern
            pattern = Regex.Replace(pattern, @"\{Date:([^}]+)\}", @"(.+)");
            
            // Convert the pattern to a regex
            pattern = "^" + Regex.Escape(pattern).Replace(@"\(.+\)", "(.+)") + "$";
            
            return Regex.IsMatch(filename, pattern);
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
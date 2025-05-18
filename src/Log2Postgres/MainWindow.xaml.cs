using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;
using System.Security;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Log2Postgres.Core.Services;
using Log2Postgres.Core.Models;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Linq;
using Microsoft.Extensions.Options;
using Serilog;
using System.Windows.Controls.Primitives;

namespace Log2Postgres;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly IConfiguration _configuration;
    private readonly PostgresService _postgresService;
    private readonly LogFileWatcher _logFileWatcher;
    private readonly ILogger<MainWindow> _logger;
    private readonly PasswordEncryption _passwordEncryption;
    private readonly PositionManager _positionManager;
    private bool _isProcessing = false;
    
    // Timer for refreshing the position display
    private System.Threading.Timer? _positionUpdateTimer;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            
            try
            {
                // Load configuration
                var app = (App)System.Windows.Application.Current;
                _configuration = app.GetConfiguration();
                
                // Get services with error handling
                try
                {
                    _postgresService = app.GetService<PostgresService>();
                }
                catch (Exception)
                {
                    // Handle or log error getting PostgresService if necessary
                }
                
                try
                {
                    _logFileWatcher = app.GetService<LogFileWatcher>();
                }
                catch (Exception)
                {
                    // Handle or log error getting LogFileWatcher if necessary
                }
                
                try
                {
                    _passwordEncryption = app.GetService<PasswordEncryption>();
                }
                catch (Exception)
                {
                    // Handle or log error getting PasswordEncryption if necessary
                }
                
                try
                {
                    _positionManager = app.GetService<PositionManager>();
                }
                catch (Exception)
                {
                    // Handle or log error getting PositionManager if necessary
                }
                
                try
                {
                    // Create logger
                    var loggerFactory = app.GetService<ILoggerFactory>();
                    if (loggerFactory != null)
                    {
                        _logger = loggerFactory.CreateLogger<MainWindow>();
                    }
                }
                catch (Exception)
                {
                    // Handle or log error creating logger if necessary
                }
            }
            catch (Exception ex)
            {
                // Log or show a more user-friendly error if service initialization fails
                System.Windows.MessageBox.Show($"Error initializing services: {ex.Message}", "Service Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            
            if (_logger != null)
            {
                _logger.LogInformation("MainWindow initialized");
            }
            
            // Register event handlers
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            
            // Register for log watcher events
            if (_logFileWatcher != null)
            {
                _logFileWatcher.ProcessingStatusChanged += OnProcessingStatusChanged;
                _logFileWatcher.EntriesProcessed += OnEntriesProcessed;
                _logFileWatcher.ErrorOccurred += OnErrorOccurred;
                
                if (_logger != null)
                {
                    _logger.LogDebug("Registered for LogFileWatcher events");
                }
            }
            else
            {
                if (_logger != null)
                {
                    _logger.LogWarning("LogFileWatcher service is null, events not registered");
                }
            }
            
            // Register for position manager events
            if (_positionManager != null)
            {
                _positionManager.PositionsLoaded += OnPositionsLoaded;
                
                // Check if positions file exists and take appropriate action
                try
                {
                    bool positionsExist = _positionManager.PositionsFileExists();
                    if (!positionsExist && _logFileWatcher != null)
                    {
                        _logFileWatcher.ResetPositionInfo();
                        if (_logger != null)
                        {
                            _logger.LogInformation("Positions file not found, position info has been reset");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Handle or log error checking positions file
                }
            }
        }
        catch (Exception ex)
        {
            // Log any errors during initialization
            System.Windows.MessageBox.Show($"Critical error in MainWindow constructor: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("MainWindow loaded");
        LoadSettings();
        UpdateStatusBar("Application started successfully");
        
        // Add test log messages to demonstrate filtering
        LogMessage("Application initialized - INFO level message");
        LogWarning("This is a sample WARNING level message");
        LogError("This is a sample ERROR level message");
        LogMessage("You can filter these messages using the toggle buttons above");
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        _logger.LogInformation("MainWindow closing");
        
        if (_isProcessing)
        {
            _logger.LogDebug("Processing is active, prompting user before exit");
            var result = System.Windows.MessageBox.Show(
                "Processing is still active. Are you sure you want to exit?",
                "Confirm Exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                _logger.LogInformation("User canceled application exit");
                e.Cancel = true;
                return;
            }

            // Stop processing before exit
            _logger.LogDebug("Stopping processing before exit");
            StopProcessing();
        }
        
        // Make sure position update timer is stopped
        StopPositionUpdateTimer();
        
        // Unregister from watcher events
        if (_logFileWatcher != null)
        {
            _logger.LogDebug("Unregistering from LogFileWatcher events");
            _logFileWatcher.ProcessingStatusChanged -= OnProcessingStatusChanged;
            _logFileWatcher.EntriesProcessed -= OnEntriesProcessed;
            _logFileWatcher.ErrorOccurred -= OnErrorOccurred;
        }
        
        _logger.LogInformation("MainWindow closed");
    }

    private void LoadSettings()
    {
        _logger.LogDebug("Loading application settings");
        try
        {
            // Database settings
            DbHost.Text = _configuration["DatabaseSettings:Host"] ?? string.Empty;
            DbPort.Text = _configuration["DatabaseSettings:Port"] ?? "5432";
            DbUsername.Text = _configuration["DatabaseSettings:Username"] ?? string.Empty;
            DbName.Text = _configuration["DatabaseSettings:Database"] ?? "postgres";
            DbSchema.Text = _configuration["DatabaseSettings:Schema"] ?? "public";
            DbTable.Text = _configuration["DatabaseSettings:Table"] ?? "orf_logs";
            DbTimeout.Text = _configuration["DatabaseSettings:ConnectionTimeout"] ?? "30";
            
            // Load and decrypt password if it exists
            string encryptedPassword = _configuration["DatabaseSettings:Password"] ?? string.Empty;
            if (!string.IsNullOrEmpty(encryptedPassword))
            {
                // DEBUG ONLY - Log the stored password - REMOVE IN PRODUCTION
                _logger.LogWarning("DEBUG PURPOSE ONLY - Stored password from config: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", encryptedPassword);
                
                try
                {
                    // Check if the password is encrypted and decrypt it
                    if (_passwordEncryption.IsEncrypted(encryptedPassword))
                    {
                        string decryptedPassword = _passwordEncryption.DecryptPassword(encryptedPassword);
                        DbPassword.Password = decryptedPassword;
                        _logger.LogDebug("Database password decrypted successfully");
                        // DEBUG ONLY - Log the decrypted password - REMOVE IN PRODUCTION
                        _logger.LogWarning("DEBUG PURPOSE ONLY - Decrypted password result: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", decryptedPassword);
                    }
                    else
                    {
                        // Handle case where password might be in plaintext (legacy or first run)
                        DbPassword.Password = encryptedPassword;
                        _logger.LogDebug("Database password loaded in plaintext (will be encrypted on next save)");
                        // DEBUG ONLY - Log the plaintext password - REMOVE IN PRODUCTION
                        _logger.LogWarning("DEBUG PURPOSE ONLY - Using plaintext password: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", encryptedPassword);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error decrypting database password: {Message}", ex.Message);
                    // Don't set password if decryption fails
                }
            }

            // Log file settings
            LogDirectory.Text = _configuration["LogMonitorSettings:BaseDirectory"] ?? string.Empty;
            LogFilePattern.Text = _configuration["LogMonitorSettings:LogFilePattern"] ?? "orfee-{Date:yyyy-MM-dd}.log";
            PollingInterval.Text = _configuration["LogMonitorSettings:PollingIntervalSeconds"] ?? "5";

            _logger.LogInformation("Settings loaded from configuration");
            _logger.LogDebug("Database settings - Host: {Host}, Port: {Port}, Database: {Database}, Schema: {Schema}, Table: {Table}", 
                DbHost.Text, DbPort.Text, DbName.Text, DbSchema.Text, DbTable.Text);
            _logger.LogDebug("Log settings - Directory: {Directory}, Pattern: {Pattern}, Polling Interval: {Interval}s", 
                LogDirectory.Text, LogFilePattern.Text, PollingInterval.Text);
                
            LogMessage("Settings loaded from configuration");
            UpdateServiceStatus(false);
            
            // Update database status
            UpdateDatabaseStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings: {Message}", ex.Message);
            LogError($"Error loading settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            // Build new configuration
            var configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var configJson = File.ReadAllText(configFile);
            dynamic configObj = Newtonsoft.Json.JsonConvert.DeserializeObject(configJson);

            // Update database settings
            configObj["DatabaseSettings"]["Host"] = DbHost.Text;
            configObj["DatabaseSettings"]["Port"] = DbPort.Text;
            configObj["DatabaseSettings"]["Username"] = DbUsername.Text;
            
            // DEBUG ONLY - Log the UI password before secure storage - REMOVE IN PRODUCTION
            _logger.LogWarning("DEBUG PURPOSE ONLY - UI password before securing: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", DbPassword.Password);
            
            string securedPassword = SecurePassword(); // Encrypt the password
            
            // DEBUG ONLY - Log the secured password - REMOVE IN PRODUCTION
            _logger.LogWarning("DEBUG PURPOSE ONLY - Secured password result: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", securedPassword);
            
            configObj["DatabaseSettings"]["Password"] = securedPassword;
            configObj["DatabaseSettings"]["Database"] = DbName.Text;
            configObj["DatabaseSettings"]["Schema"] = DbSchema.Text;
            configObj["DatabaseSettings"]["Table"] = DbTable.Text;
            configObj["DatabaseSettings"]["ConnectionTimeout"] = DbTimeout.Text;

            // Update log file settings
            configObj["LogMonitorSettings"]["BaseDirectory"] = LogDirectory.Text;
            configObj["LogMonitorSettings"]["LogFilePattern"] = LogFilePattern.Text;
            configObj["LogMonitorSettings"]["PollingIntervalSeconds"] = PollingInterval.Text;

            // Save to file
            string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(configObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(configFile, updatedJson);

            LogMessage("Settings saved successfully");
            UpdateStatusBar("Configuration saved");
            
            // Reload the configuration and restart LogFileWatcher
            ReloadConfiguration();
        }
        catch (Exception ex)
        {
            LogError($"Error saving settings: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Failed to save settings: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Reloads the configuration and restarts the LogFileWatcher service
    /// </summary>
    private async Task ReloadConfiguration()
    {
        try
        {
            LogMessage("Reloading configuration...");
            
            // Create a new LogMonitorSettings instance with current UI values
            var settings = new LogMonitorSettings
            {
                BaseDirectory = LogDirectory.Text,
                LogFilePattern = LogFilePattern.Text,
                PollingIntervalSeconds = int.Parse(PollingInterval.Text)
            };
            
            // Skip update if directory is still empty - no point trying to start with no directory
            if (string.IsNullOrWhiteSpace(settings.BaseDirectory))
            {
                LogWarning("No log directory specified. Please select a directory first.");
                return;
            }
            
            // Validate log directory exists and is accessible
            bool directoryValid = ValidateLogDirectory(settings.BaseDirectory, createIfMissing: true);
            if (!directoryValid)
            {
                // Error already logged in ValidateLogDirectory
                return;
            }
            
            // Update the current instance of LogFileWatcher with the new settings
            if (_logFileWatcher != null)
            {
                // For PostgresService, we can update it easily
                _logger.LogDebug("Updating LogFileWatcher with new settings");
                
                // Make sure the LogFileWatcher's IsProcessing state matches our UI state
                bool currentWatcherState = _logFileWatcher.GetProcessingState();
                if (currentWatcherState != _isProcessing)
                {
                    _logger.LogWarning("LogFileWatcher processing state ({WatcherState}) doesn't match UI processing state ({UiState}). Synchronizing states.", 
                        currentWatcherState, _isProcessing);
                    _logFileWatcher.SetProcessingState(_isProcessing);
                }
                else
                {
                    _logger.LogDebug("LogFileWatcher processing state matches UI state: {IsProcessing}", _isProcessing);
                }
                
                // Log whether processing is active to help with debugging
                _logger.LogDebug("Updating LogFileWatcher settings while processing is {Status}", _isProcessing ? "active" : "inactive");
                
                // Now update the LogFileWatcher with the new settings
                await _logFileWatcher.UpdateSettingsAsync(settings);
                
                // Update UI with the current file information from log watcher
                await UpdateDatabaseStatus();
                
                LogMessage("Configuration reloaded successfully");
            }
            else
            {
                LogError("LogFileWatcher service not available");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error reloading configuration: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Validates that a log directory exists and is accessible
    /// </summary>
    /// <param name="directory">Path to validate</param>
    /// <param name="createIfMissing">Whether to create the directory if it doesn't exist</param>
    /// <returns>True if directory is valid and accessible</returns>
    private bool ValidateLogDirectory(string directory, bool createIfMissing = false)
    {
        try
        {
            // Check if directory exists
            if (!Directory.Exists(directory))
            {
                if (createIfMissing)
                {
                    LogWarning($"Directory {directory} does not exist. Creating it...");
                    Directory.CreateDirectory(directory);
                    LogMessage($"Created directory: {directory}");
                }
                else
                {
                    LogWarning($"Directory {directory} does not exist.");
                    return false;
                }
            }
            
            // Check read permissions
            try
            {
                // Try to list files to verify read access
                var files = Directory.GetFiles(directory);
                var logFiles = files.Where(f => Path.GetExtension(f).Equals(".log", StringComparison.OrdinalIgnoreCase)).ToArray();
                
                if (logFiles.Length == 0)
                {
                    LogWarning($"No log files found in {directory}. Make sure log files will be written there.");
                    // This is just a warning, not an error
                }
                else
                {
                    LogMessage($"Found {logFiles.Length} log files in directory {directory}");
                }
                
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                LogError($"No read permission for directory {directory}. Please check folder permissions.");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogError($"Error validating log directory {directory}: {ex.Message}");
            return false;
        }
    }

    private string SecurePassword()
    {
        try
        {
            // Encrypt the password before saving to configuration
            string plainPassword = DbPassword.Password;
            
            // DEBUG ONLY - Log the plaintext password - REMOVE IN PRODUCTION
            _logger.LogWarning("DEBUG PURPOSE ONLY - SecurePassword method with plain password: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", plainPassword);
            
            if (!string.IsNullOrEmpty(plainPassword))
            {
                string encryptedPassword = _passwordEncryption.EncryptPassword(plainPassword);
                _logger.LogDebug("Password encrypted successfully for storage");
                
                // DEBUG ONLY - Log the encrypted password - REMOVE IN PRODUCTION
                _logger.LogWarning("DEBUG PURPOSE ONLY - SecurePassword method encrypted result: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", encryptedPassword);
                
                return encryptedPassword;
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error securing password: {Message}", ex.Message);
            // Return the plain password if encryption fails
            // This is not ideal but ensures the application can still work
            return DbPassword.Password;
        }
    }

    #region Event Handlers

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Browse button clicked");
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select log file directory"
        };
        
        if (!string.IsNullOrEmpty(LogDirectory.Text))
        {
            dialog.SelectedPath = LogDirectory.Text;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _logger.LogDebug("User selected directory: {Directory}", dialog.SelectedPath);
            LogDirectory.Text = dialog.SelectedPath;
            LogMessage($"Selected directory: {dialog.SelectedPath}");
            
            // List files that match the pattern extension (*.log)
            try
            {
                if (Directory.Exists(dialog.SelectedPath))
                {
                    var logFiles = Directory.GetFiles(dialog.SelectedPath, "*.log");
                    if (logFiles.Length > 0)
                    {
                        _logger.LogDebug("Found {Count} log files in directory", logFiles.Length);
                        LogMessage($"Found {logFiles.Length} log files in directory");
                        
                        // If no pattern is set, try to infer it from the first log file
                        if (string.IsNullOrWhiteSpace(LogFilePattern.Text) && logFiles.Length > 0)
                        {
                            string fileName = Path.GetFileName(logFiles[0]);
                            _logger.LogDebug("Setting log file pattern based on existing file: {FileName}", fileName);
                            LogFilePattern.Text = fileName;
                            LogMessage($"Set log file pattern to: {fileName}");
                        }
                        
                        // List the first few files found (limit to 5 to avoid spamming the log)
                        int displayCount = Math.Min(logFiles.Length, 5);
                        var fileNames = logFiles.Take(displayCount).Select(Path.GetFileName).ToArray();
                        LogMessage($"Sample files: {string.Join(", ", fileNames)}" + 
                                  (logFiles.Length > displayCount ? $" and {logFiles.Length - displayCount} more" : ""));
                    }
                    else
                    {
                        LogWarning("No log files found in the selected directory");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing files in selected directory: {Message}", ex.Message);
                LogError($"Error listing files: {ex.Message}");
            }
        }
        else
        {
            _logger.LogDebug("User canceled directory selection");
        }
    }

    private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Test connection button clicked");
        await TestDatabaseConnection();
    }

    private async void VerifyTableBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Verify table button clicked");
        await VerifyDatabaseTable();
    }

    private void InstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "Service installation will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UninstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "Service uninstallation will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Start button clicked");
        StartProcessingAsync().ConfigureAwait(false);
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Stop button clicked");
        StopProcessing();
    }

    private async Task StartProcessingAsync()
    {
        if (_isProcessing)
            return;

        // Validate settings before starting
        if (string.IsNullOrWhiteSpace(LogDirectory.Text))
        {
            LogWarning("Please specify a log directory path before starting.");
            return;
        }

        // Validate log directory exists and is accessible
        bool directoryValid = ValidateLogDirectory(LogDirectory.Text);
        if (!directoryValid)
        {
            LogError("Processing cannot start with an invalid log directory");
            return;
        }

        // Check if positions file exists and log the status
        bool positionsFileExists = _positionManager.PositionsFileExists();
        if (!positionsFileExists)
        {
            LogWarning("Positions file not found. A new one will be created with initial positions (0).");
        }
        else
        {
            LogMessage("Using existing positions file for tracking log file positions.");
        }

        try
        {
            // Set state first to prevent multiple calls to this method
            _isProcessing = true;
            UpdateServiceStatus(true);
            
            // Ensure settings are applied before starting, but only if settings have changed
            bool needsUpdate = false;
            
            // Create a new settings object from the UI values
            var currentSettings = new LogMonitorSettings
            {
                BaseDirectory = LogDirectory.Text,
                LogFilePattern = LogFilePattern.Text,
                PollingIntervalSeconds = int.Parse(PollingInterval.Text)
            };
            
            // Check if we need to update the LogFileWatcher settings because this is either the first start
            // or values have changed since the last update
            if (_logFileWatcher != null)
            {
                if (_logFileWatcher.CurrentFile == string.Empty || 
                    !string.Equals(_logFileWatcher.CurrentDirectory, currentSettings.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
                    _logFileWatcher.CurrentPattern != currentSettings.LogFilePattern)
                {
                    needsUpdate = true;
                }
            }
            
            // Only update if necessary
            if (needsUpdate)
            {
                LogMessage("Updating configuration before starting processing...");
                await ReloadConfiguration();
            }
            
            // Explicitly ensure the LogFileWatcher knows we're processing
            if (_logFileWatcher != null)
            {
                // Ensure the watcher knows we're in processing mode before starting
                _logFileWatcher.SetProcessingState(true);
                
                // Start the actual processing
                await _logFileWatcher.StartProcessingAsync();
                LogMessage("Processing started");
                UpdateStatusBar("Processing started");
            }
            
            // Start a timer to periodically update the position display
            StartPositionUpdateTimer();
        }
        catch (Exception ex)
        {
            LogError($"Error during processing: {ex.Message}");
            _isProcessing = false;
            
            // Make sure the LogFileWatcher state is reset on error
            if (_logFileWatcher != null)
            {
                _logFileWatcher.SetProcessingState(false);
            }
            
            UpdateServiceStatus(false);
        }
    }

    private void StopProcessing()
    {
        if (!_isProcessing)
            return;

        // Set UI state first
        _isProcessing = false;
        UpdateServiceStatus(false);
        
        // Call the StopProcessing method in LogFileWatcher
        if (_logFileWatcher != null)
        {
            // Ensure the watcher knows we're stopping processing
            _logFileWatcher.SetProcessingState(false);
            _logFileWatcher.StopProcessing();
        }
        
        // Stop the position update timer
        StopPositionUpdateTimer();
        
        // Reset UI elements
        ProcessingStatusText.Text = "Idle";
        
        LogMessage("Processing stopped");
        UpdateStatusBar("Processing stopped");
    }

    private void SaveConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void ResetConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "This will reset all settings to default values. Continue?",
            "Confirm Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            ResetSettings();
        }
    }

    private void LogFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        // Get the toggle button that was clicked
        if (sender is ToggleButton toggleButton)
        {
            _logger.LogDebug("Log filter toggled: {ToggleName} is now {State}", 
                toggleButton.Name, toggleButton.IsChecked == true ? "ON" : "OFF");
        }
        
        // Apply filtering based on toggle states
        ApplyLogFilter();
        
        // Update status bar
        UpdateStatusBar("Log filter updated");
    }

    private void ClearLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        LogTextBox.Tag = string.Empty; // Reset the original text as well
        
        // Add a message that the logs were cleared
        LogMessage("Logs cleared");
    }

    private void UpdateServiceStatus(bool isRunning)
    {
        if (isRunning)
        {
            ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Green);
            ServiceStatusText.Text = "Running";
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            ProcessingStatusText.Text = "Running";
            
            // Disable configuration buttons when processing is active
            SaveConfigBtn.IsEnabled = false;
            ResetConfigBtn.IsEnabled = false;
            
            // Display a tooltip to inform the user why these buttons are disabled
            SaveConfigBtn.ToolTip = "Stop processing to enable configuration changes";
            ResetConfigBtn.ToolTip = "Stop processing to enable configuration changes";
        }
        else
        {
            ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Red);
            ServiceStatusText.Text = "Stopped";
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            ProcessingStatusText.Text = "Idle";
            
            // Re-enable configuration buttons when processing is stopped
            SaveConfigBtn.IsEnabled = true;
            ResetConfigBtn.IsEnabled = true;
            
            // Clear tooltips
            SaveConfigBtn.ToolTip = null;
            ResetConfigBtn.ToolTip = null;
        }
    }

    #endregion

    #region Helper Methods

    private void LogMessage(string message)
    {
        // Add timestamp to message
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] INFO: {message}";
        
        // Add the message to the text box
        LogTextBox.AppendText(formattedMessage + Environment.NewLine);
        
        // Save the complete text in the Tag property for filtering
        LogTextBox.Tag = LogTextBox.Text;
        
        // Ensure we're showing it if INFO messages are enabled
        if (ShowStatusToggle.IsChecked == true)
        {
            LogTextBox.ScrollToEnd();
        }
        else
        {
            // Apply filter since we're adding a message that should be filtered
            ApplyLogFilter();
        }
    }

    private void LogWarning(string message)
    {
        // Add timestamp to message
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] WARNING: {message}";
        
        // Add the message to the text box
        LogTextBox.AppendText(formattedMessage + Environment.NewLine);
        
        // Save the complete text in the Tag property for filtering
        LogTextBox.Tag = LogTextBox.Text;
        
        // Ensure we're showing it if WARNING messages are enabled
        if (ShowWarningsToggle.IsChecked == true)
        {
            LogTextBox.ScrollToEnd();
        }
        else
        {
            // Apply filter since we're adding a message that should be filtered
            ApplyLogFilter();
        }
    }

    private void LogError(string message)
    {
        // Add timestamp to message
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] ERROR: {message}";
        
        // Add the message to the text box
        LogTextBox.AppendText(formattedMessage + Environment.NewLine);
        
        // Save the complete text in the Tag property for filtering
        LogTextBox.Tag = LogTextBox.Text;
        
        // Ensure we're showing it if ERROR messages are enabled
        if (ShowErrorsToggle.IsChecked == true)
        {
            LogTextBox.ScrollToEnd();
        }
        else
        {
            // Apply filter since we're adding a message that should be filtered
            ApplyLogFilter();
        }
        
        LastErrorText.Text = message;
    }

    private void UpdateStatusBar(string message)
    {
        StatusBarText.Text = message;
        LastUpdatedText.Text = $"Last updated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}";
    }
    
    private async Task UpdateDatabaseStatus()
    {
        try
        {
            // Test connection and update status
            bool isConnected = await _postgresService.TestConnectionAsync();
            
            if (isConnected)
            {
                DbStatusText.Text = "Connected";
                
                // Get row count
                long rowCount = await _postgresService.GetRowCountAsync();
                
                if (rowCount >= 0)
                {
                    RowCountText.Text = rowCount.ToString();
                }
                else
                {
                    RowCountText.Text = "Error";
                }
            }
            else
            {
                DbStatusText.Text = "Disconnected";
                RowCountText.Text = "N/A";
            }
            
            // Update processing status UI with current values from LogFileWatcher
            if (_logFileWatcher != null)
            {
                // Only update UI and log messages if there's actually a file being processed
                // This prevents showing position info from previous runs when app just started
                bool hasValidPosition = !string.IsNullOrEmpty(_logFileWatcher.CurrentFile) && 
                                       _logFileWatcher.CurrentPosition > 0 &&
                                       _positionManager != null && 
                                       _positionManager.PositionsFileExists();
                
                if (hasValidPosition)
                {
                    CurrentFileText.Text = _logFileWatcher.CurrentFile;
                    CurrentPositionText.Text = _logFileWatcher.CurrentPosition.ToString();
                    LinesProcessedText.Text = _logFileWatcher.TotalLinesProcessed.ToString();
                    LogMessage($"Current file: {_logFileWatcher.CurrentFile}, Position: {_logFileWatcher.CurrentPosition}");
                }
                else
                {
                    // Don't update UI with potentially outdated position info
                    CurrentFileText.Text = string.Empty;
                    CurrentPositionText.Text = "0";
                    LinesProcessedText.Text = "0";
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Error updating database status: {ex.Message}");
            DbStatusText.Text = "Error";
            RowCountText.Text = "Error";
        }
    }

    private async Task TestDatabaseConnection()
    {
        LogMessage("Testing database connection...");
        
        // Create a temporary database settings object with the current UI values
        var settings = new DatabaseSettings
        {
            Host = DbHost.Text,
            Port = DbPort.Text,
            Username = DbUsername.Text,
            Password = DbPassword.Password,
            Database = DbName.Text,
            Schema = DbSchema.Text,
            Table = DbTable.Text,
            ConnectionTimeout = int.Parse(DbTimeout.Text)
        };
        
        // DEBUG ONLY - Log the password in cleartext - REMOVE IN PRODUCTION
        _logger.LogWarning("DEBUG PURPOSE ONLY - MainWindow test using password: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", settings.Password);
        LogWarning($"DEBUG PURPOSE ONLY - Testing with password: '{settings.Password}' [REMOVE THIS LOG]");
        
        // Create a test service with these settings
        var logger = ((App)System.Windows.Application.Current).GetService<ILoggerFactory>().CreateLogger<PostgresService>();
        var options = Options.Create(settings);
        var dbService = new PostgresService(logger, options);
        
        // Test the connection
        bool result = await dbService.TestConnectionAsync();
        
        if (result)
        {
            LogMessage("Database connection successful!");
            UpdateStatusBar("Database connection tested: SUCCESS");
            
            // Clear any previous error messages
            LastErrorText.Text = string.Empty;
            DbStatusText.Text = "Connected";
        }
        else
        {
            // Try to get the detailed error from the debug log
            string errorDetails = "";
            try 
            {
                var logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                var debugLogFile = Directory.GetFiles(logFolder, "debug-*.txt")
                                          .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                                          .FirstOrDefault();
                
                if (debugLogFile != null)
                {
                    var logLines = File.ReadAllLines(debugLogFile);
                    var errorLine = logLines.Where(l => l.Contains("Database connection test failed:"))
                                           .LastOrDefault();
                    
                    if (errorLine != null)
                    {
                        var parts = errorLine.Split("Database connection test failed:", StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1)
                        {
                            errorDetails = parts[1].Trim();
                            if (errorDetails.StartsWith(" "))
                                errorDetails = errorDetails.Substring(1);
                        }
                    }
                }
            }
            catch 
            {
                // Fallback if we can't read the debug log
                errorDetails = "Unknown database error. Check debug logs for details.";
            }
            
            LogError($"Database connection failed: {errorDetails}");
            UpdateStatusBar("Database connection tested: FAILED");
            LastErrorText.Text = errorDetails;
            DbStatusText.Text = "Error";
        }
    }

    private async Task VerifyDatabaseTable()
    {
        LogMessage("Verifying database table...");
        
        // Create a temporary database settings object with the current UI values
        var settings = new DatabaseSettings
        {
            Host = DbHost.Text,
            Port = DbPort.Text,
            Username = DbUsername.Text,
            Password = DbPassword.Password,
            Database = DbName.Text,
            Schema = DbSchema.Text,
            Table = DbTable.Text,
            ConnectionTimeout = int.Parse(DbTimeout.Text)
        };
        
        // Create a test service with these settings
        var logger = ((App)System.Windows.Application.Current).GetService<ILoggerFactory>().CreateLogger<PostgresService>();
        var options = Options.Create(settings);
        var dbService = new PostgresService(logger, options);
        
        // Test the connection first
        bool connected = await dbService.TestConnectionAsync();
        
        if (!connected)
        {
            LogError("Database connection failed! Cannot verify/create table.");
            UpdateStatusBar("Database table verification failed: CONNECTION ERROR");
            return;
        }
        
        // First check if the table exists at all
        bool tableExists = await dbService.TableExistsAsync();
        
        if (!tableExists)
        {
            // Table doesn't exist - ask if it should be created
            var result = System.Windows.MessageBox.Show(
                $"Table {settings.Schema}.{settings.Table} does not exist. Would you like to create it?",
                "Create Table",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
                
            if (result != MessageBoxResult.Yes)
            {
                LogMessage("Table creation cancelled by user");
                UpdateStatusBar("Table creation cancelled");
                return;
            }
            
            // User confirmed to create the table
            bool createResult = await dbService.CreateTableAsync();
            if (createResult)
            {
                LogMessage($"Table {settings.Schema}.{settings.Table} created successfully!");
                UpdateStatusBar("Database table created: SUCCESS");
            }
            else
            {
                LogError($"Failed to create table {settings.Schema}.{settings.Table}!");
                UpdateStatusBar("Database table creation failed: ERROR");
                return;
            }
        }
        else
        {
            // Table exists - check if structure matches
            bool structureMatches = await dbService.ValidateTableStructureAsync();
            
            if (!structureMatches)
            {
                // Structure doesn't match - ask if it should be altered
                var result = System.Windows.MessageBox.Show(
                    $"Table {settings.Schema}.{settings.Table} exists but does not match the required structure.\n\nWould you like to drop and recreate the table?\n\nWARNING: This will delete all existing data in the table!",
                    "Alter Table",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                    
                if (result != MessageBoxResult.Yes)
                {
                    LogWarning("Table structure is incorrect but user chose not to alter it");
                    UpdateStatusBar("Table structure mismatch: ACTION CANCELLED");
                    return;
                }
                
                // User confirmed to alter the table
                bool dropResult = await dbService.DropTableAsync();
                if (!dropResult)
                {
                    LogError($"Failed to drop table {settings.Schema}.{settings.Table}!");
                    UpdateStatusBar("Table alteration failed: DROP ERROR");
                    return;
                }
                
                bool createResult = await dbService.CreateTableAsync();
                if (createResult)
                {
                    LogMessage($"Table {settings.Schema}.{settings.Table} recreated with correct structure!");
                    UpdateStatusBar("Database table recreated: SUCCESS");
                }
                else
                {
                    LogError($"Failed to recreate table {settings.Schema}.{settings.Table}!");
                    UpdateStatusBar("Table recreation failed: ERROR");
                    return;
                }
            }
            else
            {
                LogMessage($"Table {settings.Schema}.{settings.Table} exists and has the correct structure!");
                UpdateStatusBar("Database table verified: SUCCESS");
            }
        }
        
        // Check row count
        long rowCount = await dbService.GetRowCountAsync();
        if (rowCount >= 0)
        {
            LogMessage($"Current row count: {rowCount}");
            RowCountText.Text = rowCount.ToString();
        }
    }

    private void ResetSettings()
    {
        // Reset to default values
        DbHost.Text = "localhost";
        DbPort.Text = "5432";
        DbUsername.Text = "";
        DbPassword.Password = "";
        DbName.Text = "postgres";
        DbSchema.Text = "public";
        DbTable.Text = "orf_logs";
        DbTimeout.Text = "30";
        
        LogDirectory.Text = "";
        LogFilePattern.Text = "orfee-{Date:yyyy-MM-dd}.log";
        PollingInterval.Text = "5";
        
        LogMessage("Settings reset to defaults");
    }

    // Event handlers for the LogFileWatcher events
    
    private void OnProcessingStatusChanged(string currentFile, int count, long position)
    {
        Dispatcher.Invoke(() =>
        {
            CurrentFileText.Text = currentFile;
            CurrentPositionText.Text = position.ToString();
            LinesProcessedText.Text = _logFileWatcher.TotalLinesProcessed.ToString();
            ProcessingStatusText.Text = "Running";
            
            // Log both the current batch and total count
            LogMessage($"Processed {count} entries from {currentFile} (Total: {_logFileWatcher.TotalLinesProcessed})");
        });
    }
    
    private void OnEntriesProcessed(IEnumerable<OrfLogEntry> entries)
    {
        // We could do additional UI updates here if needed
        // For now, the OnProcessingStatusChanged handler is enough
    }

    private void OnErrorOccurred(string component, string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogError($"[{component}] {message}");
            
            // Update UI elements based on the component that had an error
            if (component == "Database")
            {
                DbStatusText.Text = "Error";
                LastErrorText.Text = message;
            }
            else if (component == "File System")
            {
                // Update file status indicators if needed
                LastErrorText.Text = message;
            }
        });
    }

    // Timer for refreshing the position display
    private void StartPositionUpdateTimer()
    {
        // Stop any existing timer
        StopPositionUpdateTimer();
        
        // Create a new timer that updates the position display every 2 seconds
        _positionUpdateTimer = new System.Threading.Timer(
            _ => Dispatcher.Invoke(UpdatePositionDisplay),
            null,
            TimeSpan.FromSeconds(0),  // Start immediately
            TimeSpan.FromSeconds(2)); // Update every 2 seconds
        
        _logger.LogDebug("Position update timer started");
    }
    
    private void StopPositionUpdateTimer()
    {
        if (_positionUpdateTimer != null)
        {
            _positionUpdateTimer.Dispose();
            _positionUpdateTimer = null;
            _logger.LogDebug("Position update timer stopped");
        }
    }
    
    private void UpdatePositionDisplay()
    {
        if (!_isProcessing || _logFileWatcher == null)
            return;
            
        try
        {
            // Track if anything changed to minimize UI updates
            bool hasChanges = false;
            
            // Update the current file if needed
            if (CurrentFileText.Text != _logFileWatcher.CurrentFile)
            {
                CurrentFileText.Text = _logFileWatcher.CurrentFile;
                hasChanges = true;
            }
            
            // Update position if needed
            string newPosition = _logFileWatcher.CurrentPosition.ToString();
            if (CurrentPositionText.Text != newPosition)
            {
                CurrentPositionText.Text = newPosition;
                hasChanges = true;
            }
            
            // Update lines processed if needed
            string newLineCount = _logFileWatcher.TotalLinesProcessed.ToString();
            if (LinesProcessedText.Text != newLineCount)
            {
                LinesProcessedText.Text = newLineCount;
                hasChanges = true;
            }
            
            // Log updates but only if there's something to report and values changed
            if (hasChanges && _logFileWatcher.CurrentPosition > 0 && !string.IsNullOrEmpty(_logFileWatcher.CurrentFile))
            {
                _logger.LogTrace("Position display updated - File: {File}, Position: {Position}, Lines: {Lines}",
                    _logFileWatcher.CurrentFile, _logFileWatcher.CurrentPosition, _logFileWatcher.TotalLinesProcessed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating position display: {Message}", ex.Message);
        }
    }

    private void OnPositionsLoaded(bool positionsLoaded)
    {
        Dispatcher.Invoke(() =>
        {
            if (!positionsLoaded && _logFileWatcher != null)
            {
                // If positions were reset (not loaded from file), reset the UI display
                _logFileWatcher.ResetPositionInfo();
                CurrentFileText.Text = string.Empty;
                CurrentPositionText.Text = "0";
                LinesProcessedText.Text = "0";
                _logger.LogInformation("Position display has been reset due to missing positions file");
            }
        });
    }

    /// <summary>
    /// Applies log level filtering based on the toggle button states.
    /// </summary>
    private void ApplyLogFilter()
    {
        try
        {
            _logger.LogDebug("Applying log filter");
            
            // Add a debug message to help with troubleshooting
            string filterState = $"Filter state - Info: {(ShowStatusToggle.IsChecked == true ? "ON" : "OFF")}, " +
                                 $"Warnings: {(ShowWarningsToggle.IsChecked == true ? "ON" : "OFF")}, " +
                                 $"Errors: {(ShowErrorsToggle.IsChecked == true ? "ON" : "OFF")}";
            _logger.LogDebug(filterState);
            
            // Get the original text from the Tag property
            string originalText = LogTextBox.Tag as string;
            
            // If no original text is available, there's nothing to filter
            if (string.IsNullOrEmpty(originalText))
            {
                _logger.LogDebug("No original text to filter");
                return;
            }
            
            // If all filters are on, just show everything
            if (ShowStatusToggle.IsChecked == true && 
                ShowWarningsToggle.IsChecked == true && 
                ShowErrorsToggle.IsChecked == true)
            {
                // Preserve the current text if it matches the original
                if (LogTextBox.Text != originalText)
                {
                    LogTextBox.Text = originalText;
                    LogTextBox.ScrollToEnd();
                }
                return;
            }
            
            // Store the current caret position
            int caretPosition = LogTextBox.CaretIndex;
            
            // Clear the current display
            LogTextBox.Clear();
            
            // Split the original text into lines
            string[] lines = originalText.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            
            // Count how many lines are displayed after filtering
            int displayedLines = 0;
            
            // Filter lines based on toggle states
            foreach (string line in lines)
            {
                bool shouldDisplay = false;
                
                // Check if line contains INFO and if we should show status messages
                if (line.Contains("INFO:") && ShowStatusToggle.IsChecked == true)
                {
                    shouldDisplay = true;
                }
                // Check if line contains WARNING and if we should show warnings
                else if (line.Contains("WARNING:") && ShowWarningsToggle.IsChecked == true)
                {
                    shouldDisplay = true;
                }
                // Check if line contains ERROR and if we should show errors
                else if (line.Contains("ERROR:") && ShowErrorsToggle.IsChecked == true)
                {
                    shouldDisplay = true;
                }
                
                // Add the line to the display if it matches the filter criteria
                if (shouldDisplay)
                {
                    LogTextBox.AppendText(line + Environment.NewLine);
                    displayedLines++;
                }
            }
            
            // Log the filter result
            _logger.LogDebug("Log filter applied - Displayed {DisplayedCount} of {TotalCount} lines", 
                displayedLines, lines.Length);
            
            // Scroll to the end to show latest entries
            LogTextBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying log filter: {Message}", ex.Message);
        }
    }

    #endregion
}
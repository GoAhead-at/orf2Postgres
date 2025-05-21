using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;
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
using System.Windows.Controls;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows.Threading;
using System.ServiceProcess;
using System.Threading;

namespace Log2Postgres;

// Define IpcMessage structure (can be shared with service later)
public class IpcMessage<T>
{
    public string Type { get; set; } = string.Empty;
    public T? Payload { get; set; }
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, IAsyncDisposable
{
    private const int MaxUiLogLines = 500; // Added constant for max log lines

    private const string DefaultAppSettingsJson = @"{
  ""Serilog"": {
    ""MinimumLevel"": {
      ""Default"": ""Debug"",
      ""Override"": {
        ""Microsoft"": ""Information"",
        ""System"": ""Information""
      }
    },
    ""WriteTo"": [
      {
        ""Name"": ""Console"",
        ""Args"": {
          ""outputTemplate"": ""[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}""
        }
      }
    ],
    ""Enrich"": [ ""FromLogContext"", ""WithThreadId"", ""WithMachineName"", ""WithProcessId"" ]
  },
  ""DatabaseSettings"": {
    ""Host"": ""localhost"",
    ""Port"": ""5432"",
    ""Username"": ""orf"",
    ""Password"": """",
    ""Database"": ""orf"",
    ""Schema"": ""orf"",
    ""Table"": ""orf_logs"",
    ""ConnectionTimeout"": 30
  },
  ""LogMonitorSettings"": {
    ""BaseDirectory"": """",
    ""LogFilePattern"": ""orfee-{Date:yyyy-MM-dd}.log"",
    ""PollingIntervalSeconds"": 5
  }
}";

    private readonly IConfiguration _configuration = null!;
    private readonly PostgresService _postgresService = null!;
    private readonly LogFileWatcher _logFileWatcher = null!;
    private readonly ILogger<MainWindow> _logger = null!;
    private readonly PasswordEncryption _passwordEncryption = null!;
    private readonly PositionManager _positionManager = null!;
    private readonly IIpcService _ipcService = null!; // Added IIpcService field
    private bool _isProcessing = false;
    private bool _hasUnsavedChanges = false;
    private System.Threading.Timer? _positionUpdateTimer;
    private DispatcherTimer? _serviceStatusTimer; // Restored declaration

#pragma warning disable CS8618
    public string ServiceOperationalState { get; set; } = string.Empty;
    public string CurrentFile { get; set; } = string.Empty;
    public string LastErrorMessage { get; set; } = string.Empty;
#pragma warning restore CS8618

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            var app = (App)System.Windows.Application.Current;
            _configuration = app.GetConfiguration();
            _logger = app.GetService<ILoggerFactory>().CreateLogger<MainWindow>();
            _postgresService = app.GetService<PostgresService>();
            _logFileWatcher = app.GetService<LogFileWatcher>();
            _passwordEncryption = app.GetService<PasswordEncryption>();
            _positionManager = app.GetService<PositionManager>();
            _ipcService = app.GetService<IIpcService>(); // Get IpcService from DI

            _logger.LogInformation("MainWindow initialized");

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            if (_logFileWatcher != null)
            {
                _logFileWatcher.ProcessingStatusChanged += OnProcessingStatusChanged;
                _logFileWatcher.EntriesProcessed += OnEntriesProcessed;
                _logFileWatcher.ErrorOccurred += OnErrorOccurred;
                _logger.LogDebug("Registered for LogFileWatcher events");
            }
            else { _logger.LogWarning("LogFileWatcher service is null, events not registered"); }

            if (_positionManager != null)
            {
                _positionManager.PositionsLoaded += OnPositionsLoaded;
                if (!_positionManager.PositionsFileExists() && _logFileWatcher != null)
                {
                    _logFileWatcher.ResetPositionInfo();
                    _logger.LogInformation("Positions file not found, position info has been reset");
                }
            }
            else { _logger.LogWarning("PositionManager service is null, events not registered"); }

            // Subscribe to IPC Service events
            _ipcService.PipeConnected += OnIpcPipeConnected;
            _ipcService.PipeDisconnected += OnIpcPipeDisconnected;
            _ipcService.ServiceStatusReceived += OnIpcServiceStatusReceived;
            _ipcService.LogEntriesReceived += OnIpcLogEntriesReceived;
            _logger.LogDebug("Registered for IpcService events (including LogEntriesReceived).");
        }
        catch (Exception ex)
        {
            // Corrected logger fallback: Use _logger directly. If it's null due to earlier DI failure, logging here might also fail or be NOP.
            var tempLogger = _logger; // If _logger is null here, it means DI for logger failed.
            tempLogger?.LogCritical(ex, "FATAL: MainWindow constructor failed.");
            System.Windows.MessageBox.Show($"Critical error during MainWindow initialization: {ex.Message}\n\nApplication will exit.", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Shutdown(1);
            }
            else
            {
                Environment.Exit(1);
            }
        }
    }

    private Task OnIpcPipeConnected()
    {
        _logger.LogInformation("IPC Pipe Connected (event from IpcService).");
        Dispatcher.Invoke(() => UpdateServiceControlUi());
        return Task.CompletedTask;
    }

    private Task OnIpcPipeDisconnected()
    {
        _logger.LogInformation("IPC Pipe Disconnected (event from IpcService).");
        Dispatcher.Invoke(() => UpdateServiceControlUi());
        return Task.CompletedTask;
    }

    private Task OnIpcServiceStatusReceived(PipeServiceStatus status)
    {
        _logger.LogDebug("ServiceStatusReceived (event from IpcService): State={ServiceState}, File={File}, Position={Position}, Lines={Lines}, IsProcessing={IsProcessing}", 
            status.ServiceOperationalState, status.CurrentFile, status.CurrentPosition, status.TotalLinesProcessedSinceStart, status.IsProcessing);

        Dispatcher.Invoke(() =>
        {
            // We have IPC status, so service is available via IPC.
            // We still need SC status for install/start/stop buttons.
            bool isServiceInstalled = false;
            ServiceControllerStatus currentScStatus = ServiceControllerStatus.Stopped;
            try
            {
                using (var sc = new ServiceController(App.WindowsServiceName)) // Use constant from App.xaml.cs
                {
                    isServiceInstalled = true;
                    currentScStatus = sc.Status;
                }
            }
            catch (InvalidOperationException) 
            {
                _logger.LogTrace("SC status check in OnIpcServiceStatusReceived: Service not installed.");
                isServiceInstalled = false; 
            }
            catch (Exception ex) 
            { 
                _logger.LogError(ex, "Error getting SC status in OnIpcServiceStatusReceived");
                // Keep defaults: isServiceInstalled = false, currentScStatus = ServiceControllerStatus.Stopped;
            }

            // Call the main UI update method WITH the received pipeStatus
            // The 'true' for serviceAvailable indicates IPC is connected and providing this status.
            UpdateUiWithServiceStatus(status, true, currentScStatus, isServiceInstalled);
        });
        return Task.CompletedTask;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        _logger.LogInformation("MainWindow loaded");
        LoadSettings();
        StartPositionUpdateTimer();

        _serviceStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _serviceStatusTimer.Tick += ServiceStatusTimer_Tick;
        _serviceStatusTimer.Start();
        _logger.LogInformation("Service status timer started for IPC.");
        UpdateServiceControlUi();
    }

    private async void ServiceStatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (!_ipcService.IsConnected)
            {
                _logger.LogDebug("ServiceStatusTimer: IPC not connected, attempting to connect.");
                try
                {
                    await _ipcService.ConnectAsync();
                    if (_ipcService.IsConnected)
                    {
                        await _ipcService.SendServiceStatusRequestAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ServiceStatusTimer: Failed to connect to IPC service.");
                }
            }
            else
            {
                await _ipcService.SendServiceStatusRequestAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ServiceStatusTimer_Tick.");
        }
        UpdateServiceControlUi();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _logger.LogInformation("MainWindow closing");
        _serviceStatusTimer?.Stop();
        
        if (_ipcService is IAsyncDisposable asyncDisposableIpc)
        {
            await asyncDisposableIpc.DisposeAsync();
        }
        else if (_ipcService is IDisposable disposableIpc)
        {
            disposableIpc.Dispose();
        }

        StopPositionUpdateTimer();

        if (_logFileWatcher != null)
        {
            _logFileWatcher.ProcessingStatusChanged -= OnProcessingStatusChanged;
            _logFileWatcher.EntriesProcessed -= OnEntriesProcessed;
            _logFileWatcher.ErrorOccurred -= OnErrorOccurred;
        }
        if (_positionManager != null)
        {
            _positionManager.PositionsLoaded -= OnPositionsLoaded;
        }
        
        Serilog.Log.CloseAndFlush();

        if (_hasUnsavedChanges)
        {
            var result = System.Windows.MessageBox.Show("There are unsaved changes. Do you want to save them before closing?",
                                           "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            { SaveSettings(); }
            else if (result == MessageBoxResult.Cancel)
            { e.Cancel = true; }
        }
    }

    private string SecurePassword()
    {
        try
        {
            string plainPassword = DatabasePassword.Password; // Assumes DatabasePassword is a XAML element
            if (!string.IsNullOrEmpty(plainPassword))
            {
                return _passwordEncryption.EncryptPassword(plainPassword);
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error securing password");
            return DatabasePassword.Password; // Fallback, not ideal
        }
    }

    private async void LoadSettings()
    {
        _logger.LogInformation("Loading settings...");
        try
        {
            DatabaseHost.Text = _configuration["DatabaseSettings:Host"];
            DatabasePort.Text = _configuration["DatabaseSettings:Port"];
            DatabaseUsername.Text = _configuration["DatabaseSettings:Username"];

            string? configuredPassword = _configuration["DatabaseSettings:Password"];
            if (!string.IsNullOrEmpty(configuredPassword))
            {
                DatabasePassword.Password = _passwordEncryption.IsEncrypted(configuredPassword) ?
                                            _passwordEncryption.DecryptPassword(configuredPassword) :
                                            configuredPassword;
            }
            else
            {
                DatabasePassword.Password = string.Empty;
            }

            DatabaseName.Text = _configuration["DatabaseSettings:Database"];
            DatabaseSchema.Text = _configuration["DatabaseSettings:Schema"];
            DatabaseTable.Text = _configuration["DatabaseSettings:Table"];
            ConnectionTimeout.Text = _configuration["DatabaseSettings:ConnectionTimeout"];

            LogDirectory.Text = _configuration["LogMonitorSettings:BaseDirectory"];
            LogFilePattern.Text = _configuration["LogMonitorSettings:LogFilePattern"];
            PollingInterval.Text = _configuration["LogMonitorSettings:PollingIntervalSeconds"];

            var filterSection = _configuration.GetSection("LogFilters");
            InfoFilterToggle.IsChecked = filterSection.GetValue<bool?>("Info") ?? true;
            WarningFilterToggle.IsChecked = filterSection.GetValue<bool?>("Warnings") ?? true;
            ErrorFilterToggle.IsChecked = filterSection.GetValue<bool?>("Errors") ?? true;

            _logger.LogInformation("Settings loaded from configuration");
            _hasUnsavedChanges = false;
            UpdateStatusBar("Settings loaded.");
            await UpdateDatabaseStatus();
            ApplyLogFilter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
            System.Windows.MessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveSettings()
    {
        _logger.LogInformation("Saving settings...");
        try
        {
            string appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            string json;
            if (File.Exists(appSettingsPath))
            {
                json = await File.ReadAllTextAsync(appSettingsPath);
            }
            else
            {
                json = DefaultAppSettingsJson;
            }

            dynamic? jsonObj;
            try
            {
                // Attempt to parse existing or default JSON. Result is typically JObject.
                jsonObj = JsonConvert.DeserializeObject<dynamic>(json);
                if (jsonObj == null) // Should only happen if json string is literally "null"
                {
                    _logger.LogWarning("Deserialized appsettings content resulted in null, re-initializing from DefaultAppSettingsJson.");
                    jsonObj = JsonConvert.DeserializeObject<dynamic>(DefaultAppSettingsJson);
                }
            }
            catch (JsonReaderException ex)
            {
                _logger.LogError(ex, $"Failed to parse existing appsettings.json (or DefaultAppSettingsJson). Content was: {json.Substring(0, Math.Min(json.Length, 200))}. Re-initializing from DefaultAppSettingsJson structure.");
                jsonObj = JsonConvert.DeserializeObject<dynamic>(DefaultAppSettingsJson); // Fallback to default structure
            }
            

            // Ensure Serilog settings exist (they are in DefaultAppSettingsJson)
            // JObject property access via dynamic behaves like indexer: jsonObj["Serilog"]
            if (jsonObj.Serilog == null || (jsonObj.Serilog is JValue serilogVal && serilogVal.Type == JTokenType.Null))
            {
                _logger.LogWarning("Serilog section missing or null in JSON object, attempting to restore from default.");
                dynamic defaultJsonForSerilog = JsonConvert.DeserializeObject<dynamic>(DefaultAppSettingsJson);
                jsonObj.Serilog = defaultJsonForSerilog.Serilog;
            }

            // DatabaseSettings section should exist if DefaultAppSettingsJson was used or appsettings.json is valid.
            // If it could be missing, ensure it's a JObject:
            if (jsonObj.DatabaseSettings == null || (jsonObj.DatabaseSettings is JValue dbVal && dbVal.Type == JTokenType.Null))
            {
                _logger.LogWarning("DatabaseSettings section missing or null, creating new JObject for it.");
                jsonObj.DatabaseSettings = new JObject();
            }
            jsonObj.DatabaseSettings.Host = DatabaseHost.Text;
            jsonObj.DatabaseSettings.Port = int.Parse(DatabasePort.Text);
            jsonObj.DatabaseSettings.Username = DatabaseUsername.Text;
            jsonObj.DatabaseSettings.Password = SecurePassword();
            jsonObj.DatabaseSettings.Database = DatabaseName.Text;
            jsonObj.DatabaseSettings.Schema = DatabaseSchema.Text;
            jsonObj.DatabaseSettings.Table = DatabaseTable.Text;
            jsonObj.DatabaseSettings.ConnectionTimeout = int.Parse(ConnectionTimeout.Text);

            // LogMonitorSettings section should also exist from defaults.
            // If it could be missing, ensure it's a JObject:
            if (jsonObj.LogMonitorSettings == null || (jsonObj.LogMonitorSettings is JValue lmVal && lmVal.Type == JTokenType.Null))
            {
                _logger.LogWarning("LogMonitorSettings section missing or null, creating new JObject for it.");
                jsonObj.LogMonitorSettings = new JObject();
            }
            jsonObj.LogMonitorSettings.BaseDirectory = LogDirectory.Text;
            jsonObj.LogMonitorSettings.LogFilePattern = LogFilePattern.Text;
            jsonObj.LogMonitorSettings.PollingIntervalSeconds = int.Parse(PollingInterval.Text);

            // LogFilters section is NOT in DefaultAppSettingsJson, so it's expected to be missing initially.
            if (jsonObj.LogFilters == null || (jsonObj.LogFilters is JValue lfVal && lfVal.Type == JTokenType.Null))
            { 
                jsonObj.LogFilters = new JObject(); // Create as JObject
            }
            jsonObj.LogFilters.Info = InfoFilterToggle.IsChecked ?? true;
            jsonObj.LogFilters.Warnings = WarningFilterToggle.IsChecked ?? true;
            jsonObj.LogFilters.Errors = ErrorFilterToggle.IsChecked ?? true;

            string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
            await File.WriteAllTextAsync(appSettingsPath, output);

            _logger.LogInformation("Settings saved successfully to {Path}", appSettingsPath);
            UpdateStatusBar("Settings saved.");
            _hasUnsavedChanges = false;

            if (_configuration is IConfigurationRoot configurationRoot)
            {
                _logger.LogInformation("Attempting to force IConfigurationRoot.Reload()");
                configurationRoot.Reload();
                _logger.LogInformation("IConfigurationRoot.Reload() called.");
            }
            else
            {
                _logger.LogWarning("IConfiguration instance is not IConfigurationRoot, cannot force reload.");
            }

            bool serviceInstalled = false;
            ServiceControllerStatus serviceStatus = ServiceControllerStatus.Stopped;
            try
            {
                using var sc = new ServiceController("Log2PostgresService");
                serviceInstalled = true;
                serviceStatus = sc.Status;
            }
            catch { /* service not installed or error */ }

            bool wasLocalProcessingIntended = _isProcessing && !serviceInstalled;

            var newLogMonitorSettings = new LogMonitorSettings
            {
                BaseDirectory = LogDirectory.Text,
                LogFilePattern = LogFilePattern.Text,
                PollingIntervalSeconds = int.TryParse(PollingInterval.Text, out int interval) ? interval : 5
            };
            await ReloadConfiguration(newLogMonitorSettings, wasLocalProcessingIntended);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            System.Windows.MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ReloadConfiguration(LogMonitorSettings settingsToApply, bool wasLocalProcessingIntended)
    {
        _logger.LogDebug("ReloadConfiguration: Entered method. Was local processing intended to be active: {WasLocalProcessingIntended}", wasLocalProcessingIntended);
        _logger.LogDebug("ReloadConfiguration: Settings to apply to LogFileWatcher: BaseDirectory='{BaseDirectory}', Pattern='{Pattern}', Interval={Interval}",
            settingsToApply.BaseDirectory, settingsToApply.LogFilePattern, settingsToApply.PollingIntervalSeconds);

        _logger.LogInformation("ReloadConfiguration: LogFileWatcher.IsProcessing BEFORE UpdateSettingsAsync: {IsProcessingState}", _logFileWatcher.IsProcessing);
        _logger.LogInformation("ReloadConfiguration: Applying settings to local LogFileWatcher via UpdateSettingsAsync.");
        await _logFileWatcher.UpdateSettingsAsync(settingsToApply);
        _logger.LogInformation("ReloadConfiguration: UpdateSettingsAsync completed for local LogFileWatcher.");
        _logger.LogInformation("ReloadConfiguration: LogFileWatcher.IsProcessing AFTER UpdateSettingsAsync: {IsProcessingState}", _logFileWatcher.IsProcessing);
        _logger.LogInformation("ReloadConfiguration: LogFileWatcher.IsProcessing (continuous state) after UpdateSettingsAsync: {IsProcessingState}", _logFileWatcher.IsProcessing);

        if (wasLocalProcessingIntended && !_logFileWatcher.IsProcessing)
        {
            _logger.LogInformation("ReloadConfiguration: Local processing was intended and is not active. Attempting to start local processing.");
            await StartProcessingAsync();
        }
        else if (!wasLocalProcessingIntended && _logFileWatcher.IsProcessing)
        {
            _logger.LogInformation("ReloadConfiguration: Local processing was NOT intended but is active. Stopping local processing.");
            StopProcessing();
        }
        else
        {
            _logger.LogInformation("ReloadConfiguration: Local processing state seems consistent with intent or no change needed.");
        }

        if (!wasLocalProcessingIntended && _logFileWatcher.IsProcessing)
        {
            _logger.LogInformation("ReloadConfiguration: Resetting position info to undo any unintended processing burst and ensure no progress is saved from it.");
            _logFileWatcher.ResetPositionInfo();
            _logger.LogDebug("Position info has been reset");
        }

        ApplyLogFilter();
        UpdateServiceControlUi();
        _logger.LogDebug("ReloadConfiguration: Exiting method normally. Final _isProcessing state: {IsProcessingState}", _isProcessing);
        _logger.LogInformation("ReloadConfiguration completed. Now checking service status for IPC.");

        bool serviceInstalled = false;
        ServiceControllerStatus serviceStatus = ServiceControllerStatus.Stopped;
        try
        {
            using var sc = new ServiceController("Log2PostgresService");
            serviceInstalled = true;
            serviceStatus = sc.Status;
        }
        catch { /* service not installed or error getting status */ }

        if (serviceInstalled && (serviceStatus == ServiceControllerStatus.Running || serviceStatus == ServiceControllerStatus.StartPending) && _ipcService.IsConnected)
        {
            _logger.LogInformation("Service is running and IPC connected. Sending settings update to service.");
            try
            {
                await _ipcService.SendSettingsAsync(settingsToApply);
                _logger.LogInformation("Successfully sent settings update command to service via IPC. (Assuming success if no exception)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending settings update to service via IPC.");
            }
        }
        else
        {
            _logger.LogInformation("Service not running/installed or IPC not connected. Settings reloaded locally. ServiceInstalled={Installed}, ServiceStatus={Status}, IpcConnected={IpcConn}",
                serviceInstalled, serviceStatus, _ipcService.IsConnected);
        }
        ApplyLogFilter();
    }

    private void AppendAndTrimLog(string formattedMessage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendAndTrimLog(formattedMessage));
            return;
        }

        string currentFullLog = (LogTextBox.Tag as string ?? "");
        currentFullLog += formattedMessage + Environment.NewLine;

        string[] linesArray = currentFullLog.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        
        var lineList = new List<string>(linesArray);

        // If the last element is an empty string due to a trailing NewLine,
        // remove it temporarily for correct counting, as it's not a log line itself.
        if (lineList.Count > 0 && string.IsNullOrEmpty(lineList.Last()) && currentFullLog.EndsWith(Environment.NewLine))
        {
            lineList.RemoveAt(lineList.Count - 1);
        }

        if (lineList.Count > MaxUiLogLines)
        {
            lineList.RemoveRange(0, lineList.Count - MaxUiLogLines);
        }
        
        string newTagContent = string.Join(Environment.NewLine, lineList);
        
        // If there are any lines, ensure the tag content ends with a newline for consistency.
        if (lineList.Any())
        {
            newTagContent += Environment.NewLine;
        }

        LogTextBox.Tag = newTagContent;
        ApplyLogFilter(); // ApplyLogFilter will handle clearing LogTextBox and repopulating from Tag, then ScrollToEnd.
    }

    private void LogMessage(string message)
    {
        // Note: Dispatcher check is handled by AppendAndTrimLog
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] INFO: {message}";
        AppendAndTrimLog(formattedMessage);
    }

    private void LogWarning(string message)
    {
        // Note: Dispatcher check is handled by AppendAndTrimLog
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] WARNING: {message}";
        AppendAndTrimLog(formattedMessage);
    }

    private void LogError(string message)
    {
        // Note: Dispatcher check is handled by AppendAndTrimLog
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] ERROR: {message}";
        AppendAndTrimLog(formattedMessage);

        // Update LastErrorText separately. This will also be on the UI thread
        // because AppendAndTrimLog ensures the whole calling context is on the UI thread.
        if (!Dispatcher.CheckAccess())
        {
             // This case should ideally not be hit if AppendAndTrimLog correctly marshals.
            Dispatcher.Invoke(() => LastErrorText.Text = message);
        }
        else
        {
            LastErrorText.Text = message;
        }
    }

    private void UpdateStatusBar(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateStatusBar(message));
            return;
        }

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
        LogMessage("Testing database connection (using current application settings)...");
        
        if (_postgresService == null)
        {
            LogError("PostgresService is not available. Cannot test connection.");
            UpdateStatusBar("Database connection test failed: SERVICE NOT AVAILABLE");
            DbStatusText.Text = "Error";
            return;
        }
        
        // Test the connection using the injected _postgresService
        bool result = await _postgresService.TestConnectionAsync();
        
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
        LogMessage("Verifying database table (using current application settings)...");
        
        if (_postgresService == null)
        {
            LogError("PostgresService is not available. Cannot verify table.");
            UpdateStatusBar("Database table verification failed: SERVICE NOT AVAILABLE");
            return;
        }
        
        // Test the connection first using the injected _postgresService
        bool connected = await _postgresService.TestConnectionAsync();
        
        if (!connected)
        {
            LogError("Database connection failed! Cannot verify/create table.");
            UpdateStatusBar("Database table verification failed: CONNECTION ERROR");
            return;
        }
        
        // Get current settings for messages, etc.
        var currentSettings = ((App)System.Windows.Application.Current).GetService<IOptionsMonitor<DatabaseSettings>>().CurrentValue;

        // First check if the table exists at all
        bool tableExists = await _postgresService.TableExistsAsync();
        
        if (!tableExists)
        {
            // Table doesn't exist - ask if it should be created
            var dr = System.Windows.MessageBox.Show(
                $"Table {currentSettings.Schema}.{currentSettings.Table} does not exist. Would you like to create it?",
                "Create Table",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
                
            if (dr != MessageBoxResult.Yes)
            {
                LogMessage("Table creation cancelled by user");
                UpdateStatusBar("Table creation cancelled");
                return;
            }
            
            // User confirmed to create the table
            bool createResult = await _postgresService.CreateTableAsync();
            if (createResult)
            {
                LogMessage($"Table {currentSettings.Schema}.{currentSettings.Table} created successfully!");
                UpdateStatusBar("Database table created: SUCCESS");
            }
            else
            {
                LogError($"Failed to create table {currentSettings.Schema}.{currentSettings.Table}!");
                UpdateStatusBar("Database table creation failed: ERROR");
                return;
            }
        }
        else
        {
            // Table exists - check if structure matches
            bool structureMatches = await _postgresService.ValidateTableStructureAsync();
            
            if (!structureMatches)
            {
                // Structure doesn't match - ask if it should be altered
                var drStruct = System.Windows.MessageBox.Show(
                    $"Table {currentSettings.Schema}.{currentSettings.Table} exists but does not match the required structure.\n\nWould you like to drop and recreate the table?\n\nWARNING: This will delete all existing data in the table!",
                    "Alter Table",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                    
                if (drStruct != MessageBoxResult.Yes)
                {
                    LogWarning("Table structure is incorrect but user chose not to alter it");
                    UpdateStatusBar("Table structure mismatch: ACTION CANCELLED");
                    return;
                }
                
                // User confirmed to alter the table
                bool dropResult = await _postgresService.DropTableAsync();
                if (!dropResult)
                {
                    LogError($"Failed to drop table {currentSettings.Schema}.{currentSettings.Table}!");
                    UpdateStatusBar("Table alteration failed: DROP ERROR");
                    return;
                }
                
                bool createResult = await _postgresService.CreateTableAsync();
                if (createResult)
                {
                    LogMessage($"Table {currentSettings.Schema}.{currentSettings.Table} recreated with correct structure!");
                    UpdateStatusBar("Database table recreated: SUCCESS");
                }
                else
                {
                    LogError($"Failed to recreate table {currentSettings.Schema}.{currentSettings.Table}!");
                    UpdateStatusBar("Table recreation failed: ERROR");
                    return;
                }
            }
            else
            {
                LogMessage($"Table {currentSettings.Schema}.{currentSettings.Table} exists and has the correct structure!");
                UpdateStatusBar("Database table verified: SUCCESS");
            }
        }
        
        // Check row count
        long rowCount = await _postgresService.GetRowCountAsync();
        if (rowCount >= 0)
        {
            LogMessage($"Current row count: {rowCount}");
            RowCountText.Text = rowCount.ToString();
        }
    }

    private void ResetSettings()
    {
        // Reset to default values
        DatabaseHost.Text = "localhost";
        DatabasePort.Text = "5432";
        DatabaseUsername.Text = "";
        DatabasePassword.Password = "";
        DatabaseName.Text = "postgres";
        DatabaseSchema.Text = "public";
        DatabaseTable.Text = "orf_logs";
        ConnectionTimeout.Text = "30";
        
        LogDirectory.Text = "";
        LogFilePattern.Text = "orfee-{Date:yyyy-MM-dd}.log";
        PollingInterval.Text = "5";
        
        LogMessage("Settings have been reset to default values.");
        _hasUnsavedChanges = true; // Values have changed and are not saved yet
        UpdateDbActionButtonsState(); // Update DB buttons based on reset settings
    }

    // Event handlers for the LogFileWatcher events
    
    private void OnProcessingStatusChanged(string currentFile, int count, long position)
    {
        Dispatcher.Invoke(() =>
        {
            if(CurrentFileText != null) CurrentFileText.Text = currentFile; 
            if(CurrentPositionText != null) CurrentPositionText.Text = position.ToString(); 
            if(LinesProcessedText != null) LinesProcessedText.Text = _logFileWatcher.TotalLinesProcessed.ToString(); 
            if(ProcessingStatusText != null) ProcessingStatusText.Text = "Running"; 
            LogMessage($"Processed {count} entries from {currentFile} (Total: {_logFileWatcher.TotalLinesProcessed})"); 
        });
    }
    
    private void OnEntriesProcessed(IEnumerable<OrfLogEntry> entries)
    {
        Dispatcher.Invoke(() =>
        {
            if (entries == null || !entries.Any()) return; 
            LogMessage($"--- Batch of {entries.Count()} Processed Entries from {entries.First().SourceFilename} ---"); 
            foreach (var entry in entries) 
            { 
                LogMessage(entry.IsSystemMessage ? 
                    $"Sys Msg: {entry.MessageId}, {entry.EventClass}, {entry.EventAction}, {entry.EventMsg}" : 
                    $"Entry: {entry.MessageId}, {entry.EventDateTime}, {entry.EventClass}, {entry.EventAction}, {entry.Sender}, '{entry.MsgSubject}'"); 
            } 
            LogMessage("--- End Batch ---"); 
        });
    }

    private void OnErrorOccurred(string component, string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogError($"[{component}] {message}"); 
            if (component == "Database" && DbStatusText != null) DbStatusText.Text = "Error"; 
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
                _logFileWatcher.ResetPositionInfo();
                if(CurrentFileText != null) CurrentFileText.Text = string.Empty;
                if(CurrentPositionText != null) CurrentPositionText.Text = "0";
                if(LinesProcessedText != null) LinesProcessedText.Text = "0";
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
            
            string filterState = $"Filter state - Info: {(InfoFilterToggle.IsChecked == true ? "ON" : "OFF")}, " +
                                 $"Warnings: {(WarningFilterToggle.IsChecked == true ? "ON" : "OFF")}, " +
                                 $"Errors: {(ErrorFilterToggle.IsChecked == true ? "ON" : "OFF")}";
            _logger.LogDebug(filterState);
            
            string? originalText = LogTextBox.Tag as string;
            if (string.IsNullOrEmpty(originalText) || LogTextBox == null || InfoFilterToggle == null || WarningFilterToggle == null || ErrorFilterToggle == null) return;

            bool showInfo = InfoFilterToggle.IsChecked == true;
            bool showWarn = WarningFilterToggle.IsChecked == true;
            bool showError = ErrorFilterToggle.IsChecked == true;

            if (showInfo && showWarn && showError)
            {
                if(LogTextBox.Text != originalText) LogTextBox.Text = originalText;
                LogTextBox.ScrollToEnd();
                return;
            }
            
            LogTextBox.Clear();
            string[] lines = originalText.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                bool display = false;
                if (line.Contains("INFO:") && showInfo) display = true;
                else if (line.Contains("WARNING:") && showWarn) display = true;
                else if (line.Contains("ERROR:") && showError) display = true;
                if (display) LogTextBox.AppendText(line + Environment.NewLine);
            }
            LogTextBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying log filter: {Message}", ex.Message);
        }
    }

    private async Task StartProcessingAsync()
    {
        _logger.LogInformation("Attempting to start local processing (UI managed)...");

        if (_hasUnsavedChanges)
        {
            _logger.LogWarning("StartProcessingAsync: Aborted due to unsaved changes.");
            System.Windows.MessageBox.Show("You have unsaved configuration changes. Please save them before starting processing.", "Unsaved Changes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_logFileWatcher == null)
        {
            LogError("LogFileWatcher service is not available. Cannot start processing locally.");
            System.Windows.MessageBox.Show("LogFileWatcher service is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_isProcessing) 
        {
            LogWarning("Local processing is already considered active by the UI.");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(LogDirectory.Text) || !Directory.Exists(LogDirectory.Text))
            {
                LogError("Log directory is not configured or does not exist. Please set a valid directory.");
                System.Windows.MessageBox.Show("Log directory is not configured or does not exist. Please set a valid directory in the settings.", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            UpdateStatusBar("Starting local processing...");
            // Button states will be managed by UpdateServiceControlUi after _isProcessing changes

            await _logFileWatcher.UIManagedStartProcessingAsync();
            
            _isProcessing = _logFileWatcher.IsProcessing; 

            if (_isProcessing)
            {
                LogMessage("Local processing started successfully.");
                UpdateStatusBar("Local processing started.");
                StartPositionUpdateTimer(); 
            }
            else
            {
                LogError("Failed to start local processing. Check logs. It might be configured to run as a service only.");
                UpdateStatusBar("Failed to start local processing. Check logs.");
            }
        }
        catch (Exception ex)
        {
            _isProcessing = false; 
            LogError($"Error starting local processing: {ex.Message}");
            UpdateStatusBar($"Error starting local processing: {ex.Message}");
        }
        UpdateServiceControlUi(); // Refresh UI based on new _isProcessing state
    }

    private void StopProcessing()
    {
        _logger.LogInformation("Attempting to stop local processing (UI managed)...");
        if (_logFileWatcher == null)
        {
            LogError("LogFileWatcher service is not available. Cannot stop local processing.");
            return;
        }

        if (!_isProcessing) 
        {
            LogWarning("Local processing is not considered active by the UI.");
            // If watcher is somehow still processing, try to stop it.
            if (_logFileWatcher.IsProcessing) { _logFileWatcher.UIManagedStopProcessing(); }
            return;
        }

        try
        {
            UpdateStatusBar("Stopping local processing...");
            
            _logFileWatcher.UIManagedStopProcessing();
            _isProcessing = _logFileWatcher.IsProcessing; 

            if (!_isProcessing) 
            {
                LogMessage("Local processing stopped successfully.");
                UpdateStatusBar("Local processing stopped.");
            }
            else
            {
                LogError("Failed to stop local processing. Check logs.");
                UpdateStatusBar("Failed to stop local processing. Check logs.");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error stopping local processing: {ex.Message}");
            UpdateStatusBar($"Error stopping local processing: {ex.Message}");
        }
        finally
        {
            StopPositionUpdateTimer();
            // UpdateServiceControlUi will be called by StopBtn_Click which called this.
            // Ensure UI state reflects that processing is stopped for local mode.
            if (!_isProcessing)
            {
                 CurrentFileText.Text = "N/A";
                 CurrentPositionText.Text = "0";
                 // LinesProcessedText could be kept or reset, depends on desired behavior for local mode.
            }
        }
        // No direct call to UpdateServiceControlUi() here, it's called by the click handler after this returns.
    }

    // Method to update DB action buttons state
    private void UpdateDbActionButtonsState()
    {
        bool canTestOrVerify = !string.IsNullOrWhiteSpace(DatabaseHost.Text) &&
                               !string.IsNullOrWhiteSpace(DatabasePort.Text) &&
                               !string.IsNullOrWhiteSpace(DatabaseUsername.Text) &&
                               !string.IsNullOrWhiteSpace(DatabaseName.Text);

        TestConnectionBtn.IsEnabled = canTestOrVerify;
        VerifyTableBtn.IsEnabled = canTestOrVerify;
        // _logger?.LogDebug($"UpdateDbActionButtonsState: CanTestOrVerify = {canTestOrVerify}");
    }

    private void ConfigSetting_Changed(object sender, TextChangedEventArgs e)
    {
        _hasUnsavedChanges = true;
        if (this.IsLoaded) // Check if the window is fully loaded
        {
            UpdateDbActionButtonsState(); // Update DB buttons as text change might be one of theirs
        }
    }

    private void DatabasePassword_Changed(object sender, RoutedEventArgs e)
    {
        _hasUnsavedChanges = true;
        // No need to call UpdateDbActionButtonsState here as password doesn't gate button enablement
    }

    private void UpdateUiWithServiceStatus(PipeServiceStatus? pipeStatus, bool serviceAvailable, ServiceControllerStatus scStatus, bool isInstalled)
    {
        if (pipeStatus != null) // IPC is connected and sent status
        {
            CurrentFileText.Text = pipeStatus.CurrentFile ?? "N/A";
            CurrentPositionText.Text = pipeStatus.CurrentPosition.ToString();
            LinesProcessedText.Text = pipeStatus.TotalLinesProcessedSinceStart.ToString();
            LastErrorText.Text = pipeStatus.LastErrorMessage ?? "None";
            ProcessingStatusText.Text = pipeStatus.IsProcessing ? "Processing" : "Idle";
            
            PipeStatusIndicator.Background = System.Windows.Media.Brushes.Green;
            PipeStatusTextBlock.Text = "Connected (Processing)"; 
        }
        else // IPC not connected or no status received via IPC
        {
            // Clear pipe-dependent fields or set to defaults for non-connected state
            CurrentFileText.Text = "N/A";
            CurrentPositionText.Text = "N/A"; // Or could show last known if desired and stored
            LinesProcessedText.Text = "N/A";
            // LastErrorText might be preserved or cleared depending on desired behavior
            ProcessingStatusText.Text = isInstalled ? scStatus.ToString() : "Not Installed"; 

            if (_ipcService.IsConnected) // Should ideally not happen if pipeStatus is null, but as a safeguard
            {
                 PipeStatusIndicator.Background = System.Windows.Media.Brushes.Green; // Connected but no status data?
                 PipeStatusTextBlock.Text = "Connected (Idle)";
            }
            else // IPC is definitely not connected
            {
                PipeStatusIndicator.Background = System.Windows.Media.Brushes.Gray;
                if (!isInstalled)
                {
                    PipeStatusTextBlock.Text = "Not Installed";
                }
                else
                {
                    // Service is installed but IPC not connected, show SC status
                    PipeStatusTextBlock.Text = scStatus.ToString(); 
                }
            }
        }
        
        StartBtn.IsEnabled = isInstalled && scStatus != ServiceControllerStatus.Running && scStatus != ServiceControllerStatus.StartPending;
        StopBtn.IsEnabled = isInstalled && (scStatus == ServiceControllerStatus.Running || scStatus == ServiceControllerStatus.StartPending);
        InstallBtn.IsEnabled = !isInstalled; 
        UninstallBtn.IsEnabled = isInstalled; 
        
        // Redundant background/text set for PipeStatusIndicator/TextBlock based on _ipcService.IsConnected already handled above.
        // If pipeStatus is null, the 'else' block above sets these.
        // If pipeStatus is not null, the 'if (pipeStatus != null)' block sets these.
        // This simplified line can be removed or kept if it clarifies the default disconnected visual when no pipe status comes through.
        // PipeStatusIndicator.Background = _ipcService.IsConnected ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red; 
        // PipeStatusTextBlock.Text = _ipcService.IsConnected ? "Connected" : "Disconnected";

        UpdateDbActionButtonsState();
    }

    private void UpdateServiceControlUi()
    {
        bool isServiceInstalled = false;
        ServiceControllerStatus currentScStatus = ServiceControllerStatus.Stopped;

        try
        {
            using (var sc = new ServiceController(App.WindowsServiceName)) // Use constant
            {
                isServiceInstalled = true;
                currentScStatus = sc.Status;
            }
        }
        catch (InvalidOperationException) // Service not installed
        {
            isServiceInstalled = false;
            currentScStatus = ServiceControllerStatus.Stopped; 
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service controller status.");
            isServiceInstalled = false; // Assume not installed or error state
            currentScStatus = ServiceControllerStatus.Stopped;
        }

        UpdateUiWithServiceStatus(null, _ipcService.IsConnected, currentScStatus, isServiceInstalled);
        
        bool isAdmin = App.IsAdministrator();
        if (InstallBtn != null) InstallBtn.ToolTip = isAdmin ? "Install the Windows service." : "Requires Administrator privileges.";
        if (UninstallBtn != null) UninstallBtn.ToolTip = isAdmin ? "Uninstall the Windows service." : "Requires Administrator privileges.";
        if (StartBtn != null) StartBtn.ToolTip = isAdmin ? "Start the Windows service." : "Requires Administrator privileges.";
        if (StopBtn != null) StopBtn.ToolTip = isAdmin ? "Stop the Windows service." : "Requires Administrator privileges.";
    }

    private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
    {
        await TestDatabaseConnection();
    }

    private async void VerifyTableBtn_Click(object sender, RoutedEventArgs e)
    {
        await VerifyDatabaseTable();
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        using (var dialog = new FolderBrowserDialog()) // System.Windows.Forms.FolderBrowserDialog
        {
            // Ensure the dialog is shown on the UI thread if called from a non-UI thread, though click handlers are UI thread.
            System.Windows.Forms.DialogResult result = dialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                LogDirectory.Text = dialog.SelectedPath;
                _hasUnsavedChanges = true;
                _logger.LogInformation($"Log directory selected: {dialog.SelectedPath}");
            }
        }
    }

    private void InstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Install Service button clicked. Attempting to call App.InstallService().");
        // The App.IsAdministrator() check and preliminary MessageBox are removed.
        // App.InstallService() will handle elevation via UAC for sc.exe.
        App.InstallService(); 
        UpdateServiceControlUi(); // Refresh UI after attempting action
    }

    private void UninstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Uninstall Service button clicked. Attempting to call App.UninstallService().");
        // The App.IsAdministrator() check and preliminary MessageBox are removed.
        // App.UninstallService() will handle elevation via UAC for sc.exe.
        App.UninstallService();
        UpdateServiceControlUi(); // Refresh UI after attempting action
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Start Service button clicked. Attempting to call App.StartWindowsService().");
        // The App.IsAdministrator() check is removed. App.StartWindowsService will handle elevation.

        if (_isProcessing) // _isProcessing refers to local UI-managed processing
        {
            var choice = System.Windows.MessageBox.Show(
                "Local UI-managed processing is currently active. It is recommended to stop it before starting the Windows service to avoid conflicts. Stop local processing now?",
                "Local Processing Active",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Yes)
            {
                StopProcessing(); // This stops local UI processing
                _logger.LogInformation("Local UI processing stopped by user before starting service.");
                await Task.Delay(200); // Give a moment for UI to update and processing to cease.
            }
            else if (choice == MessageBoxResult.Cancel)
            {
                _logger.LogInformation("Service start cancelled by user due to active local processing.");
                UpdateServiceControlUi();
                return;
            }
            // If 'No', user chose to proceed despite the warning.
        }

        App.StartWindowsService(App.WindowsServiceName); // Use constant
        UpdateServiceControlUi(); // Refresh UI after attempting action
        
        if (_ipcService != null && !_ipcService.IsConnected)
        {
            await Task.Delay(1000); // Brief delay to allow service to potentially start up
            await _ipcService.ConnectAsync(); // Attempt to connect IPC
        }
        if (_ipcService != null && _ipcService.IsConnected)
        {
            await _ipcService.SendServiceStatusRequestAsync();
        }

        UpdateServiceControlUi(); // Refresh UI after attempting action
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Stop Service button clicked. Attempting to call App.StopWindowsService().");
        // The App.IsAdministrator() check is removed. App.StopWindowsService will handle elevation.
        
        App.StopWindowsService(App.WindowsServiceName); // Use constant
        UpdateServiceControlUi(); // Refresh UI after attempting action
        // Consider awaiting IPC disconnection if StopWindowsService doesn't block.
        if (_ipcService != null && _ipcService.IsConnected)
        {
            // Service stop might take a moment, IPC might disconnect itself via its own logic.
            // Forcing a status request might not be reliable immediately.
            // UI update relies on UpdateServiceControlUi and IPC disconnection events.
            await Task.Delay(500); // Give a moment for UI to update from SC and IPC events
            UpdateServiceControlUi();
        }
    }

    private void SaveConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void ResetConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show("Are you sure you want to reset all settings to their default values? This will discard any unsaved changes.",
                                       "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            ResetSettings();
            _logger.LogInformation("Settings have been reset to default values by the user.");
            UpdateStatusBar("Settings reset to defaults. Save to apply."); // _hasUnsavedChanges is true
        }
    }

    private void LogFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        _hasUnsavedChanges = true; // Filter preferences are saved in appsettings.json
        ApplyLogFilter();
        _logger.LogDebug("Log filter toggled by user, _hasUnsavedChanges set to true.");
    }

    private void ClearLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (LogTextBox.Tag is string currentTag && string.IsNullOrEmpty(currentTag) && string.IsNullOrEmpty(LogTextBox.Text))
        {
            _logger.LogDebug("ClearLogsBtn_Click: UI logs already empty.");
            UpdateStatusBar("UI logs are already empty.");
            return;
        }
        LogTextBox.Tag = ""; // Clear the backing data store for logs
        // ApplyLogFilter will clear the LogTextBox and repopulate (with nothing)
        ApplyLogFilter();
        _logger.LogInformation("UI logs cleared by user.");
        UpdateStatusBar("UI logs cleared.");
    }

    private Task OnIpcLogEntriesReceived(List<string> logEntries)
    {
        _logger.LogDebug("OnIpcLogEntriesReceived: Received {Count} log entries from service via IPC.", logEntries.Count);
        foreach (var entryMessage in logEntries)
        {
            // Construct a formatted message similar to how local messages are logged.
            // Or, if the service sends pre-formatted messages, just pass them.
            // For now, assuming entryMessage is a simple string from the service.
            string timestamp = DateTime.Now.ToString("HH:mm:ss"); // Timestamp of UI reception
            string formattedMessage = $"[{timestamp}] SERVICE: {entryMessage}";
            AppendAndTrimLog(formattedMessage); // This method is already thread-safe
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
        _logger.LogInformation("MainWindow disposing.");
        _serviceStatusTimer?.Stop();
        // Unsubscribe from events
        if (_ipcService != null) 
        { 
            _ipcService.PipeConnected -= OnIpcPipeConnected; 
            _ipcService.PipeDisconnected -= OnIpcPipeDisconnected; 
            _ipcService.ServiceStatusReceived -= OnIpcServiceStatusReceived; 
            _ipcService.LogEntriesReceived -= OnIpcLogEntriesReceived; // Unsubscribe from new event
        }
        if (_logFileWatcher != null) { _logFileWatcher.ProcessingStatusChanged -= OnProcessingStatusChanged; _logFileWatcher.EntriesProcessed -= OnEntriesProcessed; _logFileWatcher.ErrorOccurred -= OnErrorOccurred; }
        if (_positionManager != null) { _positionManager.PositionsLoaded -= OnPositionsLoaded; }

        if (_ipcService is IAsyncDisposable ad) await ad.DisposeAsync();
        else if (_ipcService is IDisposable d) d.Dispose();
        
        StopPositionUpdateTimer(); // Calls _positionUpdateTimer?.Dispose()
        await (_positionUpdateTimer?.DisposeAsync() ?? ValueTask.CompletedTask); // Ensure async disposal if applicable
        
        Serilog.Log.CloseAndFlush();
        GC.SuppressFinalize(this);
    }
}
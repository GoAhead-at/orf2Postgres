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
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;
using System.Windows.Threading;
using System.ServiceProcess;

namespace Log2Postgres;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const string PipeName = "Log2PostgresServicePipe";
    private DispatcherTimer _serviceStatusTimer;
    private bool _isServiceUiUpdatePending = false;

    private const int MaxUiLogLines = 500; // Added constant for max log lines

    private readonly IConfiguration _configuration;
    private readonly PostgresService _postgresService;
    private readonly LogFileWatcher _logFileWatcher;
    private readonly ILogger<MainWindow> _logger;
    private readonly PasswordEncryption _passwordEncryption;
    private readonly PositionManager _positionManager;
    private bool _isProcessing = false;
    
    // Timer for refreshing the position display
    private System.Threading.Timer? _positionUpdateTimer;

    // Add a simple class to deserialize the service status
    private class PipeServiceStatus
    {
        public string ServiceOperationalState { get; set; }
        public bool IsProcessing { get; set; }
        public string CurrentFile { get; set; }
        public long CurrentPosition { get; set; }
        public long TotalLinesProcessedSinceStart { get; set; }
        public string LastErrorMessage { get; set; }
    }

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
        
        // Initialize and start IPC status timer
        _serviceStatusTimer = new DispatcherTimer();
        _serviceStatusTimer.Interval = TimeSpan.FromSeconds(5); // Query every 5 seconds
        _serviceStatusTimer.Tick += ServiceStatusTimer_Tick;
        _serviceStatusTimer.Start();
        _logger.LogInformation("Service status timer started for IPC.");

        // Initial query
        Task.Run(async () => await QueryServiceStatusAsync());
    }

    private async void ServiceStatusTimer_Tick(object sender, EventArgs e)
    {
        await QueryServiceStatusAsync();
        UpdateServiceControlUi(); // Periodically update service control UI
    }

    private async Task QueryServiceStatusAsync()
    {
        _logger.LogTrace("Querying Windows service status via IPC...");
        PipeServiceStatus status = null;
        bool serviceAvailable = false;
        bool isInstalled = WindowsServiceManager.IsServiceInstalled();
        ServiceControllerStatus currentScStatus = ServiceControllerStatus.Stopped;

        if (isInstalled)
        {
            try
            {
                currentScStatus = WindowsServiceManager.GetServiceStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error directly querying service status via ServiceController.");
            }
        }
        else
        {
            UpdateUiWithServiceStatus(null, false, currentScStatus, false);
            return;
        }

        if (currentScStatus == ServiceControllerStatus.Running || currentScStatus == ServiceControllerStatus.StartPending)
        {
            NamedPipeClientStream client = null;
            try
            {
                _logger.LogDebug("IPC Client: Attempting to create NamedPipeClientStream for pipe '{PipeName}'.", PipeName);
                client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                _logger.LogInformation("IPC Client: NamedPipeClientStream object created for pipe '{PipeName}'.", PipeName);

                _logger.LogDebug("IPC Client: Attempting to connect to pipe '{PipeName}' with timeout {Timeout}ms...", PipeName, 10000);
                await client.ConnectAsync(10000); 
                _logger.LogInformation("IPC Client: Successfully connected to IPC pipe: {PipeName}", PipeName);
                serviceAvailable = true;

                _logger.LogDebug("IPC Client: Attempting to send GET_STATUS request to service on pipe '{PipeName}'.", PipeName);
                byte[] requestBytes = Encoding.UTF8.GetBytes("GET_STATUS");
                await client.WriteAsync(requestBytes, 0, requestBytes.Length);
                await client.FlushAsync();
                _logger.LogInformation("IPC Client: Successfully sent GET_STATUS request to service on pipe '{PipeName}'.", PipeName);

                _logger.LogDebug("IPC Client: Attempting to read response from service on pipe '{PipeName}'.", PipeName);
                byte[] buffer = new byte[4096]; 
                int bytesRead = await client.ReadAsync(buffer, 0, buffer.Length);
                _logger.LogInformation("IPC Client: Successfully read {BytesRead} bytes from service on pipe '{PipeName}'.", bytesRead, PipeName);
                string jsonResponse = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                _logger.LogDebug("IPC Client: Received IPC response: {JsonResponse}", jsonResponse);

                if (!string.IsNullOrWhiteSpace(jsonResponse))
                {
                    status = JsonConvert.DeserializeObject<PipeServiceStatus>(jsonResponse);
                    _logger.LogDebug("IPC Client: Deserialized status. LastErrorMessage: '{ErrorMessage}', OperationalState: '{OpState}'", status?.LastErrorMessage, status?.ServiceOperationalState);
                    serviceAvailable = true;
                }
            }
            catch (System.TimeoutException tex) 
            {
                _logger.LogWarning(tex, "IPC Client: Timeout connecting to pipe '{PipeName}'. Service might be starting, stopping, or unresponsive.", PipeName);
                serviceAvailable = false; 
            }
            catch (IOException ioex)
            {
                 _logger.LogWarning(ioex, "IPC Client: IOException with pipe '{PipeName}'. Pipe may have broken or not ready.", PipeName);
                 serviceAvailable = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC Client: Generic exception during IPC status query for pipe '{PipeName}'.", PipeName);
                serviceAvailable = false; 
            }
            finally
            {
                if (client != null)
                {
                    if (client.IsConnected)
                    {
                        try
                        {
                            client.Close(); 
                        }
                        catch (Exception exClose)
                        {
                            _logger.LogWarning(exClose, "IPC Client: Exception while closing pipe '{PipeName}'.", PipeName);
                        }
                    }
                    client.Dispose();
                    _logger.LogDebug("IPC Client: NamedPipeClientStream for pipe '{PipeName}' disposed.", PipeName);
                }
            }
        }
        else 
        {
             _logger.LogInformation("Service 'Log2Postgres' is installed but not running (Status: {Status}). Skipping IPC check.", currentScStatus);
             serviceAvailable = false; 
        }
        
        UpdateUiWithServiceStatus(status, serviceAvailable, currentScStatus, isInstalled);
    }

    private async Task<bool> SendUpdateSettingsToServiceAsync(LogMonitorSettings settings)
    {
        _logger.LogInformation("Attempting to send settings update to service via IPC.");
        if (!WindowsServiceManager.IsServiceInstalled() || WindowsServiceManager.GetServiceStatus() != ServiceControllerStatus.Running)
        {
            _logger.LogWarning("Service not installed or not running. Cannot send settings update via IPC.");
            return false;
        }

        try
        {
            using (var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                _logger.LogDebug("IPC Client (UpdateSettings): Attempting to connect to pipe '{PipeName}' with timeout 5000ms...", PipeName);
                await pipeClient.ConnectAsync(5000); 
                _logger.LogInformation("IPC Client (UpdateSettings): Successfully connected to IPC pipe: {PipeName}", PipeName);

                string settingsJson = JsonConvert.SerializeObject(settings);
                string command = $"UPDATE_SETTINGS:{settingsJson}";
                _logger.LogDebug("IPC Client (UpdateSettings): Sending command: {Command}", command);

                byte[] requestBytes = Encoding.UTF8.GetBytes(command);
                await pipeClient.WriteAsync(requestBytes, 0, requestBytes.Length);
                await pipeClient.FlushAsync();
                _logger.LogInformation("IPC Client (UpdateSettings): Successfully sent UPDATE_SETTINGS command.");

                byte[] buffer = new byte[1024];
                int bytesRead = await pipeClient.ReadAsync(buffer, 0, buffer.Length);
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                _logger.LogInformation("IPC Client (UpdateSettings): Received response: {Response}", response);

                if (response == "ACK_UPDATE_SETTINGS")
                {
                    _logger.LogInformation("Service acknowledged settings update.");
                    return true;
                }
                else
                {
                    _logger.LogError("Service responded with an error or unexpected response to settings update: {Response}", response);
                    return false;
                }
            }
        }
        catch (System.TimeoutException) 
        {
            _logger.LogWarning("IPC Client (UpdateSettings): Timeout connecting to service pipe '{PipeName}' for settings update.", PipeName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC Client (UpdateSettings): Error communicating with service pipe '{PipeName}' for settings update.", PipeName);
            return false;
        }
    }

    private void UpdateUiWithServiceStatus(PipeServiceStatus? status, bool serviceAvailable, ServiceControllerStatus scStatus, bool isInstalled)
    {
        // This method needs to be called on the UI thread
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateUiWithServiceStatus(status, serviceAvailable, scStatus, isInstalled));
            return;
        }

        _logger.LogDebug($"Updating UI: ServiceAvailable={serviceAvailable}, SCStatus={scStatus}, IsInstalled={isInstalled}, PipeStatus provided: {status != null}");

        // Update general service status indicator (top right)
        if (isInstalled)
        {
            switch (scStatus)
            {
                case ServiceControllerStatus.Running:
                    ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Green);
                    ServiceStatusText.Text = "Running";
                    break;
                case ServiceControllerStatus.Stopped:
                    ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Red);
                    ServiceStatusText.Text = "Stopped";
                    break;
                case ServiceControllerStatus.Paused:
                    ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Orange);
                    ServiceStatusText.Text = "Paused";
                    break;
                case ServiceControllerStatus.StartPending:
                    ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Yellow);
                    ServiceStatusText.Text = "Starting";
                    break;
                case ServiceControllerStatus.StopPending:
                    ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Yellow);
                    ServiceStatusText.Text = "Stopping";
                    break;
                default:
                    ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Gray);
                    ServiceStatusText.Text = "Unknown";
                    break;
            }
        }
        else
        {
            ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Gray);
            ServiceStatusText.Text = "Not Installed";
        }

        // Update detailed processing status if available from IPC
        if (serviceAvailable && status != null)
        {
            _logger.LogInformation($"IPC Status: State='{status.ServiceOperationalState}', IsProcessing={status.IsProcessing}, File='{status.CurrentFile}', Pos={status.CurrentPosition}");
            ProcessingStatusText.Text = status.IsProcessing ? "Processing Active (Service)" : status.ServiceOperationalState;
            CurrentFileText.Text = status.CurrentFile ?? "N/A";
            CurrentPositionText.Text = status.CurrentPosition.ToString();
            LinesProcessedText.Text = status.TotalLinesProcessedSinceStart.ToString(); // Assuming this is from service
            
            // Prepare status message for UI text box
            string statusMessage = $"IPC Status: State='{status.ServiceOperationalState}', IsProcessing={status.IsProcessing}, File='{status.CurrentFile}', Pos={status.CurrentPosition}";
            List<string> statusTextParts = new List<string>();
            statusTextParts.Add($"State: {status.ServiceOperationalState}");
            statusTextParts.Add($"Processing: {status.IsProcessing}");

            if (!string.IsNullOrEmpty(status.LastErrorMessage))
            {
                LogError($"Service reported an error: {status.LastErrorMessage}"); // Use existing LogError helper
                statusTextParts.Add($"Error: {status.LastErrorMessage}");
                statusMessage += $" Error: {status.LastErrorMessage}";
                LastErrorText.Text = status.LastErrorMessage;
                ServiceStatusIndicator.Background = new SolidColorBrush(Colors.DarkRed); // More distinct error color
                ServiceStatusText.Text = "Error";
            }
            else if (status.ServiceOperationalState == "Error" && string.IsNullOrEmpty(status.LastErrorMessage))
            {
                LogError("Service is in an Error state but provided no specific error message via LastErrorMessage.");
                statusTextParts.Add("Error: (No specific message)");
                statusMessage += " Error: (No specific message)";
                LastErrorText.Text = "Error (No specific message)";
                ServiceStatusIndicator.Background = new SolidColorBrush(Colors.DarkRed);
                ServiceStatusText.Text = "Error";
            }
            else
            {
                LastErrorText.Text = "None"; // Clear last error if no current error
            }
            
            // If service is controlling processing, UI controls for local processing should be disabled.
            // And the UI's local _isProcessing should reflect the service's state.
            _isProcessing = status.IsProcessing; 
        }
        else if (isInstalled) // Service installed but IPC not available or no status
        {
            ProcessingStatusText.Text = $"Service {scStatus}, IPC N/A";
            CurrentFileText.Text = "N/A";
            CurrentPositionText.Text = "N/A";
            LinesProcessedText.Text = "N/A";
            // Don't clear LastErrorText if service just became unavailable.
        }
        // If not installed, the UI might be in local processing mode, don't overwrite its status fields here
        // unless explicitly needed. UpdateServiceControlUi will handle button states for local processing.

        UpdateServiceControlUi(); // Call this to update buttons based on new state
    }
    
    private void UpdateServiceControlUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateServiceControlUi);
            return;
        }

        bool isAdmin = WindowsServiceManager.IsAdministrator(); // Still useful for logging or other specific checks
        bool serviceInstalled = WindowsServiceManager.IsServiceInstalled();
        ServiceControllerStatus status = ServiceControllerStatus.Stopped; // Default if not installed or error
        
        if (serviceInstalled)
        {
            try
            {
                status = WindowsServiceManager.GetServiceStatus();
            }
            catch(Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get service status in UpdateServiceControlUi.");
            }
        }

        InstallServiceBtn.IsEnabled = !serviceInstalled;
        UninstallServiceBtn.IsEnabled = serviceInstalled;

        if (serviceInstalled)
        {
            StartBtn.Content = "Start Service";
            StopBtn.Content = "Stop Service";

            // Enablement no longer depends on isAdmin for service operations, as they will UAC prompt
            StartBtn.IsEnabled = (status == ServiceControllerStatus.Stopped || status == ServiceControllerStatus.StopPending);
            StopBtn.IsEnabled = (status == ServiceControllerStatus.Running || status == ServiceControllerStatus.StartPending || status == ServiceControllerStatus.Paused);
        }
        else // Service not installed, UI controls local processing
        {
            StartBtn.Content = "Start Processing";
            StopBtn.Content = "Stop Processing";

            // Enable StartBtn if not already processing locally.
            // _isProcessing reflects local processing state when service is not installed.
            StartBtn.IsEnabled = !_isProcessing; 
            StopBtn.IsEnabled = _isProcessing;
        }
        
        // Log the state of buttons
        _logger.LogDebug($"UpdateServiceControlUi: Admin={isAdmin}, Installed={serviceInstalled}, Status={status}, StartBtn.IsEnabled={StartBtn.IsEnabled}, StopBtn.IsEnabled={StopBtn.IsEnabled}, InstallBtn.IsEnabled={InstallServiceBtn.IsEnabled}, UninstallBtn.IsEnabled={UninstallServiceBtn.IsEnabled}");
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        _logger.LogInformation("MainWindow closing");
        _serviceStatusTimer?.Stop(); // Stop IPC Timer
        _logger.LogInformation("Service status timer stopped.");
        
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
            _logger.LogInformation("Saving settings...");
            var configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var configJson = File.ReadAllText(configFile);
            dynamic configObj = Newtonsoft.Json.JsonConvert.DeserializeObject(configJson);

            // Capture UI values on the UI thread
            string currentLogDir = LogDirectory.Text;
            string currentLogPattern = LogFilePattern.Text;
            string currentPollingIntervalStr = PollingInterval.Text;
            int currentPollingInterval = int.TryParse(currentPollingIntervalStr, out int intervalVal) ? intervalVal : 5; // Default if parse fails

            configObj["DatabaseSettings"]["Host"] = DbHost.Text;
            configObj["DatabaseSettings"]["Port"] = DbPort.Text;
            configObj["DatabaseSettings"]["Username"] = DbUsername.Text;
            string securedPassword = SecurePassword(); 
            configObj["DatabaseSettings"]["Password"] = securedPassword;
            configObj["DatabaseSettings"]["Database"] = DbName.Text; 
            configObj["DatabaseSettings"]["Schema"] = DbSchema.Text;
            configObj["DatabaseSettings"]["Table"] = DbTable.Text;

            configObj["LogMonitorSettings"]["BaseDirectory"] = currentLogDir;
            configObj["LogMonitorSettings"]["LogFilePattern"] = currentLogPattern;
            configObj["LogMonitorSettings"]["PollingIntervalSeconds"] = currentPollingIntervalStr; // Store as string as it was
            
            string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(configObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(configFile, updatedJson);

            LogMessage("Settings saved successfully to appsettings.json");
            UpdateStatusBar("Configuration saved to appsettings.json");
            
            Task.Run(async () => 
            {
                _logger.LogDebug("SaveSettings.Task.Run: Entered Task.Run block.");
                
                // Create settings object from captured values for internal use and IPC
                var settingsForReloadAndIpc = new LogMonitorSettings
                {
                    BaseDirectory = currentLogDir,
                    LogFilePattern = currentLogPattern,
                    PollingIntervalSeconds = currentPollingInterval
                };

                await ReloadConfiguration(settingsForReloadAndIpc); 
                _logger.LogInformation("SaveSettings.Task.Run: ReloadConfiguration completed. Now checking service status for IPC.");

                // Pass the same settingsForReloadAndIpc to SendUpdateSettingsToServiceAsync
                bool serviceInstalled = WindowsServiceManager.IsServiceInstalled();
                ServiceControllerStatus serviceStatus = ServiceControllerStatus.Stopped; 
                if(serviceInstalled)
                {
                    try { serviceStatus = WindowsServiceManager.GetServiceStatus(); }
                    catch (Exception ex) { _logger.LogError(ex, "SaveSettings.Task.Run: Error getting service status."); }
                }
                
                _logger.LogInformation("SaveSettings.Task.Run: IPC Check: ServiceInstalled={IsInstalled}, ServiceStatus={Status}.", 
                    serviceInstalled, serviceStatus);

                if (serviceInstalled && serviceStatus == ServiceControllerStatus.Running)
                {
                    _logger.LogInformation("SaveSettings.Task.Run: Service is running. Attempting SendUpdateSettingsToServiceAsync.");
                    bool ipcUpdateSuccess = await SendUpdateSettingsToServiceAsync(settingsForReloadAndIpc); // Use the same settings object
                    if (ipcUpdateSuccess)
                    {
                        _logger.LogInformation("SaveSettings.Task.Run: IPC update of LogMonitorSettings to service succeeded.");
                        Dispatcher.Invoke(() => LogMessage("Running service notified of settings change."));
                        await QueryServiceStatusAsync(); 
                    }
                    else
                    {
                        _logger.LogError("SaveSettings.Task.Run: IPC update of LogMonitorSettings to service failed.");
                        Dispatcher.Invoke(() => LogError("Settings saved, but failed to update running service in real-time. Restart service to apply."));
                    }
                }
                else
                {
                     _logger.LogInformation("SaveSettings.Task.Run: Service not running/installed for IPC update. ServiceInstalled={IsInstalled}, ServiceStatus={Status}. Local settings reloaded.",
                         serviceInstalled, serviceStatus);
                }
                _logger.LogDebug("SaveSettings.Task.Run: Exiting Task.Run block.");
            });
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
    private async Task ReloadConfiguration(LogMonitorSettings settingsToApply)
    {
        _logger.LogDebug("ReloadConfiguration: Entered method."); 
        try
        {
            _logger.LogDebug("ReloadConfiguration: Applying LogMonitorSettings: Dir='{Dir}', Pattern='{Pattern}', Interval={Interval}",
                settingsToApply.BaseDirectory, settingsToApply.LogFilePattern, settingsToApply.PollingIntervalSeconds);

            if (_logFileWatcher != null)
            {
                _logger.LogInformation("ReloadConfiguration: _logFileWatcher is NOT null. Attempting to call UpdateSettingsAsync.");
                await _logFileWatcher.UpdateSettingsAsync(settingsToApply);
                _logger.LogInformation("ReloadConfiguration: Call to _logFileWatcher.UpdateSettingsAsync completed.");
                LogMessage("LogFileWatcher settings updated (local UI instance).");
            }
            else
            {
                _logger.LogWarning("ReloadConfiguration: _logFileWatcher IS NULL. Cannot update local watcher.");
            }

            LogMessage("Configuration reloaded and services notified (if applicable).");
            UpdateStatusBar("Configuration saved and reloaded.");

            // Regarding restarting processing after config change:
            // LogFileWatcher.UpdateSettingsAsync already handles reprocessing existing files if dir changed while active.
            // IF THE WATCHER IS ALREADY PROCESSING, UpdateSettingsAsync SHOULD HANDLE THE CHANGE.
            // WE SHOULD NOT EXPLICITLY RESTART PROCESSING HERE AS IT'S CONFUSING BEHAVIOR.
            _logger.LogDebug("ReloadConfiguration: Exiting method normally.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReloadConfiguration: CAUGHT EXCEPTION."); 
            LogError($"Error reloading configuration: {ex.Message}");
            UpdateStatusBar($"Error reloading config: {ex.Message}");
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

    private async void InstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Install Service button clicked.");
        try
        {
            WindowsServiceManager.InstallServiceViaSC();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during InstallServiceBtn_Click");
            System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await Task.Delay(500); // Give SCM time
        UpdateServiceControlUi(); // Refresh UI
    }

    private async void UninstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Uninstall Service button clicked.");
        try
        {
            WindowsServiceManager.UninstallServiceViaSC();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during UninstallServiceBtn_Click");
            System.Windows.MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await Task.Delay(500); // Give SCM time
        UpdateServiceControlUi(); // Refresh UI
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation($"Start button clicked. Current content: {StartBtn.Content}");
        if (WindowsServiceManager.IsServiceInstalled())
        {
            _logger.LogInformation("Attempting to start Windows Service via WindowsServiceManager...");
            WindowsServiceManager.StartWindowsService();
            // Status will be updated by the timer or a manual refresh if desired after a delay
        }
        else
        {
            _logger.LogInformation("Attempting to start local processing...");
            await StartProcessingAsync(); // This handles UI updates for local processing
        }
        UpdateServiceControlUi(); // Ensure UI state is refreshed
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation($"Stop button clicked. Current content: {StopBtn.Content}");
        if (WindowsServiceManager.IsServiceInstalled())
        {
            _logger.LogInformation("Attempting to stop Windows Service via WindowsServiceManager...");
            WindowsServiceManager.StopWindowsService();
            // Status will be updated by the timer or a manual refresh if desired after a delay
        }
        else
        {
            _logger.LogInformation("Attempting to stop local processing...");
            this.StopProcessing(); // Corrected: Call the existing StopProcessing method
        }
        UpdateServiceControlUi(); // Ensure UI state is refreshed
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
        Dispatcher.Invoke(() =>
        {
            if (entries == null || !entries.Any())
            {
                return;
            }

            LogMessage($"--- Begin Batch of {entries.Count()} Processed Entries from file: {entries.First().SourceFilename} ---");
            foreach (var entry in entries)
            {
                if (entry.IsSystemMessage)
                {
                    LogMessage($"System Message: ID: {entry.MessageId}, Class: {entry.EventClass}, Action: {entry.EventAction}, Msg: {entry.EventMsg}");
                }
                else
                {
                    LogMessage($"Processed Entry: ID: {entry.MessageId}, Time: {entry.EventDateTime}, Class: {entry.EventClass}, Action: {entry.EventAction}, Sender: {entry.Sender}, Subject: '{entry.MsgSubject}'");
                }
            }
            LogMessage($"--- End Batch of Processed Entries ---");
        });
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

    private async Task StartProcessingAsync()
    {
        _logger.LogInformation("Attempting to start local processing (UI managed)...");
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

    #endregion
}
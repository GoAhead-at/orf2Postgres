using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.ServiceProcess;
using System.Linq;
using System.Windows.Controls;
using GridRow = System.Windows.Controls.RowDefinition;
using System.Security.Principal;
using System.Collections.Concurrent;

namespace orf2Postgres
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IDisposable
    {
        private NamedPipeClientStream? _pipeClient;
        private BinaryWriter? _pipeWriter;
        private BinaryReader? _pipeReader;
        private CancellationTokenSource _cts;
        private bool _disposedValue;
        private const string LocalConfigFilePath = "config.json";
        private bool _isServiceInstalled = false;
        private bool _isPipeConnected = false;
        private bool _isProcessingEnabled = false;
        private Process? _inAppProcessInstance = null;
        private const string ServiceName = "ORF2PostgresService";
        private bool _connectionMessageShown = false;
        // Add a field for operation state tracking
        private bool _operationInProgress = false;
        private const int PipeBufferSize = 65536; // 64KB buffer, match server
        private string _lastLoggedFile = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            _cts = new CancellationTokenSource();
            
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LogToUI("Initializing UI...");
            
            // Set initial status to Stopped
            StatusTextBlock.Text = "Status: Stopped";
            StatusTextBlock.Foreground = Brushes.Black;
            _isProcessingEnabled = false;
            
            // Rename buttons (this is a one-time code change that will persist in XAML,
            // but we're doing it here to ensure it's applied without editing XAML directly)
            StartServiceButton.Content = "Enable";
            StopServiceButton.Content = "Disable";
            
            // First try to load from local config file (highest priority)
            if (!LoadConfigurationFromFile())
            {
                // Then try appsettings.json
                if (!LoadConfigurationFromAppSettings())
                {
                    // If both fail, use default values
                    LoadDefaultConfiguration();
                }
            }
            
            await InitializeNamedPipeClient();
            
            // Check if service is installed
            await CheckServiceInstallationStatus();
        }

        private bool LoadConfigurationFromAppSettings()
        {
            try
            {
                // Try multiple locations for appsettings.json
                string[] possiblePaths = new[]
                {
                    "appsettings.json",                             // Current directory
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"), // App base directory
                    "bin/Debug/net6.0-windows/appsettings.json"     // Debug output folder
                };

                string? appSettingsPath = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        appSettingsPath = path;
                        LogToUI($"Found appsettings.json at: {path}");
                        break;
                    }
                }
                
                if (appSettingsPath == null)
                {
                    LogToUI("No appsettings.json file found in any expected location");
                    return false;
                }
                
                LogToUI("Loading configuration from appsettings.json");
                string json = File.ReadAllText(appSettingsPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty("AppSettings", out JsonElement appSettings))
                {
                    if (appSettings.TryGetProperty("LogDirectory", out JsonElement logDir))
                        LogDirectoryTextBox.Text = logDir.GetString() ?? string.Empty;
                        
                    if (appSettings.TryGetProperty("LogFilePattern", out JsonElement pattern))
                        LogFilePatternTextBox.Text = pattern.GetString() ?? string.Empty;
                        
                    if (appSettings.TryGetProperty("PollingIntervalSeconds", out JsonElement polling))
                        PollingIntervalTextBox.Text = polling.ToString();
                        
                    if (appSettings.TryGetProperty("OffsetFilePath", out JsonElement offset))
                        OffsetFilePathTextBox.Text = offset.GetString() ?? string.Empty;
                }
                
                if (doc.RootElement.TryGetProperty("PostgresConnection", out JsonElement postgres))
                {
                    if (postgres.TryGetProperty("Host", out JsonElement host))
                        DbHostTextBox.Text = host.GetString() ?? string.Empty;
                        
                    if (postgres.TryGetProperty("Port", out JsonElement port))
                        DbPortTextBox.Text = port.ToString();
                        
                    if (postgres.TryGetProperty("Username", out JsonElement user))
                        DbUserTextBox.Text = user.GetString() ?? string.Empty;
                        
                    if (postgres.TryGetProperty("Password", out JsonElement pass))
                        DbPasswordBox.Password = pass.GetString() ?? string.Empty;
                        
                    if (postgres.TryGetProperty("Database", out JsonElement db))
                        DbNameTextBox.Text = db.GetString() ?? string.Empty;
                        
                    if (postgres.TryGetProperty("Schema", out JsonElement schema))
                        DbSchemaTextBox.Text = schema.GetString() ?? string.Empty;
                        
                    if (postgres.TryGetProperty("Table", out JsonElement table))
                        DbTableTextBox.Text = table.GetString() ?? string.Empty;
                }
                
                LogToUI("Configuration loaded from appsettings.json");
                return true;
            }
            catch (Exception ex)
            {
                LogToUI($"Error loading configuration from appsettings.json: {ex.Message}");
                return false;
            }
        }
        
        private void LoadDefaultConfiguration()
        {
            LogToUI("Loading default configuration values");
            
            // Set default values for the configuration fields (used only if both appsettings.json and local config.json are missing)
            if (string.IsNullOrEmpty(LogDirectoryTextBox.Text))
            {
                LogDirectoryTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            
            if (string.IsNullOrEmpty(LogFilePatternTextBox.Text))
            {
                LogFilePatternTextBox.Text = "orfee-*.log";
            }
            
            if (string.IsNullOrEmpty(PollingIntervalTextBox.Text))
            {
                PollingIntervalTextBox.Text = "5";
            }
            
            if (string.IsNullOrEmpty(OffsetFilePathTextBox.Text))
            {
                OffsetFilePathTextBox.Text = "./offsets.json";
            }
            
            if (string.IsNullOrEmpty(DbHostTextBox.Text))
            {
                DbHostTextBox.Text = "localhost";
            }
            
            if (string.IsNullOrEmpty(DbPortTextBox.Text))
            {
                DbPortTextBox.Text = "5432";
            }
        }

        private async Task CheckServiceInstallationStatus()
        {
            try
            {
                LogToUI("Performing complete status check...");
                
                // Clear cached states - important for detecting changes
                bool oldServiceInstalled = _isServiceInstalled;
                bool oldPipeConnected = _isPipeConnected;
                bool wasProcessingEnabled = _isProcessingEnabled;
                
                // Reset service state
                _isServiceInstalled = false;
                _isPipeConnected = false;
                
                // Check if the Windows Service is installed
                _isServiceInstalled = IsServiceInstalled(ServiceName);
                LogToUI($"Service installed: {_isServiceInstalled}");
                
                // Get actual service status directly from the ServiceController
                bool isServiceRunning = false;
                if (_isServiceInstalled)
                {
                    try
                    {
                        using (var sc = new ServiceController(ServiceName))
                        {
                            sc.Refresh(); // Make sure we get the latest status
                            isServiceRunning = (sc.Status == ServiceControllerStatus.Running);
                            LogToUI($"Service controller reports service status: {sc.Status}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToUI($"Error checking service status from controller: {ex.Message}");
                    }
                }
                
                // Check if the pipe is connected (service is running AND communication works)
                if (_pipeClient != null && _pipeClient.IsConnected)
                {
                    _isPipeConnected = true;
                    LogToUI("Pipe is connected to service");
                }
                else if (isServiceRunning)
                {
                    // The service is running but we're not connected to the pipe
                    // Try to reconnect
                    LogToUI("Service is running but pipe not connected - attempting to connect");
                    try
                    {
                        // Create a new pipe client with a short timeout
                        using (var timeoutCts = new CancellationTokenSource(2000))
                        {
                            var tempClient = new NamedPipeClientStream(".", "Global\\orf2PostgresPipe", 
                                PipeDirection.InOut, PipeOptions.Asynchronous);
                            
                            await tempClient.ConnectAsync(timeoutCts.Token);
                            
                            if (tempClient.IsConnected)
                            {
                                LogToUI("Connection test successful, reinitializing pipe client");
                                tempClient.Close();
                                
                                // Reinitialize the main pipe client
                                await InitializeNamedPipeClient();
                                _isPipeConnected = _pipeClient != null && _pipeClient.IsConnected;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToUI($"Could not connect to service pipe: {ex.Message}");
                        _isPipeConnected = false;
                    }
                }
                else
                {
                    _isPipeConnected = false;
                    LogToUI("Pipe is not connected to service");
                }
                
                // Now update the UI based on the determined states
                
                if (_isPipeConnected && isServiceRunning)
                {
                    // Service is running and we have a pipe connection
                    LogToUI("Service is installed and running with pipe connection");
                    StatusTextBlock.Text = "Status: Running (Service Mode)";
                    StatusTextBlock.Foreground = Brushes.Green;
                    
                    // Enable/Disable buttons control the service
                    StartServiceButton.IsEnabled = false; // Already running
                    StopServiceButton.IsEnabled = true;   // Can stop service
                    
                    // Disable install/uninstall when service is running
                    InstallServiceButton.IsEnabled = false;
                    UninstallServiceButton.IsEnabled = true;
                    
                    // Disable process past files when service running
                    ProcessPastFilesButton.IsEnabled = false;
                    
                    // Stop any local timer processing if running
                    if (_processingTimer != null && _processingTimer.IsEnabled)
                    {
                        LogToUI("Stopping local timer processing since service is running");
                        _processingTimer.Stop();
                    }
                    
                    _isProcessingEnabled = true;
                }
                else if (isServiceRunning)
                {
                    // Service is running but pipe is not connected - permission issue
                    LogToUI("Service is running but pipe connection failed - permission issue");
                    StatusTextBlock.Text = "Status: Running (No Connection)";
                    StatusTextBlock.Foreground = Brushes.Orange;
                    
                    // Show a message with options, but only once per session
                    if (!_connectionMessageShown)
                    {
                        _connectionMessageShown = true;
                        Task.Run(() => {
                            MessageBox.Show(
                                "The service is running but the application can't connect to it due to permissions.\n\n" +
                                "You can either:\n" +
                                "1. Click 'Enable' and choose to run in application mode\n" +
                                "2. Uninstall and reinstall the service as administrator\n\n" +
                                "For now, you can still use the 'Enable' button to run in application mode.",
                                "Service Communication Issue",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        });
                    }
                    
                    // Enable buttons based on real status
                    StartServiceButton.IsEnabled = true;  // Can switch to app mode
                    StopServiceButton.IsEnabled = true;   // Can try to stop service
                    
                    // Allow uninstall, prevent reinstall
                    InstallServiceButton.IsEnabled = false;
                    UninstallServiceButton.IsEnabled = true;
                    
                    // Allow process past files when service installed but not connected
                    ProcessPastFilesButton.IsEnabled = true;
                    
                    // Service is running but we can't control it
                    _isProcessingEnabled = false;
                }
                else if (_isServiceInstalled)
                {
                    LogToUI("Service is installed but not running");
                    StatusTextBlock.Text = "Status: Stopped (Service Installed)";
                    StatusTextBlock.Foreground = Brushes.Black;
                    
                    // Enable/Disable buttons control the service
                    StartServiceButton.IsEnabled = true;  // Can start service
                    StopServiceButton.IsEnabled = false;  // Already stopped
                    
                    // Allow uninstall, prevent reinstall
                    InstallServiceButton.IsEnabled = false;
                    UninstallServiceButton.IsEnabled = true;
                    
                    // Allow process past files when service installed but not running
                    ProcessPastFilesButton.IsEnabled = true;
                    
                    // Not running
                    _isProcessingEnabled = false;
                    
                    // Stop timer if running
                    if (_processingTimer != null && _processingTimer.IsEnabled)
                    {
                        LogToUI("Stopping timer-based processing");
                        _processingTimer.Stop();
                    }
                }
                else if (_processingTimer != null && _processingTimer.IsEnabled)
                {
                    // No service, but timer is running in window mode
                    LogToUI("Running in window mode with timer");
                    StatusTextBlock.Text = "Status: Running (Window Mode)";
                    StatusTextBlock.Foreground = Brushes.Green;
                    StartServiceButton.IsEnabled = false;
                    StopServiceButton.IsEnabled = true;
                    ProcessPastFilesButton.IsEnabled = false;
                    
                    // Allow installing service
                    InstallServiceButton.IsEnabled = true;
                    UninstallServiceButton.IsEnabled = false;
                    
                    _isProcessingEnabled = true;
                }
                else 
                {
                    // No service, no timer - completely stopped state
                    LogToUI("No service detected and not processing");
                    StatusTextBlock.Text = "Status: Stopped";
                    StatusTextBlock.Foreground = Brushes.Black;
                    StartServiceButton.IsEnabled = true;
                    StopServiceButton.IsEnabled = false;
                    
                    // Allow installing service, no service to uninstall
                    InstallServiceButton.IsEnabled = true;
                    UninstallServiceButton.IsEnabled = false;
                    
                    // Allow process past files when no service and not processing
                    ProcessPastFilesButton.IsEnabled = true;
                    
                    _isProcessingEnabled = false;
                }
                
                // Debug state information
                LogToUI($"Current state: ServiceInstalled={_isServiceInstalled}, PipeConnected={_isPipeConnected}, ProcessingEnabled={_isProcessingEnabled}");
                
                // If there was a state change, log it
                if (oldServiceInstalled != _isServiceInstalled || oldPipeConnected != _isPipeConnected || wasProcessingEnabled != _isProcessingEnabled)
                {
                    LogToUI("State changed during status check");
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error checking service status: {ex.Message}");
                _isPipeConnected = false;
                StatusTextBlock.Text = "Status: Error";
                StatusTextBlock.Foreground = Brushes.Red;
                
                // Enable basic controls in error state
                StartServiceButton.IsEnabled = true;
                StopServiceButton.IsEnabled = false;
                InstallServiceButton.IsEnabled = true;
                UninstallServiceButton.IsEnabled = false;
                ProcessPastFilesButton.IsEnabled = true;
            }
            
            // Return without completing a task since the method is async
            return;
        }
        
        private bool IsServiceInstalled(string serviceName)
        {
            try
            {
                ServiceController[] services = ServiceController.GetServices();
                return services.Any(service => service.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                LogToUI($"Error checking if service is installed: {ex.Message}");
                return false;
            }
        }

        private void UpdateButtonState(bool enableProcessingButtons)
        {
            StartServiceButton.IsEnabled = enableProcessingButtons && !_isProcessingEnabled;
            StopServiceButton.IsEnabled = enableProcessingButtons && _isProcessingEnabled;
            // Only enable the Process Past Files button when running in application window and not processing
            ProcessPastFilesButton.IsEnabled = !_isPipeConnected && !_isProcessingEnabled;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // Stop in-app processing if running
            StopInAppProcessing();
            _cts?.Cancel();
            Dispose();
        }

        private async Task InitializeNamedPipeClient()
        {
            try
            {
                // First check if the Windows Service is actually installed
                bool isServiceInstalled = IsServiceInstalled(ServiceName);
                if (!isServiceInstalled)
                {
                    LogToUI("No Windows Service installed, skipping pipe connection attempt");
                    _isPipeConnected = false;
                    _isServiceInstalled = false;
                    HandleDisconnected();
                    return;
                }

                LogToUI("Windows Service is installed, attempting to connect to service pipe...");
                
                try
                {
                    // Create a new cancellation token with a longer timeout to avoid UI blocking
                    using (var timeoutCts = new CancellationTokenSource(2000)) // 2 second timeout
                    {
                        // Try to connect to the named pipe with optimized buffer sizes
                        _pipeClient = new NamedPipeClientStream(
                            ".", 
                            "Global\\orf2PostgresPipe", 
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous);
                        
                        // Try to connect with timeout
                        await _pipeClient.ConnectAsync(timeoutCts.Token);
                        
                        if (_pipeClient.IsConnected)
                        {
                            LogToUI("Connected to service pipe");
                            _pipeWriter = new BinaryWriter(_pipeClient, Encoding.UTF8, leaveOpen: true);
                            _pipeReader = new BinaryReader(_pipeClient, Encoding.UTF8, leaveOpen: true);
                            
                            // Start background task to listen for messages from the service
                            _ = Task.Run(ReceiveServiceMessages, _cts.Token);
                            
                            // Get initial status
                            await SendMessageToService("status");
                            
                            // Load configuration from service (might override local config)
                            await SendMessageToService("config");
                            
                            // Enable all buttons
                            InstallServiceButton.IsEnabled = true;
                            UninstallServiceButton.IsEnabled = true;
                            SaveConfigButton.IsEnabled = true;
                            TestConnectionButton.IsEnabled = true;
                            EnsureTableButton.IsEnabled = true;
                            
                            // Mark connected state in flags
                            _isPipeConnected = true;
                            _isServiceInstalled = true;
                            
                            // Update UI for connected service
                            UpdateButtonState(true);
                            StatusTextBlock.Text = "Status: Connected to Service";
                            StatusTextBlock.Foreground = Brushes.Blue;
                            
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogToUI($"Could not connect to service: {ex.Message}");
                }
                
                // Try to restart service if we get here (couldn't connect)
                using (var sc = new ServiceController(ServiceName))
                {
                    sc.Refresh();
                    
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        // Service is running but we couldn't connect - try restarting it
                        try
                        {
                            LogToUI("Attempting to restart the service...");
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                            LogToUI($"Service restarted. New status: {sc.Status}");
                            
                            // Try connecting again after restart
                            await Task.Delay(2000); // Give it time to initialize
                            await InitializeNamedPipeClient();
                            return;
                        }
                        catch (Exception ex)
                        {
                            LogToUI($"Failed to restart service: {ex.Message}");
                        }
                    }
                    else if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        LogToUI("Service is installed but not running");
                        _isPipeConnected = false;
                        UpdateButtonState(true);
                    }
                    else
                    {
                        LogToUI($"Service is in state: {sc.Status}");
                        _isPipeConnected = false;
                        UpdateButtonState(true);
                    }
                }
                
                // If we get here, we couldn't establish communication with the service
                HandleDisconnected();
            }
            catch (Exception ex)
            {
                LogToUI($"Error during service connection: {ex.Message}");
                HandleDisconnected();
                _isPipeConnected = false;
                UpdateButtonState(true);
            }
            
            // Check if the service is installed
            await CheckServiceInstallationStatus();
        }

        private async Task ReceiveServiceMessages()
        {
            try
            {
                await Dispatcher.InvokeAsync(() => LogToUI("ReceiveServiceMessages started"));
                
                while (_pipeClient != null && _pipeClient.IsConnected && !_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Check if there's data to read
                        if (_pipeReader == null || !_pipeClient.IsConnected)
                        {
                            await Task.Delay(100, _cts.Token);
                            continue;
                        }

                        // Use only text-based communication - more reliable for JSON messages
                        if (_pipeClient.IsConnected && _pipeClient.CanRead)
                        {
                            try 
                            {
                                // Use a separate StreamReader with explicit encoding settings
                                using (var timeoutCts = new CancellationTokenSource(300))
                                using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cts.Token))
                                {
                                    // Create a new StreamReader each time to avoid encoding state issues
                                    using (var reader = new StreamReader(
                                        _pipeClient, 
                                        new UTF8Encoding(false, true), // throwOnInvalidBytes=true to catch encoding issues early
                                        leaveOpen: true,
                                        bufferSize: 4096))
                                    {
                                        // Only try to read if data is available or pipe is readable
                                        if (_pipeClient.CanRead)
                                        {
                                            try
                                            {
                                                string textLine = await reader.ReadLineAsync();
                                                
                                                if (!string.IsNullOrEmpty(textLine))
                                                {
                                                    // Process message on UI thread
                                                    await Dispatcher.InvokeAsync(() => 
                                                    {
                                                        try 
                                                        {
                                                            // Process the message, handling encoding issues internally
                                                            HandleServiceMessage(textLine);
                                                            
                                                            // Don't log "Service log message received" - it's too noisy
                                                            // Process log messages silently
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            LogToUI($"Error processing message: {ex.Message}", true);
                                                        }
                                                    });
                                                }
                                            }
                                            catch (Exception readEx)
                                            {
                                                // Catch encoding errors at read time but don't abort the loop
                                                if (readEx.Message.Contains("translate") || 
                                                    readEx.Message.Contains("encoding") || 
                                                    readEx.Message.Contains("Unicode"))
                                                {
                                                    await Dispatcher.InvokeAsync(() => 
                                                        LogToUI($"Handled encoding error: {readEx.Message}", true));
                                                    
                                                    // Try to drain some bytes to resynchronize
                                                    try
                                                    {
                                                        // Read and discard some bytes to try to resync the stream
                                                        byte[] junkBuffer = new byte[16]; // Small buffer to resync
                                                        await _pipeClient.ReadAsync(junkBuffer, 0, junkBuffer.Length);
                                                    }
                                                    catch
                                                    {
                                                        // Ignore errors during recovery
                                                    }
                                                }
                                                else
                                                {
                                                    // For other errors, log but continue
                                                    await Dispatcher.InvokeAsync(() => 
                                                        LogToUI($"Pipe read error: {readEx.Message}", true));
                                                }
                                                
                                                // Add delay to avoid tight loop on errors
                                                await Task.Delay(50);
                                            }
                                        }
                                        else
                                        {
                                            // Pipe not readable, wait before trying again
                                            await Task.Delay(100);
                                        }
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Timeout reading line, just try again
                                continue;
                            }
                            catch (IOException ioEx)
                            {
                                // Connection may have been closed or broken
                                await Dispatcher.InvokeAsync(() => LogToUI($"Pipe connection error: {ioEx.Message}", true));
                                break; // Exit the loop - pipe is broken
                            }
                            catch (Exception ex)
                            {
                                await Dispatcher.InvokeAsync(() => LogToUI($"Error reading from pipe: {ex.Message}", true));
                                
                                // Add delay to avoid tight loop on errors
                                await Task.Delay(100);
                            }
                        }
                        else
                        {
                            // Pipe not readable or not connected
                            await Task.Delay(100);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Non-fatal error, log and continue
                        await Dispatcher.InvokeAsync(() => 
                        {
                            LogToUI($"Error reading from pipe: {ex.Message}");
                        });
                        await Task.Delay(200, _cts.Token); // Throttle reconnection attempts
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToUI($"Pipe communication error: {ex.Message}");
                        HandleDisconnected();
                    });
                }
            }
        }

        private void HandleServiceMessage(string message)
        {
            try
            {
                // Check for empty or invalid messages
                if (string.IsNullOrEmpty(message))
                {
                    LogToUI("Received empty message from service", true);
                    return;
                }

                // Configure JSON options for parsing
                var options = new JsonSerializerOptions
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    PropertyNameCaseInsensitive = true
                };

                try
                {
                    // First try parsing as a batch of messages
                    using (JsonDocument doc = JsonDocument.Parse(message))
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement element in doc.RootElement.EnumerateArray())
                            {
                                ProcessSingleMessage(element);
                            }
                        }
                        else
                        {
                            // Single message
                            ProcessSingleMessage(doc.RootElement);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Try to clean up the message and parse again
                    string cleanMessage = message.Replace("\\", "\\\\")  // Escape backslashes
                                               .Replace("\r", "\\r")     // Escape CR
                                               .Replace("\n", "\\n");    // Escape LF
                    
                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(cleanMessage))
                        {
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                                {
                                    ProcessSingleMessage(element);
                                }
                            }
                            else
                            {
                                ProcessSingleMessage(doc.RootElement);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // If still fails, log the message for debugging
                        LogToUI($"Failed to parse service message: {message}", true);
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error handling service message: {ex.Message}", true);
            }
        }

        private void ProcessSingleMessage(JsonElement element)
        {
            try
            {
                // Extract the message properties
                var response = new Dictionary<string, string>();
                
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string value = "";
                    
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        value = property.Value.GetString() ?? "";
                        
                        // Unescape file paths (convert \\\\ to \)
                        if ((property.Name.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
                             property.Name.Equals("message", StringComparison.OrdinalIgnoreCase)) &&
                            value.Contains("\\\\"))
                        {
                            value = value.Replace("\\\\", "\\");
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        value = property.Value.ToString();
                    }
                    else if (property.Value.ValueKind == JsonValueKind.True)
                    {
                        value = "true";
                    }
                    else if (property.Value.ValueKind == JsonValueKind.False)
                    {
                        value = "false";
                    }
                    
                    response[property.Name.ToLower()] = value;
                }

                // Process based on action type
                string action = response.GetValueOrDefault("action", "").ToLower();
                
                if (action == "log_message")
                {
                    string logMessage = response.GetValueOrDefault("message", "");
                    bool isError = response.GetValueOrDefault("status", "").Equals("error", StringComparison.OrdinalIgnoreCase);
                    LogToUI(logMessage, isError);
                    return;
                }
                
                // Process status updates
                if (response.ContainsKey("status"))
                {
                    string status = response["status"];
                    
                    // Special handling for file not found
                    if (status.Equals("FileNotFound", StringComparison.OrdinalIgnoreCase))
                    {
                        string filePath = response.GetValueOrDefault("filepath", "(unknown)");
                        string errorMsg = response.GetValueOrDefault("errormessage", "");
                        
                        LogToUI($"File not found: {filePath}", true);
                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            LogToUI($"Error: {errorMsg}", true);
                        }
                    }
                    
                    // Update UI with status
                    Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = $"Status: {status}";
                        
                        // Log file processing activity
                        string filePath = response.GetValueOrDefault("filepath", "");
                        if (!string.IsNullOrEmpty(filePath) && filePath != _lastLoggedFile)
                        {
                            LogToUI($"Processing: {filePath}");
                            _lastLoggedFile = filePath;
                        }
                        
                        // Log any error messages
                        string errorMsg = response.GetValueOrDefault("errormessage", "");
                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            LogToUI($"Error: {errorMsg}", true);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error processing message: {ex.Message}", true);
            }
        }

        private void UpdateStatusUI(Dictionary<string, JsonElement> status)
        {
            try
            {
                // Don't log raw status updates - they're too noisy and low-value
                // LogToUI($"[DEBUG] Received status update: {JsonSerializer.Serialize(status)}");
                
                // First check for special FileNotFound status which should always be displayed
                // This is a custom status we added specifically to ensure file not found messages are visible
                if (status.TryGetValue("status", out var statusElement) && 
                    statusElement.ValueKind == JsonValueKind.String &&
                    statusElement.GetString() == "FileNotFound" &&
                    status.TryGetValue("filePath", out var missingFileElement))
                {
                    string missingFilePath = missingFileElement.GetString();
                    LogToUI($"Service reports file not found: {missingFilePath}");
                    
                    if (status.TryGetValue("message", out var messageElement) && 
                        messageElement.ValueKind == JsonValueKind.String)
                    {
                        // Log the explicit message too
                        LogToUI(messageElement.GetString());
                    }
                    
                    // Continue processing so UI elements get updated too
                }
                
                // Check if required fields exist
                if (!status.TryGetValue("status", out statusElement) ||
                    !status.TryGetValue("filePath", out var filePathElement) ||
                    !status.TryGetValue("offset", out var offsetElement))
                {
                    LogToUI("Received incomplete status data from service");
                    return;
                }

                string serviceStatus = statusElement.GetString();
                string filePath = filePathElement.GetString();
                long offset = offsetElement.GetInt64();
                string errorMessage = status.TryGetValue("errorMessage", out var errorElement) ? errorElement.GetString() : string.Empty;
                string message = status.TryGetValue("message", out var msgElement) ? msgElement.GetString() : string.Empty;

                // Only update if we have a valid status
                if (string.IsNullOrEmpty(serviceStatus))
                {
                    LogToUI("Received empty status from service");
                    return;
                }
                
                // ALWAYS log file processing activity in service mode
                if (!string.IsNullOrEmpty(filePath))
                {
                    // Log file processing messages that mimic the window mode output
                    LogToUI($"Processing log file: {filePath}");
                    _lastLoggedFile = filePath;
                    
                    // If there's an error message containing "not found", always log it
                    if (!string.IsNullOrEmpty(errorMessage) && 
                        errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        LogToUI($"File not found: {filePath}");
                    }
                }
                
                // If there's an explicit message, always log it
                if (!string.IsNullOrEmpty(message))
                {
                    LogToUI($"Service message: {message}");
                }
                
                // Update UI
                StatusTextBlock.Text = $"Status: {serviceStatus}";
                CurrentFileTextBlock.Text = string.IsNullOrEmpty(filePath) ? "(none)" : filePath;
                CurrentOffsetTextBlock.Text = offset.ToString();

                // Update status color
                if (serviceStatus == "Running")
                {
                    StatusTextBlock.Foreground = Brushes.Green;
                    if (!_isPipeConnected)
                    {
                        StartServiceButton.IsEnabled = false;
                        StopServiceButton.IsEnabled = true;
                    }
                }
                else if (serviceStatus == "Stopped")
                {
                    StatusTextBlock.Foreground = Brushes.Black;
                    if (!_isPipeConnected)
                    {
                        StartServiceButton.IsEnabled = true;
                        StopServiceButton.IsEnabled = false;
                    }
                }
                else if (serviceStatus == "Error")
                {
                    StatusTextBlock.Foreground = Brushes.Red;
                    if (!_isPipeConnected)
                    {
                        StartServiceButton.IsEnabled = true;
                        StopServiceButton.IsEnabled = false;
                    }
                    
                    if (!string.IsNullOrEmpty(errorMessage))
                    {
                        LogToUI($"Service Error: {errorMessage}");
                    }
                }
                else if (serviceStatus == "Processing" && !string.IsNullOrEmpty(filePath))
                {
                    // For Processing status, indicate that the file is being actively processed
                    StatusTextBlock.Foreground = Brushes.Blue;
                }

                // Only log status changes, not continuous status updates with the same info
                // This helps avoid UI log spam while still retaining important information
                if (serviceStatus != "Running" || !string.IsNullOrEmpty(errorMessage))
                {
                    LogToUI($"Status update: {serviceStatus}, File: {filePath}, Offset: {offset}");
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error updating status UI: {ex.Message}");
            }
        }

        private void UpdateConfigurationUI(JsonElement configElement)
        {
            try
            {
                var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configElement.GetRawText());
                if (config == null) return;

                LogDirectoryTextBox.Text = GetStringValue(config, "logDirectory");
                LogFilePatternTextBox.Text = GetStringValue(config, "logFilePattern");
                PollingIntervalTextBox.Text = GetStringValue(config, "pollingIntervalSeconds");
                OffsetFilePathTextBox.Text = GetStringValue(config, "offsetFilePath");
                DbHostTextBox.Text = GetStringValue(config, "host");
                DbPortTextBox.Text = GetStringValue(config, "port");
                DbUserTextBox.Text = GetStringValue(config, "username");
                DbNameTextBox.Text = GetStringValue(config, "database");
                DbSchemaTextBox.Text = GetStringValue(config, "schema");
                DbTableTextBox.Text = GetStringValue(config, "table");

                LogToUI("Configuration loaded from service");
            }
            catch (Exception ex)
            {
                LogToUI($"Error updating configuration UI: {ex.Message}");
            }
        }

        private string GetStringValue(Dictionary<string, JsonElement> dict, string key)
        {
            if (dict.TryGetValue(key, out var element))
            {
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            }
            return string.Empty;
        }

        private Dictionary<string, string> GetConfigurationFromUI()
        {
            return new Dictionary<string, string>
            {
                ["logDirectory"] = LogDirectoryTextBox.Text,
                ["logFilePattern"] = LogFilePatternTextBox.Text,
                ["pollingIntervalSeconds"] = PollingIntervalTextBox.Text,
                ["offsetFilePath"] = OffsetFilePathTextBox.Text,
                ["host"] = DbHostTextBox.Text,
                ["port"] = DbPortTextBox.Text,
                ["username"] = DbUserTextBox.Text,
                ["password"] = EncryptPassword(DbPasswordBox.Password),
                ["database"] = DbNameTextBox.Text,
                ["schema"] = DbSchemaTextBox.Text,
                ["table"] = DbTableTextBox.Text
            };
        }

        private bool SaveConfigurationToFile()
        {
            try
            {
                var config = GetConfigurationFromUI();
                string jsonConfig = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(LocalConfigFilePath, jsonConfig);
                LogToUI("Configuration saved to local file");
                return true;
            }
            catch (Exception ex)
            {
                LogToUI($"Error saving configuration to file: {ex.Message}");
                return false;
            }
        }

        private bool LoadConfigurationFromFile()
        {
            try
            {
                if (!File.Exists(LocalConfigFilePath))
                {
                    LogToUI("No local configuration file found");
                    return false;
                }

                string jsonConfig = File.ReadAllText(LocalConfigFilePath);
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonConfig);
                
                if (config == null)
                {
                    LogToUI("Invalid configuration file format");
                    return false;
                }

                LogDirectoryTextBox.Text = GetValueOrDefault(config, "logDirectory", "");
                LogFilePatternTextBox.Text = GetValueOrDefault(config, "logFilePattern", "");
                PollingIntervalTextBox.Text = GetValueOrDefault(config, "pollingIntervalSeconds", "");
                OffsetFilePathTextBox.Text = GetValueOrDefault(config, "offsetFilePath", "");
                DbHostTextBox.Text = GetValueOrDefault(config, "host", "");
                DbPortTextBox.Text = GetValueOrDefault(config, "port", "");
                DbUserTextBox.Text = GetValueOrDefault(config, "username", "");
                
                // Decrypt password if it's encrypted
                string encryptedPassword = GetValueOrDefault(config, "password", "");
                DbPasswordBox.Password = DecryptPassword(encryptedPassword);
                
                DbNameTextBox.Text = GetValueOrDefault(config, "database", "");
                DbSchemaTextBox.Text = GetValueOrDefault(config, "schema", "");
                DbTableTextBox.Text = GetValueOrDefault(config, "table", "");

                LogToUI("Configuration loaded from local file");
                return true;
            }
            catch (Exception ex)
            {
                LogToUI($"Error loading configuration from file: {ex.Message}");
                return false;
            }
        }

        // Encrypt password using Windows Data Protection API
        private string EncryptPassword(string password)
        {
            try
            {
                if (string.IsNullOrEmpty(password))
                    return string.Empty;
                
                // Use LocalMachine scope instead of CurrentUser for service mode compatibility
                // This ensures the service can decrypt passwords even when running under a different account
                byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                byte[] encryptedBytes = ProtectedData.Protect(
                    passwordBytes, 
                    null, 
                    DataProtectionScope.LocalMachine);
                
                return Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                LogToUI($"Error encrypting password: {ex.Message}");
                // If encryption fails, return plain text but log a warning
                LogToUI("WARNING: Password could not be encrypted and will be stored as plain text", true);
                return password;
            }
        }

        // Decrypt password using Windows Data Protection API
        private string DecryptPassword(string encryptedPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(encryptedPassword))
                    return string.Empty;
                
                // Check if the password is actually encrypted (simple heuristic)
                if (!IsBase64String(encryptedPassword))
                    return encryptedPassword; // Return as-is if it's not encrypted
                
                // First try with CurrentUser scope (for compatibility with existing passwords)
                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(encryptedPassword);
                    byte[] passwordBytes = ProtectedData.Unprotect(
                        encryptedBytes, 
                        null, 
                        DataProtectionScope.CurrentUser);
                    
                    return Encoding.UTF8.GetString(passwordBytes);
                }
                catch
                {
                    // If that fails, try with LocalMachine scope
                    LogToUI("Trying password decryption with LocalMachine scope");
                    try
                    {
                        byte[] encryptedBytes = Convert.FromBase64String(encryptedPassword);
                        byte[] passwordBytes = ProtectedData.Unprotect(
                            encryptedBytes, 
                            null, 
                            DataProtectionScope.LocalMachine);
                        
                        return Encoding.UTF8.GetString(passwordBytes);
                    }
                    catch (Exception exMachine)
                    {
                        LogToUI($"Error decrypting password with LocalMachine scope: {exMachine.Message}");
                        // If all decryption attempts fail, use as plain text
                        return encryptedPassword;
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error in password decryption logic: {ex.Message}");
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

        private string GetValueOrDefault(Dictionary<string, string> dict, string key, string defaultValue)
        {
            return dict.TryGetValue(key, out var value) ? value : defaultValue;
        }

        private void HandleDisconnected()
        {
            LogToUI("Service not connected - running in limited mode");
            
            // Reset status
            StatusTextBlock.Text = "Status: No Service Connected";
            StatusTextBlock.Foreground = Brushes.Red;
            _isProcessingEnabled = false;
            _isPipeConnected = false;
            
            UpdateButtonState(true);
            
            // Always enable these buttons
            InstallServiceButton.IsEnabled = true;
            UninstallServiceButton.IsEnabled = true;
            SaveConfigButton.IsEnabled = true;
            TestConnectionButton.IsEnabled = true;
            EnsureTableButton.IsEnabled = true;
        }

        private async Task SendMessageToService(string action, Dictionary<string, string>? additionalParams = null)
        {
            if (_pipeClient == null || !_pipeClient.IsConnected)
            {
                LogToUI("Cannot send message: Not connected to service", true);
                await InitializeNamedPipeClient();
                if (_pipeClient == null || !_pipeClient.IsConnected)
                {
                    return;
                }
            }

            try
            {
                // Always use StreamWriter for more reliable delivery
                using (var writer = new StreamWriter(_pipeClient, new UTF8Encoding(false), 1024, true))
                {
                    // Create JSON command string safely
                    StringBuilder messageBuilder = new StringBuilder();
                    messageBuilder.Append("{\"action\":\"");
                    messageBuilder.Append(action);
                    messageBuilder.Append("\"");
                    
                    // Add any additional parameters
                    if (additionalParams != null && additionalParams.Count > 0)
                    {
                        foreach (var param in additionalParams)
                        {
                            messageBuilder.Append(",\"");
                            messageBuilder.Append(param.Key);
                            messageBuilder.Append("\":\"");
                            
                            // Escape special characters in value
                            string escapedValue = param.Value
                                .Replace("\\", "\\\\")  // Escape backslashes first
                                .Replace("\"", "\\\"")  // Then escape quotes
                                .Replace("\r", "\\r")   // Escape control chars
                                .Replace("\n", "\\n")
                                .Replace("\t", "\\t");
                                
                            messageBuilder.Append(escapedValue);
                            messageBuilder.Append("\"");
                        }
                    }
                    
                    // Close JSON object and add line ending
                    messageBuilder.Append("}");
                    string finalMessage = messageBuilder.ToString();
                    
                    // Send the message with line ending
                    await writer.WriteLineAsync(finalMessage);
                    await writer.FlushAsync();
                    
                    LogToUI($"Sent to service: {action}");
                    
                    // If we're stopping the service, give it a moment to process the command
                    if (action.Equals("stop", StringComparison.OrdinalIgnoreCase))
                    {
                        // Short delay to let the service process the stop command and update its state
                        await Task.Delay(1000);
                        
                        // Update the UI to reflect stopped state immediately
                        StatusTextBlock.Text = "Status: Stopped (Service Installed)";
                        StatusTextBlock.Foreground = Brushes.Black;
                        StartServiceButton.IsEnabled = true;
                        StopServiceButton.IsEnabled = false;
                        
                        // For UI clarity, also clear any showing error
                        CurrentFileTextBlock.Text = "(none)";
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error sending message to service: {ex.Message}", true);
                
                // Try to reconnect
                _isPipeConnected = false;
                
                // Close any existing pipe
                try
                {
                    _pipeWriter?.Dispose();
                    _pipeReader?.Dispose();
                    _pipeClient?.Dispose();
                }
                catch
                {
                    // Ignore errors during cleanup
                }
                
                _pipeWriter = null;
                _pipeReader = null;
                _pipeClient = null;
                
                await InitializeNamedPipeClient();
            }
        }

        private void BrowseLogDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            // First let the user select a directory using FolderBrowserDialog
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select Log Directory";
                folderDialog.ShowNewFolderButton = true;
                
                if (!string.IsNullOrEmpty(LogDirectoryTextBox.Text) && Directory.Exists(LogDirectoryTextBox.Text))
                {
                    folderDialog.SelectedPath = LogDirectoryTextBox.Text;
                }
                
                DialogResult result = folderDialog.ShowDialog();
                
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(folderDialog.SelectedPath))
                {
                    // Set the selected directory
                    LogDirectoryTextBox.Text = folderDialog.SelectedPath;
                    
                    // Then ask if they want to see log files in this directory
                    var showFilesResult = MessageBox.Show(
                        "Do you want to view log files in this directory to verify your selection?", 
                        "View Log Files", 
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (showFilesResult == MessageBoxResult.Yes)
                    {
                        // Show available log files in the selected directory
                        try
                        {
                            // Check if directory exists and has any files
                            if (Directory.Exists(folderDialog.SelectedPath))
                            {
                                string[] logFiles = Directory.GetFiles(folderDialog.SelectedPath, "*.log");
                                string[] allFiles = Directory.GetFiles(folderDialog.SelectedPath);
                                
                                if (logFiles.Length == 0 && allFiles.Length > 0)
                                {
                                    // No .log files but other files exist
                                    var showAllResult = MessageBox.Show(
                                        "No .log files found in this directory. Do you want to see all files?",
                                        "No Log Files Found",
                                        MessageBoxButton.YesNo,
                                        MessageBoxImage.Question);
                                    
                                    if (showAllResult == MessageBoxResult.Yes)
                                    {
                                        ShowFileList(folderDialog.SelectedPath, allFiles);
                                    }
                                }
                                else if (logFiles.Length > 0)
                                {
                                    // Show the log files
                                    ShowFileList(folderDialog.SelectedPath, logFiles);
                                }
                                else
                                {
                                    MessageBox.Show(
                                        "No files found in the selected directory.",
                                        "No Files Found",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                $"Error listing files: {ex.Message}",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                    }
                }
            }
        }
        
        private void ShowFileList(string directory, string[] files)
        {
            // Create a simple dialog to show the list of files
            var fileListWindow = new Window
            {
                Title = $"Files in {directory}",
                Width = 600,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            
            var listBox = new System.Windows.Controls.ListBox();
            
            foreach (var file in files)
            {
                listBox.Items.Add(Path.GetFileName(file));
            }
            
            fileListWindow.Content = listBox;
            fileListWindow.ShowDialog();
        }

        private async void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate input
                if (string.IsNullOrWhiteSpace(LogDirectoryTextBox.Text))
                {
                    MessageBox.Show("Log directory is required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (!Directory.Exists(LogDirectoryTextBox.Text))
                {
                    var result = MessageBox.Show($"Directory '{LogDirectoryTextBox.Text}' does not exist. Do you want to create it?", 
                        "Directory Not Found", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        Directory.CreateDirectory(LogDirectoryTextBox.Text);
                    }
                    else
                    {
                        return;
                    }
                }

                // Build configuration object
                var config = GetConfigurationFromUI();

                // Save to local file
                SaveConfigurationToFile();

                // Send config to service if connected
                if (_pipeClient != null && _pipeClient.IsConnected)
                {
                    await SendMessageToService("config", new Dictionary<string, string>
                    {
                        ["config"] = JsonSerializer.Serialize(config)
                    });
                }
                else
                {
                    LogToUI("Configuration saved locally (service not connected)");
                    MessageBox.Show("Configuration saved locally. Note: The service is not running, so changes will take effect when the service starts.", 
                        "Configuration Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error saving configuration: {ex.Message}");
                MessageBox.Show($"Error saving configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogToUI("Testing database connection...");
                
                // Validate input parameters
                if (string.IsNullOrWhiteSpace(DbHostTextBox.Text))
                {
                    MessageBox.Show("Database host cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(DbPortTextBox.Text))
                {
                    MessageBox.Show("Database port cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(DbNameTextBox.Text))
                {
                    MessageBox.Show("Database name cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(DbUserTextBox.Text))
                {
                    MessageBox.Show("Database username cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Get connection parameters from UI
                string connectionString = BuildConnectionString();
                
                // Test the connection directly without needing the service
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    try
                    {
                        await connection.OpenAsync();
                        LogToUI("Connection successful!");
                        MessageBox.Show("Connection to PostgreSQL successful!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        // Immediately save and update the service with the working configuration
                        SaveConfigButton_Click(null, null);
                        
                        // Check if we're connected to a service pipe and need to notify it
                        if (_isServiceInstalled && _pipeClient != null && _pipeClient.IsConnected)
                        {
                            LogToUI("Updating service with working database configuration...");
                            
                            // Build configuration object to send to service
                            var config = GetConfigurationFromUI();
                            
                            // Specifically let service know these DB credentials work
                            await SendMessageToService("config", new Dictionary<string, string>
                            {
                                ["config"] = JsonSerializer.Serialize(config)
                            });
                            
                            LogToUI("Service configuration updated with working database settings");
                            
                            // Restart service processing if needed
                            await SendMessageToService("start");
                        }
                    }
                    catch (NpgsqlException npgEx)
                    {
                        string userFriendlyMessage = GetUserFriendlyDbErrorMessage(npgEx);
                        LogToUI($"Database connection failed: {userFriendlyMessage}");
                        MessageBox.Show(userFriendlyMessage, "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error testing connection: {ex.Message}");
                MessageBox.Show($"Error testing connection: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetUserFriendlyDbErrorMessage(NpgsqlException ex)
        {
            // Convert PostgreSQL error codes to user-friendly messages
            if (ex.Message.Contains("28P01") || ex.Message.Contains("password authentication failed"))
            {
                return "The username or password is incorrect. Please check your credentials.";
            }
            else if (ex.Message.Contains("3D000") || ex.Message.Contains("database") && ex.Message.Contains("does not exist"))
            {
                return "The specified database does not exist. Please check the database name.";
            }
            else if (ex.Message.Contains("connection refused") || ex.Message.Contains("timeout") || ex.Message.Contains("Unable to connect"))
            {
                return "Could not connect to the PostgreSQL server. Please verify that the server is running and that the host and port are correct.";
            }
            else if (ex.Message.Contains("no password supplied"))
            {
                return "A password is required to connect to this database.";
            }
            else
            {
                return $"Database error: {ex.Message}";
            }
        }

        private string BuildConnectionString()
        {
            // Use empty string for null values to prevent null reference errors
            string host = DbHostTextBox.Text ?? string.Empty;
            string port = DbPortTextBox.Text ?? string.Empty;
            string username = DbUserTextBox.Text ?? string.Empty;
            string password = DbPasswordBox.Password ?? string.Empty;
            string database = DbNameTextBox.Text ?? string.Empty;
            
            return $"Host={host};" +
                   $"Port={port};" +
                   $"Username={username};" +
                   $"Password={password};" +
                   $"Database={database}";
        }

        private async void EnsureTableButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogToUI("Checking database table...");
                
                // Validate input parameters
                if (string.IsNullOrWhiteSpace(DbHostTextBox.Text))
                {
                    MessageBox.Show("Database host cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(DbPortTextBox.Text))
                {
                    MessageBox.Show("Database port cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(DbNameTextBox.Text))
                {
                    MessageBox.Show("Database name cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(DbUserTextBox.Text))
                {
                    MessageBox.Show("Database username cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(DbSchemaTextBox.Text))
                {
                    MessageBox.Show("Database schema cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(DbTableTextBox.Text))
                {
                    MessageBox.Show("Table name cannot be empty.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Get connection parameters
                string connectionString = BuildConnectionString();
                string tableName = DbTableTextBox.Text;
                string schema = DbSchemaTextBox.Text;
                
                // First check if the table already exists
                try
                {
                    TableCheckResult checkResult = await CheckTableStructureAsync(connectionString, schema, tableName);
                    
                    if (checkResult.Exists)
                    {
                        if (checkResult.HasCorrectStructure)
                        {
                            // Table exists with correct structure
                            LogToUI("Table already exists with correct structure!");
                            MessageBox.Show($"The table {schema}.{tableName} already exists with the correct structure.", 
                                "Table Verified", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            // Check if entry_hash column is missing
                            bool needsEntryHashColumn = await CheckIfEntryHashColumnNeededAsync(connectionString, schema, tableName);
                            
                            if (needsEntryHashColumn)
                            {
                                var addColumnResult = MessageBox.Show(
                                    $"The table {schema}.{tableName} exists but is missing the entry_hash column needed for deduplication.\n\n" +
                                    $"Would you like to add this column?",
                                    "Add Entry Hash Column",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);
                                
                                if (addColumnResult == MessageBoxResult.Yes)
                                {
                                    await AddEntryHashColumnAsync(connectionString, schema, tableName);
                                    LogToUI("Added entry_hash column for deduplication!");
                                    MessageBox.Show($"Added entry_hash column to {schema}.{tableName} to prevent duplicate entries!", 
                                        "Column Added", MessageBoxButton.OK, MessageBoxImage.Information);
                                    return;
                                }
                            }
                            
                            // Table exists but has wrong structure
                            var result = MessageBox.Show(
                                $"The table {schema}.{tableName} exists but doesn't have the correct column structure. " +
                                $"Would you like to:\n\n" +
                                $"- Yes: Drop and recreate the table (all data will be lost)\n" +
                                $"- No: Keep the table as is", 
                                "Table Structure Mismatch", 
                                MessageBoxButton.YesNo, 
                                MessageBoxImage.Warning);
                            
                            if (result == MessageBoxResult.Yes)
                            {
                                // User chose to recreate the table
                                await DropAndRecreateTableAsync(connectionString, schema, tableName);
                                LogToUI("Table dropped and recreated successfully!");
                                MessageBox.Show($"Table {schema}.{tableName} was recreated with the correct structure!", 
                                    "Table Recreated", MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                LogToUI("Table structure mismatch - user chose to keep existing table.");
                            }
                        }
                    }
                    else
                    {
                        // Ask for confirmation before creating the table
                        var result = MessageBox.Show($"The table {schema}.{tableName} does not exist. Do you want to create it?", 
                                                    "Create Table?", 
                                                    MessageBoxButton.YesNo, 
                                                    MessageBoxImage.Question);
                        
                        if (result == MessageBoxResult.Yes)
                        {
                            // User confirmed, create the table
                            await CreateTableAsync(connectionString, schema, tableName);
                            LogToUI("Table created successfully!");
                            MessageBox.Show($"Table {schema}.{tableName} was created successfully!", 
                                "Table Created", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            LogToUI("Table creation cancelled by user.");
                        }
                    }
                }
                catch (NpgsqlException npgEx)
                {
                    string userFriendlyMessage = GetUserFriendlyDbErrorMessage(npgEx);
                    LogToUI($"Database error: {userFriendlyMessage}");
                    MessageBox.Show(userFriendlyMessage, "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error checking/creating table: {ex.Message}");
                MessageBox.Show($"Error checking/creating table: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private async Task<bool> CheckIfEntryHashColumnNeededAsync(string connectionString, string schema, string tableName)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
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
                    cmd.Parameters.AddWithValue("schema", schema);
                    cmd.Parameters.AddWithValue("tableName", tableName);
                    
                    bool hasEntryHashColumn = (bool)await cmd.ExecuteScalarAsync();
                    return !hasEntryHashColumn; // Return true if the column is needed (doesn't exist)
                }
            }
        }
        
        private async Task AddEntryHashColumnAsync(string connectionString, string schema, string tableName)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = $@"
                        -- Add the entry_hash column for deduplication
                        ALTER TABLE {schema}.{tableName} ADD COLUMN IF NOT EXISTS entry_hash TEXT;
                        
                        -- Create a unique index on the entry_hash column
                        CREATE UNIQUE INDEX IF NOT EXISTS idx_{tableName}_entry_hash 
                        ON {schema}.{tableName} (entry_hash);
                    ";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        
        private class TableCheckResult
        {
            public bool Exists { get; set; }
            public bool HasCorrectStructure { get; set; }
        }
        
        private async Task<TableCheckResult> CheckTableStructureAsync(string connectionString, string schema, string tableName)
        {
            var result = new TableCheckResult
            {
                Exists = false,
                HasCorrectStructure = false
            };
            
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                // First check if the table exists
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = @"
                        SELECT EXISTS (
                            SELECT FROM information_schema.tables 
                            WHERE table_schema = @schema
                            AND table_name = @tableName
                        )";
                    cmd.Parameters.AddWithValue("schema", schema);
                    cmd.Parameters.AddWithValue("tableName", tableName);
                    
                    result.Exists = (bool)await cmd.ExecuteScalarAsync();
                }
                
                // If table exists, check its structure
                if (result.Exists)
                {
                    // Get list of required columns and their types
                    // These should match the structure in CreateTableAsync
                    var requiredColumns = new List<(string Name, string Type)>
                    {
                        ("id", "serial"),
                        ("message_id", "text"),
                        ("event_source", "text"),
                        ("event_datetime", "timestamp with time zone"),
                        ("event_class", "text"),
                        ("event_severity", "text"),
                        ("event_action", "text"),
                        ("filtering_point", "text"),
                        ("ip", "text"),
                        ("sender", "text"),
                        ("recipients", "text"),
                        ("msg_subject", "text"),
                        ("msg_author", "text"),
                        ("remote_peer", "text"),
                        ("source_ip", "text"),
                        ("country", "text"),
                        ("event_msg", "text"),
                        ("filename", "text"),
                        ("processed_at", "timestamp with time zone"),
                        ("entry_hash", "text")
                    };
                    
                    // Check if all required columns exist with correct types
                    using (var cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = connection;
                        cmd.CommandText = @"
                            SELECT column_name, data_type, udt_name
                            FROM information_schema.columns
                            WHERE table_schema = @schema
                            AND table_name = @tableName";
                        cmd.Parameters.AddWithValue("schema", schema);
                        cmd.Parameters.AddWithValue("tableName", tableName);
                        
                        var existingColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string columnName = reader.GetString(0).ToLower();
                                string dataType = reader.GetString(1).ToLower();
                                
                                // For special types like serial, we need to check udt_name
                                if (dataType == "integer" && columnName == "id")
                                {
                                    string udtName = reader.GetString(2).ToLower();
                                    if (udtName == "int4")
                                    {
                                        dataType = "serial"; // This is an approximation, we're checking if it's an auto-increment integer
                                    }
                                }
                                
                                // For timestamp with time zone, PostgreSQL reports different strings
                                if (dataType.Contains("timestamp") && dataType.Contains("zone"))
                                {
                                    dataType = "timestamp with time zone";
                                }
                                
                                existingColumns[columnName] = dataType;
                            }
                        }
                        
                        // Check if all required columns exist with correct types
                        bool hasAllColumns = true;
                        foreach (var column in requiredColumns)
                        {
                            if (!existingColumns.TryGetValue(column.Name.ToLower(), out string existingType) ||
                                !IsCompatibleType(existingType, column.Type))
                            {
                                hasAllColumns = false;
                                break;
                            }
                        }
                        
                        result.HasCorrectStructure = hasAllColumns;
                        
                        // Also check if the unique index on entry_hash exists
                        if (hasAllColumns)
                        {
                            cmd.CommandText = @"
                                SELECT EXISTS (
                                    SELECT 1 FROM pg_indexes 
                                    WHERE schemaname = @schema 
                                    AND tablename = @tableName 
                                    AND indexdef LIKE '%entry_hash%'
                                )";
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("schema", schema);
                            cmd.Parameters.AddWithValue("tableName", tableName);
                            
                            bool hasEntryHashIndex = (bool)await cmd.ExecuteScalarAsync();
                            result.HasCorrectStructure = result.HasCorrectStructure && hasEntryHashIndex;
                        }
                    }
                }
            }
            
            return result;
        }
        
        private bool IsCompatibleType(string existingType, string requiredType)
        {
            // Convert PostgreSQL type names to standardized format for comparison
            existingType = existingType.ToLower();
            requiredType = requiredType.ToLower();
            
            // Direct match
            if (existingType == requiredType)
                return true;
                
            // Special case for serial / integer / bigint
            if (requiredType == "serial" && (existingType == "integer" || existingType == "int4"))
                return true;
                
            // Special case for text / character varying
            if (requiredType == "text" && (existingType == "character varying" || existingType.StartsWith("varchar")))
                return true;
                
            // Special case for timestamp with time zone (different formats)
            if ((requiredType == "timestamp with time zone" || requiredType == "timestamptz") &&
                (existingType == "timestamp with time zone" || existingType == "timestamptz"))
                return true;
                
            return false;
        }
        
        private async Task DropAndRecreateTableAsync(string connectionString, string schema, string tableName)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                // Drop the existing table
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = $"DROP TABLE IF EXISTS {schema}.{tableName}";
                    await cmd.ExecuteNonQueryAsync();
                }
                
                // Recreate the table
                await CreateTableAsync(connectionString, schema, tableName);
            }
        }
        
        private async Task<bool> CheckIfTableExistsAsync(string connectionString, string schema, string tableName)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = @"
                        SELECT EXISTS (
                            SELECT FROM information_schema.tables 
                            WHERE table_schema = @schema
                            AND table_name = @tableName
                        )";
                    cmd.Parameters.AddWithValue("schema", schema);
                    cmd.Parameters.AddWithValue("tableName", tableName);
                    
                    return (bool)await cmd.ExecuteScalarAsync();
                }
            }
        }

        private void StartServiceButton_Click(object sender, RoutedEventArgs e)
        {
            // Prevent re-entry or multiple operations
            if (_operationInProgress)
            {
                LogToUI("Operation already in progress, please wait...");
                return;
            }
            
            // Disable all buttons immediately
            StartServiceButton.IsEnabled = false;
            StopServiceButton.IsEnabled = false;
            ProcessPastFilesButton.IsEnabled = false;
            InstallServiceButton.IsEnabled = false;
            UninstallServiceButton.IsEnabled = false;
            
            // Update status
            StatusTextBlock.Text = "Status: Processing...";
            StatusTextBlock.Foreground = Brushes.Orange;
            LogToUI("Starting operation...");
            
            // Set flag to prevent re-entry
            _operationInProgress = true;
            
            // Launch the operation on a thread pool thread without waiting
            // This is important - we do not await this to avoid any chance of UI thread blocking
            Task.Run(() => StartServiceOperationAsync())
                .ContinueWith(task => {
                    // When complete (success or error), reset UI on the UI thread
                    Dispatcher.Invoke(() => {
                        _operationInProgress = false;
                        
                        if (task.IsFaulted)
                        {
                            LogToUI($"Operation failed: {task.Exception?.InnerException?.Message ?? "Unknown error"}");
                            StatusTextBlock.Text = "Status: Error";
                            StatusTextBlock.Foreground = Brushes.Red;
                        }
                        
                        // Re-enable buttons based on current state
                        CheckServiceInstallationStatus();
                    });
                });
            
            // Return immediately, don't wait for the operation
            LogToUI("Enable button clicked - processing started on background thread");
        }

        // Completely isolated method that runs on background thread
        private async Task StartServiceOperationAsync()
        {
            // This method never updates UI directly, only through Dispatcher
            try
            {
                await Dispatcher.InvokeAsync(() => LogToUI("Background operation started"));
                
                // Step 1: Determine if we have a service and what to do
                bool serviceInstalled = false;
                bool serviceRunning = false;
                
                // First check service status
                await Dispatcher.InvokeAsync(() => LogToUI("Checking Windows service status..."));
                
                using (var timeoutCts = new CancellationTokenSource(5000)) // 5 second timeout
                {
                    try
                    {
                        await Task.Run(() => {
                            try
                            {
                                using (var sc = new ServiceController("ORF2PostgresService"))
                                {
                                    sc.Refresh();
                                    serviceInstalled = true;
                                    serviceRunning = (sc.Status == ServiceControllerStatus.Running);
                                    
                                    Dispatcher.Invoke(() => LogToUI($"Service status: {sc.Status}"));
                                }
                            }
                            catch (InvalidOperationException)
                            {
                                // Service not found/installed
                                serviceInstalled = false;
                                serviceRunning = false;
                                Dispatcher.Invoke(() => LogToUI("Service not installed"));
                            }
                            catch (Exception ex)
                            {
                                // Other errors checking service
                                Dispatcher.Invoke(() => LogToUI($"Error checking service: {ex.Message}"));
                                serviceInstalled = false;
                                serviceRunning = false;
                            }
                        }, timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        await Dispatcher.InvokeAsync(() => LogToUI("Timeout checking service status"));
                        // Assume not installed to be safe
                        serviceInstalled = false;
                        serviceRunning = false;
                    }
                }
                
                // Step 2: Based on service status, decide next action
                if (!serviceInstalled)
                {
                    // No service - run in app mode
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI("No service installed - starting in application mode");
                        // Don't call StartInAppProcessing directly, let it run on its own thread
                        StartInAppProcessing();
                        // UI will be updated from within StartInAppProcessing's background task
                    });
                    
                    // Operation complete
                    return;
                }
                
                if (serviceRunning)
                {
                    // Service is running - try to connect
                    await Dispatcher.InvokeAsync(() => LogToUI("Service is running - attempting to connect"));
                    
                    // Try to connect with timeout
                    bool connected = false;
                    using (var timeoutCts = new CancellationTokenSource(5000))
                    {
                        try
                        {
                            // Test connection with timeout
                            using (var pipeClient = new NamedPipeClientStream(".", "Global\\orf2PostgresPipe", 
                                PipeDirection.InOut, PipeOptions.Asynchronous))
                            {
                                await Task.Run(async () => {
                                    try
                                    {
                                        await pipeClient.ConnectAsync(timeoutCts.Token);
                                        connected = pipeClient.IsConnected;
                                    }
                                    catch
                                    {
                                        connected = false;
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            await Dispatcher.InvokeAsync(() => LogToUI($"Error connecting to service: {ex.Message}"));
                            connected = false;
                        }
                    }
                    
                    if (connected)
                    {
                        // Successfully connected to running service
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI("Connected to service - enabling pipe connection");
                            
                            // Run on the UI thread so it can access UI components
                            // We don't await because we want the background task to finish
                            Task.Run(() => Dispatcher.Invoke(async () => {
                                // These operations must run on the UI thread but we 
                                // don't want to block the StartServiceOperationAsync completion
                                                        await InitializeNamedPipeClient();
                        if (_isPipeConnected)
                        {
                            await SendMessageToService("start");
                            StatusTextBlock.Text = "Status: Running (Service Mode)";
                            StatusTextBlock.Foreground = Brushes.Green;
                            
                            // Explicitly set button states for service mode
                            StartServiceButton.IsEnabled = false;
                            StopServiceButton.IsEnabled = true;
                            InstallServiceButton.IsEnabled = false;
                            UninstallServiceButton.IsEnabled = true;
                            ProcessPastFilesButton.IsEnabled = false;
                        }
                            }));
                        });
                        
                        // Operation complete
                        return;
                    }
                    else
                    {
                        // Service running but can't connect - ask user what to do
                        var result = await Dispatcher.InvokeAsync(() => {
                            return MessageBox.Show(
                                "The service is running but the application can't connect to it. This is likely due to permissions.\n\n" +
                                "You have two options:\n" +
                                "1. Run in application mode instead (click Yes)\n" +
                                "2. Try to fix permissions by reinstalling the service as administrator (click No)",
                                "Service Connection Issue",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);
                        });
                        
                        if (result == MessageBoxResult.Yes)
                        {
                            // Run in application mode
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI("Starting in application mode (user choice)");
                                StartInAppProcessing();
                                StatusTextBlock.Text = "Status: Running (Window Mode)";
                                StatusTextBlock.Foreground = Brushes.Green;
                            });
                            
                            // Operation complete
                            return;
                        }
                        else
                        {
                            // User chose not to continue
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI("Operation cancelled by user");
                                StatusTextBlock.Text = "Status: Stopped";
                                StatusTextBlock.Foreground = Brushes.Black;
                                
                                // Show reinstall instructions
                                MessageBox.Show(
                                    "To reinstall the service, please:\n" +
                                    "1. Click 'Uninstall Service'\n" +
                                    "2. Close and reopen this application as administrator\n" +
                                    "3. Click 'Install Service'\n" +
                                    "4. Click 'Enable' again",
                                    "Reinstall Instructions",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            });
                            
                            // Operation complete
                            return;
                        }
                    }
                }
                
                // Service installed but not running - try to start it
                await Dispatcher.InvokeAsync(() => {
                    LogToUI("Service installed but not running - attempting to start it");
                    StatusTextBlock.Text = "Status: Starting service...";
                    StatusTextBlock.Foreground = Brushes.Orange;
                });
                
                // Try to start the service
                bool startSuccess = false;
                using (var timeoutCts = new CancellationTokenSource(30000)) // 30 second timeout
                {
                    try
                    {
                        await Task.Run(() => {
                            try
                            {
                                using (var sc = new ServiceController("ORF2PostgresService"))
                                {
                                    // Log starting service
                                    Dispatcher.Invoke(() => LogToUI("Sending start command to service..."));
                                    
                                    // Start service
                                    sc.Start();
                                    
                                    // Wait for service to start
                                    Dispatcher.Invoke(() => LogToUI("Waiting for service to start (up to 30 seconds)..."));
                                    
                                    // Wait with progress updates
                                    for (int i = 0; i < 30; i++)
                                    {
                                        if (timeoutCts.Token.IsCancellationRequested)
                                            break;
                                        
                                        // Check status
                                        sc.Refresh();
                                        Dispatcher.Invoke(() => LogToUI($"Service status: {sc.Status}"));
                                        
                                        if (sc.Status == ServiceControllerStatus.Running)
                                        {
                                            startSuccess = true;
                                            break;
                                        }
                                        
                                        // Wait between checks
                                        Thread.Sleep(1000);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => LogToUI($"Error starting service: {ex.Message}"));
                                startSuccess = false;
                            }
                        }, timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        await Dispatcher.InvokeAsync(() => LogToUI("Timeout waiting for service to start"));
                        startSuccess = false;
                    }
                }
                
                if (startSuccess)
                {
                    // Service started - try to connect
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI("Service started successfully, waiting for initialization...");
                    });
                    
                    // Wait for service to fully initialize
                    await Task.Delay(3000);
                    
                    // Try to connect to the service
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI("Attempting to connect to service pipe...");
                        
                        // Run on UI thread to access UI components
                        // We're not awaiting to avoid blocking the operation completion
                        Task.Run(() => Dispatcher.Invoke(async () => {
                            await InitializeNamedPipeClient();
                            
                            if (_isPipeConnected)
                            {
                                LogToUI("Connected to service pipe");
                                await SendMessageToService("start");
                                StatusTextBlock.Text = "Status: Running (Service Mode)";
                                StatusTextBlock.Foreground = Brushes.Green;
                            }
                            else
                            {
                                // Failed to connect - ask if user wants app mode
                                var result = MessageBox.Show(
                                    "The service started successfully, but the application can't connect to it.\n\n" +
                                    "Would you like to run in application mode instead?",
                                    "Connection Issue",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);
                                    
                                if (result == MessageBoxResult.Yes)
                                {
                                    LogToUI("Starting in application mode (user choice)");
                                    StartInAppProcessing();
                                    StatusTextBlock.Text = "Status: Running (Window Mode)";
                                    StatusTextBlock.Foreground = Brushes.Green;
                                }
                                else
                                {
                                    LogToUI("Operation cancelled by user");
                                    StatusTextBlock.Text = "Status: Stopped";
                                    StatusTextBlock.Foreground = Brushes.Black;
                                }
                            }
                        }));
                    });
                }
                else
                {
                    // Failed to start service - offer app mode
                    var result = await Dispatcher.InvokeAsync(() => {
                        LogToUI("Failed to start service");
                        StatusTextBlock.Text = "Status: Error";
                        StatusTextBlock.Foreground = Brushes.Red;
                        
                        return MessageBox.Show(
                            "Failed to start the service.\n\n" +
                            "Would you like to run the processor in application mode instead?",
                            "Run in Application Mode",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                    });
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI("Starting in application mode (user choice)");
                            StartInAppProcessing();
                            StatusTextBlock.Text = "Status: Running (Window Mode)";
                            StatusTextBlock.Foreground = Brushes.Green;
                        });
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI("Operation cancelled by user");
                            StatusTextBlock.Text = "Status: Stopped";
                            StatusTextBlock.Foreground = Brushes.Black;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error but don't update UI directly
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($"Error in service start operation: {ex.Message}");
                });
                
                // Re-throw to be caught by the continuation
                throw;
            }
            finally
            {
                // Ensure operation is marked as complete
                await Dispatcher.InvokeAsync(() => {
                    LogToUI("Service operation completed");
                });
            }
        }

        private async void StopServiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Disable button immediately to prevent multiple clicks
                StopServiceButton.IsEnabled = false;
                LogToUI("Disable button clicked - stopping processing...");
                
                // Stop any timer processing first to ensure we don't continue processing during service stop
                StopInAppProcessing();
                
                if (_isServiceInstalled)
                {
                    LogToUI("Stopping installed Windows service...");
                    bool serviceStopSuccess = false;
                    
                    // 1. Try to stop via pipe if connected
                    if (_isPipeConnected)
                    {
                        LogToUI("Attempting to stop via named pipe connection...");
                        try 
                        {
                            await SendMessageToService("stop");
                            LogToUI("Stop command sent via pipe");
                            serviceStopSuccess = true;
                        }
                        catch (Exception pipeEx)
                        {
                            LogToUI($"Error sending stop via pipe: {pipeEx.Message}");
                        }
                    }
                    
                    // 2. Also try to stop via service controller (regardless of pipe)
                    try
                    {
                        using (var sc = new ServiceController(ServiceName))
                        {
                            sc.Refresh();
                            if (sc.Status == ServiceControllerStatus.Running)
                            {
                                LogToUI("Stopping Windows service via ServiceController...");
                                
                                // Run on background thread to avoid blocking UI
                                await Task.Run(() => {
                                    sc.Stop();
                                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                                    Dispatcher.Invoke(() => LogToUI("Service stop command completed"));
                                    serviceStopSuccess = true;
                                });
                                
                                LogToUI("Checking final service status...");
                                sc.Refresh();
                                LogToUI($"Service status after stop command: {sc.Status}");
                            }
                            else
                            {
                                LogToUI($"Service already in non-running state: {sc.Status}");
                                serviceStopSuccess = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToUI($"Error stopping service via ServiceController: {ex.Message}");
                    }
                    
                    // 3. Update UI immediately to provide feedback
                    if (serviceStopSuccess) 
                    {
                        StatusTextBlock.Text = "Status: Stopped (Service Installed)";
                        StatusTextBlock.Foreground = Brushes.Black;
                        CurrentFileTextBlock.Text = "(none)";
                        CurrentOffsetTextBlock.Text = "0";
                    }
                    else
                    {
                        StatusTextBlock.Text = "Status: Error Stopping Service";
                        StatusTextBlock.Foreground = Brushes.Red;
                    }
                    
                    // 4. Force disconnection of pipe
                    if (_pipeClient != null && _pipeClient.IsConnected)
                    {
                        try 
                        {
                            _pipeClient.Close();
                            _pipeClient = null;
                            _pipeWriter = null;
                            _pipeReader = null;
                            _isPipeConnected = false;
                            LogToUI("Closed pipe connection");
                        }
                        catch (Exception pipeEx)
                        {
                            LogToUI($"Error closing pipe: {pipeEx.Message}");
                        }
                    }
                }
                else
                {
                    // No service installed, just stop any local processing
                    LogToUI("No service installed - stopping local processing only");
                    
                    // Update UI
                    StatusTextBlock.Text = "Status: Stopped";
                    StatusTextBlock.Foreground = Brushes.Black;
                    CurrentFileTextBlock.Text = "(none)";
                    CurrentOffsetTextBlock.Text = "0";
                }
                
                // Update final state
                _isProcessingEnabled = false;
                
                // Perform full status check to update all UI elements
                await Task.Delay(500); // Small delay to let service changes propagate
                await CheckServiceInstallationStatus();
            }
            catch (Exception ex)
            {
                LogToUI($"Error disabling processing: {ex.Message}");
                MessageBox.Show($"Error disabling processing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                
                // Make sure to update UI in case of error
                await CheckServiceInstallationStatus();
            }
        }

        private void InstallServiceButton_Click(object sender, RoutedEventArgs e)
        {
            InstallOrUninstallService("--install");
        }

        private void UninstallServiceButton_Click(object sender, RoutedEventArgs e)
        {
            InstallOrUninstallService("--uninstall");
        }

        private void InstallOrUninstallService(string argument)
        {
            try
            {
                // Create a status window to show installation progress
                var statusWindow = new Window
                {
                    Title = argument == "--install" ? "Installing Service" : "Uninstalling Service",
                    Width = 500,
                    Height = 250,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize
                };
                
                var statusGrid = new Grid();
                statusGrid.RowDefinitions.Add(new GridRow { Height = new GridLength(1, GridUnitType.Star) });
                statusGrid.RowDefinitions.Add(new GridRow { Height = new GridLength(1, GridUnitType.Auto) });
                statusGrid.Margin = new Thickness(10);
                
                var statusTextBox = new System.Windows.Controls.TextBox
                {
                    IsReadOnly = true,
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap
                };
                
                var closeButton = new System.Windows.Controls.Button
                {
                    Content = "Close",
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    IsEnabled = false
                };
                
                closeButton.Click += (s, e) => statusWindow.Close();
                
                Grid.SetRow(statusTextBox, 0);
                Grid.SetRow(closeButton, 1);
                
                statusGrid.Children.Add(statusTextBox);
                statusGrid.Children.Add(closeButton);
                
                statusWindow.Content = statusGrid;
                
                // Show the status window
                statusWindow.Show();
                
                void AddStatus(string message)
                {
                    if (statusTextBox.Dispatcher.CheckAccess())
                    {
                        statusTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                        statusTextBox.ScrollToEnd();
                        LogToUI(message); // Also log to main UI
                    }
                    else
                    {
                        statusTextBox.Dispatcher.Invoke(() => AddStatus(message));
                    }
                }
                
                // Get the path to the current executable
                var currentProcess = Process.GetCurrentProcess();
                var mainModule = currentProcess.MainModule;
                
                if (mainModule == null)
                {
                    AddStatus("Error: Cannot determine the application path.");
                    MessageBox.Show("Cannot determine the application path.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    closeButton.IsEnabled = true;
                    return;
                }
                
                string exePath = mainModule.FileName;
                AddStatus($"Current executable path: {exePath}");
                
                // Run the operation in a background task
                Task.Run(async () =>
                {
                    try
                    {
                        AddStatus($"Starting {(argument == "--install" ? "installation" : "uninstallation")} process...");
                        AddStatus("Requesting administrator privileges...");
                        
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = argument,
                            Verb = "runas", // Request elevation
                            UseShellExecute = true,
                            CreateNoWindow = false
                        };
                        
                        Process process = Process.Start(startInfo);
                        if (process != null)
                        {
                            AddStatus("Administrator privileges granted. Waiting for process to complete...");
                            process.WaitForExit();
                            
                            int exitCode = process.ExitCode;
                            if (exitCode == 0)
                            {
                                AddStatus($"Service {(argument == "--install" ? "installation" : "uninstallation")} completed successfully (Exit code: {exitCode})");
                            }
                            else
                            {
                                AddStatus($"Process completed with exit code: {exitCode}. This may indicate an error.");
                            }
                        }
                        else
                        {
                            AddStatus("Failed to start process. This might be due to UAC being rejected.");
                        }
                        
                        // Wait 3 seconds before checking service status
                        AddStatus("Waiting for service registration to complete...");
                        await Task.Delay(3000);
                        
                        // Check if the service is installed
                        bool isServiceInstalled = IsServiceInstalled("ORF2PostgresService");
                        if (argument == "--install")
                        {
                            if (isServiceInstalled)
                            {
                                AddStatus("✅ Service is now installed in Windows Service registry.");
                                
                                // Try to get service status
                                try
                                {
                                    using (var sc = new ServiceController("ORF2PostgresService"))
                                    {
                                        AddStatus($"Service status: {sc.Status}");
                                        
                                        if (sc.Status != ServiceControllerStatus.Running)
                                        {
                                            AddStatus("Attempting to start the service...");
                                            try
                                            {
                                                sc.Start();
                                                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                                                AddStatus($"Service started successfully. Status: {sc.Status}");
                                            }
                                            catch (Exception ex)
                                            {
                                                AddStatus($"Failed to start service: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AddStatus($"Error accessing service: {ex.Message}");
                                }
                            }
                            else
                            {
                                AddStatus("❌ Service installation failed - service not found in Windows Service registry.");
                                AddStatus("Try running this application as administrator directly.");
                            }
                        }
                        else
                        {
                            if (!isServiceInstalled)
                            {
                                AddStatus("✅ Service has been successfully uninstalled.");
                            }
                            else
                            {
                                AddStatus("❌ Service uninstallation failed - service still exists in Windows Service registry.");
                            }
                        }
                        
                                                // Reinitialize connection to check if service is now available
                        await Dispatcher.InvokeAsync(async () => 
                        {
                            AddStatus("Refreshing connection status...");
                            
                            // Force the pipe client to be disposed and recreated
                            if (_pipeClient != null)
                            {
                                try
                                {
                                    _pipeClient.Close();
                                    _pipeClient = null;
                                    _pipeWriter = null;
                                    _pipeReader = null;
                                    _isPipeConnected = false;
                                    AddStatus("Closed existing pipe connection");
                                }
                                catch (Exception ex)
                                {
                                    AddStatus($"Error closing pipe: {ex.Message}");
                                }
                            }
                            
                            // Wait a moment for service registration to complete
                            AddStatus("Waiting for service registration to finalize...");
                            await Task.Delay(2000);
                            
                            // Reinitialize connection to check if service is now available
                            AddStatus("Attempting to connect to service...");
                            await InitializeNamedPipeClient();
                            
                            // Update all UI state
                            AddStatus("Performing complete status check...");
                            await CheckServiceInstallationStatus();
                            
                            AddStatus("Status refresh complete.");
                            
                            // Show current status in main log
                            LogToUI($"Current status after install/uninstall: ServiceInstalled={_isServiceInstalled}, PipeConnected={_isPipeConnected}");
                        });
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // The operation was canceled by the user
                    {
                        AddStatus("Administrator elevation was denied by the user.");
                        await Dispatcher.InvokeAsync(() => MessageBox.Show("The operation was canceled because you denied administrator permissions.", 
                            "Operation Canceled", MessageBoxButton.OK, MessageBoxImage.Warning));
                    }
                    catch (Exception ex)
                    {
                        AddStatus($"Error: {ex.Message}");
                        await Dispatcher.InvokeAsync(() => MessageBox.Show($"Error {argument}: {ex.Message}", 
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                    }
                    finally
                    {
                        await Dispatcher.InvokeAsync(() => closeButton.IsEnabled = true);
                    }
                });
            }
            catch (Exception ex)
            {
                LogToUI($"Error {argument}: {ex.Message}");
                MessageBox.Show($"Error {argument}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LogToUI(string message, bool isError = false)
{
    try
    {
        // Filter out noisy messages that flood the UI
        if (message.Contains("Processing batch of") || 
            message.Contains("Service log message received"))
        {
            // Skip these noisy messages entirely
            return;
        }

        // If we're already on the UI thread, execute directly
        if (Dispatcher.CheckAccess())
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            
            // Add to service log text box
            if (isError)
            {
                // Make errors stand out
                string formattedMessage = $"[{timestamp}] !!! ERROR: {message} !!!\n";
                ServiceLogTextBox.AppendText(formattedMessage);
            }
            // Highlight important DB/file processing messages with better visual markers
            else if (message.Contains("📊") || 
                     message.Contains("DATABASE") || 
                     message.Contains("entries"))
            {
                // Database operations - highlight prominently
                string formattedMessage = $"[{timestamp}] 📊 {message}\n";
                ServiceLogTextBox.AppendText(formattedMessage);
            }
            else if (message.Contains("📂") || 
                     message.Contains("PROCESSING FILE") || 
                     message.Contains("CURRENT FILE") || 
                     message.Contains("SERVICE MODE") ||
                     message.Contains("Processing log file"))
            {
                // File processing - highlight prominently
                string formattedMessage = $"[{timestamp}] 📂 {message}\n";
                ServiceLogTextBox.AppendText(formattedMessage);
            }
            else if (message.Contains("log file", StringComparison.OrdinalIgnoreCase) || 
                     message.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("processed", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("offset", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("Offset", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("SAVING", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("LOADING", StringComparison.OrdinalIgnoreCase))
            {
                // Other important operational messages
                string formattedMessage = $"[{timestamp}] >>> {message}\n";
                ServiceLogTextBox.AppendText(formattedMessage);
            }
            else
            {
                // Normal messages
                string formattedMessage = $"[{timestamp}] {message}\n";
                ServiceLogTextBox.AppendText(formattedMessage);
            }
            
            // Always ensure the newest message is visible
            ServiceLogTextBox.ScrollToEnd();
        }
        else
        {
            // Otherwise dispatch to UI thread
            Dispatcher.Invoke(() => LogToUI(message, isError));
        }
    }
    catch (Exception)
    {
        // Ignore errors in logging
    }
}

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _pipeWriter?.Dispose();
                    _pipeReader?.Dispose();
                    _pipeClient?.Dispose();
                    _cts?.Dispose();
                }

                _pipeWriter = null;
                _pipeReader = null;
                _pipeClient = null;
                _cts = null;
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private async void ProcessPastFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate log directory
                if (string.IsNullOrWhiteSpace(LogDirectoryTextBox.Text) || !Directory.Exists(LogDirectoryTextBox.Text))
                {
                    MessageBox.Show("Please provide a valid log directory.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Validate database connection information
                if (string.IsNullOrWhiteSpace(DbHostTextBox.Text) ||
                    string.IsNullOrWhiteSpace(DbPortTextBox.Text) ||
                    string.IsNullOrWhiteSpace(DbNameTextBox.Text) ||
                    string.IsNullOrWhiteSpace(DbUserTextBox.Text))
                {
                    MessageBox.Show("Database connection information is incomplete. Please provide host, port, database name, and username.", 
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Find all matching log files
                string logDirectory = LogDirectoryTextBox.Text;
                string filePattern = LogFilePatternTextBox.Text;
                
                // If the pattern contains a date placeholder, create a pattern to match all dates
                string searchPattern;
                if (filePattern.Contains("{Date:"))
                {
                    // Extract the date format pattern
                    int startIndex = filePattern.IndexOf("{Date:");
                    int endIndex = filePattern.IndexOf("}", startIndex);
                    if (startIndex >= 0 && endIndex > startIndex)
                    {
                        // Replace date placeholder with wildcard
                        searchPattern = filePattern.Substring(0, startIndex) + "*" + filePattern.Substring(endIndex + 1);
                    }
                    else
                    {
                        searchPattern = filePattern;
                    }
                }
                else
                {
                    searchPattern = filePattern;
                }
                
                // Replace any remaining placeholders with asterisks
                searchPattern = searchPattern.Replace("{", "*").Replace("}", "*");
                
                // Ensure we have at least a .log extension if the pattern is completely replaced
                if (!searchPattern.Contains("."))
                {
                    searchPattern = "*.log";
                }
                
                LogToUI($"Searching for past log files with pattern: {searchPattern}");
                string[] logFiles = Directory.GetFiles(logDirectory, searchPattern);
                
                if (logFiles.Length == 0)
                {
                    MessageBox.Show($"No log files found matching the pattern {searchPattern} in directory {logDirectory}.",
                        "No Files Found", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // Sort files by name (assumes date is part of filename and alphanumeric sorting works)
                Array.Sort(logFiles);
                
                // Ask for confirmation
                var confirmResult = MessageBox.Show(
                    $"Found {logFiles.Length} log files to process:\n\n" +
                    $"• {string.Join("\n• ", logFiles.Select(Path.GetFileName).Take(10))}" +
                    (logFiles.Length > 10 ? $"\n• ... and {logFiles.Length - 10} more files" : "") +
                    $"\n\nDo you want to process all these files?",
                    "Confirm Batch Processing",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (confirmResult != MessageBoxResult.Yes)
                {
                    LogToUI("Batch processing cancelled by user.");
                    return;
                }
                
                // Disable all buttons during processing
                ProcessPastFilesButton.IsEnabled = false;
                StartServiceButton.IsEnabled = false;
                StopServiceButton.IsEnabled = false;
                InstallServiceButton.IsEnabled = false;
                UninstallServiceButton.IsEnabled = false;
                SaveConfigButton.IsEnabled = false;
                TestConnectionButton.IsEnabled = false;
                EnsureTableButton.IsEnabled = false;
                
                StatusTextBlock.Text = "Status: Processing Past Files";
                StatusTextBlock.Foreground = Brushes.Green;
                
                // Create a progress window
                var progressWindow = new Window
                {
                    Title = "Processing Past Files",
                    Width = 400,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize
                };
                
                var progressGrid = new Grid();
                progressGrid.RowDefinitions.Add(new GridRow { Height = new GridLength(1, GridUnitType.Auto) });
                progressGrid.RowDefinitions.Add(new GridRow { Height = new GridLength(1, GridUnitType.Auto) });
                progressGrid.Margin = new Thickness(10);
                
                var progressLabel = new TextBlock
                {
                    Text = "Processing file 0 of " + logFiles.Length,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                
                var progressBar = new System.Windows.Controls.ProgressBar
                {
                    Minimum = 0,
                    Maximum = logFiles.Length,
                    Value = 0,
                    Height = 20
                };
                
                Grid.SetRow(progressLabel, 0);
                Grid.SetRow(progressBar, 1);
                
                progressGrid.Children.Add(progressLabel);
                progressGrid.Children.Add(progressBar);
                
                progressWindow.Content = progressGrid;
                progressWindow.Show();
                
                // Process each file
                int successCount = 0;
                int errorCount = 0;
                int totalLinesProcessed = 0;
                int totalEntriesInserted = 0;
                int totalEntriesSkipped = 0;
                
                try
                {
                    // Build connection string
                    string connectionString = BuildConnectionString();
                    
                    for (int i = 0; i < logFiles.Length; i++)
                    {
                        string filePath = logFiles[i];
                        try
                        {
                            // Update progress UI
                            progressLabel.Text = $"Processing file {i + 1} of {logFiles.Length}: {Path.GetFileName(filePath)}";
                            progressBar.Value = i + 1;
                            
                            // Allow UI to update
                            await Task.Delay(1);
                            
                            // Process the file
                            LogToUI($"Processing file: {filePath}");
                            CurrentFileTextBlock.Text = filePath;
                            
                            // Read and process the file
                            var (processed, inserted, skipped) = await ProcessPastLogFileAsync(filePath, connectionString);
                            
                            totalLinesProcessed += processed;
                            totalEntriesInserted += inserted;
                            totalEntriesSkipped += skipped;
                            
                            LogToUI($"Processed {processed} lines from {Path.GetFileName(filePath)} - Inserted: {inserted}, Duplicates: {skipped}");
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            LogToUI($"Error processing file {filePath}: {ex.Message}");
                            errorCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogToUI($"Fatal error during batch processing: {ex.Message}");
                    MessageBox.Show($"Error during batch processing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    // Close the progress window
                    progressWindow.Close();
                    
                    // Re-enable buttons
                    UpdateButtonState(true);
                    InstallServiceButton.IsEnabled = true;
                    UninstallServiceButton.IsEnabled = true;
                    SaveConfigButton.IsEnabled = true;
                    TestConnectionButton.IsEnabled = true;
                    EnsureTableButton.IsEnabled = true;
                    
                    StatusTextBlock.Text = "Status: Stopped";
                    StatusTextBlock.Foreground = Brushes.Black;
                    
                    CurrentFileTextBlock.Text = "(none)";
                    CurrentOffsetTextBlock.Text = "N/A";
                    
                    // Show summary message
                    MessageBox.Show(
                        $"Batch processing complete.\n\n" +
                        $"Successfully processed: {successCount} files\n" +
                        $"Errors encountered: {errorCount} files\n" +
                        $"Total lines processed: {totalLinesProcessed}\n" +
                        $"New entries inserted: {totalEntriesInserted}\n" +
                        $"Duplicate entries skipped: {totalEntriesSkipped}\n\n" +
                        $"See the log for details.",
                        "Batch Processing Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                        
                    // Ask if the user wants to start monitoring now to prevent missing new logs
                    var startMonitoringResult = MessageBox.Show(
                        "Do you want to start monitoring for new log files now?\n\n" +
                        "This is recommended to ensure no new logs are missed after batch processing.",
                        "Start Monitoring?",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                        
                    if (startMonitoringResult == MessageBoxResult.Yes)
                    {
                        LogToUI("Starting monitoring after batch processing");
                        StartServiceButton_Click(null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error in batch processing: {ex.Message}");
                MessageBox.Show($"Error in batch processing: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private async Task<(int processed, int inserted, int skipped)> ProcessPastLogFileAsync(string filePath, string connectionString)
        {
            // Implementation to process a single log file and insert records into the database
            int linesProcessed = 0;
            int entriesInserted = 0;
            int entriesSkipped = 0;
            
            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream, Encoding.UTF8, true))
                {
                    // Read and batch records
                    const int batchSize = 250;
                    var buffer = new List<Dictionary<string, string>>(batchSize);
                    string line;
                    
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        // Skip comment lines and empty lines
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                        {
                            continue;
                        }
                        
                        // Parse the line
                        var parsedLine = ParseLogLine(line);
                        if (parsedLine != null && parsedLine.Count > 0)
                        {
                            buffer.Add(parsedLine);
                            linesProcessed++;
                            
                            // Process in batches
                            if (buffer.Count >= batchSize)
                            {
                                var (inserted, skipped) = await InsertLogBatchAsync(buffer, filePath, connectionString);
                                entriesInserted += inserted;
                                entriesSkipped += skipped;
                                buffer.Clear();
                                
                                // Update the UI with progress information - use Dispatcher
                                await Dispatcher.InvokeAsync(() => {
                                    CurrentOffsetTextBlock.Text = $"{linesProcessed} (I:{entriesInserted}/S:{entriesSkipped})";
                                });
                                
                                // Allow UI to update
                                await Task.Delay(1);
                            }
                        }
                    }
                    
                    // Process any remaining records
                    if (buffer.Count > 0)
                    {
                        var (inserted, skipped) = await InsertLogBatchAsync(buffer, filePath, connectionString);
                        entriesInserted += inserted;
                        entriesSkipped += skipped;
                    }
                    
                    // Update the offset file so the service knows we've processed this file
                    // Ensure this happens on a background thread to avoid UI freezes
                    await Task.Run(async () => {
                        try {
                            await UpdateOffsetFileAsync(filePath, fileStream.Length);
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI($"Updated offset file for {Path.GetFileName(filePath)}");
                            });
                        }
                        catch (Exception ex) {
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI($"Error updating offset file: {ex.Message}");
                            });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($"Error processing file {filePath}: {ex.Message}");
                });
                throw;
            }
            
            return (linesProcessed, entriesInserted, entriesSkipped);
        }
        
        private async Task UpdateOffsetFileAsync(string filePath, long offset)
        {
            try
            {
                // Get offset file path from UI thread
                string offsetFilePath = "";
                await Dispatcher.InvokeAsync(() => {
                    offsetFilePath = OffsetFilePathTextBox.Text;
                });
                
                if (string.IsNullOrEmpty(offsetFilePath))
                {
                    offsetFilePath = "./offsets.json";
                }
                
                // Log info on UI thread
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($"Processing offset update for {Path.GetFileName(filePath)} at position {offset}");
                });
                
                // Make sure the path exists
                string offsetDirectory = Path.GetDirectoryName(offsetFilePath);
                if (!string.IsNullOrEmpty(offsetDirectory) && !Directory.Exists(offsetDirectory))
                {
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"Creating offset directory: {offsetDirectory}");
                    });
                    Directory.CreateDirectory(offsetDirectory);
                }
                
                // Read existing offsets if the file exists
                Dictionary<string, long> fileOffsets = new Dictionary<string, long>();
                if (File.Exists(offsetFilePath))
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI($"Reading existing offset file: {offsetFilePath}");
                        });
                        
                        string offsetFileContent = await File.ReadAllTextAsync(offsetFilePath);
                        fileOffsets = JsonSerializer.Deserialize<Dictionary<string, long>>(offsetFileContent) ?? new Dictionary<string, long>();
                        
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI($"Loaded {fileOffsets.Count} entries from offset file");
                        });
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI($"Error reading offset file, creating new one: {ex.Message}");
                        });
                    }
                }
                
                // Update/add the offset for this file
                fileOffsets[filePath] = offset;
                
                // Use a unique temp filename to avoid conflicts
                string tempId = Guid.NewGuid().ToString().Substring(0, 8);
                string tempPath = $"{offsetFilePath}.{tempId}.tmp";
                
                try
                {
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"Writing to temp offset file: {tempPath}");
                    });
                    
                    string offsetJson = JsonSerializer.Serialize(fileOffsets, new JsonSerializerOptions { WriteIndented = true });
                    
                    await File.WriteAllTextAsync(tempPath, offsetJson);
                    
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"Temp file written, copying to final location: {offsetFilePath}");
                    });
                    
                    File.Copy(tempPath, offsetFilePath, true);
                    
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"Offset file updated, deleting temp file");
                    });
                    
                    File.Delete(tempPath);
                    
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"Offset update completed successfully");
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"Error during offset file write operation: {ex.Message}");
                    });
                    
                    // Try to clean up temp file
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI("Cleaned up temporary offset file after error");
                            });
                        }
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                    throw;
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($"Warning: Failed to update offset file: {ex.Message}");
                });
            }
                }

        private Dictionary<string, string> ParseLogLine(string line)
        {
            var result = new Dictionary<string, string>(16); // Pre-allocate capacity
            
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
                    result["event_datetime"] = fields[2]; // ISO-8601 format (e.g., 2025-05-12T00:01:19)
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
                    
                    // The rest is the event message
                    if (fields.Length > 15)
                    {
                        StringBuilder sb = new StringBuilder();
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
                    // Use dispatcher for UI operations
                    Dispatcher.Invoke(() => {
                        LogToUI($"Malformed log line (expected at least 15 fields, got {fields.Length}): {line}");
                    });
                    
                    // Try to extract available fields
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
                // Use dispatcher for UI operations
                Dispatcher.Invoke(() => {
                    LogToUI($"Error parsing log line: {ex.Message} - Line: {line}");
                });
            }
            
            return result;
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
        
        private async Task<(int inserted, int skipped)> InsertLogBatchAsync(List<Dictionary<string, string>> logEntries, string filePath, string connectionString)
        {
            if (logEntries.Count == 0) return (0, 0);
            
            int entriesInserted = 0;
            int entriesSkipped = 0;
            
            // Cache schema and table name locally to avoid cross-thread access to UI elements
            string schema = "";
            string tableName = "";
            
            // Get schema and table name from UI - must be done on UI thread
            await Dispatcher.InvokeAsync(() => {
                schema = DbSchemaTextBox.Text;
                tableName = DbTableTextBox.Text;
            });
            
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    
                    // Use a transaction for the batch
                    using (var transaction = await connection.BeginTransactionAsync())
                    {
                        using (var cmd = new NpgsqlCommand())
                        {
                            cmd.Connection = connection;
                            
                            // Prepare the command once with the SQL and parameter definitions
                            cmd.CommandText = $@"
                            WITH inserted AS (
                                INSERT INTO {schema}.{tableName} (
                                    message_id, event_source, event_datetime, event_class, 
                                    event_severity, event_action, filtering_point, ip,
                                    sender, recipients, msg_subject, msg_author,
                                    remote_peer, source_ip, country, event_msg, filename, entry_hash
                                ) VALUES (
                                    @messageId, @eventSource, @eventDateTime, @eventClass,
                                    @eventSeverity, @eventAction, @filteringPoint, @ip,
                                    @sender, @recipients, @msgSubject, @msgAuthor,
                                    @remotePeer, @sourceIp, @country, @eventMsg, @filename, @entryHash
                                ) ON CONFLICT (entry_hash) DO NOTHING
                                RETURNING 1
                            )
                            SELECT COUNT(*) FROM inserted";
                            
                            // Create the parameter objects once and reuse them
                            var paramMessageId = cmd.Parameters.Add("messageId", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramEventSource = cmd.Parameters.Add("eventSource", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramEventDateTime = cmd.Parameters.Add("eventDateTime", NpgsqlTypes.NpgsqlDbType.TimestampTz);
                            var paramEventClass = cmd.Parameters.Add("eventClass", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramEventSeverity = cmd.Parameters.Add("eventSeverity", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramEventAction = cmd.Parameters.Add("eventAction", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramFilteringPoint = cmd.Parameters.Add("filteringPoint", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramIp = cmd.Parameters.Add("ip", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramSender = cmd.Parameters.Add("sender", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramRecipients = cmd.Parameters.Add("recipients", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramMsgSubject = cmd.Parameters.Add("msgSubject", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramMsgAuthor = cmd.Parameters.Add("msgAuthor", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramRemotePeer = cmd.Parameters.Add("remotePeer", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramSourceIp = cmd.Parameters.Add("sourceIp", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramCountry = cmd.Parameters.Add("country", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramEventMsg = cmd.Parameters.Add("eventMsg", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramFilename = cmd.Parameters.Add("filename", NpgsqlTypes.NpgsqlDbType.Text);
                            var paramEntryHash = cmd.Parameters.Add("entryHash", NpgsqlTypes.NpgsqlDbType.Text);
                            
                            string filename = Path.GetFileName(filePath);
                            
                            foreach (var entry in logEntries)
                            {
                                try
                                {
                                    // Set parameter values
                                    paramMessageId.Value = entry.TryGetValue("message_id", out var msgId) ? (object)msgId : DBNull.Value;
                                    paramEventSource.Value = entry.TryGetValue("event_source", out var src) ? (object)src : DBNull.Value;
                                    
                                    // Handle DateTime conversion with explicit UTC specification
                                    DateTime dateTimeValue = ParseDateTime(entry.TryGetValue("event_datetime", out var dt) ? dt : null);
                                    paramEventDateTime.Value = dateTimeValue;
                                    
                                    paramEventClass.Value = entry.TryGetValue("event_class", out var cls) ? (object)cls : DBNull.Value;
                                    paramEventSeverity.Value = entry.TryGetValue("event_severity", out var sev) ? (object)sev : DBNull.Value;
                                    paramEventAction.Value = entry.TryGetValue("event_action", out var act) ? (object)act : DBNull.Value;
                                    paramFilteringPoint.Value = entry.TryGetValue("filtering_point", out var fp) ? (object)fp : DBNull.Value;
                                    paramIp.Value = entry.TryGetValue("ip", out var ip) ? (object)ip : DBNull.Value;
                                    paramSender.Value = entry.TryGetValue("sender", out var sender) ? (object)sender : DBNull.Value;
                                    paramRecipients.Value = entry.TryGetValue("recipients", out var rcpt) ? (object)rcpt : DBNull.Value;
                                    paramMsgSubject.Value = entry.TryGetValue("msg_subject", out var subj) ? (object)subj : DBNull.Value;
                                    paramMsgAuthor.Value = entry.TryGetValue("msg_author", out var auth) ? (object)auth : DBNull.Value;
                                    paramRemotePeer.Value = entry.TryGetValue("remote_peer", out var peer) ? (object)peer : DBNull.Value;
                                    paramSourceIp.Value = entry.TryGetValue("source_ip", out var sip) ? (object)sip : DBNull.Value;
                                    paramCountry.Value = entry.TryGetValue("country", out var country) ? (object)country : DBNull.Value;
                                    paramEventMsg.Value = entry.TryGetValue("event_msg", out var msg) ? (object)msg : DBNull.Value;
                                    paramFilename.Value = filename;
                                    paramEntryHash.Value = entry.TryGetValue("entry_hash", out var hash) ? (object)hash : DBNull.Value;
                                    
                                    // Execute and get the count of inserted rows (will be 0 for conflicts)
                                    int inserted = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                                    if (inserted > 0)
                                    {
                                        entriesInserted++;
                                    }
                                    else
                                    {
                                        entriesSkipped++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // Log to UI on dispatcher thread
                                    await Dispatcher.InvokeAsync(() => {
                                        LogToUI($"Error inserting log entry: {ex.Message}");
                                    });
                                }
                            }
                        }
                        
                        await transaction.CommitAsync();
                    }
                }
                
                return (entriesInserted, entriesSkipped);
            }
            catch (Exception ex)
            {
                // Log to UI on dispatcher thread
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($"Database error: {ex.Message}");
                });
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
            // Use dispatcher for UI operations
            Dispatcher.Invoke(() => {
                LogToUI($"Could not parse date '{dateTimeString}', using current UTC time instead");
            });
            return DateTime.UtcNow;
        }

        private void StopInAppProcessing()
        {
            try
            {
                LogToUI("Stopping processing...");
                
                // Stop timer-based processing if active
                if (_processingTimer != null && _processingTimer.IsEnabled)
                {
                    _processingTimer.Stop();
                    LogToUI("Timer-based processing stopped");
                }
                
                // For backward compatibility, also stop any existing process
                if (_inAppProcessInstance != null && !_inAppProcessInstance.HasExited)
                {
                    LogToUI("Stopping process-based processing...");
                    _inAppProcessInstance.Kill();
                    _inAppProcessInstance.Dispose();
                    _inAppProcessInstance = null;
                }
                
                // Update state
                _isProcessingEnabled = false;
                LogToUI("Processing stopped");
            }
            catch (Exception ex)
            {
                LogToUI($"Error stopping processing: {ex.Message}");
            }
        }

        // Processing timer for window mode
        private System.Windows.Threading.DispatcherTimer _processingTimer;
        private DateTime _lastProcessingTime = DateTime.MinValue;

        private void StartInAppProcessing()
        {
            try
            {
                LogToUI("Starting timer-based processing");
                
                // Save configuration asynchronously without blocking UI thread
                Task.Run(() => {
                    try
                    {
                        Dispatcher.Invoke(() => {
                            try
                            {
                                SaveConfigurationToFile();
                                LogToUI("Configuration saved for processing");
                            }
                            catch (Exception ex)
                            {
                                LogToUI($"Warning: Failed to save config: {ex.Message}");
                            }
                        });
                    }
                    catch
                    {
                        // Ignore errors in background thread
                    }
                });
                
                // Make sure no other process is running
        StopInAppProcessing();
        
        // Clear any existing timer
        if (_processingTimer != null && _processingTimer.IsEnabled)
        {
            _processingTimer.Stop();
        }
        
        // Create a new dispatcher timer for polling log files
        _processingTimer = new System.Windows.Threading.DispatcherTimer();
        
        // Get polling interval from UI
        int pollingInterval = 5; // Default 5 seconds
        if (int.TryParse(PollingIntervalTextBox.Text, out int interval) && interval > 0)
        {
            pollingInterval = interval;
        }
        
        // Configure timer
        _processingTimer.Interval = TimeSpan.FromSeconds(pollingInterval);
        _processingTimer.Tick += ProcessingTimer_Tick;
        
        // Start timer
        _processingTimer.Start();
        
        // Update UI state
        _isProcessingEnabled = true;
        StatusTextBlock.Text = "Status: Running (Window Mode)";
        StatusTextBlock.Foreground = Brushes.Green;
        StartServiceButton.IsEnabled = false;
        StopServiceButton.IsEnabled = true;
        ProcessPastFilesButton.IsEnabled = false;
        
        LogToUI($"Timer-based processing started with {pollingInterval} second interval");
    }
    catch (Exception ex)
    {
        LogToUI($"Error starting in-app processing: {ex.Message}");
        _isProcessingEnabled = false;
        StatusTextBlock.Text = "Status: Error";
        StatusTextBlock.Foreground = Brushes.Red;
        StartServiceButton.IsEnabled = true;
        StopServiceButton.IsEnabled = false;
        ProcessPastFilesButton.IsEnabled = true;
    }
}

// Timer tick handler for processing
private void ProcessingTimer_Tick(object sender, EventArgs e)
{
    try
    {
        // Skip if last process was too recent (avoid overlap)
        if (DateTime.Now.Subtract(_lastProcessingTime).TotalSeconds < 1)
        {
            return;
        }
        
        // Update last processing time
        _lastProcessingTime = DateTime.Now;
        
        // Show activity in UI
        Dispatcher.Invoke(() => {
            CurrentFileTextBlock.Text = $"Checking at {DateTime.Now:HH:mm:ss}...";
        });
        
        // Run processing on background thread
        ThreadPool.QueueUserWorkItem(async _ => {
            try
            {
                // Get configuration
                string logDirectory = "";
                string logFilePattern = "";
                string offsetFilePath = "";
                string connectionString = "";
                
                // Safely get configuration from UI thread
                await Dispatcher.InvokeAsync(() => {
                    try
                    {
                        logDirectory = LogDirectoryTextBox.Text;
                        logFilePattern = LogFilePatternTextBox.Text;
                        offsetFilePath = OffsetFilePathTextBox.Text;
                        connectionString = BuildConnectionString();
                    }
                    catch (Exception ex)
                    {
                        LogToUI($"Error getting configuration: {ex.Message}");
                    }
                });
                
                // Process log files 
                // Use the existing ProcessPastLogFileAsync method to handle file processing
                string resolvedPattern = ResolveFilePattern(logFilePattern);
                string fullPath = Path.Combine(logDirectory, resolvedPattern);
                
                // Log activity
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($">>> Processing log file: {fullPath}");
                });
                
                // Check if file exists
                if (File.Exists(fullPath))
                {
                    try {
                        // Process single file
                        var result = await ProcessPastLogFileAsync(fullPath, connectionString);
                        
                        // Update UI with result
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI($"Processed {result.processed} entries: {result.inserted} inserted, {result.skipped} skipped");
                            CurrentOffsetTextBlock.Text = $"{result.processed} (I:{result.inserted}/S:{result.skipped})";
                        });
                    }
                    catch (Exception ex) {
                        // Log error on UI thread
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI($">>> Database error: {ex.Message}", true);
                            LogToUI($"Error processing file {fullPath}: {ex.Message}", true);
                        });
                    }
                }
                else if (resolvedPattern.Contains("*") || resolvedPattern.Contains("?"))
                {
                    // Wildcard pattern - try to find matching files
                    string[] matchingFiles = Directory.GetFiles(logDirectory, Path.GetFileName(resolvedPattern));
                    
                    if (matchingFiles.Length > 0)
                    {
                        try {
                            // Process most recent file only (to avoid flooding)
                            string mostRecentFile = matchingFiles
                                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                                .First();
                                
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI($"Processing most recent matching file: {Path.GetFileName(mostRecentFile)}");
                            });
                            
                            var result = await ProcessPastLogFileAsync(mostRecentFile, connectionString);
                            
                            // Update UI with result
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI($"Processed {result.processed} entries: {result.inserted} inserted, {result.skipped} skipped");
                                CurrentOffsetTextBlock.Text = $"{result.processed} (I:{result.inserted}/S:{result.skipped})";
                            });
                        }
                        catch (Exception ex) {
                            // Log error on UI thread
                            await Dispatcher.InvokeAsync(() => {
                                LogToUI($">>> Database error: {ex.Message}", true);
                                LogToUI($"Error processing matching files: {ex.Message}", true);
                            });
                        }
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() => {
                            LogToUI($"No matching files found for pattern: {resolvedPattern}");
                        });
                    }
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => {
                        LogToUI($"File not found: {fullPath}");
                    });
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => {
                    LogToUI($"Error in processing cycle: {ex.Message}", true);
                });
            }
        });
    }
    catch (Exception ex)
    {
        Dispatcher.Invoke(() => {
            LogToUI($"Error in timer tick: {ex.Message}", true);
        });
    }
}

// Helper to resolve file patterns with date
private string ResolveFilePattern(string pattern)
{
    if (string.IsNullOrEmpty(pattern))
        return "*.log";
        
    if (pattern.Contains("{Date:"))
    {
        // Extract date format
        int startIndex = pattern.IndexOf("{Date:");
        int endIndex = pattern.IndexOf("}", startIndex);
        
        if (startIndex >= 0 && endIndex > startIndex)
        {
            string dateFormat = pattern.Substring(startIndex + 6, endIndex - startIndex - 6);
            string formattedDate = DateTime.Now.ToString(dateFormat);
            return pattern.Replace($"{{Date:{dateFormat}}}", formattedDate);
        }
    }
    
    return pattern;
}

        private async Task CreateTableAsync(string connectionString, string schema, string tableName)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                // First ensure the schema exists
                using (var schemaCmd = new NpgsqlCommand())
                {
                    schemaCmd.Connection = connection;
                    schemaCmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {schema}";
                    await schemaCmd.ExecuteNonQueryAsync();
                }
                
                // Create the table with appropriate columns
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = connection;
                    cmd.CommandText = $@"
                    CREATE TABLE {schema}.{tableName} (
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
                    
                    -- Create a unique index on the entry_hash column for deduplication
                    CREATE UNIQUE INDEX idx_{tableName}_entry_hash 
                    ON {schema}.{tableName} (entry_hash);
                    
                    -- Create an index on event_datetime for better query performance
                    CREATE INDEX idx_{tableName}_event_datetime
                    ON {schema}.{tableName} (event_datetime);
                    
                    -- Create an index on message_id for better lookups
                    CREATE INDEX idx_{tableName}_message_id
                    ON {schema}.{tableName} (message_id);
                    ";
                    
                    await cmd.ExecuteNonQueryAsync();
                    LogToUI($"Created table {schema}.{tableName} with indexes");
                }
            }
        }

        private async void ReconnectDatabaseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // No reconnect button functionality needed
                
                // First test connection locally (UI thread) to verify credentials
                LogToUI(">>> Testing database connection locally first...");
                
                string connectionString = BuildConnectionString();
                bool connectionSuccess = false;
                
                try
                {
                    // Test connection to the PostgreSQL database
                    using (var connection = new NpgsqlConnection(connectionString))
                    {
                        await connection.OpenAsync();
                        LogToUI("Local connection test successful!");
                        connectionSuccess = true;
                    }
                }
                catch (NpgsqlException ex)
                {
                    // Handle database errors with user-friendly message
                    string errorMessage = GetUserFriendlyDbErrorMessage(ex);
                    LogToUI($"Local connection test failed: {errorMessage}", true);
                    MessageBox.Show($"Database connection failed: {errorMessage}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                // Save configuration to ensure latest settings are used
                if (connectionSuccess && SaveConfigurationToFile())
                {
                    LogToUI("Configuration saved to local file");
                    
                    // Update service configuration if connected
                    if (_isPipeConnected)
                    {
                        LogToUI(">>> Updating service with working database configuration...");
                        
                        // Send config to service
                        try
                        {
                            await SendMessageToService("config", GetConfigurationFromUI());
                            LogToUI(">>> Service configuration updated with working database settings");
                            
                            // Send a special reconnect command
                            LogToUI(">>> Requesting database reconnection from service...");
                            await SendMessageToService("reconnect");
                            LogToUI(">>> Reconnection request sent to service");
                            
                            // Add small delay to allow service to process
                            await Task.Delay(1000);
                            
                            // Send a status request to get latest service state
                            await SendMessageToService("status");
                        }
                        catch (Exception sendEx)
                        {
                            LogToUI($"Error sending configuration to service: {sendEx.Message}", true);
                            MessageBox.Show($"Failed to update service configuration: {sendEx.Message}", "Service Communication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        LogToUI("Service not connected - updated configuration will be used on next start");
                    }
                }
                else
                {
                    LogToUI("Failed to save configuration", true);
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error during reconnect operation: {ex.Message}", true);
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // No additional cleanup needed
            }
        }

        // Helper method to safely parse JSON that may have improperly escaped Windows paths
        private T? SafeJsonDeserialize<T>(string json) where T : class
        {
            try
            {
                // First remove any invalid control characters that often mess up JSON parsing
                StringBuilder cleanedJson = new StringBuilder(json.Length);
                bool insideQuotes = false;
                bool escaped = false;
                
                // Clean all control characters except \r \n \t
                for (int i = 0; i < json.Length; i++)
                {
                    char c = json[i];
                    
                    // Track if we're inside a JSON string
                    if (c == '"' && !escaped)
                    {
                        insideQuotes = !insideQuotes;
                    }
                    
                    // Track escape sequences
                    if (c == '\\' && !escaped)
                    {
                        escaped = true;
                    }
                    else
                    {
                        escaped = false;
                    }
                    
                    // Filter out invalid control characters
                    if (c < 32 && c != '\r' && c != '\n' && c != '\t')
                    {
                        // Skip control chars
                        continue;
                    }
                    
                    // Filter replacement character
                    if (c == '\uFFFD')
                    {
                        continue;
                    }
                    
                    // Accept this character
                    cleanedJson.Append(c);
                }
                
                // Get the cleaned string
                string processedJson = cleanedJson.ToString();
                
                // Trim any non-JSON content from the beginning and end
                // This helps with half-received messages from the pipe
                int firstJsonChar = -1;
                int lastJsonChar = -1;
                
                // Find first JSON structure character
                for (int i = 0; i < processedJson.Length; i++)
                {
                    if (processedJson[i] == '{' || processedJson[i] == '[')
                    {
                        firstJsonChar = i;
                        break;
                    }
                }
                
                // Find last JSON structure character
                for (int i = processedJson.Length - 1; i >= 0; i--)
                {
                    if (processedJson[i] == '}' || processedJson[i] == ']')
                    {
                        lastJsonChar = i;
                        break;
                    }
                }
                
                // If we found valid JSON structure characters, extract the JSON part
                if (firstJsonChar >= 0 && lastJsonChar > firstJsonChar)
                {
                    processedJson = processedJson.Substring(firstJsonChar, lastJsonChar - firstJsonChar + 1);
                }
                
                // Check for incomplete objects or arrays and try to fix them
                int openBraces = processedJson.Count(c => c == '{');
                int closeBraces = processedJson.Count(c => c == '}');
                int openBrackets = processedJson.Count(c => c == '[');
                int closeBrackets = processedJson.Count(c => c == ']');
                
                // Fix unbalanced braces/brackets
                if (openBraces > closeBraces)
                {
                    processedJson += new string('}', openBraces - closeBraces);
                }
                
                if (openBrackets > closeBrackets)
                {
                    processedJson += new string(']', openBrackets - closeBrackets);
                }
                
                // Fix common JSON syntax issues
                processedJson = processedJson
                    .Replace(",}", "}")
                    .Replace(",]", "]")
                    .Replace("\\\\", "\\\\\\\\") // Double escape backslashes in strings
                    .Replace("\\'", "\\\\'");    // Properly escape single quotes
                
                // First attempt: try direct deserialization with the cleaned JSON
                try
                {
                    return JsonSerializer.Deserialize<T>(processedJson);
                }
                catch (JsonException)
                {
                    // If that fails, try with Windows path fixes
                    try
                    {
                        // Common issue: Windows paths with single backslashes in JSON
                        // These need to be double-escaped: \ becomes \\
                        string pathFixedJson = processedJson
                            .Replace(@":\", @":\\")                          // Drive letter fix
                            .Replace(@"\bin", @"\\bin")                      // Common folder patterns
                            .Replace(@"\Debug", @"\\Debug")
                            .Replace(@"\Release", @"\\Release")
                            .Replace(@"\net", @"\\net")
                            .Replace(@"\orf", @"\\orf")
                            .Replace(@"\log", @"\\log")
                            .Replace(@"\Windows", @"\\Windows")
                            .Replace(@"\Program", @"\\Program")
                            .Replace(@"\Files", @"\\Files")
                            .Replace(@"\SHARED", @"\\SHARED")
                            .Replace(@"\DEV", @"\\DEV")
                            .Replace(@"\Projects", @"\\Projects")
                            .Replace(@"\Cursor", @"\\Cursor")
                            .Replace(@"\AppData", @"\\AppData")
                            .Replace(@"\Local", @"\\Local")
                            .Replace(@"\Temp", @"\\Temp")
                            .Replace(@"\orf2Postgres", @"\\orf2Postgres");
                                                   
                        return JsonSerializer.Deserialize<T>(pathFixedJson);
                    }
                    catch
                    {
                        // Last resort attempt: try a more permissive JSON reader
                        try
                        {
                            var options = new JsonSerializerOptions
                            {
                                AllowTrailingCommas = true,
                                ReadCommentHandling = JsonCommentHandling.Skip,
                                PropertyNameCaseInsensitive = true
                            };
                            
                            return JsonSerializer.Deserialize<T>(processedJson, options);
                        }
                        catch (Exception finalEx)
                        {
                            LogToUI($"All JSON parsing attempts failed: {finalEx.Message}");
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUI($"Error parsing JSON: {ex.Message}", true);
                // Detailed debugging disabled
                // LogToUI("Raw message that failed: " + json, true);
                return null;
            }
        }
    }
} 
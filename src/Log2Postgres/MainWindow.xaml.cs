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
using System.Threading; // Added for CancellationTokenSource

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
public partial class MainWindow : Window
{
    private const string PipeName = "Log2PostgresServicePipe";
    private DispatcherTimer _serviceStatusTimer = null!;

    private const int MaxUiLogLines = 500; // Added constant for max log lines

            private NamedPipeClientStream? _servicePipeClient;
        private StreamReader? _servicePipeReader;
        private StreamWriter? _servicePipeWriter;
        private CancellationTokenSource? _pipeListenerCts;
        private Task? _pipeListenerTask;
        private readonly object _pipeLock = new object();
        private bool _isPipeConnected = false;

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
    private bool _isProcessing = false;
    private bool _hasUnsavedChanges = false; // Flag for unsaved changes
    
    // Timer for refreshing the position display
    private System.Threading.Timer? _positionUpdateTimer;

    // Properties for UI binding related to service status
#pragma warning disable CS8618 // Properties are initialized at declaration to string.Empty
    public string ServiceOperationalState { get; set; } = string.Empty;
    public string CurrentFile { get; set; } = string.Empty;
    public string LastErrorMessage { get; set; } = string.Empty;
#pragma warning restore CS8618

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
            
            // Constructor initializations for CS8618 were removed as properties are handled at declaration.
            
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
                catch (Exception ex_lfw) // Catch and log the specific exception
                {
                    // Log the detailed exception when LogFileWatcher fails to resolve
                    if (_logger != null) // Check if logger itself was initialized
                    {
                        _logger.LogError(ex_lfw, "Failed to get LogFileWatcher service from DI container.");
                    }
                    else
                    {
                        // Fallback if logger is not available
                        System.Diagnostics.Debug.WriteLine($"Critical Error: Failed to get LogFileWatcher. Logger not available. Exception: {ex_lfw}");
                        System.Windows.MessageBox.Show($"Critical Error: Could not initialize the LogFileWatcher service. Exception: {ex_lfw.Message}", "Service Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    // _logFileWatcher will remain null, subsequent checks will handle it
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
                catch (Exception)
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

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        _logger.LogInformation("MainWindow loaded");
        LoadSettings();
        UpdateDbActionButtonsState();
        UpdateStatusBar("Application started successfully");
        
        _serviceStatusTimer = new DispatcherTimer();
        _serviceStatusTimer.Interval = TimeSpan.FromSeconds(5); 
        _serviceStatusTimer.Tick += ServiceStatusTimer_Tick;
        _serviceStatusTimer.Start();
        _logger.LogInformation("Service status timer started for IPC.");

        Task.Run(async () => await EnsureConnectedToServicePipeAsync());
    }

    private async void ServiceStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPipeConnected)
        {
            await EnsureConnectedToServicePipeAsync();
        }
        else if (_servicePipeWriter != null)
        {
            try
            {
                await _servicePipeWriter.WriteLineAsync("GET_STATUS");
                _logger.LogDebug("IPC Client: Sent GET_STATUS request via persistent connection.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IPC Client: Error sending GET_STATUS via persistent connection.");
                await AttemptPipeReconnectAsync();
            }
        }
        UpdateServiceControlUi();
    }

    // ---- NEW IPC METHODS START ----
    private async Task EnsureConnectedToServicePipeAsync()
    {
        bool isServiceRunning = false;
        try
        {
            if (WindowsServiceManager.IsServiceInstalled())
            {
                var scStatus = WindowsServiceManager.GetServiceStatus();
                if (scStatus == ServiceControllerStatus.Running || scStatus == ServiceControllerStatus.StartPending)
                {
                    isServiceRunning = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking service status for pipe connection.");
        }

        if (!isServiceRunning)
        {
            if (_isPipeConnected) 
            {
                _logger.LogInformation("Service is not running. Disconnecting pipe.");
                await DisconnectFromServicePipeAsync();
            }
            UpdateUiWithServiceStatus(null, false, WindowsServiceManager.GetServiceStatus(), WindowsServiceManager.IsServiceInstalled());
            return;
        }

        if (isServiceRunning && !_isPipeConnected)
        {
            _logger.LogInformation("Service is running, attempting to connect IPC pipe.");
            await ConnectToServicePipeAsync();
        }
    }

    private async Task ConnectToServicePipeAsync()
    {
        if (_isPipeConnected) return;
        lock (_pipeLock) 
        {
            if (_isPipeConnected || _pipeListenerTask != null && !_pipeListenerTask.IsCompleted) 
            {
                _logger.LogDebug("Pipe connection or listener task already active/pending. Skipping new connection attempt.");
                return; 
            }
        }
        
        _logger.LogInformation("Attempting to connect to service pipe: {PipeName}", PipeName);
        
        const int maxRetries = 3;
        const int connectTimeoutMs = 3000; // 3 seconds timeout for each attempt
        const int retryDelayMs = 2000;    // 2 seconds delay between retries

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Connection attempt {AttemptNumber}/{MaxAttempts} to pipe: {PipeName}", attempt, maxRetries, PipeName);
                _servicePipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _servicePipeClient.ConnectAsync(connectTimeoutMs); 

                if (_servicePipeClient.IsConnected)
                {
                    _servicePipeReader = new StreamReader(_servicePipeClient);
                    _servicePipeWriter = new StreamWriter(_servicePipeClient) { AutoFlush = true };
                    _isPipeConnected = true;

                    _pipeListenerCts = new CancellationTokenSource();
                    _pipeListenerTask = Task.Run(() => ListenForServiceMessagesAsync(_pipeListenerCts.Token), _pipeListenerCts.Token);
                    _logger.LogInformation("Successfully connected to service pipe and started listener after {AttemptNumber} attempt(s).", attempt);
                    
                    if (_servicePipeWriter != null)
                    {
                        await _servicePipeWriter.WriteLineAsync("GET_STATUS");
                    }
                    return; // Connection successful, exit method
                }
                // This part should ideally not be reached if ConnectAsync throws on timeout,
                // but included for completeness if it returns without connecting.
                _logger.LogWarning("ConnectAsync completed but pipe not connected on attempt {AttemptNumber}.", attempt);
                await _servicePipeClient.DisposeAsync(); // Clean up the unsuccessful client
                _servicePipeClient = null;

            }
            catch (System.TimeoutException) // Explicitly use System.TimeoutException
            {
                _logger.LogWarning("Timeout connecting to service pipe on attempt {AttemptNumber}/{MaxAttempts}.", attempt, maxRetries);
                if (_servicePipeClient != null) await _servicePipeClient.DisposeAsync();
                _servicePipeClient = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during ConnectToServicePipeAsync attempt {AttemptNumber}/{MaxAttempts}.", attempt, maxRetries);
                if (_servicePipeClient != null) await _servicePipeClient.DisposeAsync();
                _servicePipeClient = null;
                // For unexpected errors, break the loop and proceed to disconnect.
                // We don't want to keep retrying on, for example, a fundamental configuration error.
                break; 
            }

            if (attempt < maxRetries)
            {
                _logger.LogInformation("Waiting {DelayMs}ms before next connection attempt.", retryDelayMs);
                await Task.Delay(retryDelayMs);
            }
        }

        // If all retries failed
        _logger.LogError("Failed to connect to service pipe after {MaxAttempts} attempts.", maxRetries);
        await DisconnectFromServicePipeAsync(true); 
    }

    private async Task ListenForServiceMessagesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("IPC Message Listener: Started.");
        try
        {
            while (!cancellationToken.IsCancellationRequested && _servicePipeReader != null && _isPipeConnected)
            {
                string? jsonMessage = await _servicePipeReader.ReadLineAsync(cancellationToken);
                if (jsonMessage == null)
                {
                    _logger.LogInformation("IPC Message Listener: Pipe closed by server or stream ended.");
                    break; 
                }

                _logger.LogDebug("IPC Message Listener: Received raw message: {JsonMessage}", jsonMessage);
                try
                {
                    var genericMessage = JsonConvert.DeserializeObject<IpcMessage<object>>(jsonMessage);
                    if (genericMessage == null || string.IsNullOrEmpty(genericMessage.Type))
                    {
                        _logger.LogWarning("IPC Message Listener: Received malformed IPC message (no type): {JsonMessage}", jsonMessage);
                        continue;
                    }
                    _logger.LogInformation("IPC Message Listener: Message Type = {MessageType}", genericMessage.Type);

                    switch (genericMessage.Type)
                    {
                        case "LOG_ENTRIES":
                            _logger.LogDebug("IPC Message Listener: Processing LOG_ENTRIES message.");
                            var logEntriesMessage = JsonConvert.DeserializeObject<IpcMessage<List<string>>>(jsonMessage);
                            if (logEntriesMessage?.Payload != null)
                            {
                                _logger.LogInformation("IPC Message Listener: Received {Count} log entries from service.", logEntriesMessage.Payload.Count);
                                Dispatcher.InvokeAsync(() => {
                                    foreach (var logEntry in logEntriesMessage.Payload)
                                    {
                                        AppendAndTrimLog($"[SERVICE] {logEntry}"); 
                                    }
                                });
                            }
                            else
                            {
                                _logger.LogWarning("IPC Message Listener: LOG_ENTRIES message payload is null or message deserialization failed. Raw: {JsonMessage}", jsonMessage);
                            }
                            break;
                        case "SERVICE_STATUS": 
                            var statusMessage = JsonConvert.DeserializeObject<IpcMessage<PipeServiceStatus>>(jsonMessage);
                            if (statusMessage?.Payload != null)
                            {
                                UpdateUiWithServiceStatus(statusMessage.Payload, true, WindowsServiceManager.GetServiceStatus(), WindowsServiceManager.IsServiceInstalled());
                            }
                            break;
                        default:
                            _logger.LogWarning("IPC Message Listener: Received unknown IPC message type: {MessageType}", genericMessage.Type);
                            break;
                    }
                }
                catch (JsonException jsonEx)
                { 
                    _logger.LogError(jsonEx, "IPC Message Listener: JSON deserialization error: {JsonMessage}", jsonMessage);
                }
                catch (Exception ex)
                { 
                    _logger.LogError(ex, "IPC Message Listener: Error processing message: {JsonMessage}", jsonMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("IPC Message Listener: Operation cancelled.");
        }
        catch (IOException ioEx)
        {
            _logger.LogWarning(ioEx, "IPC Message Listener: IOException (pipe likely closed).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC Message Listener: An unhandled exception occurred.");
        }
        finally
        {
            _logger.LogInformation("IPC Message Listener: Stopping.");
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(async () => await AttemptPipeReconnectAsync());
            }
        }
    }
    
    private async Task AttemptPipeReconnectAsync()
    {
        _logger.LogInformation("Attempting to reconnect to service pipe due to disconnection or error.");
        await DisconnectFromServicePipeAsync(true); 
        await Task.Delay(2000); 
        await EnsureConnectedToServicePipeAsync(); 
    }

    private async Task DisconnectFromServicePipeAsync(bool fromErrorOrClosure = false)
    {
        _logger.LogInformation("DisconnectFromServicePipeAsync called. FromErrorOrClosure: {FromError}", fromErrorOrClosure);
        if (!_isPipeConnected && !fromErrorOrClosure && _pipeListenerTask == null) 
        { 
            _logger.LogDebug("Pipe already disconnected or never connected.");
            return;
        }
        lock(_pipeLock)
        {
            if (_pipeListenerCts != null)
            {
                if (!_pipeListenerCts.IsCancellationRequested) _pipeListenerCts.Cancel();
                _pipeListenerCts.Dispose();
                _pipeListenerCts = null;
                _logger.LogDebug("Pipe listener CTS cancelled and disposed.");
            }
        }
        if (_pipeListenerTask != null)
        {
            try
            {
                _logger.LogDebug("Waiting for pipe listener task to complete...");
                bool completedInTime = await Task.WhenAny(_pipeListenerTask, Task.Delay(2000)) == _pipeListenerTask;
                if (completedInTime)
                {
                    _logger.LogInformation("Pipe listener task completed.");
                }
                else
                {
                    _logger.LogWarning("Pipe listener task did not complete within timeout during disconnect.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception waiting for pipe listener task on disconnect.");
            }
            _pipeListenerTask = null;
        }
        lock(_pipeLock)
        {
            if (_servicePipeWriter != null) { try { _servicePipeWriter.Dispose(); } catch {} _servicePipeWriter = null; }
            if (_servicePipeReader != null) { try { _servicePipeReader.Dispose(); } catch {} _servicePipeReader = null; }
            if (_servicePipeClient != null) { try { _servicePipeClient.Close(); _servicePipeClient.Dispose(); } catch {} _servicePipeClient = null; }
            _isPipeConnected = false;
            _logger.LogInformation("Service pipe client and streams disposed. _isPipeConnected set to false.");
        }
        if (fromErrorOrClosure)
        {
            UpdateUiWithServiceStatus(null, false, WindowsServiceManager.GetServiceStatus(), WindowsServiceManager.IsServiceInstalled());
        }
    }
    // ---- NEW IPC METHODS END ----

    private async Task RequestServiceStatusUpdateAsync()
    {
        // If not connected, we rely on the existing timer or connection logic
        // to eventually establish a connection and get status.
        // This method's prime role is to request an update if already connected.
        if (!_isPipeConnected)
        {
            _logger.LogInformation("RequestServiceStatusUpdateAsync: Pipe not connected. Status update will occur once connection is established by other mechanisms.");
            // Optionally, one could call await EnsureConnectedToServicePipeAsync(); here
            // if an immediate connection attempt is desired, but EnsureConnectedToServicePipeAsync
            // itself sends GET_STATUS upon successful connection.
            return;
        }

        if (_servicePipeWriter != null)
        {
            try
            {
                _logger.LogInformation("RequestServiceStatusUpdateAsync: Sending GET_STATUS request via persistent connection.");
                await _servicePipeWriter.WriteLineAsync("GET_STATUS");
                // Response will be handled by ListenForServiceMessagesAsync
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RequestServiceStatusUpdateAsync: Error sending GET_STATUS.");
                await AttemptPipeReconnectAsync(); // Use existing error handling for pipe issues
            }
        }
        else
        {
            // This state (connected but no writer) should ideally not happen with current logic.
            _logger.LogWarning("RequestServiceStatusUpdateAsync: Pipe is marked connected, but _servicePipeWriter is null. Attempting reconnect.");
            await AttemptPipeReconnectAsync();
        }
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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
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

    private async void LoadSettings()
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
            await UpdateDatabaseStatus();

            _hasUnsavedChanges = false; // Settings are now in sync with UI
            UpdateDbActionButtonsState(); // Update DB buttons based on loaded settings
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings: {Message}", ex.Message);
            LogError($"Error loading settings: {ex.Message}");
        }
    }

    private async void SaveSettings()
    {
        try
        {
            _logger.LogInformation("Saving settings...");
            var configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            Newtonsoft.Json.Linq.JObject? configObj;
            bool isNewFile = !File.Exists(configFile);

            if (isNewFile)
            {
                _logger.LogInformation($"appsettings.json not found at {configFile}. A new SUPER-MINIMAL file will be created for testing (LogMonitorSettings ONLY).");
                configObj = new Newtonsoft.Json.Linq.JObject();
                var logMonitorSettingsObj = new Newtonsoft.Json.Linq.JObject();
                logMonitorSettingsObj["BaseDirectory"] = LogDirectory.Text;
                logMonitorSettingsObj["LogFilePattern"] = LogFilePattern.Text;
                logMonitorSettingsObj["PollingIntervalSeconds"] = int.TryParse(PollingInterval.Text, out int intervalTmp) ? intervalTmp : 5;
                configObj["LogMonitorSettings"] = logMonitorSettingsObj;
            }
            else
            {
                var configJson = File.ReadAllText(configFile);
                configObj = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(configJson);
                if (configObj == null) { 
                    _logger.LogWarning("Config file existed but deserialized to null. Reinitializing.");
                    configObj = new Newtonsoft.Json.Linq.JObject();
                }
                Newtonsoft.Json.Linq.JObject logMonitorSettingsJsonExisting;
                if (configObj["LogMonitorSettings"] is Newtonsoft.Json.Linq.JObject existingLMS) {
                    logMonitorSettingsJsonExisting = existingLMS;
                } else {
                    logMonitorSettingsJsonExisting = new Newtonsoft.Json.Linq.JObject();
                    configObj["LogMonitorSettings"] = logMonitorSettingsJsonExisting;
                }
                logMonitorSettingsJsonExisting["BaseDirectory"] = LogDirectory.Text;
                logMonitorSettingsJsonExisting["LogFilePattern"] = LogFilePattern.Text;
                logMonitorSettingsJsonExisting["PollingIntervalSeconds"] = int.TryParse(PollingInterval.Text, out int intervalTmp2) ? intervalTmp2 : 5;

                Newtonsoft.Json.Linq.JObject dbSettingsJson;
                if (configObj["DatabaseSettings"] is Newtonsoft.Json.Linq.JObject existingDb) {
                    dbSettingsJson = existingDb;
                } else {
                    dbSettingsJson = new Newtonsoft.Json.Linq.JObject();
                    configObj["DatabaseSettings"] = dbSettingsJson;
                    var defaultDb = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(DefaultAppSettingsJson)?["DatabaseSettings"];
                    if (defaultDb != null) configObj["DatabaseSettings"] = defaultDb;
                }
                dbSettingsJson["Host"] = DbHost.Text;
                dbSettingsJson["Port"] = DbPort.Text;
                dbSettingsJson["Username"] = DbUsername.Text;
                dbSettingsJson["Password"] = SecurePassword(); 
                dbSettingsJson["Database"] = DbName.Text; 
                dbSettingsJson["Schema"] = DbSchema.Text;
                dbSettingsJson["Table"] = DbTable.Text;
                dbSettingsJson["ConnectionTimeout"] = int.TryParse(DbTimeout.Text, out int timeoutDb) ? timeoutDb : 30;

                if (configObj["Serilog"] == null) {
                    var defaultSerilog = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(DefaultAppSettingsJson)?["Serilog"];
                    if (defaultSerilog != null) configObj["Serilog"] = defaultSerilog;
                }
            }

            string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(configObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(configFile, updatedJson);
            _logger.LogInformation("Settings saved successfully to appsettings.json");

            if (_configuration is Microsoft.Extensions.Configuration.IConfigurationRoot configurationRoot)
            {
                _logger.LogInformation("Attempting to force IConfigurationRoot.Reload()");
                configurationRoot.Reload();
                _logger.LogInformation("IConfigurationRoot.Reload() called.");
            }
            else
            {
                _logger.LogWarning("Cannot force configuration reload: _configuration is not IConfigurationRoot.");
            }

            var settingsForReloadAndIpc = new LogMonitorSettings
            {
                BaseDirectory = LogDirectory.Text,
                LogFilePattern = LogFilePattern.Text,
                PollingIntervalSeconds = int.TryParse(PollingInterval.Text, out int currentIntervalVal) ? currentIntervalVal : 5 
            };

            bool serviceInstalled = WindowsServiceManager.IsServiceInstalled();
            ServiceControllerStatus serviceStatus = ServiceControllerStatus.Stopped;
            if (serviceInstalled)
            {
                try { serviceStatus = WindowsServiceManager.GetServiceStatus(); }
                catch (Exception ex) { _logger.LogError(ex, "SaveSettings: Error getting service status for intended state check."); }
            }
            bool intendedLocalProcessingState = _isProcessing && (!serviceInstalled || serviceStatus != ServiceControllerStatus.Running);
            _logger.LogDebug($"SaveSettings: Intended local processing state after save: {intendedLocalProcessingState} (based on current _isProcessing: {_isProcessing}, serviceInstalled: {serviceInstalled}, serviceStatus: {serviceStatus})");
            
            await ReloadConfiguration(settingsForReloadAndIpc, intendedLocalProcessingState); 
            _logger.LogInformation("ReloadConfiguration completed. Now checking service status for IPC.");

            bool currentServiceInstalled = WindowsServiceManager.IsServiceInstalled(); 
            ServiceControllerStatus currentServiceStatus = ServiceControllerStatus.Stopped; 
            if(currentServiceInstalled) 
            {
                try { currentServiceStatus = WindowsServiceManager.GetServiceStatus(); }
                catch (Exception ex) { _logger.LogError(ex, "SaveSettings: Error getting service status post-reload."); }
            }
            
            _logger.LogInformation("IPC Check: ServiceInstalled={IsInstalled}, ServiceStatus={Status}.", 
                currentServiceInstalled, currentServiceStatus);

            if (currentServiceInstalled && currentServiceStatus == ServiceControllerStatus.Running)
            {
                _logger.LogInformation("Service is running. Attempting SendUpdateSettingsToServiceAsync.");
                bool ipcUpdateSuccess = await SendUpdateSettingsToServiceAsync(settingsForReloadAndIpc);
                if (ipcUpdateSuccess)
                {
                    _logger.LogInformation("IPC update of LogMonitorSettings to service succeeded.");
                    Dispatcher.Invoke(() => LogMessage("Running service notified of settings change."));
                    await RequestServiceStatusUpdateAsync(); 
                }
                else
                {
                    _logger.LogError("IPC update of LogMonitorSettings to service failed.");
                    Dispatcher.Invoke(() => LogError("Settings saved, but failed to update running service in real-time. Restart service to apply."));
                }
            }
            else
            {
                 _logger.LogInformation("Service not running/installed for IPC update. ServiceInstalled={IsInstalled}, ServiceStatus={Status}. Local settings reloaded.",
                     currentServiceInstalled, currentServiceStatus);
            }
            LogMessage("Settings saved successfully to appsettings.json"); 
            UpdateStatusBar("Configuration saved to appsettings.json");   
            _hasUnsavedChanges = false; // Settings have been saved
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
    /// Reloads the configuration for the LogFileWatcher and ensures local processing state is maintained.
    /// </summary>
    private async Task ReloadConfiguration(LogMonitorSettings settingsToApply, bool wasLocalProcessingIntended)
    {
        _logger.LogDebug($"ReloadConfiguration: Entered method. Was local processing intended to be active: {wasLocalProcessingIntended}");
        _logger.LogDebug($"ReloadConfiguration: Settings to apply to LogFileWatcher: BaseDirectory='{settingsToApply.BaseDirectory}', Pattern='{settingsToApply.LogFilePattern}', Interval={settingsToApply.PollingIntervalSeconds}");

        try
        {
            if (_logFileWatcher == null)
            {
                _logger.LogWarning("ReloadConfiguration: _logFileWatcher IS NULL. Cannot update local watcher.");
                UpdateStatusBar("Configuration saved, but LogFileWatcher not available.");
                return;
            }

            _logger.LogInformation($"ReloadConfiguration: LogFileWatcher.IsProcessing BEFORE UpdateSettingsAsync: {_logFileWatcher.IsProcessing}");
            _logger.LogInformation("ReloadConfiguration: Applying settings to LogFileWatcher via UpdateSettingsAsync.");
            await _logFileWatcher.UpdateSettingsAsync(settingsToApply);
            _logger.LogInformation("ReloadConfiguration: UpdateSettingsAsync completed.");
            _logger.LogInformation($"ReloadConfiguration: LogFileWatcher.IsProcessing AFTER UpdateSettingsAsync: {_logFileWatcher.IsProcessing}");

            bool isWatcherInContinuousProcessing = _logFileWatcher.IsProcessing;
            _logger.LogInformation($"ReloadConfiguration: LogFileWatcher.IsProcessing (continuous state) after UpdateSettingsAsync: {isWatcherInContinuousProcessing}");

            string statusMessage;

            if (!wasLocalProcessingIntended)
            {
                _logger.LogInformation("ReloadConfiguration: Local processing was NOT intended.");
                if (isWatcherInContinuousProcessing)
                {
                    _logger.LogInformation("ReloadConfiguration: Watcher is in continuous processing mode. Stopping it.");
                    _logFileWatcher.UIManagedStopProcessing();
                }
                
                _logger.LogInformation("ReloadConfiguration: Resetting position info to undo any unintended processing burst and ensure no progress is saved from it.");
                _logFileWatcher.ResetPositionInfo(); // This is key to clear positions from any burst processing.

                _isProcessing = false;
                statusMessage = "Settings updated. Local processing stopped; positions reset to prevent saving unintended progress.";
                LogMessage(statusMessage);
                Dispatcher.Invoke(() =>
                {
                    ProcessingStatusText.Text = "Idle (Settings Updated)";
                    StopPositionUpdateTimer();
                });
            }
            else // wasLocalProcessingIntended is true
            {
                _logger.LogInformation("ReloadConfiguration: Local processing WAS intended.");
                if (!isWatcherInContinuousProcessing)
                {
                    _logger.LogInformation("ReloadConfiguration: Watcher is not in continuous processing mode. Attempting to start it via UIManagedStartProcessingAsync.");
                    await _logFileWatcher.UIManagedStartProcessingAsync(); 
                    isWatcherInContinuousProcessing = _logFileWatcher.IsProcessing; // Re-check state after start attempt
                }

                _isProcessing = isWatcherInContinuousProcessing;
                if (_isProcessing)
                {
                    statusMessage = "Settings updated. Local processing is active.";
                    LogMessage(statusMessage);
                    Dispatcher.Invoke(() =>
                    {
                        ProcessingStatusText.Text = "Running (Settings Updated)";
                        if (_positionUpdateTimer == null) StartPositionUpdateTimer();
                    });
                }
                else
                {
                    statusMessage = "Settings updated. Local processing was intended but failed to start/remain active.";
                    LogError(statusMessage); // Log as error or warning
                    Dispatcher.Invoke(() =>
                    {
                        ProcessingStatusText.Text = "Idle (Failed to Start)";
                        StopPositionUpdateTimer();
                    });
                }
            }
            
            // Always refresh button states based on the final _isProcessing and service status
            Dispatcher.Invoke(UpdateServiceControlUi); 

            UpdateStatusBar("Configuration saved and reloaded.");
            _logger.LogDebug($"ReloadConfiguration: Exiting method normally. Final _isProcessing state: {_isProcessing}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReloadConfiguration: CAUGHT EXCEPTION."); 
            LogError($"Error reloading configuration: {ex.Message}");
            UpdateStatusBar($"Error reloading config: {ex.Message}");
            // Attempt to restore a consistent UI state on error
            Dispatcher.Invoke(() =>
            {
                _isProcessing = _logFileWatcher?.IsProcessing ?? false;
                ProcessingStatusText.Text = _isProcessing ? "Error - Still Running?" : "Error - Stopped";
                UpdateServiceControlUi();
                if (!_isProcessing) StopPositionUpdateTimer();
                else if (wasLocalProcessingIntended && _positionUpdateTimer == null && _isProcessing) StartPositionUpdateTimer();
            });
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

    private void StopBtn_Click(object? sender, RoutedEventArgs e)
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
        
        LogMessage("Settings have been reset to default values.");
        _hasUnsavedChanges = true; // Values have changed and are not saved yet
        UpdateDbActionButtonsState(); // Update DB buttons based on reset settings
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
            string? originalText = LogTextBox.Tag as string;
            
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
        bool canTestOrVerify = !string.IsNullOrWhiteSpace(DbHost.Text) &&
                               !string.IsNullOrWhiteSpace(DbPort.Text) &&
                               !string.IsNullOrWhiteSpace(DbUsername.Text) &&
                               !string.IsNullOrWhiteSpace(DbName.Text);

        TestConnectionBtn.IsEnabled = canTestOrVerify;
        VerifyTableBtn.IsEnabled = canTestOrVerify;
        // _logger?.LogDebug($"UpdateDbActionButtonsState: CanTestOrVerify = {canTestOrVerify}");
    }

    private void ConfigSetting_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _hasUnsavedChanges = true;
        if (this.IsLoaded) // Check if the window is fully loaded
        {
            UpdateDbActionButtonsState(); // Update DB buttons as text change might be one of theirs
        }
    }

    private void DbPassword_Changed(object sender, RoutedEventArgs e)
    {
        _hasUnsavedChanges = true;
        // No need to call UpdateDbActionButtonsState here as password doesn't gate button enablement
    }

    #endregion
}
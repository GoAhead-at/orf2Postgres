using Microsoft.Extensions.Logging;using System;using System.Collections.ObjectModel;using System.ComponentModel;using System.Runtime.CompilerServices;using System.ServiceProcess;using System.Threading.Tasks;using System.Windows.Input;using Log2Postgres.Core.Services;using Log2Postgres.UI.Services;

namespace Log2Postgres.UI.ViewModels
{
    /// <summary>
    /// ViewModel for the MainWindow, implementing MVVM pattern
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ILogger<MainWindowViewModel> _logger;
        private readonly IServiceManager _serviceManager;
        private readonly IAppConfigurationManager _configurationManager;
        private readonly PostgresService _postgresService;
        private readonly LogFileWatcher _logFileWatcher;
        private readonly IIpcService _ipcService;

        // UI State Properties
        private bool _isProcessing;
        private string _currentFile = "N/A";
        private long _currentPosition;
        private long _totalLinesProcessed;
        private string _serviceOperationalState = "Stopped";
        private bool _isDatabaseConnected;
        private string _lastErrorMessage = string.Empty;
        private ObservableCollection<string> _logEntries = new();

        // Configuration Properties
        private string _databaseHost = "localhost";
        private string _databasePort = "5432";
        private string _databaseUsername = string.Empty;
        private string _databasePassword = string.Empty;
        private string _databaseName = "orf";
        private string _databaseSchema = "orf";
        private string _databaseTable = "orf_logs";
        private int _connectionTimeout = 30;
        private string _logDirectory = string.Empty;
        private string _logFilePattern = "orfee-{Date:yyyy-MM-dd}.log";
        private int _pollingInterval = 5;

        // Service State Properties
        private bool _isServiceInstalled;
        private ServiceControllerStatus _serviceStatus = ServiceControllerStatus.Stopped;
        private bool _isIpcConnected;
        private bool _isLoadingConfiguration;
        private long _localProcessingTotalLines;

        public MainWindowViewModel(
            ILogger<MainWindowViewModel> logger,
            IServiceManager serviceManager,
            IAppConfigurationManager configurationManager,
            PostgresService postgresService,
            LogFileWatcher logFileWatcher,
            IIpcService ipcService)
        {
            _logger = logger;
            _serviceManager = serviceManager;
            _configurationManager = configurationManager;
            _postgresService = postgresService;
            _logFileWatcher = logFileWatcher;
            _ipcService = ipcService;

            InitializeCommands();
            SubscribeToEvents();
        }

        #region Properties

        public bool IsProcessing
        {
            get => _isProcessing;
            set => SetProperty(ref _isProcessing, value);
        }

        public string CurrentFile
        {
            get => _currentFile;
            set => SetProperty(ref _currentFile, value);
        }

        public long CurrentPosition
        {
            get => _currentPosition;
            set => SetProperty(ref _currentPosition, value);
        }

        public long TotalLinesProcessed
        {
            get => _totalLinesProcessed;
            set => SetProperty(ref _totalLinesProcessed, value);
        }

        public string ServiceOperationalState
        {
            get => _serviceOperationalState;
            set => SetProperty(ref _serviceOperationalState, value);
        }

        public bool IsDatabaseConnected
        {
            get => _isDatabaseConnected;
            set => SetProperty(ref _isDatabaseConnected, value);
        }

        public string LastErrorMessage
        {
            get => _lastErrorMessage;
            set => SetProperty(ref _lastErrorMessage, value);
        }

        public ObservableCollection<string> LogEntries => _logEntries;

        // Configuration Properties
        public string DatabaseHost
        {
            get => _databaseHost;
            set
            {
                if (SetProperty(ref _databaseHost, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string DatabasePort
        {
            get => _databasePort;
            set
            {
                if (SetProperty(ref _databasePort, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string DatabaseUsername
        {
            get => _databaseUsername;
            set
            {
                if (SetProperty(ref _databaseUsername, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string DatabasePassword
        {
            get => _databasePassword;
            set
            {
                if (SetProperty(ref _databasePassword, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string DatabaseName
        {
            get => _databaseName;
            set
            {
                if (SetProperty(ref _databaseName, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string DatabaseSchema
        {
            get => _databaseSchema;
            set
            {
                if (SetProperty(ref _databaseSchema, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string DatabaseTable
        {
            get => _databaseTable;
            set
            {
                if (SetProperty(ref _databaseTable, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public int ConnectionTimeout
        {
            get => _connectionTimeout;
            set
            {
                if (SetProperty(ref _connectionTimeout, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string LogDirectory
        {
            get => _logDirectory;
            set
            {
                if (SetProperty(ref _logDirectory, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public string LogFilePattern
        {
            get => _logFilePattern;
            set
            {
                if (SetProperty(ref _logFilePattern, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        public int PollingInterval
        {
            get => _pollingInterval;
            set
            {
                if (SetProperty(ref _pollingInterval, value) && !_isLoadingConfiguration)
                    _configurationManager.MarkAsChanged();
            }
        }

        // Service State Properties
        public bool IsServiceInstalled
        {
            get => _isServiceInstalled;
            set => SetProperty(ref _isServiceInstalled, value);
        }

        public ServiceControllerStatus ServiceStatus
        {
            get => _serviceStatus;
            set => SetProperty(ref _serviceStatus, value);
        }

        public bool IsIpcConnected
        {
            get => _isIpcConnected;
            set => SetProperty(ref _isIpcConnected, value);
        }

        public bool HasUnsavedChanges => _configurationManager.HasUnsavedChanges;

        #endregion

        #region Commands

        public ICommand TestConnectionCommand { get; private set; } = null!;
        public ICommand VerifyTableCommand { get; private set; } = null!;
        public ICommand SaveConfigurationCommand { get; private set; } = null!;
        public ICommand ResetConfigurationCommand { get; private set; } = null!;
        public ICommand InstallServiceCommand { get; private set; } = null!;
        public ICommand UninstallServiceCommand { get; private set; } = null!;
        public ICommand StartServiceCommand { get; private set; } = null!;
        public ICommand StopServiceCommand { get; private set; } = null!;
        public ICommand StartProcessingCommand { get; private set; } = null!;
        public ICommand StopProcessingCommand { get; private set; } = null!;
        public ICommand ClearLogsCommand { get; private set; } = null!;

        #endregion

        private void InitializeCommands()
        {
            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
            VerifyTableCommand = new AsyncRelayCommand(VerifyTableAsync);
            SaveConfigurationCommand = new AsyncRelayCommand(SaveConfigurationAsync);
            ResetConfigurationCommand = new AsyncRelayCommand(ResetConfigurationAsync);
            InstallServiceCommand = new AsyncRelayCommand(InstallServiceAsync, () => !IsServiceInstalled);
            UninstallServiceCommand = new AsyncRelayCommand(UninstallServiceAsync, () => IsServiceInstalled);
            StartServiceCommand = new AsyncRelayCommand(StartServiceAsync, CanStartService);
            StopServiceCommand = new AsyncRelayCommand(StopServiceAsync, CanStopService);
            StartProcessingCommand = new AsyncRelayCommand(StartProcessingAsync, CanStartLocalProcessing);
            StopProcessingCommand = new AsyncRelayCommand(StopProcessingAsync, CanStopLocalProcessing);
            ClearLogsCommand = new RelayCommand(ClearLogs);
        }

        private void SubscribeToEvents()
        {
            _serviceManager.ServiceStatusChanged += OnServiceStatusChanged;
            _configurationManager.ConfigurationChanged += OnConfigurationChanged;
            _configurationManager.UnsavedChangesChanged += OnUnsavedChangesChanged;
            
            if (_logFileWatcher != null)
            {
                _logFileWatcher.ProcessingStatusChanged += OnProcessingStatusChanged;
                _logFileWatcher.EntriesProcessed += OnEntriesProcessed;
                _logFileWatcher.ErrorOccurred += OnErrorOccurred;
            }

            if (_ipcService != null)
            {
                _ipcService.PipeConnected += OnIpcConnected;
                _ipcService.PipeDisconnected += OnIpcDisconnected;
                _ipcService.ServiceStatusReceived += OnServiceStatusReceived;
                _ipcService.LogEntriesReceived += OnLogEntriesReceived;
            }
        }

        public async Task InitializeAsync()
        {
            _logger.LogInformation("Initializing MainWindow ViewModel");
            
            await _configurationManager.LoadSettingsAsync();
            await _serviceManager.RefreshServiceStatusAsync();
            UpdateServiceOperationalState(); // Update status display after checking service
            await UpdateDatabaseConnectionStatusAsync();
            
            // Try to connect to IPC if service is running
            await TryConnectToIpcAsync();
        }

        private async Task TryConnectToIpcAsync()
        {
            if (_ipcService == null)
                return;
                
            // Only try to connect if service is installed and running
            if (IsServiceInstalled && ServiceStatus == ServiceControllerStatus.Running)
            {
                try
                {
                    _logger.LogInformation("Attempting to connect to service via IPC...");
                    await _ipcService.ConnectAsync();
                    _logger.LogInformation("Successfully connected to service via IPC");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to connect to service via IPC");
                    AddLogEntry("⚠ Could not connect to service for real-time updates");
                }
            }
            else
            {
                _logger.LogDebug("Service is not running, skipping IPC connection attempt");
            }
        }

        #region Command Implementations

        private async Task TestConnectionAsync()
        {
            try
            {
                _logger.LogInformation("Testing database connection");
                var isConnected = await _postgresService.TestConnectionAsync();
                IsDatabaseConnected = isConnected;
                
                if (isConnected)
                {
                    AddLogEntry("✓ Database connection successful");
                }
                else
                {
                    AddLogEntry("✗ Database connection failed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing database connection");
                AddLogEntry($"✗ Database connection error: {ex.Message}");
                IsDatabaseConnected = false;
            }
        }

        private async Task VerifyTableAsync()
        {
            try
            {
                _logger.LogInformation("Verifying database table");
                var tableExists = await _postgresService.TableExistsAsync();
                
                if (tableExists)
                {
                    var isValid = await _postgresService.ValidateTableStructureAsync();
                    if (isValid)
                    {
                        AddLogEntry("✓ Database table exists and structure is valid");
                    }
                    else
                    {
                        AddLogEntry("⚠ Database table exists but structure is invalid");
                    }
                }
                else
                {
                    AddLogEntry("✗ Database table does not exist");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying database table");
                AddLogEntry($"✗ Table verification error: {ex.Message}");
            }
        }

        private async Task SaveConfigurationAsync()
        {
            try
            {
                var dbSettings = new Core.Services.DatabaseSettings
                {
                    Host = DatabaseHost,
                    Port = DatabasePort,
                    Username = DatabaseUsername,
                    Password = DatabasePassword,
                    Database = DatabaseName,
                    Schema = DatabaseSchema,
                    Table = DatabaseTable,
                    ConnectionTimeout = ConnectionTimeout
                };
                
                var logSettings = new Core.Services.LogMonitorSettings
                {
                    BaseDirectory = LogDirectory,
                    LogFilePattern = LogFilePattern,
                    PollingIntervalSeconds = PollingInterval
                };

                await _configurationManager.SaveSettingsAsync(dbSettings, logSettings);
                AddLogEntry("✓ Configuration saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving configuration");
                AddLogEntry($"✗ Configuration save error: {ex.Message}");
            }
        }

        private async Task ResetConfigurationAsync()
        {
            try
            {
                await _configurationManager.ResetToDefaultsAsync();
                AddLogEntry("✓ Configuration reset to defaults");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting configuration");
                AddLogEntry($"✗ Configuration reset error: {ex.Message}");
            }
        }

        private async Task InstallServiceAsync()
        {
            try
            {
                AddLogEntry("⏳ Installing service...");
                await _serviceManager.InstallServiceAsync();
                
                // Refresh service status to update UI state
                await _serviceManager.RefreshServiceStatusAsync();
                
                // Explicitly refresh command states to update button visibility immediately
                CommandManager.InvalidateRequerySuggested();
                
                // Check if installation was successful
                if (IsServiceInstalled)
                {
                    AddLogEntry("✓ Service installed successfully");
                }
                else
                {
                    AddLogEntry("⚠ Service installation status unclear - please check manually");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error installing service");
                AddLogEntry($"✗ Service installation error: {ex.Message}");
            }
        }

        private async Task UninstallServiceAsync()
        {
            try
            {
                AddLogEntry("⏳ Uninstalling service...");
                await _serviceManager.UninstallServiceAsync();
                
                // Refresh service status to update UI state
                await _serviceManager.RefreshServiceStatusAsync();
                
                // Explicitly refresh command states to update button visibility immediately
                CommandManager.InvalidateRequerySuggested();
                
                // Check if uninstallation was successful
                if (!IsServiceInstalled)
                {
                    AddLogEntry("✓ Service uninstalled successfully");
                }
                else
                {
                    AddLogEntry("⚠ Service uninstallation status unclear - please check manually");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uninstalling service");
                AddLogEntry($"✗ Service uninstallation error: {ex.Message}");
            }
        }

        private async Task StartServiceAsync()
        {
            try
            {
                AddLogEntry("⏳ Starting service...");
                await _serviceManager.StartServiceAsync();
                
                // Refresh service status to update UI state
                await _serviceManager.RefreshServiceStatusAsync();
                
                // Check if service started successfully
                if (ServiceStatus == ServiceControllerStatus.Running || ServiceStatus == ServiceControllerStatus.StartPending)
                {
                    AddLogEntry("✓ Service started successfully");
                }
                else
                {
                    AddLogEntry("⚠ Service start status unclear - please check manually");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting service");
                AddLogEntry($"✗ Start service error: {ex.Message}");
            }
        }

        private async Task StopServiceAsync()
        {
            try
            {
                AddLogEntry("⏳ Stopping service...");
                await _serviceManager.StopServiceAsync();
                
                // Refresh service status to update UI state
                await _serviceManager.RefreshServiceStatusAsync();
                
                // Check if service stopped successfully
                if (ServiceStatus == ServiceControllerStatus.Stopped || ServiceStatus == ServiceControllerStatus.StopPending)
                {
                    AddLogEntry("✓ Service stopped successfully");
                }
                else
                {
                    AddLogEntry("⚠ Service stop status unclear - please check manually");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping service");
                AddLogEntry($"✗ Stop service error: {ex.Message}");
            }
        }

        private async Task StartProcessingAsync()
        {
            try
            {
                // Reset local processing counter when starting
                _localProcessingTotalLines = 0;
                TotalLinesProcessed = 0;
                
                // Only start local processing - service has its own Start/Stop commands
                await _logFileWatcher.UIManagedStartProcessingAsync();
                IsProcessing = true;
                AddLogEntry("✓ Local processing started");
                
                // Trigger command CanExecute refresh to update button states
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting processing");
                AddLogEntry($"✗ Start processing error: {ex.Message}");
            }
        }

        private async Task StopProcessingAsync()
        {
            try
            {
                // Only stop local processing - service has its own Start/Stop commands
                _logFileWatcher.UIManagedStopProcessing();
                IsProcessing = false;
                AddLogEntry("✓ Local processing stopped");
                
                // Trigger command CanExecute refresh to update button states
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping processing");
                AddLogEntry($"✗ Stop processing error: {ex.Message}");
            }
        }

        private void ClearLogs()
        {
            // Ensure UI updates happen on the UI thread
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                LogEntries.Clear();
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => LogEntries.Clear());
            }
            _logger.LogInformation("UI logs cleared");
        }

        #region CanExecute Methods

        private bool CanStartService()
        {
            return IsServiceInstalled && 
                   (ServiceStatus == ServiceControllerStatus.Stopped || 
                    ServiceStatus == ServiceControllerStatus.StopPending);
        }

        private bool CanStopService()
        {
            return IsServiceInstalled && 
                   (ServiceStatus == ServiceControllerStatus.Running || 
                    ServiceStatus == ServiceControllerStatus.StartPending);
        }

        private bool CanStartLocalProcessing()
        {
            // Can start local processing ONLY if:
            // 1. Not currently processing locally
            // 2. Service is NOT installed (local mode only)
            // When service is installed, all processing should go through service commands
            return !IsProcessing && !IsServiceInstalled;
        }

        private bool CanStopLocalProcessing()
        {
            // Can stop local processing if currently processing locally
            return IsProcessing;
        }

        #endregion

        #endregion

        #region Event Handlers

        private void UpdateServiceOperationalState()
        {
            if (!IsServiceInstalled)
            {
                ServiceOperationalState = "Local Mode";
            }
            else if (IsIpcConnected && ServiceStatus == ServiceControllerStatus.Running)
            {
                // IPC will provide detailed status - keep current value
                // ServiceOperationalState is updated via OnServiceStatusReceived
            }
            else
            {
                ServiceOperationalState = ServiceStatus switch
                {
                    ServiceControllerStatus.Running => IsIpcConnected ? "Service Running" : "Service Running (Connecting...)",
                    ServiceControllerStatus.Stopped => "Service Stopped",
                    ServiceControllerStatus.StartPending => "Service Starting...",
                    ServiceControllerStatus.StopPending => "Service Stopping...",
                    ServiceControllerStatus.Paused => "Service Paused",
                    ServiceControllerStatus.PausePending => "Service Pausing...",
                    ServiceControllerStatus.ContinuePending => "Service Resuming...",
                    _ => $"Service Status: {ServiceStatus}"
                };
            }
        }

        private void OnServiceStatusChanged(object? sender, ServiceStatusChangedEventArgs e)
        {
            // Ensure UI property updates happen on the UI thread
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                UpdateServiceStatusProperties(e);
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UpdateServiceStatusProperties(e));
            }
        }
        
        private void UpdateServiceStatusProperties(ServiceStatusChangedEventArgs e)
        {
            IsServiceInstalled = e.IsInstalled;
            ServiceStatus = e.Status;
            
            // Update ServiceOperationalState based on service installation and status
            UpdateServiceOperationalState();
            
            _logger.LogDebug("Service status changed: Installed={IsInstalled}, Status={Status}", 
                e.IsInstalled, e.Status);
            
            // Try to connect to IPC when service starts running
            if (e.IsInstalled && e.Status == ServiceControllerStatus.Running)
            {
                _ = Task.Run(async () => await TryConnectToIpcAsync());
            }
            // Disconnect IPC when service stops
            else if (!e.IsInstalled || e.Status == ServiceControllerStatus.Stopped)
            {
                _ = Task.Run(async () => 
                {
                    if (_ipcService != null && _ipcService.IsConnected)
                    {
                        await _ipcService.DisconnectAsync();
                    }
                });
            }
            
            // Trigger command CanExecute refresh
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnConfigurationChanged(object? sender, ConfigurationChangedEventArgs e)
        {
            // Set loading flag to prevent marking as changed during configuration loading
            _isLoadingConfiguration = true;
            
            try
            {
                // Update UI properties from configuration
                DatabaseHost = e.DatabaseSettings.Host;
                DatabasePort = e.DatabaseSettings.Port;
                DatabaseUsername = e.DatabaseSettings.Username;
                DatabasePassword = e.DatabaseSettings.Password;
                DatabaseName = e.DatabaseSettings.Database;
                DatabaseSchema = e.DatabaseSettings.Schema;
                DatabaseTable = e.DatabaseSettings.Table;
                ConnectionTimeout = e.DatabaseSettings.ConnectionTimeout;
                
                LogDirectory = e.LogSettings.BaseDirectory;
                LogFilePattern = e.LogSettings.LogFilePattern;
                PollingInterval = e.LogSettings.PollingIntervalSeconds;
                
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
            finally
            {
                // Always reset the loading flag
                _isLoadingConfiguration = false;
            }
        }

        private void OnUnsavedChangesChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }

        private void OnProcessingStatusChanged(string currentFile, int count, long position)
        {
            CurrentFile = System.IO.Path.GetFileName(currentFile);
            CurrentPosition = position;
            
            // For local processing, accumulate the lines processed
            // The 'count' parameter represents new lines processed in this batch
            _localProcessingTotalLines += count;
            TotalLinesProcessed = _localProcessingTotalLines;
        }

        private void OnEntriesProcessed(System.Collections.Generic.IEnumerable<Core.Models.OrfLogEntry> entries)
        {
            foreach (var entry in entries)
            {
                AddLogEntry($"Processed: {entry.EventDateTime:HH:mm:ss} - {entry.EventAction} - {entry.Sender}");
            }
        }

        private void OnErrorOccurred(string component, string message)
        {
            LastErrorMessage = $"[{component}] {message}";
            AddLogEntry($"✗ Error in {component}: {message}");
        }

        private Task OnIpcConnected()
        {
            // Ensure UI property updates happen on the UI thread
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                IsIpcConnected = true;
                UpdateServiceOperationalState();
                AddLogEntry("✓ IPC connection established");
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsIpcConnected = true;
                    UpdateServiceOperationalState();
                    AddLogEntry("✓ IPC connection established");
                });
            }
            return Task.CompletedTask;
        }

        private Task OnIpcDisconnected()
        {
            // Ensure UI property updates happen on the UI thread
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                IsIpcConnected = false;
                UpdateServiceOperationalState();
                AddLogEntry("⚠ IPC connection lost");
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsIpcConnected = false;
                    UpdateServiceOperationalState();
                    AddLogEntry("⚠ IPC connection lost");
                });
            }
            return Task.CompletedTask;
        }

        private Task OnServiceStatusReceived(PipeServiceStatus status)
        {
            // Ensure UI property updates happen on the UI thread
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                UpdateServiceStatusFromIpc(status);
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UpdateServiceStatusFromIpc(status));
            }
            return Task.CompletedTask;
        }

        private void UpdateServiceStatusFromIpc(PipeServiceStatus status)
        {
            ServiceOperationalState = status.ServiceOperationalState;
            CurrentFile = System.IO.Path.GetFileName(status.CurrentFile);
            CurrentPosition = status.CurrentPosition;
            
            // Only update TotalLinesProcessed from IPC if we're not doing local processing
            // Local processing maintains its own state and accumulated count
            // NOTE: IsProcessing should NEVER be set from IPC - it only represents LOCAL processing state
            if (!IsProcessing)
            {
                TotalLinesProcessed = status.TotalLinesProcessedSinceStart;
            }
        }

        private Task OnLogEntriesReceived(System.Collections.Generic.List<string> logEntries)
        {
            // AddLogEntry is already thread-safe, but let's be explicit about threading here
            foreach (var entry in logEntries)
            {
                AddLogEntry(entry); // AddLogEntry handles thread safety internally
            }
            return Task.CompletedTask;
        }

        #endregion

        private void AddLogEntry(string message)
        {
            const int maxLogEntries = 500;
            
            var timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            
            // Ensure UI updates happen on the UI thread
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                // We're on the UI thread, update directly
                LogEntries.Add(timestampedMessage);
                
                // Keep log entries within limit
                while (LogEntries.Count > maxLogEntries)
                {
                    LogEntries.RemoveAt(0);
                }
            }
            else
            {
                // We're on a background thread, dispatch to UI thread
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    LogEntries.Add(timestampedMessage);
                    
                    // Keep log entries within limit
                    while (LogEntries.Count > maxLogEntries)
                    {
                        LogEntries.RemoveAt(0);
                    }
                });
            }
        }

        private async Task UpdateDatabaseConnectionStatusAsync()
        {
            try
            {
                IsDatabaseConnected = await _postgresService.TestConnectionAsync();
            }
            catch
            {
                IsDatabaseConnected = false;
            }
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _serviceManager.ServiceStatusChanged -= OnServiceStatusChanged;
            _configurationManager.ConfigurationChanged -= OnConfigurationChanged;
            _configurationManager.UnsavedChangesChanged -= OnUnsavedChangesChanged;
            
            if (_logFileWatcher != null)
            {
                _logFileWatcher.ProcessingStatusChanged -= OnProcessingStatusChanged;
                _logFileWatcher.EntriesProcessed -= OnEntriesProcessed;
                _logFileWatcher.ErrorOccurred -= OnErrorOccurred;
            }

            if (_ipcService != null)
            {
                _ipcService.PipeConnected -= OnIpcConnected;
                _ipcService.PipeDisconnected -= OnIpcDisconnected;
                _ipcService.ServiceStatusReceived -= OnServiceStatusReceived;
                _ipcService.LogEntriesReceived -= OnLogEntriesReceived;
            }
        }

        #endregion
    }

    #region Command Helpers

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                try
                {
                    _isExecuting = true;
                    CommandManager.InvalidateRequerySuggested(); // Refresh UI when execution starts
                    await _execute();
                }
                finally
                {
                    _isExecuting = false;
                    CommandManager.InvalidateRequerySuggested(); // Refresh UI when execution completes
                }
            }
        }
    }

    #endregion
} 
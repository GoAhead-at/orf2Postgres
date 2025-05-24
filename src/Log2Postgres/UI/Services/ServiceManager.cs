using Microsoft.Extensions.Logging;
using System;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace Log2Postgres.UI.Services
{
    /// <summary>
    /// Manages Windows Service operations and status monitoring
    /// </summary>
    public interface IServiceManager
    {
        bool IsServiceInstalled { get; }
        ServiceControllerStatus ServiceStatus { get; }
        
        Task RefreshServiceStatusAsync();
        Task InstallServiceAsync();
        Task UninstallServiceAsync();
        Task StartServiceAsync();
        Task StopServiceAsync();
        
        event EventHandler<ServiceStatusChangedEventArgs> ServiceStatusChanged;
    }

    public class ServiceManager : IServiceManager
    {
        private readonly ILogger<ServiceManager> _logger;
        private bool _isServiceInstalled;
        private ServiceControllerStatus _serviceStatus = ServiceControllerStatus.Stopped;

        public bool IsServiceInstalled => _isServiceInstalled;
        public ServiceControllerStatus ServiceStatus => _serviceStatus;

        public event EventHandler<ServiceStatusChangedEventArgs>? ServiceStatusChanged;

        public ServiceManager(ILogger<ServiceManager> logger)
        {
            _logger = logger;
        }

        public async Task RefreshServiceStatusAsync()
        {
            await Task.Run(() =>
            {
                var previousInstalled = _isServiceInstalled;
                var previousStatus = _serviceStatus;

                try
                {
                    using var sc = new ServiceController(App.WindowsServiceName);
                    _isServiceInstalled = true;
                    _serviceStatus = sc.Status;
                }
                catch (InvalidOperationException)
                {
                    _isServiceInstalled = false;
                    _serviceStatus = ServiceControllerStatus.Stopped;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting service controller status");
                    _isServiceInstalled = false;
                    _serviceStatus = ServiceControllerStatus.Stopped;
                }

                // Notify if status changed
                if (previousInstalled != _isServiceInstalled || previousStatus != _serviceStatus)
                {
                    ServiceStatusChanged?.Invoke(this, new ServiceStatusChangedEventArgs
                    {
                        IsInstalled = _isServiceInstalled,
                        Status = _serviceStatus,
                        PreviousStatus = previousStatus
                    });
                }
            });
        }

        public async Task InstallServiceAsync()
        {
            await Task.Run(() =>
            {
                _logger.LogInformation("Installing Windows Service");
                App.InstallService();
            });
            
            // Add delay and retry mechanism for service installation detection
            await RefreshServiceStatusWithRetryAsync(retryCount: 5, delayMs: 1000);
        }

        public async Task UninstallServiceAsync()
        {
            await Task.Run(() =>
            {
                _logger.LogInformation("Uninstalling Windows Service");
                App.UninstallService();
            });
            
            // Add delay and retry mechanism for service uninstallation detection
            await RefreshServiceStatusWithRetryAsync(retryCount: 5, delayMs: 1000);
        }

        private async Task RefreshServiceStatusWithRetryAsync(int retryCount = 3, int delayMs = 500)
        {
            _logger.LogInformation("Starting service status refresh with retry, attempts: {RetryCount}, delay: {DelayMs}ms", retryCount, delayMs);
            
            for (int i = 0; i < retryCount; i++)
            {
                if (i > 0)
                {
                    _logger.LogInformation("Retrying service status refresh, attempt {Attempt}/{MaxAttempts}", i + 1, retryCount);
                    await Task.Delay(delayMs);
                }
                
                var previousInstalled = _isServiceInstalled;
                var previousStatus = _serviceStatus;
                
                _logger.LogDebug("Attempt {Attempt}: Before refresh - Installed: {PreviousInstalled}, Status: {PreviousStatus}", 
                    i + 1, previousInstalled, previousStatus);
                
                await RefreshServiceStatusAsync();
                
                _logger.LogInformation("Attempt {Attempt}: After refresh - Installed: {CurrentInstalled}, Status: {CurrentStatus}", 
                    i + 1, _isServiceInstalled, _serviceStatus);
                
                // If status changed or this is the last attempt, break
                if (_isServiceInstalled != previousInstalled || i == retryCount - 1)
                {
                    if (_isServiceInstalled != previousInstalled)
                    {
                        _logger.LogInformation("✅ Service status change detected after {Attempts} attempts: {PreviousInstalled} → {CurrentInstalled}", 
                            i + 1, previousInstalled, _isServiceInstalled);
                    }
                    else if (i == retryCount - 1)
                    {
                        _logger.LogWarning("⚠️ Service status did not change after {MaxAttempts} attempts. Final state: Installed={CurrentInstalled}, Status={CurrentStatus}", 
                            retryCount, _isServiceInstalled, _serviceStatus);
                    }
                    break;
                }
                
                _logger.LogDebug("No status change detected on attempt {Attempt}, will retry...", i + 1);
            }
            
            _logger.LogInformation("Service status refresh with retry completed. Final state: Installed={CurrentInstalled}, Status={CurrentStatus}", 
                _isServiceInstalled, _serviceStatus);
        }

        public async Task StartServiceAsync()
        {
            await Task.Run(() =>
            {
                _logger.LogInformation("Starting Windows Service");
                App.StartWindowsService(App.WindowsServiceName);
            });
            
            // Wait a moment for service to start
            await Task.Delay(1000);
            await RefreshServiceStatusAsync();
        }

        public async Task StopServiceAsync()
        {
            await Task.Run(() =>
            {
                _logger.LogInformation("Stopping Windows Service");
                App.StopWindowsService(App.WindowsServiceName);
            });
            
            // Wait a moment for service to stop
            await Task.Delay(1000);
            await RefreshServiceStatusAsync();
        }
    }

    public class ServiceStatusChangedEventArgs : EventArgs
    {
        public bool IsInstalled { get; set; }
        public ServiceControllerStatus Status { get; set; }
        public ServiceControllerStatus PreviousStatus { get; set; }
    }
} 
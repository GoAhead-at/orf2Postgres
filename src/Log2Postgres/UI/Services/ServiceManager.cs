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
            await RefreshServiceStatusAsync();
        }

        public async Task UninstallServiceAsync()
        {
            await Task.Run(() =>
            {
                _logger.LogInformation("Uninstalling Windows Service");
                App.UninstallService();
            });
            await RefreshServiceStatusAsync();
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
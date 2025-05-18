using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;
using System.Windows;
using Log2Postgres.Core.Services;
using System.Linq;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Log2Postgres
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private readonly IHost _host;
        private static Mutex _singleInstanceMutex;
        private const string MutexName = "Log2PostgresAppSingleInstance";
        
        // Win32 API imports for finding and activating existing window
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        private const int SW_RESTORE = 9;

        public App()
        {
            try
            {
                // Check for startup error file from previous runs
                string errorFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.txt");
                if (File.Exists(errorFilePath))
                {
                    try
                    {
                        string errorContent = File.ReadAllText(errorFilePath);
                        System.Windows.MessageBox.Show(
                            $"A previous startup error was detected:\n\n{errorContent.Substring(0, Math.Min(500, errorContent.Length))}...\n\nThis file will be renamed to startup_error.old.txt",
                            "Previous Startup Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                            
                        // Rename the file so it doesn't show up next time
                        if (File.Exists(errorFilePath + ".old"))
                            File.Delete(errorFilePath + ".old");
                            
                        File.Move(errorFilePath, errorFilePath + ".old");
                    }
                    catch
                    {
                        // Ignore errors in displaying previous error file
                    }
                }

                // Check if another instance is already running
                bool createdNew;
                _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
                
                if (!createdNew)
                {
                    // Another instance is already running - find it and activate it
                    ActivateExistingInstance();
                    
                    // Shut down this instance
                    Shutdown();
                    return;
                }
                
                // We're the first instance, so create the host
                try
                {
                    _host = CreateHostBuilder().Build();
                }
                catch (Exception ex)
                {
                    // Log the error and continue without the host
                    // If we can't create the host, we'll run with a simplified window
                    
                    // Show a minimal window instead of crashing
                    Dispatcher.InvokeAsync(() => {
                        try
                        {
                            // Create a simplified window for diagnostics
                            var mainWindow = new Window
                            {
                                Title = "Log2Postgres - Error Window",
                                Width = 800,
                                Height = 600
                            };
                            
                            // Add a label to show the error
                            var textBlock = new System.Windows.Controls.TextBlock
                            {
                                Text = $"Error starting application:\n\n{ex.Message}",
                                Margin = new Thickness(20),
                                TextWrapping = TextWrapping.Wrap
                            };
                            
                            mainWindow.Content = textBlock;
                            
                            // Show the window
                            mainWindow.Show();
                        }
                        catch
                        {
                            // If we can't even show a window, just log and exit
                            Shutdown();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // Log the error
                 System.Windows.MessageBox.Show(
                    $"Error in App constructor: {ex.Message}", 
                    "App Constructor Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
                    
                // Exit the application
                Shutdown();
            }
        }
        
        private void ActivateExistingInstance()
        {
            try
            {
                // Find the existing process
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);
                
                foreach (var process in processes)
                {
                    // Skip the current process
                    if (process.Id == currentProcess.Id)
                        continue;
                    
                    // Ensure it's a process with a main window
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        // Activate the window
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        
                        // Log the action
                        Console.WriteLine($"Found existing instance (PID: {process.Id}), activating its window.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log but don't crash - worst case, we just start another instance
                Console.WriteLine($"Error activating existing instance: {ex.Message}");
                System.Windows.MessageBox.Show($"Another instance is already running, but couldn't be activated: {ex.Message}",
                    "Application Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        
        public IConfiguration GetConfiguration()
        {
            return _host.Services.GetRequiredService<IConfiguration>();
        }

        public T GetService<T>() where T : class
        {
            return _host.Services.GetRequiredService<T>();
        }

        public static IHostBuilder CreateHostBuilder(string[]? args = null)
        {
            return Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "Log2Postgres";
                })
                .UseSerilog((hostingContext, loggerConfiguration) =>
                {
                    loggerConfiguration
                        .ReadFrom.Configuration(hostingContext.Configuration)
                        .Enrich.FromLogContext();
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Register the password encryption service first
                    services.AddSingleton<PasswordEncryption>();
                    
                    // Get database settings from configuration
                    var dbSettings = hostContext.Configuration.GetSection("DatabaseSettings").Get<DatabaseSettings>();
                    
                    // Create the logger factory and get a logger for password encryption
                    var loggerFactory = LoggerFactory.Create(builder => 
                    { 
                        builder.AddConfiguration(hostContext.Configuration.GetSection("Logging"))
                               .AddConsole()
                               .AddDebug(); 
                    });
                    var encryptionLogger = loggerFactory.CreateLogger<PasswordEncryption>();
                    
                    // Create password encryption service and decrypt password if needed
                    var passwordEncryption = new PasswordEncryption(encryptionLogger);
                    
                    // Decrypt the password if it's encrypted
                    if (!string.IsNullOrEmpty(dbSettings.Password) && passwordEncryption.IsEncrypted(dbSettings.Password))
                    {
                        dbSettings.Password = passwordEncryption.DecryptPassword(dbSettings.Password);
                    }
                    
                    // Register the database settings with the decrypted password
                    services.Configure<DatabaseSettings>(options => 
                    {
                        options.Host = dbSettings.Host;
                        options.Port = dbSettings.Port;
                        options.Username = dbSettings.Username;
                        options.Password = dbSettings.Password; // This is now the decrypted password
                        options.Database = dbSettings.Database;
                        options.Schema = dbSettings.Schema;
                        options.Table = dbSettings.Table;
                        options.ConnectionTimeout = dbSettings.ConnectionTimeout;
                    });
                    
                    // Get log monitor settings from configuration
                    var logMonitorSettings = hostContext.Configuration.GetSection("LogMonitorSettings").Get<LogMonitorSettings>();
                    
                    // Register configuration sections for log monitor settings with default values if needed
                    services.Configure<LogMonitorSettings>(options =>
                    {
                        // Initialize with default values
                        options.LogFilePattern = "orfee-{Date:yyyy-MM-dd}.log";
                        options.PollingIntervalSeconds = 5;
                        
                        // Use values from config if available
                        if (logMonitorSettings != null)
                        {
                            if (!string.IsNullOrEmpty(logMonitorSettings.BaseDirectory))
                                options.BaseDirectory = logMonitorSettings.BaseDirectory;
                                
                            if (!string.IsNullOrEmpty(logMonitorSettings.LogFilePattern))
                                options.LogFilePattern = logMonitorSettings.LogFilePattern;
                                
                            if (logMonitorSettings.PollingIntervalSeconds > 0)
                                options.PollingIntervalSeconds = logMonitorSettings.PollingIntervalSeconds;
                        }
                    });

                    // Register core services
                    services.AddSingleton<OrfLogParser>();
                    services.AddSingleton<PositionManager>();
                    services.AddSingleton<PostgresService>();
                    
                    // Register the log file watcher as a hosted service
                    services.AddHostedService<LogFileWatcher>();
                    
                    // Make the LogFileWatcher also available for DI
                    services.AddSingleton(provider => {
                        var service = provider.GetServices<IHostedService>()
                            .OfType<LogFileWatcher>()
                            .FirstOrDefault();
                        return service!; // Non-null assertion as we know it's registered
                    });
                });
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            try
            {
                // If the host is null, we can't proceed normally
                if (_host == null)
                {
                    // We've already shown a simplified window in the constructor
                    return;
                }
                
                try
                {
                    await _host.StartAsync();
                }
                catch (Exception hostEx)
                {
                     System.Windows.MessageBox.Show(
                        $"Error starting host: {hostEx.Message}", 
                        "Host Start Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                    return;
                }
                
                // Explicitly create and show the main window
                try
                {
                    var mainWindow = new MainWindow();
                    mainWindow.Show();
                }
                catch (Exception winEx)
                {
                    System.Windows.MessageBox.Show(
                        $"Error creating or showing MainWindow: {winEx.Message}", 
                        "MainWindow Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                    return;
                }
                
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                // Log the error and show a message box to alert the user
                System.Windows.MessageBox.Show(
                    $"Application failed to start: {ex.Message}", 
                    "Startup Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
                    
                // Shutdown the application
                Current.Shutdown();
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            // Release the mutex
            if (_singleInstanceMutex != null && _host != null)
            {
                // Only clean up if we successfully acquired the mutex and created the host
                await _host.StopAsync();
                _host.Dispose();
                
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
            
            base.OnExit(e);
        }
    }
}


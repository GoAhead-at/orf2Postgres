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
using Log2Postgres.Core.Models;
using System.Security.Principal; // For WindowsIdentity

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
            // If not installing/uninstalling (checked by Program.Main), proceed with normal application startup
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
            // Determine if running as a service.
            var effectiveArgs = args ?? Environment.GetCommandLineArgs();
            string commandLineForLog = string.Join(" ", effectiveArgs);

            // A Windows service runs non-interactively. The --windows-service argument is a strong indicator too.
            // Environment.UserInteractive is true if the process is running in an interactive environment (e.g., user double-clicked).
            // It's false when run by SCM (services.exe).
            bool isLaunchedBySCM = !Environment.UserInteractive;
            bool hasServiceArgument = effectiveArgs.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
            
            // Consider it a service if either SCM launched it OR the specific argument is present.
            // This provides flexibility for testing with the argument even in an interactive session.
            bool runAsService = isLaunchedBySCM || hasServiceArgument;

            // CRITICAL DIAGNOSTIC LOGGING:
            // Use a temporary logger here if the main one isn't set up yet, or write to a temp file.
            // For simplicity, let's assume a simple Console.WriteLine will get picked up by service logs if redirected
            // or at least be visible if testing service startup from command line.
            Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: effectiveArgs = '{commandLineForLog}'");
            Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: Environment.UserInteractive = {Environment.UserInteractive}");
            Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: isLaunchedBySCM (derived from !UserInteractive) = {isLaunchedBySCM}");
            Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: hasServiceArgument (--windows-service) = {hasServiceArgument}");
            Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: runAsService (isLaunchedBySCM || hasServiceArgument) = {runAsService}");

            string contentRoot = AppContext.BaseDirectory;
            Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: Setting ContentRootPath to: {contentRoot}");

            var hostBuilder = Host.CreateDefaultBuilder(effectiveArgs)
                .UseContentRoot(contentRoot);

            if (runAsService)
            {
                hostBuilder.UseWindowsService(options =>
                {
                    options.ServiceName = "Log2Postgres";
                });

                // Minimal Programmatic Serilog for service mode WITH SELFLog for diagnostics
                var selfLogFilePath = Path.Combine(contentRoot, "logs", "serilog_selflog.txt");
                Serilog.Debugging.SelfLog.Enable(msg => File.AppendAllText(selfLogFilePath, msg + Environment.NewLine));
                Console.WriteLine($"[App.CreateHostBuilder] DIAGNOSTIC: Serilog SelfLog enabled to: {selfLogFilePath}");

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.File(Path.Combine(contentRoot, "logs", "service_debug_.txt"), // Different name to avoid confusion
                                  rollingInterval: RollingInterval.Day,
                                  shared: true,
                                  flushToDiskInterval: TimeSpan.FromSeconds(1))
                    .Enrich.FromLogContext()
                    .CreateLogger();
                
                Console.WriteLine("[App.CreateHostBuilder] DIAGNOSTIC: Minimal programmatic Serilog re-configured for service mode.");
                hostBuilder.UseSerilog(); // Use the programmatically configured logger

                hostBuilder.ConfigureLogging(logging =>
                {
                    logging.AddEventLog();
                });
            }
            else
            {
                // UI Mode: Continue using Serilog from appsettings.json
                hostBuilder.UseSerilog((hostingContext, loggerConfiguration) =>
                {
                    loggerConfiguration
                        .ReadFrom.Configuration(hostingContext.Configuration)
                        .Enrich.FromLogContext();
                });
            }

            hostBuilder.ConfigureServices((hostContext, services) =>
            {
                // Configure WindowsServiceSettings based on run mode
                services.Configure<WindowsServiceSettings>(options => options.RunAsService = runAsService);
                
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
                    options.BaseDirectory = logMonitorSettings.BaseDirectory ?? string.Empty; 
                    options.LogFilePattern = logMonitorSettings.LogFilePattern ?? "orfee-{Date:yyyy-MM-dd}.log";
                    options.PollingIntervalSeconds = logMonitorSettings.PollingIntervalSeconds > 0 ? logMonitorSettings.PollingIntervalSeconds : 5;
                });
                
                // Register core processing services as singletons
                services.AddSingleton<OrfLogParser>();
                services.AddSingleton<PositionManager>();
                services.AddSingleton<PostgresService>(); // Ensure PostgresService and its dependencies (like IOptions<DatabaseSettings>) are correctly set up

                // Conditionally register LogFileWatcher
                if (runAsService)
                {
                    // When running as a Windows service, LogFileWatcher is the main worker.
                    // It will auto-start its processing loop via its ExecuteAsync.
                    services.AddHostedService<LogFileWatcher>();
                    
                    // Log that we are in service registration mode
                    var logger = services.BuildServiceProvider().GetRequiredService<ILogger<App>>();
                    logger.LogInformation("Application configured to run as a Windows Service. LogFileWatcher registered as HostedService.");
                }
                else
                {
                    // When running in UI mode, LogFileWatcher is available as a singleton.
                    // The UI will call UIManagedStartProcessingAsync / UIManagedStopProcessing on it.
                    services.AddSingleton<LogFileWatcher>();
                    
                    // Log that we are in UI/desktop registration mode
                    var logger = services.BuildServiceProvider().GetRequiredService<ILogger<App>>();
                    logger.LogInformation("Application configured to run in UI/Desktop mode. LogFileWatcher registered as Singleton.");
                }

                // Register MainWindow for WPF
                services.AddSingleton<MainWindow>();
            });

            return hostBuilder;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            if (_host == null) 
            {
                 // Host creation failed, App constructor showed an error window, or shut down.
                 // Ensure we don't proceed with host-dependent logic.
                 base.OnStartup(e);
                 if (Current == null || Current.MainWindow == null) 
                 {
                    // If even the error window couldn't show, or we are shutting down.
                    Shutdown();
                 }
                 return;
            }

            try
            {
                await _host.StartAsync();

                var mainWindow = GetService<MainWindow>();
                Current.MainWindow = mainWindow;
                mainWindow.Show();
                
                 // Log application startup mode
                var logger = GetService<ILogger<App>>();
                var serviceSettings = GetService<IOptions<WindowsServiceSettings>>();
                if (serviceSettings.Value.RunAsService)
                {
                    logger.LogInformation("Log2Postgres application starting in Service Mode.");
                }
                else
                {
                    logger.LogInformation("Log2Postgres application starting in UI/Desktop Mode.");
                }
            }
            catch (Exception ex)
            {
                 // Log the error to a file if possible, as logging service might not be up
                try
                {
                    string errorFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.txt");
                    File.WriteAllText(errorFilePath, $"Error during OnStartup: {ex.ToString()}");

                    System.Windows.MessageBox.Show(
                        $"Critical error during application startup: {ex.Message}\n\nDetails have been logged to startup_error.txt", 
                        "Startup Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                }
                catch
                {
                    // Fallback if we can't even write the error file
                    System.Windows.MessageBox.Show(
                        $"Catastrophic error during application startup: {ex.Message}", 
                        "Fatal Startup Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                }
                
                // Ensure the application shuts down if startup fails critically
                if (Current != null)
                {
                    Current.Shutdown(-1); // Exit with an error code
                }
                else
                {
                    Environment.Exit(-1); // Force exit if Current is null
                }
                return; // Stop further execution in this method
            }

            base.OnStartup(e);
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

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static void InstallService()
        {
            string serviceName = "Log2Postgres";
            string displayName = "Log2Postgres ORF Log Watcher";
            // Get the path to the current executable
            string? exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // This case might happen if it's a .dll being run via dotnet.exe.
                // For a published .exe, exePath should be correct.
                // If it's a framework-dependent deployment run via `dotnet YourApp.dll`,
                // the path needs to be `dotnet "path\to\YourApp.dll" --windows-service`
                // For self-contained, exePath is fine.
                // Assuming self-contained .exe for now.
                // A more robust solution handles both by checking Process.MainModule.FileName or Assembly.EntryPoint
                var mainModule = Process.GetCurrentProcess().MainModule;
                if (mainModule != null)
                {
                    exePath = mainModule.FileName;
                    // If running via `dotnet.exe`, mainModule.FileName is dotnet.exe.
                    // We need the path to our DLL in that case.
                    if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // This is a framework-dependent deployment. We need to find the DLL path.
                        // Assembly.GetEntryAssembly().Location should give the DLL.
                        string? entryAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                        if (!string.IsNullOrEmpty(entryAssemblyLocation) && entryAssemblyLocation.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)){
                             exePath = $"\"{Environment.ProcessPath}\" \"{entryAssemblyLocation}\""; // Path to dotnet.exe and path to DLL
                        }
                        else {
                            Console.WriteLine("Error: Could not determine application DLL path for service installation.");
                            System.Windows.MessageBox.Show("Error: Could not determine application DLL path for service installation.", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                }
                else
                {
                     Console.WriteLine("Error: Could not determine executable path for service installation.");
                     System.Windows.MessageBox.Show("Error: Could not determine executable path for service installation.", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                     return;
                }
            }

            string binPath = $"\"{exePath}\" --windows-service"; // Crucial: --windows-service arg for SCM
            // For LocalSystem: obj= LocalSystem
            // For NetworkService: obj= "NT AUTHORITY\NetworkService"
            // For LocalService: obj= "NT AUTHORITY\LocalService"
            string command = $"create {serviceName} binPath= \"{binPath}\" DisplayName= \"{displayName}\" start= auto obj= \"NT AUTHORITY\\NetworkService\"";

            Console.WriteLine($"Attempting to install service with command: sc.exe {command}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service installed successfully.");
                        System.Windows.MessageBox.Show("Service installed successfully.", "Service Installation", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Console.WriteLine($"Service installation failed. Exit code: {process.ExitCode}");
                        Console.WriteLine("Output: " + output);
                        Console.WriteLine("Error: " + error);
                        System.Windows.MessageBox.Show($"Service installation failed. Exit code: {process.ExitCode}\nOutput: {output}\nError: {error}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service installation: {ex.Message}");
                System.Windows.MessageBox.Show($"Exception during service installation: {ex.Message}", "Installation Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void UninstallService()
        {
            string serviceName = "Log2Postgres";

            // Attempt to stop the service first
            string stopCommand = $"stop {serviceName}";
            Console.WriteLine($"Attempting to stop service with command: sc.exe {stopCommand}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = stopCommand;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string stopOutput = process.StandardOutput.ReadToEnd();
                    string stopError = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    // Exit code 1062 means service not started, 1060 means service does not exist - both are fine for uninstall.
                    if (process.ExitCode == 0 || process.ExitCode == 1062 || process.ExitCode == 1060)
                    {
                        Console.WriteLine("Service stop command executed (or service was not running/not found).");
                    }
                    else
                    {
                        Console.WriteLine($"Service stop command failed. Exit code: {process.ExitCode}");
                        Console.WriteLine("Output: " + stopOutput);
                        Console.WriteLine("Error: " + stopError);
                        // Don't necessarily block uninstallation if stop fails, but log it.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service stop attempt: {ex.Message}");
                // Continue with uninstallation
            }

            string deleteCommand = $"delete {serviceName}";
            Console.WriteLine($"Attempting to uninstall service with command: sc.exe {deleteCommand}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = deleteCommand;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service uninstalled successfully.");
                        System.Windows.MessageBox.Show("Service uninstalled successfully.", "Service Uninstallation", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Console.WriteLine($"Service uninstallation failed. Exit code: {process.ExitCode}");
                        Console.WriteLine("Output: " + output);
                        Console.WriteLine("Error: " + error);
                        System.Windows.MessageBox.Show($"Service uninstallation failed. Exit code: {process.ExitCode}\nOutput: {output}\nError: {error}", "Uninstallation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service uninstallation: {ex.Message}");
                System.Windows.MessageBox.Show($"Exception during service uninstallation: {ex.Message}", "Uninstallation Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}


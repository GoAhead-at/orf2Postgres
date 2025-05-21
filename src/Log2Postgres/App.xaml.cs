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
using Serilog.Settings.Configuration;
// using Serilog.Sinks.Console; // Removed - suspected cause of CS0234
// using Serilog.Sinks.File;   // Removed - suspected cause of CS0234

namespace Log2Postgres
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public const string WindowsServiceName = "Log2Postgres";
        private IHost _host;
        private static Mutex _singleInstanceMutex = null!;
        private const string MutexName = "Log2PostgresAppSingleInstance";
        
        // Win32 API imports for finding and activating existing window
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        private const int SW_RESTORE = 9;

        public App(IHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            InitializeGlobalErrorHandling();
        }

        public App()
        {
            InitializeGlobalErrorHandling();
            Console.WriteLine("[App..ctor] Parameterless constructor called. Building its own host.");
            _host = CreateHostBuilder(Environment.GetCommandLineArgs()).Build();
        }

        private void InitializeGlobalErrorHandling()
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "App_Constructor_Start.txt"), $"App constructor/init called at {DateTime.Now}\\nAppCcontext.BaseDirectory: {AppContext.BaseDirectory}");
            }
            catch { /* ignore */ }

            var selfLogFilePath = Path.Combine(AppContext.BaseDirectory, "Log2Postgres_Serilog_SelfLog_AppDir.txt");
            Serilog.Debugging.SelfLog.Enable(msg => 
            {
                try 
                {
                    File.AppendAllText(selfLogFilePath, $"[{DateTime.Now:o}] {msg}{Environment.NewLine}");
                }
                catch 
                {
                    Console.Error.WriteLine("[SelfLog-FileError] ({selfLogFilePath}): " + msg);
                }
            });

            string errorFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.txt");
            if (File.Exists(errorFilePath))
            {
                try
                {
                    string errorContent = File.ReadAllText(errorFilePath);
                    System.Windows.MessageBox.Show(
                        $"A previous startup error was detected:\\n\\n{errorContent.Substring(0, Math.Min(500, errorContent.Length))}...\\n\\nThis file will be renamed to startup_error.old.txt",
                        "Previous Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    if (File.Exists(errorFilePath + ".old")) File.Delete(errorFilePath + ".old");
                    File.Move(errorFilePath, errorFilePath + ".old");
                }
                catch { /* Ignore */ }
            }
        }
        
        private void ActivateExistingInstance()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);
                
                foreach (var process in processes)
                {
                    if (process.Id == currentProcess.Id)
                        continue;
                    
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        
                        Console.WriteLine($"Found existing instance (PID: {process.Id}), activating its window.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error activating existing instance: {ex.Message}");
                System.Windows.MessageBox.Show($"Another instance is already running, but couldn't be activated: {ex.Message}",
                    "Application Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        
        public IConfiguration GetConfiguration()
        {
            if (_host == null)
            {
                throw new InvalidOperationException("Host is not initialized, cannot get configuration.");
            }
            return _host.Services.GetRequiredService<IConfiguration>();
        }

        public T GetService<T>() where T : class
        {
            if (_host == null)
            {
                throw new InvalidOperationException("Host is not initialized, cannot get service.");
            }
            return _host.Services.GetRequiredService<T>();
        }

        public static IHostBuilder CreateHostBuilder(string[]? args = null)
        {
            // VERY EARLY DIAGNOSTIC LOGGING - BEFORE ANYTHING ELSE
            string diagLogPath = Path.Combine(AppContext.BaseDirectory, "CreateHostBuilder_Diag.txt");
            try
            {
                string initialArgs = (args == null) ? "null" : $"['{string.Join("','", args)}']";
                string envArgsLine = "null";
                try { envArgsLine = $"['{string.Join("','", Environment.GetCommandLineArgs())}']"; } catch {}
                string userInteractiveLine = "unknown";
                try { userInteractiveLine = Environment.UserInteractive.ToString(); } catch {}

                File.AppendAllText(diagLogPath, 
                    $"[{DateTime.Now:o}] CreateHostBuilder ENTERED.\n" +
                    $"  Passed args: {initialArgs}\n" +
                    $"  Environment.UserInteractive: {userInteractiveLine}\n" +
                    $"  Environment.GetCommandLineArgs(): {envArgsLine}\n");
            }
            catch (Exception exDiag)
            {
                try { File.AppendAllText(diagLogPath, $"[{DateTime.Now:o}] ERROR writing diag log: {exDiag.Message}\n"); }
                catch { /* utterly failed */ }
            }

            var effectiveArgs = args ?? Environment.GetCommandLineArgs();
            string commandLineForLog = string.Join(" ", effectiveArgs);

            bool isLaunchedBySCM = !Environment.UserInteractive;
            bool hasServiceArgument = effectiveArgs.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
            
            bool runAsService = isLaunchedBySCM || hasServiceArgument;

            // Log determination factors AFTER attempting to log them above
            try
            {
                File.AppendAllText(diagLogPath,
                    $"  EffectiveArgs for Contains check: ['{string.Join("','", effectiveArgs)}']\n" +
                    $"  isLaunchedBySCM (!UserInteractive): {isLaunchedBySCM}\n" +
                    $"  hasServiceArgument (--windows-service): {hasServiceArgument}\n" +
                    $"  DETERMINED runAsService: {runAsService}\n---\n");
            }
            catch { /* ignore */ }

            // Console.WriteLine diagnostics remain but might not be visible in service context
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
                    options.ServiceName = WindowsServiceName;
                });

                hostBuilder.UseSerilog((hostingContext, services, loggerConfiguration) => 
                {
                    string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
                    string logFilePath = Path.Combine(logDirectory, "service_log_.txt");

                    try
                    {
                        Directory.CreateDirectory(logDirectory);
                        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "CreateHostBuilder_Diag.txt"),
                            $"[{DateTime.Now:o}] SERVICE_LOG_PATH_SETUP: Ensured directory exists: {logDirectory}. Logging to: {logFilePath}\n");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "CreateHostBuilder_Diag.txt"),
                            $"[{DateTime.Now:o}] SERVICE_LOG_PATH_SETUP_ERROR: Failed to create directory {logDirectory}: {ex.Message}\n");
                    }

                    loggerConfiguration
                        .ReadFrom.Configuration(hostingContext.Configuration)
                        .Enrich.FromLogContext()
                        .WriteTo.File(logFilePath, 
                            rollingInterval: Serilog.RollingInterval.Day, 
                            retainedFileCountLimit: 7,
                            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");
                });
            }
            else
            {
                hostBuilder.UseSerilog((hostingContext, services, loggerConfiguration) => 
                {
                    string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
                    string uiLogFilePath = Path.Combine(logDirectory, "ui_log_.txt"); 

                    try
                    {
                        Directory.CreateDirectory(logDirectory);
                        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "CreateHostBuilder_Diag.txt"),
                           $"[{DateTime.Now:o}] UI_LOG_PATH_SETUP: Ensured directory exists: {logDirectory}. Logging to: {uiLogFilePath}\n");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "CreateHostBuilder_Diag.txt"),
                           $"[{DateTime.Now:o}] UI_LOG_PATH_SETUP_ERROR: Failed to create directory {logDirectory}: {ex.Message}\n");
                    }
                    
                    loggerConfiguration
                        .ReadFrom.Configuration(hostingContext.Configuration)
                        .Enrich.FromLogContext()
                        .WriteTo.File(uiLogFilePath, 
                            rollingInterval: Serilog.RollingInterval.Day, 
                            retainedFileCountLimit: 7,
                            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");
                });
            }

            hostBuilder.ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton<IConfiguration>(hostContext.Configuration);
                services.AddSingleton<PasswordEncryption>();

                services.Configure<WindowsServiceSettings>(options => 
                {
                    options.RunAsService = runAsService;
                });

                services.Configure<DatabaseSettings>(hostContext.Configuration.GetSection("DatabaseSettings"));
                
                services.AddSingleton<IPostConfigureOptions<DatabaseSettings>, DatabaseSettingsPostConfigureOptions>();
                
                services.Configure<LogMonitorSettings>(hostContext.Configuration.GetSection("LogMonitorSettings"));

                services.PostConfigure<LogMonitorSettings>(options =>
                {
                    bool wasBaseDirNull = false;
                    if (options.BaseDirectory == null)
                    {
                        options.BaseDirectory = string.Empty;
                        wasBaseDirNull = true;
                    }

                    bool wasPatternNull = false;
                    if (string.IsNullOrEmpty(options.LogFilePattern))
                    {
                        options.LogFilePattern = "orfee-{Date:yyyy-MM-dd}.log";
                        wasPatternNull = true;
                    }
                    bool pollingIntervalAdjusted = false;
                    if (options.PollingIntervalSeconds <= 0) {
                        options.PollingIntervalSeconds = 5;
                        pollingIntervalAdjusted = true;
                    }

                    Console.WriteLine($"[App.PostConfigure<LogMonitorSettings>] DIAGNOSTIC: Options.BaseDirectory = '{options.BaseDirectory}' (was null: {wasBaseDirNull})");
                    Console.WriteLine($"[App.PostConfigure<LogMonitorSettings>] DIAGNOSTIC: Options.LogFilePattern = '{options.LogFilePattern}' (was null/empty: {wasPatternNull})");
                    Console.WriteLine($"[App.PostConfigure<LogMonitorSettings>] DIAGNOSTIC: Options.PollingIntervalSeconds = '{options.PollingIntervalSeconds}' (adjusted: {pollingIntervalAdjusted})");
                });
                
                services.AddSingleton<OrfLogParser>();
                services.AddSingleton<PostgresService>(sp => 
                {
                    var logger = sp.GetRequiredService<ILogger<PostgresService>>();
                    var dbSettings = sp.GetRequiredService<IOptionsMonitor<DatabaseSettings>>();
                    return new PostgresService(logger, dbSettings);
                });
                services.AddSingleton<IPasswordEncryption, PasswordEncryption>();
                services.AddSingleton<PositionManager>();
                services.AddSingleton<IIpcService, IpcService>();

                services.AddHostedService<LogFileWatcher>();

                services.AddSingleton<LogFileWatcher>(sp => 
                {
                    var hostedServices = sp.GetServices<IHostedService>();
                    var serviceInstance = hostedServices.OfType<LogFileWatcher>().FirstOrDefault();
                    
                    if (serviceInstance == null)
                    {
                        Console.Error.WriteLine("CRITICAL_DI_ERROR: LogFileWatcher instance not found among IHostedService registrations. MainWindow/IPC will likely fail to get the correct instance.");
                        throw new InvalidOperationException("LogFileWatcher instance, registered via AddHostedService, could not be resolved for singleton access. This indicates a DI configuration or resolution order problem.");
                    }
                    return serviceInstance;
                });

                services.AddTransient<MainWindow>();
            });

            return hostBuilder;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Console.WriteLine("[App.OnStartup] Application starting.");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup ENTERED.\n");

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance is already running.
                Console.WriteLine("[App.OnStartup] Another instance detected. Activating it and shutting down current instance.");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: Another instance detected. Activating and exiting.\n");
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            // Proceed with starting the host and showing the main window for the new instance.
            if (_host == null)
            {
                Console.WriteLine("[App.OnStartup] _host is null, attempting to build it.");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: _host is null, building.\n");
                _host = CreateHostBuilder(Environment.GetCommandLineArgs()).Build(); 
            }
            
            Console.WriteLine("[App.OnStartup] Calling _host.StartAsync().");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: Calling _host.StartAsync().\n");
            await _host.StartAsync(); // Await the host start
            Console.WriteLine("[App.OnStartup] _host.StartAsync() completed.");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: _host.StartAsync() completed.\n");

            if (!IsRunningAsService()) // Only show UI if not running as a service
            {
                var mainWindow = _host.Services.GetService<MainWindow>();
                if (mainWindow != null) 
                {
                    this.MainWindow = mainWindow; // Assign to App.MainWindow
                    mainWindow.Show();
                    Console.WriteLine("[App.OnStartup] MainWindow shown.");
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: MainWindow shown.\n");
                }
                else
                {
                    Console.Error.WriteLine("[App.OnStartup] CRITICAL: MainWindow could not be resolved from services. UI will not show.");
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: CRITICAL - MainWindow is null.\n");
                    // Optionally, shutdown or show a message box
                    System.Windows.MessageBox.Show("Critical error: Main window could not be created. Application will exit.", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                }
            }
            else
            {
                Console.WriteLine("[App.OnStartup] Running as a service. UI (MainWindow) will not be shown.");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup: Running as service, UI not shown.\n");
            }
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnStartup EXITED NORMALLY.\n");
        }

        private static bool IsRunningAsService()
        {
            // This logic should be consistent with CreateHostBuilder
            // Consider if effectiveArgs needs to be passed or re-evaluated if this is called before CreateHostBuilder sets them up.
            // For OnStartup, CreateHostBuilder has likely run.
            string[] effectiveArgs = Environment.GetCommandLineArgs(); // Or retrieve from a shared place if set by CreateHostBuilder
            bool isLaunchedBySCM = !Environment.UserInteractive;
            bool hasServiceArgument = effectiveArgs.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
            return isLaunchedBySCM || hasServiceArgument;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnExit ENTERED. ExitCode: {e.ApplicationExitCode}\n");
            Console.WriteLine($"[App.OnExit] Application exiting with code: {e.ApplicationExitCode}");
            
            if (_host != null)
            {
                Console.WriteLine("[App.OnExit] Calling _host.StopAsync().");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnExit: Calling _host.StopAsync().\n");
                await _host.StopAsync(); // Await the host stop
                _host.Dispose();
                Console.WriteLine("[App.OnExit] _host stopped and disposed.");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnExit: _host stopped and disposed.\n");
            }
            else
            {
                Console.WriteLine("[App.OnExit] _host was null. No host stop/dispose action taken.");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnExit: _host was null.\n");
            }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            Console.WriteLine("[App.OnExit] Single instance mutex released and disposed.");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnExit: Mutex released/disposed.\n");

            Serilog.Log.CloseAndFlush();
            Console.WriteLine("[App.OnExit] Serilog closed and flushed.");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "App_Lifecycle.txt"), $"[{DateTime.Now:o}] OnExit: Serilog flushed. EXITED NORMALLY.\n");
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
            string serviceName = WindowsServiceName;
            string displayName = "Log2Postgres ORF Log Watcher";
            string? exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var mainModule = Process.GetCurrentProcess().MainModule;
                if (mainModule != null)
                {
                    exePath = mainModule.FileName;
                    if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string? entryAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                        if (!string.IsNullOrEmpty(entryAssemblyLocation) && entryAssemblyLocation.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)){
                             exePath = $"\"{Environment.ProcessPath}\" \"{entryAssemblyLocation}\"";
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

            string binPath = $"\"{exePath}\" --windows-service";
            string command = $"create {serviceName} binPath= \"{binPath}\" DisplayName= \"{displayName}\" start= auto obj= \"NT AUTHORITY\\NetworkService\"";

            Console.WriteLine($"Attempting to install service with command: sc.exe {command}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service installed successfully.");
                        System.Windows.MessageBox.Show("Service installed successfully.", "Service Installation", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Console.WriteLine($"Service installation failed. Exit code: {process.ExitCode}");
                        System.Windows.MessageBox.Show($"Service installation failed. Exit code: {process.ExitCode}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            string serviceName = WindowsServiceName;

            string stopCommand = $"stop {serviceName}";
            Console.WriteLine($"Attempting to stop service with command: sc.exe {stopCommand}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = stopCommand;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0 || process.ExitCode == 1062 || process.ExitCode == 1060)
                    {
                        Console.WriteLine("Service stop command executed (or service was not running/not found).");
                    }
                    else
                    {
                        Console.WriteLine($"Service stop command failed. Exit code: {process.ExitCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service stop attempt: {ex.Message}");
            }

            string deleteCommand = $"delete {serviceName}";
            Console.WriteLine($"Attempting to uninstall service with command: sc.exe {deleteCommand}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = deleteCommand;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service uninstalled successfully.");
                        System.Windows.MessageBox.Show("Service uninstalled successfully.", "Service Uninstallation", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Console.WriteLine($"Service uninstallation failed. Exit code: {process.ExitCode}");
                        System.Windows.MessageBox.Show($"Service uninstallation failed. Exit code: {process.ExitCode}", "Uninstallation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service uninstallation: {ex.Message}");
                System.Windows.MessageBox.Show($"Exception during service uninstallation: {ex.Message}", "Uninstallation Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void StartWindowsService(string serviceName)
        {
            string command = $"start {serviceName}";
            Console.WriteLine($"Attempting to start service '{serviceName}' with command: sc.exe {command}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";        
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit(); // Wait for the UAC prompt and sc.exe to complete

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"Service '{serviceName}' start command issued successfully.");
                        // Consider a small delay or a loop to check service status if immediate feedback is needed beyond UAC.
                        // For now, UAC prompt success/cancel is the main feedback.
                    }
                    else
                    {
                        Console.WriteLine($"Service '{serviceName}' start command failed. Exit code: {process.ExitCode}. This might be normal if UAC was cancelled.");
                        // No MessageBox here as UAC itself provides feedback or cancellation indication.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service start: {ex.Message}");
                System.Windows.MessageBox.Show($"Exception during service start: {ex.Message}", "Service Start Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void StopWindowsService(string serviceName)
        {
            string command = $"stop {serviceName}";
            Console.WriteLine($"Attempting to stop service '{serviceName}' with command: sc.exe {command}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"Service '{serviceName}' stop command issued successfully.");
                    }
                    else
                    {
                        Console.WriteLine($"Service '{serviceName}' stop command failed. Exit code: {process.ExitCode}. This might be normal if UAC was cancelled or service was already stopped.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service stop: {ex.Message}");
                System.Windows.MessageBox.Show($"Exception during service stop: {ex.Message}", "Service Stop Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal class ApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedSource = new CancellationTokenSource();
        private readonly CancellationTokenSource _stoppingSource = new CancellationTokenSource();
        private readonly CancellationTokenSource _stoppedSource = new CancellationTokenSource();

        public CancellationToken ApplicationStarted => _startedSource.Token;
        public CancellationToken ApplicationStopping => _stoppingSource.Token;
        public CancellationToken ApplicationStopped => _stoppedSource.Token;

        public void StopApplication()
        {
            _stoppingSource.Cancel();
        }

        public void NotifyStarted() => _startedSource.Cancel(); 
        public void NotifyStopped() => _stoppedSource.Cancel();
    }
}



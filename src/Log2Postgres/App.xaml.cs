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
            Console.WriteLine("[App..ctor(IHost)] Constructor with IHost called. This is expected for Program.Main driven UI.");
        }

        public App()
        {
            InitializeGlobalErrorHandling();
            Console.WriteLine("[App..ctor] PARAMETERLESS App constructor called! This is UNEXPECTED in Program.Main driven flow. The application might be misconfigured or starting up incorrectly. An IHost should be provided.");
            
            _host = null;
        }

        private void InitializeGlobalErrorHandling()
        {
            // Set up Serilog self-logging to console instead of file
            Serilog.Debugging.SelfLog.Enable(msg => 
            {
                Console.Error.WriteLine($"[SelfLog]: {msg}");
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
            var effectiveArgs = args ?? Environment.GetCommandLineArgs();
            string commandLineForLog = string.Join(" ", effectiveArgs);

            bool isLaunchedBySCM = !Environment.UserInteractive;
            bool hasServiceArgument = effectiveArgs.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
            
            bool runAsService = isLaunchedBySCM || hasServiceArgument;

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
                    ConfigureSerilogWithErrorHandling(hostingContext, loggerConfiguration);
                });
            }
            else
            {
                hostBuilder.UseSerilog((hostingContext, services, loggerConfiguration) => 
                {
                    ConfigureSerilogWithErrorHandling(hostingContext, loggerConfiguration);
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

                // Register new UI services
                services.AddSingleton<UI.Services.IServiceManager, UI.Services.ServiceManager>();
                services.AddSingleton<UI.Services.IAppConfigurationManager, UI.Services.AppConfigurationManager>();
                services.AddTransient<UI.ViewModels.MainWindowViewModel>();
                services.AddTransient<MainWindow>();
                
                // Register infrastructure services
                services.AddSingleton<Infrastructure.Resilience.IResilienceService, Infrastructure.Resilience.ResilienceService>();
                services.AddSingleton<Infrastructure.Resources.IResourceManager, Infrastructure.Resources.ResourceManager>();
                
                // Register optimized performance services
                services.AddSingleton<Core.Services.OptimizedOrfLogParser>();
                services.AddSingleton<Core.Services.IOptimizedLogFileProcessor, Core.Services.OptimizedLogFileProcessor>();
            });

            return hostBuilder;
        }

        private static void ConfigureSerilogWithErrorHandling(Microsoft.Extensions.Hosting.HostBuilderContext hostingContext, Serilog.LoggerConfiguration loggerConfiguration)
        {
            try
            {
                // For single-file publishing compatibility, explicitly load Serilog assemblies
                var assemblies = new[]
                {
                    System.Reflection.Assembly.Load("Serilog.Sinks.Console"),
                    System.Reflection.Assembly.Load("Serilog.Sinks.File"),
                    System.Reflection.Assembly.Load("Serilog.Enrichers.Thread"),
                    System.Reflection.Assembly.Load("Serilog.Enrichers.Environment"),
                    System.Reflection.Assembly.Load("Serilog.Enrichers.Process")
                }.OfType<System.Reflection.Assembly>().ToArray();

                var readerOptions = new Serilog.Settings.Configuration.ConfigurationReaderOptions(assemblies);
                
                loggerConfiguration
                    .ReadFrom.Configuration(hostingContext.Configuration, readerOptions)
                    .Enrich.FromLogContext();
            }
            catch (Exception ex)
            {
                // Fallback to basic console logging if configuration fails
                Console.WriteLine($"[SERILOG_CONFIG_ERROR] Failed to configure Serilog from configuration: {ex.Message}");
                Console.WriteLine("[SERILOG_CONFIG_ERROR] Falling back to basic console logging");
                
                loggerConfiguration
                    .MinimumLevel.Debug()
                    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .Enrich.FromLogContext()
                    .Enrich.WithThreadId()
                    .Enrich.WithMachineName()
                    .Enrich.WithProcessId();
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            try
            {
                bool isServiceMode = IsRunningAsService();

                if (!isServiceMode)
                {
                    _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
                    if (!createdNew)
                    {
                        ActivateExistingInstance();
                        Shutdown();
                        return;
                    }
                }

                base.OnStartup(e);

                if (_host == null)
                {
                    try
                    {
                        _host = CreateHostBuilder(e.Args).Build();
                    }
                    catch (Exception hostBuildEx)
                    {
                        // Configuration loading failed - try to recover
                        await HandleConfigurationError(hostBuildEx, e.Args);
                        return;
                    }
                }
                
                await _host.StartAsync();

                // Initialize PositionManager
                var positionManager = _host.Services.GetService<PositionManager>();
                if (positionManager != null)
                {
                    try
                    {
                        await positionManager.InitializeAsync();
                    }
                    catch (Exception exInitialize)
                    {
                        // Decide how to handle this critical failure.
                        // For now, log and continue, but this might leave the app in a bad state.
                        System.Windows.MessageBox.Show($"Critical error initializing PositionManager: {exInitialize.Message}. Application might not work correctly.", 
                                        "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                     System.Windows.MessageBox.Show("Critical error: PositionManager service not found. Application cannot start.", 
                                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(-1); // Exit if PositionManager is essential and not found
                    return;
                }

                if (!isServiceMode)
                {
                    var mainWindow = _host.Services.GetService<MainWindow>();
                    if (mainWindow != null)
                    {
                        Current.MainWindow = mainWindow; // Set the main window
                        mainWindow.Show();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("Critical error: MainWindow could not be resolved. Application cannot start.", 
                                        "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Shutdown(-1);
                        return;
                    }
                }
                else
                {
                    // In service mode, the host runs, but no UI is shown.
                    // The LogFileWatcher (BackgroundService) will be started by the host.
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"A critical error occurred during application startup: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Ensure shutdown if error is catastrophic
                if (Current != null && Current.MainWindow == null) // If main window never showed
                {
                    Shutdown(-1);
                }
            }
        }

        private async Task HandleConfigurationError(Exception configException, string[] args)
        {
            try
            {
                Console.WriteLine($"[CONFIG_ERROR] Configuration loading failed: {configException.Message}");
                
                // Try to create a backup of the corrupted config and create a new default one
                string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                
                if (File.Exists(configPath))
                {
                    string backupPath = Path.Combine(AppContext.BaseDirectory, $"appsettings.json.backup.{DateTime.Now:yyyyMMdd_HHmmss}");
                    File.Copy(configPath, backupPath);
                    Console.WriteLine($"[CONFIG_ERROR] Backed up corrupted config to: {backupPath}");
                }

                // Create default configuration with proper Serilog Using section
                const string defaultConfig = @"{
  ""Serilog"": {
    ""Using"": [
      ""Serilog.Sinks.Console"",
      ""Serilog.Sinks.File"",
      ""Serilog.Enrichers.Environment"",
      ""Serilog.Enrichers.Process"",
      ""Serilog.Enrichers.Thread""
    ],
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

                await File.WriteAllTextAsync(configPath, defaultConfig);
                Console.WriteLine("[CONFIG_ERROR] Created new default configuration file");

                // Try to build host again with the new configuration
                _host = CreateHostBuilder(args).Build();
                await _host.StartAsync();

                // Show user-friendly message about the recovery
                bool isServiceMode = IsRunningAsService();
                if (!isServiceMode)
                {
                    System.Windows.MessageBox.Show(
                        $"The configuration file was corrupted and has been reset to defaults.\n\n" +
                        $"Original file backed up to: appsettings.json.backup.{DateTime.Now:yyyyMMdd_HHmmss}\n\n" +
                        $"Please reconfigure your database and log directory settings.",
                        "Configuration Recovered", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Warning);
                }

                Console.WriteLine("[CONFIG_ERROR] Successfully recovered from configuration error");
            }
            catch (Exception recoveryEx)
            {
                Console.WriteLine($"[CONFIG_ERROR] Failed to recover from configuration error: {recoveryEx.Message}");
                
                System.Windows.MessageBox.Show(
                    $"Critical configuration error that could not be automatically recovered:\n\n" +
                    $"Original error: {configException.Message}\n" +
                    $"Recovery error: {recoveryEx.Message}\n\n" +
                    $"Please check the appsettings.json file manually or delete it to reset to defaults.",
                    "Critical Configuration Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
                
                Shutdown(-1);
            }
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
            Console.WriteLine($"[App.OnExit] Application exiting with code: {e.ApplicationExitCode}");
            
            if (_host != null)
            {
                Console.WriteLine("[App.OnExit] Calling _host.StopAsync().");
                await _host.StopAsync(); // Await the host stop
                _host.Dispose();
                Console.WriteLine("[App.OnExit] _host stopped and disposed.");
            }
            else
            {
                Console.WriteLine("[App.OnExit] _host was null. No host stop/dispose action taken.");
            }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            Console.WriteLine("[App.OnExit] Single instance mutex released and disposed.");

            Serilog.Log.CloseAndFlush();
            Console.WriteLine("[App.OnExit] Serilog closed and flushed.");
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

            // Use a single elevated cmd.exe process to run both stop and delete commands
            // This reduces UAC prompts from 2 to 1, matching the install experience
            string batchCommands = $"sc.exe stop {serviceName} & sc.exe delete {serviceName}";
            
            Console.WriteLine($"Attempting to stop and uninstall service with combined command: cmd.exe /c \"{batchCommands}\"");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = $"/c \"{batchCommands}\"";
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas"; // Single UAC prompt for both operations
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service stopped and uninstalled successfully.");
                    }
                    else
                    {
                        Console.WriteLine($"Service uninstallation process failed. Exit code: {process.ExitCode}");
                        // Note: Even if stop fails (service already stopped), delete might succeed
                        // The batch command continues with & operator regardless of first command result
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



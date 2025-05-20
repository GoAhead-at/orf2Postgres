using System;
using System.IO;
using System.Windows;
using System.Linq;
using Microsoft.Extensions.Hosting; // Added for IHost
using Microsoft.Extensions.DependencyInjection; // Added for GetRequiredService
using Log2Postgres.Core.Models; // Added for WindowsServiceSettings
using Microsoft.Extensions.Logging; // Added for ILogger extension methods like LogInformation
using Microsoft.Extensions.Options;
// Add System.Environment for Environment.Exit and Environment.GetCommandLineArgs if App.xaml.cs doesn't pass args
// Add System.Security.Principal for WindowsIdentity/WindowsPrincipal if IsAdministrator is moved here
// Add System.Diagnostics for Process if service methods are moved here

namespace Log2Postgres
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args) // Changed back to void Main for STA simplicity with WPF
        {
            // Write to a diagnostic file immediately to confirm Main execution and capture args
            // For service context, AppContext.BaseDirectory might be System32, ensure writable path or adjust.
            // However, current AppContext.BaseDirectory seems to be the app's dir.
            var programMainStartLog = Path.Combine(AppContext.BaseDirectory, "Program_Main_Start.txt");
            try
            {
                File.AppendAllText(programMainStartLog, $"Program.Main started at {DateTime.Now:dd.MM.yyyy HH:mm:ss}\nAppContext.BaseDirectory: {AppContext.BaseDirectory}\nArgs: {string.Join(" ", args)}\n---\n");
            }
            catch (Exception ex)
            {
                // Fallback if AppContext.BaseDirectory is not writable or another issue
                var fallbackLogPath = Path.Combine(Path.GetTempPath(), "Log2Postgres_Program_Main_Start_FAIL.txt");
                File.AppendAllText(fallbackLogPath, $"Program.Main started at {DateTime.Now:dd.MM.yyyy HH:mm:ss}\nError writing to AppContext.BaseDirectory log: {ex.Message}\nArgs: {string.Join(" ", args)}\n---\n");
            }

            // Handle install/uninstall directly as before, as these are command-line utilities
            // and don't need the full host or WPF app.
            bool installRequested = args.Contains("--install", StringComparer.OrdinalIgnoreCase);
            bool uninstallRequested = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);

            if (installRequested || uninstallRequested)
            {
                if (!App.IsAdministrator())
                {
                    Console.WriteLine("Administrator privileges are required to install or uninstall the service.");
                    System.Windows.MessageBox.Show("Administrator privileges are required to install or uninstall the service. Please run as administrator.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.Exit(1);
                    return;
                }

                if (installRequested)
                {
                    Console.WriteLine("Installation requested via Program.Main...");
                    App.InstallService();
                    Environment.Exit(0);
                    return;
                }

                if (uninstallRequested)
                {
                    Console.WriteLine("Uninstallation requested via Program.Main...");
                    App.UninstallService();
                    Environment.Exit(0);
                    return;
                }
            }

            // Build the host using the static method from App
            // This allows App to define service configurations and DI
            IHost host = App.CreateHostBuilder(args).Build();

            // Determine run mode from the configured WindowsServiceSettings
            var serviceSettings = host.Services.GetRequiredService<IOptions<WindowsServiceSettings>>().Value;
            bool runAsService = serviceSettings.RunAsService; // Determined by CreateHostBuilder based on args and UserInteractive
            
            // Log this determination from Program.Main as well
            try 
            {
                var logger = host.Services.GetService<Microsoft.Extensions.Logging.ILogger<Program>>();
                logger?.LogInformation("[Program.Main] Effective command line args for host: {Args}", string.Join(" ", args));
                logger?.LogInformation("[Program.Main] Determined runAsService = {RunAsService}", runAsService);
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"[Program.Main] Error getting logger or logging run mode: {ex.Message}");
            }

            var runModeLogPath = Path.Combine(AppContext.BaseDirectory, runAsService ? "Program_Main_ServicePath.txt" : "Program_Main_UIPath.txt");
            File.AppendAllText(runModeLogPath, $"Program.Main: Determined runAsService = {runAsService} at {DateTime.Now:o}\n");

            if (runAsService)
            {
                string serviceLifecycleLog = Path.Combine(AppContext.BaseDirectory, "Program_Main_Service_Lifecycle.txt");
                try
                {
                    File.AppendAllText(serviceLifecycleLog, $"[{DateTime.Now:o}] SERVICE_PATH: About to call host.RunAsync().GetAwaiter().GetResult().\n");
                    host.RunAsync().GetAwaiter().GetResult(); // Blocking call for service
                    File.AppendAllText(serviceLifecycleLog, $"[{DateTime.Now:o}] SERVICE_PATH: host.RunAsync().GetAwaiter().GetResult() completed (service is shutting down normally).\n");
                }
                catch (Exception ex)
                {
                    File.AppendAllText(serviceLifecycleLog, $"[{DateTime.Now:o}] SERVICE_PATH: CRITICAL EXCEPTION during host.RunAsync() or related service operations: {ex.ToString()}\n");
                    // Rethrow the exception so the SCM knows the service failed to start/run
                    throw;
                }
            }
            else
            {
                // Run as a Desktop WPF application. This path MUST be STA.
                try
                {
                    File.AppendAllText(runModeLogPath, $"Program.Main: UI Path - Starting host and running WPF App at {DateTime.Now:o}\n");
                    // Start the host synchronously to ensure we don't leave STA context
                    host.Start(); 
                    Console.WriteLine("[Program.Main] Host started. Initializing and running WPF App.");

                    var app = new App(host); 
                    app.InitializeComponent();
                    app.Run(); // This blocks until the WPF application closes. Must be on STA thread.

                    Console.WriteLine("[Program.Main] WPF App.Run() completed.");
                    // App.OnExit will handle host.StopAsync(), host.Dispose(), and Log.CloseAndFlush()
                }
                catch (Exception ex)
                {
                    string errorLogPath = Path.Combine(AppContext.BaseDirectory, "wpf_startup_crash.txt");
                    File.WriteAllText(errorLogPath, $"WPF application crashed at startup: {ex.ToString()}");
                    System.Windows.MessageBox.Show(
                        $"Fatal error during WPF application startup: {ex.Message}\\n\\nFull details logged to: {errorLogPath}",
                        "Startup Crash",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                    Serilog.Log.CloseAndFlush();
                    Environment.Exit(1);
                }
                // No explicit host.Dispose() here for WPF path, as App.OnExit handles it if app runs and exits normally.
                // However, if app.Run() crashes catastrophically, host might not be disposed by App.OnExit.
                // Consider if host.Dispose() is needed in a finally here if App.OnExit might not run.
                // For now, relying on App.OnExit for graceful shutdown.
            }
        }
    }
} 
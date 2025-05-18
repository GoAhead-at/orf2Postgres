using System;
using System.IO;
using System.Windows;
using System.Linq;
// Add System.Environment for Environment.Exit and Environment.GetCommandLineArgs if App.xaml.cs doesn't pass args
// Add System.Security.Principal for WindowsIdentity/WindowsPrincipal if IsAdministrator is moved here
// Add System.Diagnostics for Process if service methods are moved here

namespace Log2Postgres
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args) // args from command line are already here
        {
            bool installRequested = args.Contains("--install", StringComparer.OrdinalIgnoreCase);
            bool uninstallRequested = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);

            if (installRequested || uninstallRequested)
            {
                // Ensure console output is visible for command-line operations
                // If this app is a WPF app without a console by default, this might be needed.
                // However, sc.exe calls usually show in the calling console.
                // App.AttachConsole(); // If you implement AttachConsole helper

                if (!App.IsAdministrator()) // Call static method from App.xaml.cs
                {
                    Console.WriteLine("Administrator privileges are required to install or uninstall the service.");
                    // Avoid MessageBox here for purely command-line operations if possible, rely on console.
                    // However, if invoked from UI button that re-launches, MessageBox might be seen.
                    System.Windows.MessageBox.Show("Administrator privileges are required to install or uninstall the service. Please run as administrator.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.Exit(1); // Exit with an error code
                    return;
                }

                if (installRequested)
                {
                    Console.WriteLine("Installation requested via Program.Main...");
                    App.InstallService(); // Call static method from App.xaml.cs
                    Environment.Exit(0); // Exit cleanly
                    return;
                }

                if (uninstallRequested)
                {
                    Console.WriteLine("Uninstallation requested via Program.Main...");
                    App.UninstallService(); // Call static method from App.xaml.cs
                    Environment.Exit(0); // Exit cleanly
                    return;
                }
            }

            // If not installing/uninstalling, proceed with normal WPF application startup or service startup
            try
            {
                // Initialize position file if it doesn't exist (can stay here, it's harmless)
                string positionsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "positions.json");
                if (!File.Exists(positionsFilePath))
                {
                    File.WriteAllText(positionsFilePath, "[]");
                    Console.WriteLine($"Created new positions file at {positionsFilePath}");
                }
                
                // Normal application start using the WPF application class
                // The App class constructor will handle its own setup, including HostBuilder using these 'args'.
                var app = new App(); // The App constructor will use Environment.GetCommandLineArgs() or be passed 'args'
                app.InitializeComponent(); // This loads App.xaml and its resources
                app.Run(); // This starts the WPF message loop and shows the main window via OnStartup
            }
            catch (Exception ex)
            {
                string errorLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_crash.txt");
                File.WriteAllText(
                    errorLogPath,
                    $"Application crashed at startup: {ex.ToString()}"); // Use ex.ToString() for full details
                
                try
                {
                    System.Windows.MessageBox.Show(
                        $"Fatal error during application startup: {ex.Message}\n\nFull details logged to: {errorLogPath}",
                        "Startup Crash",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // If MessageBox fails, console or log file is the last resort.
                    Console.WriteLine($"Application crashed at startup. Full details in {errorLogPath}");
                }
                Environment.Exit(1); // Ensure application exits on fatal startup error
            }
        }
    }
} 
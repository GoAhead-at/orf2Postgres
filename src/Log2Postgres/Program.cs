using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Linq;

namespace Log2Postgres
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Determine if we should run as a window or service
            var shouldRunAsWindow = ShouldRunAsWindow(args);

            if (shouldRunAsWindow)
            {
                // Run as WPF application
                var application = new App();
                application.InitializeComponent();
                application.Run();
            }
            else
            {
                // Run as a service or console app
                CreateHostBuilder(args).Build().Run();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "Log2Postgres";
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // We'll configure services here for service/console mode
                });

        private static bool ShouldRunAsWindow(string[] args)
        {
            // Check if running as a Windows service
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
                return false;

            // Check if running as a console app
            if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
                return false;

            // When no specific arguments and not running as a service, default to window mode
            return !IsRunningAsService();
        }

        private static bool IsRunningAsService()
        {
            using var process = Process.GetCurrentProcess();
            return process.ProcessName.Contains("services", StringComparison.OrdinalIgnoreCase);
        }
    }
} 
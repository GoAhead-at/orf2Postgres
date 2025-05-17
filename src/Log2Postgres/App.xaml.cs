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

namespace Log2Postgres
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private readonly IHost _host;

        public App()
        {
            _host = CreateHostBuilder().Build();
        }
        
        public IConfiguration GetConfiguration()
        {
            return _host.Services.GetRequiredService<IConfiguration>();
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
                    // Register configuration sections
                    services.Configure<LogMonitorSettings>(
                        hostContext.Configuration.GetSection("LogMonitorSettings"));

                    // Register core services
                    services.AddSingleton<OrfLogParser>();
                    services.AddSingleton<PositionManager>();
                    
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
            await _host.StartAsync();
            base.OnStartup(e);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            await _host.StopAsync();
            _host.Dispose();
            base.OnExit(e);
        }
    }
}


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace orf2Postgres
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Check command line args for service install/uninstall commands
            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "--install":
                        if (!IsRunningAsAdmin())
                        {
                            // Restart as admin if not already running as admin
                            RestartAsAdmin(args);
                            return;
                        }
                        InstallService();
                        return;
                    case "--uninstall":
                        if (!IsRunningAsAdmin())
                        {
                            // Restart as admin if not already running as admin
                            RestartAsAdmin(args);
                            return;
                        }
                        UninstallService();
                        return;
                    case "--console":
                        RunAsConsole(args.Skip(1).ToArray());
                        return;
                    case "--ui":
                        RunAsWpfApplication();
                        return;
                }
            }

            // If no arguments or not recognized, check if running as a service
            if (!Environment.UserInteractive)
            {
                // Running as a service
                RunAsService();
            }
            else
            {
                // Default to UI in interactive mode
                RunAsWpfApplication();
            }
        }

        private static void RunAsConsole(string[] args)
        {
            Console.WriteLine("Running as console application...");
            
            try 
            {
                var host = CreateHostBuilder(args).Build();
                
                Console.WriteLine("Log processor started. Press Ctrl+C to exit.");
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true; // Prevent the process from terminating immediately
                    Console.WriteLine("Shutting down...");
                    host.StopAsync().Wait();
                };
                
                // Set up unhandled exception handler for better user feedback
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                {
                    var ex = e.ExceptionObject as Exception;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"FATAL ERROR: {GetUserFriendlyErrorMessage(ex)}");
                    Console.ResetColor();
                };
                
                // Run the host
                host.Run();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Failed to start application: {GetUserFriendlyErrorMessage(ex)}");
                Console.ResetColor();
                
                // Pause so user can read the error message
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static string GetUserFriendlyErrorMessage(Exception ex)
        {
            if (ex == null) return "Unknown error occurred";
            
            // Convert technical exceptions to more user-friendly messages
            if (ex is DirectoryNotFoundException)
            {
                return $"Directory not found: {ex.Message}. Please check your configuration and ensure the directory exists.";
            }
            else if (ex is FileNotFoundException)
            {
                return $"File not found: {ex.Message}. Please check your configuration and ensure the file exists.";
            }
            else if (ex is UnauthorizedAccessException)
            {
                return $"Access denied: The application does not have permission to access a required file or directory.";
            }
            else if (ex.GetType().Name.Contains("Npgsql") || ex.GetType().Name.Contains("PostgreSQL"))
            {
                return $"Database error: Could not connect to or query the PostgreSQL database. Verify your connection settings. Details: {ex.Message}";
            }
            else if (ex is IOException && ex.Message.Contains("being used by another process"))
            {
                return $"File is locked by another process. Please ensure required files are not open in another application.";
            }
            else
            {
                // For unknown exceptions, provide the technical message but with a more friendly prefix
                return $"An unexpected error occurred: {ex.Message}";
            }
        }

        private static void RunAsService()
        {
            CreateHostBuilder(new string[0]).Build().Run();
        }

        private static void RunAsWpfApplication()
        {
            var application = new App();
            application.InitializeComponent();
            application.Run();
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "ORF2PostgresService";
                })
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    // Add appsettings.json
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    
                    // Check for local config.json (which would have been created by UI)
                    string localConfigPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, 
                        "config.json");
                    
                    if (File.Exists(localConfigPath))
                    {
                        try
                        {
                            // Read config and handle password decryption
                            string jsonConfig = File.ReadAllText(localConfigPath);
                            var configDict = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonConfig);
                            
                            if (configDict != null)
                            {
                                // If password is encrypted, decrypt it
                                if (configDict.TryGetValue("password", out string encryptedPassword) && !string.IsNullOrEmpty(encryptedPassword))
                                {
                                    try
                                    {
                                        string decryptedPassword = DecryptPassword(encryptedPassword);
                                        configDict["password"] = decryptedPassword;
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Warning: Could not decrypt password: {ex.Message}");
                                    }
                                }
                                
                                // Convert flat dictionary to hierarchical configuration
                                var appSettings = new Dictionary<string, string>();
                                var postgresConnection = new Dictionary<string, string>();
                                
                                foreach (var item in configDict)
                                {
                                    switch (item.Key)
                                    {
                                        case "logDirectory":
                                        case "logFilePattern":
                                        case "pollingIntervalSeconds":
                                        case "offsetFilePath":
                                            appSettings[item.Key] = item.Value;
                                            break;
                                            
                                        case "host":
                                        case "port":
                                        case "username":
                                        case "password":
                                        case "database":
                                        case "schema":
                                        case "table":
                                            postgresConnection[item.Key] = item.Value;
                                            break;
                                    }
                                }
                                
                                var inMemoryConfig = new Dictionary<string, string?>();
                                
                                foreach (var item in appSettings)
                                {
                                    inMemoryConfig[$"AppSettings:{item.Key}"] = item.Value;
                                }
                                
                                foreach (var item in postgresConnection)
                                {
                                    inMemoryConfig[$"PostgresConnection:{item.Key}"] = item.Value;
                                }
                                
                                config.AddInMemoryCollection(inMemoryConfig);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error loading local config: {ex.Message}");
                        }
                    }
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<OrfProcessorService>();
                    services.AddLogging(builder =>
                    {
                        builder.AddConsole();
                        builder.AddEventLog(options => 
                        {
                            options.SourceName = "ORF2PostgresService";
                            options.LogName = "Application";
                            options.Filter = (category, level) => level >= LogLevel.Information;
                        });
                    });
                });

        private static void InstallService()
        {
            Console.WriteLine("Installing ORF2Postgres service...");
            
            // Ensure the executable path is valid
            string servicePath = Assembly.GetExecutingAssembly().Location;
            string serviceExePath = Path.ChangeExtension(servicePath, ".exe");
            
            // Create arguments for the service
            string serviceArgs = "";
            
            // Resolve the directory where the EXE is located
            string serviceDirectory = Path.GetDirectoryName(serviceExePath) ?? "";
            
            try
            {
                // Check if service already exists
                bool exists = ServiceController.GetServices()
                    .Any(s => s.ServiceName == "ORF2PostgresService");
                
                if (exists)
                {
                    // Remove existing service
                    Console.WriteLine("Service already exists. Removing it first...");
                    
                    // Need to stop service if it's running
                    using (var sc = new ServiceController("ORF2PostgresService"))
                    {
                        if (sc.Status == ServiceControllerStatus.Running)
                        {
                            Console.WriteLine("Stopping existing service...");
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        }
                    }
                    
                    // Uninstall service
                    Process proc = new Process();
                    proc.StartInfo.FileName = "sc.exe";
                    proc.StartInfo.Arguments = "delete ORF2PostgresService";
                    proc.StartInfo.CreateNoWindow = true;
                    proc.StartInfo.UseShellExecute = false;
                    proc.Start();
                    proc.WaitForExit();
                    
                    Console.WriteLine("Existing service removed.");
                }
                
                // Install new service using sc.exe for more control
                Process process = new Process();
                process.StartInfo.FileName = "sc.exe";
                process.StartInfo.Arguments = $"create ORF2PostgresService binPath= \"{serviceExePath}\" start= auto DisplayName= \"ORF to Postgres Service\"";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.Start();
                process.WaitForExit();
                
                if (process.ExitCode != 0)
                {
                    throw new Exception($"Failed to create service: sc.exe returned {process.ExitCode}");
                }
                
                // Grant permissions to authenticated users for the service
                Process permProcess = new Process();
                permProcess.StartInfo.FileName = "sc.exe";
                permProcess.StartInfo.Arguments = "sdset ORF2PostgresService D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;AU)(A;;CCLCSWRPWPDTLOCRRC;;;PU)";
                permProcess.StartInfo.CreateNoWindow = true;
                permProcess.StartInfo.UseShellExecute = false;
                permProcess.Start();
                permProcess.WaitForExit();
                
                // Start the service
                using (var sc = new ServiceController("ORF2PostgresService"))
                {
                    Console.WriteLine("Starting service...");
                    sc.Start();
                    
                    // Wait for the service to start
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    Console.WriteLine($"Service installed and started. Status: {sc.Status}");
                }
                
                Console.WriteLine("Service installation complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error installing service: {ex.Message}");
                throw;
            }
        }

        private static void UninstallService()
        {
            try
            {
                Console.WriteLine("Stopping ORF2PostgresService service...");
                ProcessStartInfo stopInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "stop ORF2PostgresService",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(stopInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    
                    if (process.ExitCode != 0 && !error.Contains("1062")) // 1062 is service not active
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Warning stopping service: {error}");
                        Console.ResetColor();
                    }
                }

                Console.WriteLine("Uninstalling ORF2PostgresService service...");
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "delete ORF2PostgresService",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error uninstalling service (Exit code: {process.ExitCode})");
                        Console.WriteLine($"Error: {error}");
                        Console.WriteLine($"Output: {output}");
                        Console.ResetColor();
                        Environment.ExitCode = process.ExitCode;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Service uninstalled successfully!");
                        Console.ResetColor();
                        Console.WriteLine(output);
                    }
                }
                
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error uninstalling service: {GetUserFriendlyErrorMessage(ex)}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Console.ResetColor();
                
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.ExitCode = 1;
            }
        }

        // Decrypt password using Windows Data Protection API
        private static string DecryptPassword(string encryptedPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(encryptedPassword))
                    return string.Empty;
                
                // Check if the password is actually encrypted (simple heuristic)
                if (!IsBase64String(encryptedPassword))
                    return encryptedPassword; // Return as-is if it's not encrypted
                
                // First try with CurrentUser scope (for compatibility with existing passwords)
                try
                {
                    byte[] encryptedBytes = Convert.FromBase64String(encryptedPassword);
                    byte[] passwordBytes = ProtectedData.Unprotect(
                        encryptedBytes, 
                        null, 
                        DataProtectionScope.CurrentUser);
                    
                    return Encoding.UTF8.GetString(passwordBytes);
                }
                catch
                {
                    // If that fails, try with LocalMachine scope
                    // This is needed especially for service mode
                    try
                    {
                        Console.WriteLine("Trying password decryption with LocalMachine scope");
                        byte[] encryptedBytes = Convert.FromBase64String(encryptedPassword);
                        byte[] passwordBytes = ProtectedData.Unprotect(
                            encryptedBytes, 
                            null, 
                            DataProtectionScope.LocalMachine);
                        
                        return Encoding.UTF8.GetString(passwordBytes);
                    }
                    catch (Exception exMachine)
                    {
                        Console.WriteLine($"Error decrypting password with LocalMachine scope: {exMachine.Message}");
                        // If all decryption attempts fail, use as plain text
                        return encryptedPassword;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in password decryption logic: {ex.Message}");
                return encryptedPassword; // Return encrypted string if decryption fails
            }
        }
        
        // Helper to check if a string is Base64 encoded
        private static bool IsBase64String(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;
                
            try
            {
                Convert.FromBase64String(input);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRunningAsAdmin()
        {
            System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        
        private static void RestartAsAdmin(string[] args)
        {
            // Create a new process start info
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                Verb = "runas" // This requests elevation
            };
            
            // Add the arguments
            if (args.Length > 0)
            {
                startInfo.Arguments = args[0];
            }
            
            try
            {
                Console.WriteLine("Restarting with admin privileges...");
                Process.Start(startInfo);
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (ex.NativeErrorCode == 1223) // The operation was canceled by the user.
                {
                    Console.WriteLine("Elevation cancelled by the user.");
                }
                else
                {
                    Console.WriteLine("Could not restart with admin rights.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
} 
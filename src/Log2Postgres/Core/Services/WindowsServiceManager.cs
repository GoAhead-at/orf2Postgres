using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows;

namespace Log2Postgres.Core.Services
{
    public static class WindowsServiceManager
    {
        private const string ServiceName = "Log2Postgres";
        private const string ServiceDisplayName = "Log2Postgres Log Monitoring Service";

        public static bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static ServiceControllerStatus GetServiceStatus()
        {
            try
            {
                ServiceController sc = new ServiceController(ServiceName);
                return sc.Status;
            }
            catch (InvalidOperationException) // Service not installed
            {
                return ServiceControllerStatus.Stopped; // Treat as stopped if not installed for simplicity in UI logic initially
                                                        // A more distinct status might be better: e.g., an enum with NotInstalled
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting service status: {ex.Message}");
                // Log this error properly in a real scenario
                return ServiceControllerStatus.Stopped; // Default to stopped on other errors
            }
        }

        public static bool IsServiceInstalled()
        {
            return ServiceController.GetServices().Any(s => s.ServiceName == ServiceName);
        }

        public static void StartWindowsService()
        {
            if (!IsServiceInstalled()) 
            {
                System.Windows.MessageBox.Show("Service is not installed.", "Service Control", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            string command = $"start \"{ServiceName}\"";
            Console.WriteLine($"Attempting to start service with command: sc.exe {command}");
            try
            {
                // First, check current status without elevation to avoid unnecessary UAC if already running/pending
                ServiceController sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                {
                    System.Windows.MessageBox.Show($"Service is already {sc.Status}.", "Service Control", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit(); // Wait for sc.exe to finish

                    // After sc.exe, re-check status with ServiceController to confirm
                    // Give it a moment for SCM to update status
                    System.Threading.Thread.Sleep(1000); 
                    sc.Refresh(); 

                    if (process.ExitCode == 0 && (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending))
                    {
                        Console.WriteLine("Service started successfully (or is starting).");
                    }
                    else if (process.ExitCode != 0)
                    {
                        System.Windows.MessageBox.Show($"Failed to start service. SC.exe (elevated) exited with code: {process.ExitCode}. Current status: {sc.Status}", "Service Control Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else // sc.exe exited 0 but service not running/starting
                    {
                         System.Windows.MessageBox.Show($"SC.exe command to start service succeeded, but service is not in Running or StartPending state. Current status: {sc.Status}. It might have stopped quickly or failed to initialize.", "Service Control Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception winEx) when (winEx.NativeErrorCode == 1223) // UAC denied
            {
                System.Windows.MessageBox.Show("Service start operation was cancelled (UAC prompt denied or closed).", "Service Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error starting service: {ex.Message}", "Service Control Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void StopWindowsService()
        {
            if (!IsServiceInstalled()) 
            {
                System.Windows.MessageBox.Show("Service is not installed.", "Service Control", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string command = $"stop \"{ServiceName}\"";
            Console.WriteLine($"Attempting to stop service with command: sc.exe {command}");
            try
            {
                // First, check current status without elevation to avoid unnecessary UAC if already stopped/pending
                ServiceController sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending)
                {
                    System.Windows.MessageBox.Show($"Service is already {sc.Status}.", "Service Control", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit(); // Wait for sc.exe to finish

                    // After sc.exe, re-check status with ServiceController to confirm
                    System.Threading.Thread.Sleep(1000); 
                    sc.Refresh(); 

                    if (process.ExitCode == 0 && (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending))
                    {
                        Console.WriteLine("Service stopped successfully (or is stopping).");
                    }
                    else if (process.ExitCode != 0)
                    {
                        System.Windows.MessageBox.Show($"Failed to stop service. SC.exe (elevated) exited with code: {process.ExitCode}. Current status: {sc.Status}", "Service Control Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                     else // sc.exe exited 0 but service not stopped/stopping
                    {
                         System.Windows.MessageBox.Show($"SC.exe command to stop service succeeded, but service is not in Stopped or StopPending state. Current status: {sc.Status}. It might have restarted or encountered an issue.", "Service Control Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception winEx) when (winEx.NativeErrorCode == 1223) // UAC denied
            {
                System.Windows.MessageBox.Show("Service stop operation was cancelled (UAC prompt denied or closed).", "Service Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error stopping service: {ex.Message}", "Service Control Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void InstallServiceViaSC()
        {
            string? exePath;
            // Correctly determine the path whether running as deployed .exe or via `dotnet run`
            using (var processModule = Process.GetCurrentProcess().MainModule)
            {
                if (processModule != null)
                {
                    exePath = processModule.FileName;
                    if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || 
                        exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
                    {
                        string? entryAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                        if (!string.IsNullOrEmpty(entryAssemblyLocation) && entryAssemblyLocation.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            // For framework-dependent, binPath is "dotnet.exe pathTo.dll --windows-service"
                            // Ensure dotnet.exe path is quoted if it contains spaces, and DLL path is quoted.
                            exePath = $"\"{Environment.ProcessPath}\" \"{entryAssemblyLocation}\""; 
                        }
                        else
                        {
                            System.Windows.MessageBox.Show("Error: Could not determine application DLL path for service installation.", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    // For self-contained, exePath is already the app .exe, just ensure it's quoted if needed.
                }
                else
                {
                    System.Windows.MessageBox.Show("Error: Could not determine executable path for service installation.", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            
            // If exePath contains only the DLL path (because GetEntryAssembly().Location was used directly),
            // it still needs to be prefixed by dotnet.exe for framework-dependent scenarios.
            // The logic above tries to ensure `exePath` is either the self-contained .exe or "dotnet.exe path/to.dll"

            // Ensure the base executable path (self-contained .exe or dotnet.exe) is quoted for sc.exe
            string binPathArg;
            if (exePath.Contains(".dll\"")) // Indicates it's already in the "dotnet.exe" "path.dll" format
            {
                binPathArg = $"{exePath} --windows-service"; // --windows-service added after the DLL
            }
            else // Assumed to be a self-contained .exe
            {
                binPathArg = $"\"{exePath}\" --windows-service";
            }

            string command = $"create \"{ServiceName}\" binPath= \"{binPathArg}\" DisplayName= \"{ServiceDisplayName}\" start= auto obj= \"NT AUTHORITY\\NetworkService\"";

            Console.WriteLine($"Attempting to install service with command: sc.exe {command}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = command;
                    process.StartInfo.UseShellExecute = true; // Changed for elevation
                    process.StartInfo.Verb = "runas";         // Request elevation
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service installed successfully.");
                    }
                    else
                    {
                        // We don't have detailed error output when UseShellExecute = true
                        System.Windows.MessageBox.Show($"Service installation failed. SC.exe (elevated) exited with code: {process.ExitCode}.\nPossible reasons: Service already exists, invalid service parameters, or issues with the 'NetworkService' account permissions.", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception winEx) when (winEx.NativeErrorCode == 1223) // 1223: ERROR_CANCELLED (UAC prompt denied)
            {
                System.Windows.MessageBox.Show("Service installation was cancelled (UAC prompt denied or closed).", "Installation Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Exception during service installation: {ex.Message}", "Installation Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void UninstallServiceViaSC()
        {
            // Attempt to stop the service first (without elevation, as it might not be needed)
            string stopCommand = $"stop \"{ServiceName}\"";
            Console.WriteLine($"Attempting to stop service with command: sc.exe {stopCommand}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = stopCommand;
                    process.StartInfo.UseShellExecute = false; // Keep false for stop, to get output if needed
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string stopOutput = process.StandardOutput.ReadToEnd();
                    string stopError = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0 || process.ExitCode == 1062 || process.ExitCode == 1060) // 1062: not started, 1060: DNE
                    {
                        Console.WriteLine("Service stop command executed (or service was not running/not found).");
                    }
                    else
                    {
                        Console.WriteLine($"Service stop command failed. SC.exe Output: {stopOutput}, SC.exe Error: {stopError}");
                        // Optionally, ask user if they want to proceed with uninstallation despite stop failure
                        // System.Windows.MessageBox.Show($"Failed to stop the service (Exit Code: {process.ExitCode}). You might need to stop it manually. Do you want to proceed with uninstallation?", "Stop Service Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during service stop attempt: {ex.Message}");
            }

            string deleteCommand = $"delete \"{ServiceName}\"";
            Console.WriteLine($"Attempting to uninstall service with command: sc.exe {deleteCommand}");
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "sc.exe";
                    process.StartInfo.Arguments = deleteCommand;
                    process.StartInfo.UseShellExecute = true; // Changed for elevation
                    process.StartInfo.Verb = "runas";         // Request elevation
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine("Service uninstalled successfully.");
                    }
                    // 1060: The specified service does not exist as an installed service.
                    else if (process.ExitCode == 1060) // This might not be reliably returned if runas fails earlier.
                    {
                        Console.WriteLine("Service was not installed or already uninstalled.");
                    }
                    else
                    {
                        System.Windows.MessageBox.Show($"Service uninstallation failed. SC.exe (elevated) exited with code: {process.ExitCode}.\nThis can happen if the service is still running and could not be stopped, or other system issues.", "Uninstallation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception winEx) when (winEx.NativeErrorCode == 1223) // 1223: ERROR_CANCELLED (UAC prompt denied)
            {
                System.Windows.MessageBox.Show("Service uninstallation was cancelled (UAC prompt denied or closed).", "Uninstallation Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Exception during service uninstallation: {ex.Message}", "Uninstallation Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
} 
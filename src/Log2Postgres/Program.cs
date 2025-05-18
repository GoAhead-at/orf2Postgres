using System;
using System.IO;
using System.Windows;
using System.Linq;

namespace Log2Postgres
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // Initialize position file if it doesn't exist
                string positionsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "positions.json");
                if (!File.Exists(positionsFilePath))
                {
                    // Create a minimal valid JSON array for positions
                    File.WriteAllText(positionsFilePath, "[]");
                    Console.WriteLine($"Created new positions file at {positionsFilePath}");
                }
                
                // Normal application start using the WPF application class
                var app = new App();
                app.InitializeComponent(); // Ensure App.xaml resources are loaded
                app.Run();
            }
            catch (Exception ex)
            {
                // Log the error
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_crash.txt"),
                    $"Application crashed at startup: {ex.Message}\n\nStack trace:\n{ex.StackTrace}");
                
                // Show an error message if possible
                try
                {
                    System.Windows.MessageBox.Show(
                        $"Fatal error: {ex.Message}\n\nCheck startup_crash.txt for details.",
                        "Startup Crash",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // If showing a message box fails, at least we have the log file
                }
            }
        }
    }
} 
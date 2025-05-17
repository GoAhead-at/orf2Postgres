using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32;
using System.Security;
using System.Windows.Forms;

namespace Log2Postgres;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly IConfiguration _configuration;
    private bool _isProcessing = false;
    private int _linesProcessed = 0;

    public MainWindow()
    {
        InitializeComponent();
        
        // Load configuration
        _configuration = ((App)System.Windows.Application.Current).GetConfiguration();
        
        // Register event handlers
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        UpdateStatusBar("Application started successfully");
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_isProcessing)
        {
            var result = System.Windows.MessageBox.Show(
                "Processing is still active. Are you sure you want to exit?",
                "Confirm Exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            // Stop processing before exit
            StopProcessing();
        }
    }

    private void LoadSettings()
    {
        try
        {
            // Database settings
            DbHost.Text = _configuration["DatabaseSettings:Host"] ?? string.Empty;
            DbPort.Text = _configuration["DatabaseSettings:Port"] ?? "5432";
            DbUsername.Text = _configuration["DatabaseSettings:Username"] ?? string.Empty;
            DbName.Text = _configuration["DatabaseSettings:Database"] ?? "postgres";
            DbSchema.Text = _configuration["DatabaseSettings:Schema"] ?? "public";
            DbTable.Text = _configuration["DatabaseSettings:Table"] ?? "orf_logs";
            DbTimeout.Text = _configuration["DatabaseSettings:ConnectionTimeout"] ?? "30";

            // Log file settings
            LogDirectory.Text = _configuration["LogMonitorSettings:BaseDirectory"] ?? string.Empty;
            LogFilePattern.Text = _configuration["LogMonitorSettings:LogFilePattern"] ?? "orfee-{Date:yyyy-MM-dd}.log";
            PollingInterval.Text = _configuration["LogMonitorSettings:PollingIntervalSeconds"] ?? "5";

            LogMessage("Settings loaded from configuration");
            UpdateServiceStatus(false);
        }
        catch (Exception ex)
        {
            LogError($"Error loading settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            // Build new configuration
            var configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var configJson = File.ReadAllText(configFile);
            dynamic configObj = Newtonsoft.Json.JsonConvert.DeserializeObject(configJson);

            // Update database settings
            configObj["DatabaseSettings"]["Host"] = DbHost.Text;
            configObj["DatabaseSettings"]["Port"] = DbPort.Text;
            configObj["DatabaseSettings"]["Username"] = DbUsername.Text;
            configObj["DatabaseSettings"]["Password"] = SecurePassword(); // Handle password securely
            configObj["DatabaseSettings"]["Database"] = DbName.Text;
            configObj["DatabaseSettings"]["Schema"] = DbSchema.Text;
            configObj["DatabaseSettings"]["Table"] = DbTable.Text;
            configObj["DatabaseSettings"]["ConnectionTimeout"] = DbTimeout.Text;

            // Update log file settings
            configObj["LogMonitorSettings"]["BaseDirectory"] = LogDirectory.Text;
            configObj["LogMonitorSettings"]["LogFilePattern"] = LogFilePattern.Text;
            configObj["LogMonitorSettings"]["PollingIntervalSeconds"] = PollingInterval.Text;

            // Save to file
            string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(configObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(configFile, updatedJson);

            LogMessage("Settings saved successfully");
            UpdateStatusBar("Configuration saved");
        }
        catch (Exception ex)
        {
            LogError($"Error saving settings: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Failed to save settings: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string SecurePassword()
    {
        // In a real application, this would use secure methods to encrypt the password
        // For now, we'll return an empty placeholder
        return string.Empty; // We'll implement secure password handling later
    }

    #region Event Handlers

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select log file directory"
        };
        
        if (!string.IsNullOrEmpty(LogDirectory.Text))
        {
            dialog.SelectedPath = LogDirectory.Text;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            LogDirectory.Text = dialog.SelectedPath;
            LogMessage($"Selected directory: {dialog.SelectedPath}");
        }
    }

    private void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
    {
        TestDatabaseConnection();
    }

    private void VerifyTableBtn_Click(object sender, RoutedEventArgs e)
    {
        VerifyDatabaseTable();
    }

    private void ValidatePathBtn_Click(object sender, RoutedEventArgs e)
    {
        ValidateLogPath();
    }

    private void InstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "Service installation will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UninstallServiceBtn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            "Service uninstallation will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        StartProcessing();
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        StopProcessing();
    }

    private void SaveConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void ResetConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "This will reset all settings to default values. Continue?",
            "Confirm Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            ResetSettings();
        }
    }

    private void LogFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        // Filter logs based on selected categories
        // Implementation will be added later
    }

    private void ClearLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        LogMessage("Logs cleared");
    }

    private void ViewSampleDataBtn_Click(object sender, RoutedEventArgs e)
    {
        ViewSampleData();
    }

    #endregion

    #region Helper Methods

    private async void StartProcessing()
    {
        if (_isProcessing)
            return;

        // Validate settings before starting
        if (string.IsNullOrWhiteSpace(LogDirectory.Text))
        {
            System.Windows.MessageBox.Show(
                "Please specify a log directory path before starting.",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            _isProcessing = true;
            UpdateServiceStatus(true);
            
            LogMessage("Processing started");
            UpdateStatusBar("Processing started");
            
            // Simulate processing for now
            // This will be replaced with actual file watching implementation
            await Task.Run(() => 
            {
                // Placeholder for actual processing logic
                for (int i = 0; i < 10; i++)
                {
                    if (!_isProcessing)
                        break;
                        
                    System.Threading.Thread.Sleep(1000);
                    Dispatcher.Invoke(() => 
                    {
                        _linesProcessed += 5;
                        LinesProcessedText.Text = _linesProcessed.ToString();
                        CurrentFileText.Text = $"orfee-{DateTime.Now:yyyy-MM-dd}.log";
                        CurrentPositionText.Text = (_linesProcessed * 100).ToString();
                        ProcessingStatusText.Text = "Running";
                        LogMessage($"Processed 5 more lines. Total: {_linesProcessed}");
                    });
                }
            });
        }
        catch (Exception ex)
        {
            LogError($"Error during processing: {ex.Message}");
            _isProcessing = false;
            UpdateServiceStatus(false);
        }
    }

    private void StopProcessing()
    {
        if (!_isProcessing)
            return;

        _isProcessing = false;
        UpdateServiceStatus(false);
        LogMessage("Processing stopped");
        UpdateStatusBar("Processing stopped");
    }

    private void UpdateServiceStatus(bool isRunning)
    {
        if (isRunning)
        {
            ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Green);
            ServiceStatusText.Text = "Running";
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            ProcessingStatusText.Text = "Running";
        }
        else
        {
            ServiceStatusIndicator.Background = new SolidColorBrush(Colors.Red);
            ServiceStatusText.Text = "Stopped";
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            ProcessingStatusText.Text = "Idle";
        }
    }

    private void LogMessage(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        LogTextBox.AppendText($"[{timestamp}] INFO: {message}\n");
        LogTextBox.ScrollToEnd();
    }

    private void LogWarning(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        LogTextBox.AppendText($"[{timestamp}] WARNING: {message}\n");
        LogTextBox.ScrollToEnd();
    }

    private void LogError(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        LogTextBox.AppendText($"[{timestamp}] ERROR: {message}\n");
        LogTextBox.ScrollToEnd();
        LastErrorText.Text = message;
    }

    private void UpdateStatusBar(string message)
    {
        StatusBarText.Text = message;
        LastUpdatedText.Text = $"Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private void TestDatabaseConnection()
    {
        // TODO: Implement actual database connection test
        LogMessage("Testing database connection...");
        // Placeholder implementation
        System.Windows.MessageBox.Show(
            "Database connection test will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void VerifyDatabaseTable()
    {
        // TODO: Implement actual table verification
        LogMessage("Verifying database table...");
        // Placeholder implementation
        System.Windows.MessageBox.Show(
            "Database table verification will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ValidateLogPath()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogDirectory.Text))
            {
                LogWarning("Log directory path is empty");
                return;
            }

            if (!Directory.Exists(LogDirectory.Text))
            {
                var result = System.Windows.MessageBox.Show(
                    $"Directory '{LogDirectory.Text}' does not exist. Create it?",
                    "Directory Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Directory.CreateDirectory(LogDirectory.Text);
                    LogMessage($"Created directory: {LogDirectory.Text}");
                }
                else
                {
                    LogWarning("Directory creation was cancelled");
                    return;
                }
            }
            else
            {
                LogMessage($"Directory exists: {LogDirectory.Text}");
            }

            // Check if we can write to the directory
            string testFile = Path.Combine(LogDirectory.Text, "test_write_access.tmp");
            File.WriteAllText(testFile, "Test");
            File.Delete(testFile);
            LogMessage("Directory is writable");

            System.Windows.MessageBox.Show(
                "Log directory validation successful!",
                "Validation Successful",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogError($"Error validating log path: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Error validating log path: {ex.Message}",
                "Validation Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ResetSettings()
    {
        // Reset to default values
        DbHost.Text = "localhost";
        DbPort.Text = "5432";
        DbUsername.Text = "";
        DbPassword.Password = "";
        DbName.Text = "postgres";
        DbSchema.Text = "public";
        DbTable.Text = "orf_logs";
        DbTimeout.Text = "30";
        
        LogDirectory.Text = "";
        LogFilePattern.Text = "orfee-{Date:yyyy-MM-dd}.log";
        PollingInterval.Text = "5";
        
        LogMessage("Settings reset to defaults");
    }

    private void ViewSampleData()
    {
        // TODO: Implement actual sample data viewing
        LogMessage("Fetching sample data...");
        // Placeholder implementation
        System.Windows.MessageBox.Show(
            "Sample data viewing will be implemented in a future update.",
            "Not Implemented",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    #endregion
}
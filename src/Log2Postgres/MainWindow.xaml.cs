using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Log2Postgres.UI.ViewModels;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Forms;

namespace Log2Postgres;

/// <summary>
/// Interaction logic for MainWindow.xaml - MVVM implementation
/// </summary>
public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly ILogger<MainWindow> _logger;
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            var app = (App)System.Windows.Application.Current;
            _logger = app.GetService<ILoggerFactory>().CreateLogger<MainWindow>();
            _viewModel = app.GetService<MainWindowViewModel>();

            // Set the DataContext to enable binding
            DataContext = _viewModel;

            _logger.LogInformation("MainWindow initialized with MVVM pattern");

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }
        catch (Exception ex)
        {
            var tempLogger = _logger; // Fallback logger handling
            tempLogger?.LogCritical(ex, "FATAL: MainWindow constructor failed.");
            System.Windows.MessageBox.Show($"Critical error during MainWindow initialization: {ex.Message}\n\nApplication will exit.", 
                "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Shutdown(1);
            }
            else
            {
                Environment.Exit(1);
            }
        }
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("MainWindow loaded, initializing ViewModel");
            await _viewModel.InitializeAsync();
            
            // Set up password binding (PasswordBox doesn't support direct binding for security)
            DatabasePassword.Password = _viewModel.DatabasePassword;
            DatabasePassword.PasswordChanged += (s, e) => _viewModel.DatabasePassword = DatabasePassword.Password;
            
            _logger.LogInformation("ViewModel initialization completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during MainWindow loading");
            System.Windows.MessageBox.Show($"Error during window initialization: {ex.Message}", 
                "Initialization Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _logger.LogInformation("MainWindow closing, cleaning up ViewModel");
            
            // Check if there are unsaved changes
            if (_viewModel.HasUnsavedChanges)
            {
                var result = System.Windows.MessageBox.Show(
                    "You have unsaved configuration changes. Do you want to save them before closing?",
                    "Unsaved Changes", 
                    MessageBoxButton.YesNoCancel, 
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                
                if (result == MessageBoxResult.Yes)
                {
                    // Save configuration before closing
                    if (_viewModel.SaveConfigurationCommand.CanExecute(null))
                    {
                        _viewModel.SaveConfigurationCommand.Execute(null);
                        // Wait a moment for the async operation to complete
                        await Task.Delay(100);
                    }
                }
            }

            // Stop processing if active
            if (_viewModel.IsProcessing)
            {
                _logger.LogInformation("Stopping processing before window close");
                if (_viewModel.StopProcessingCommand.CanExecute(null))
                {
                    _viewModel.StopProcessingCommand.Execute(null);
                    // Wait a moment for the async operation to complete
                    await Task.Delay(100);
                }
            }

            _logger.LogInformation("MainWindow cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during MainWindow closing");
            // Don't prevent closing due to cleanup errors
        }
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        using (var dialog = new FolderBrowserDialog())
        {
            System.Windows.Forms.DialogResult result = dialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                _viewModel.LogDirectory = dialog.SelectedPath;
                _logger.LogInformation($"Log directory selected: {dialog.SelectedPath}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _logger?.LogInformation("MainWindow DisposeAsync called");
            _viewModel?.Dispose();
            _logger?.LogInformation("MainWindow disposed successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during MainWindow disposal");
        }
    }
}

/// <summary>
/// Converter to invert boolean values for UI binding
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return !boolValue;
        return false;
    }
}

/// <summary>
/// Converter to convert boolean database connection status to display text
/// </summary>
public class BooleanToStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? "Connected" : "Disconnected";
        return "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
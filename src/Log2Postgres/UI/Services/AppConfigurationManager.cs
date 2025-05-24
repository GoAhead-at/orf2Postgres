using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;
using Log2Postgres.Core.Services;

namespace Log2Postgres.UI.Services
{
    /// <summary>
    /// Manages application configuration loading and saving
    /// </summary>
    public interface IAppConfigurationManager
    {
        Task LoadSettingsAsync();
        Task SaveSettingsAsync(DatabaseSettings dbSettings, LogMonitorSettings logSettings);
        Task ResetToDefaultsAsync();
        void MarkAsChanged();
        
        event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;
        event EventHandler UnsavedChangesChanged;
        
        bool HasUnsavedChanges { get; }
        DatabaseSettings DatabaseSettings { get; }
        LogMonitorSettings LogSettings { get; }
    }

    public class AppConfigurationManager : IAppConfigurationManager
    {
        private readonly ILogger<AppConfigurationManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly PasswordEncryption _passwordEncryption;
        private readonly string _configFilePath;
        
        private DatabaseSettings _databaseSettings = new();
        private LogMonitorSettings _logSettings = new();
        private bool _hasUnsavedChanges;

        public bool HasUnsavedChanges 
        { 
            get => _hasUnsavedChanges;
            private set
            {
                if (_hasUnsavedChanges != value)
                {
                    _hasUnsavedChanges = value;
                    UnsavedChangesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public DatabaseSettings DatabaseSettings => _databaseSettings;
        public LogMonitorSettings LogSettings => _logSettings;

        public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;
        public event EventHandler? UnsavedChangesChanged;

        public AppConfigurationManager(
            ILogger<AppConfigurationManager> logger,
            IConfiguration configuration,
            PasswordEncryption passwordEncryption)
        {
            _logger = logger;
            _configuration = configuration;
            _passwordEncryption = passwordEncryption;
            _configFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }

        public async Task LoadSettingsAsync()
        {
            try
            {
                _logger.LogInformation("Loading configuration settings");

                // Load database settings
                var dbSection = _configuration.GetSection("DatabaseSettings");
                _databaseSettings = new DatabaseSettings
                {
                    Host = dbSection["Host"] ?? "localhost",
                    Port = dbSection["Port"] ?? "5432",
                    Username = dbSection["Username"] ?? "",
                    Password = await DecryptPasswordAsync(dbSection["Password"] ?? ""),
                    Database = dbSection["Database"] ?? "postgres",
                    Schema = dbSection["Schema"] ?? "public",
                    Table = dbSection["Table"] ?? "orf_logs",
                    ConnectionTimeout = int.TryParse(dbSection["ConnectionTimeout"], out int timeout) ? timeout : 30
                };

                // Load log monitor settings
                var logSection = _configuration.GetSection("LogMonitorSettings");
                _logSettings = new LogMonitorSettings
                {
                    BaseDirectory = logSection["BaseDirectory"] ?? "",
                    LogFilePattern = logSection["LogFilePattern"] ?? "orfee-{Date:yyyy-MM-dd}.log",
                    PollingIntervalSeconds = int.TryParse(logSection["PollingIntervalSeconds"], out int interval) ? interval : 5
                };

                HasUnsavedChanges = false;
                _logger.LogInformation("Configuration settings loaded successfully");
                
                ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs 
                { 
                    DatabaseSettings = _databaseSettings,
                    LogSettings = _logSettings 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading configuration settings");
                await CreateDefaultConfigurationAsync();
            }
        }

        public async Task SaveSettingsAsync(DatabaseSettings dbSettings, LogMonitorSettings logSettings)
        {
            try
            {
                _logger.LogInformation("Saving configuration settings");

                if (!File.Exists(_configFilePath))
                {
                    await CreateDefaultConfigurationAsync();
                }

                var jsonText = await File.ReadAllTextAsync(_configFilePath);
                var jsonObject = JObject.Parse(jsonText);

                // Update database settings
                var dbSection = jsonObject["DatabaseSettings"] as JObject ?? new JObject();
                dbSection["Host"] = dbSettings.Host;
                dbSection["Port"] = dbSettings.Port;
                dbSection["Username"] = dbSettings.Username;
                dbSection["Password"] = await EncryptPasswordAsync(dbSettings.Password);
                dbSection["Database"] = dbSettings.Database;
                dbSection["Schema"] = dbSettings.Schema;
                dbSection["Table"] = dbSettings.Table;
                dbSection["ConnectionTimeout"] = dbSettings.ConnectionTimeout;
                jsonObject["DatabaseSettings"] = dbSection;

                // Update log monitor settings
                var logSection = jsonObject["LogMonitorSettings"] as JObject ?? new JObject();
                logSection["BaseDirectory"] = logSettings.BaseDirectory;
                logSection["LogFilePattern"] = logSettings.LogFilePattern;
                logSection["PollingIntervalSeconds"] = logSettings.PollingIntervalSeconds;
                jsonObject["LogMonitorSettings"] = logSection;

                // Write to temp file first, then replace
                var tempFilePath = _configFilePath + ".tmp";
                await File.WriteAllTextAsync(tempFilePath, jsonObject.ToString());
                File.Move(tempFilePath, _configFilePath, true);

                // Update internal state
                _databaseSettings = dbSettings;
                _logSettings = logSettings;
                HasUnsavedChanges = false;

                _logger.LogInformation("Configuration settings saved successfully");
                
                ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs 
                { 
                    DatabaseSettings = _databaseSettings,
                    LogSettings = _logSettings 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving configuration settings");
                throw;
            }
        }

        public async Task ResetToDefaultsAsync()
        {
            try
            {
                _logger.LogInformation("Resetting configuration to defaults");
                await CreateDefaultConfigurationAsync();
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting configuration to defaults");
                throw;
            }
        }

        public void MarkAsChanged()
        {
            HasUnsavedChanges = true;
        }

        private async Task CreateDefaultConfigurationAsync()
        {
            const string defaultConfig = @"{
  ""Serilog"": {
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

            await File.WriteAllTextAsync(_configFilePath, defaultConfig);
            _logger.LogInformation("Default configuration file created");
        }

        private async Task<string> EncryptPasswordAsync(string password)
        {
            if (string.IsNullOrEmpty(password))
                return "";

            try
            {
                return await Task.Run(() => _passwordEncryption.EncryptPassword(password));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error encrypting password, saving as plain text");
                return password;
            }
        }

        private async Task<string> DecryptPasswordAsync(string encryptedPassword)
        {
            if (string.IsNullOrEmpty(encryptedPassword))
                return "";

            try
            {
                return await Task.Run(() => _passwordEncryption.DecryptPassword(encryptedPassword));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error decrypting password, treating as plain text");
                return encryptedPassword;
            }
        }
    }

    public class ConfigurationChangedEventArgs : EventArgs
    {
        public DatabaseSettings DatabaseSettings { get; set; } = new();
        public LogMonitorSettings LogSettings { get; set; } = new();
    }
} 
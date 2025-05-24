using Log2Postgres.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Service for handling PostgreSQL database operations
    /// </summary>
    public class PostgresService
    {
        private readonly ILogger<PostgresService> _logger;
        private readonly IOptionsMonitor<DatabaseSettings> _settingsMonitor;

        // Constructor for DI using IOptionsMonitor (preferred for general use)
        public PostgresService(ILogger<PostgresService> logger, IOptionsMonitor<DatabaseSettings> settingsMonitor)
        {
            _logger = logger;
            _settingsMonitor = settingsMonitor;
            
            _logger.LogDebug("PostgresService constructor initialized with IOptionsMonitor");
        }

        /// <summary>
        /// Builds the connection string from current settings (respecting which constructor was used)
        /// </summary>
        private string GetCurrentConnectionString()
        {
            DatabaseSettings currentSettings = _settingsMonitor.CurrentValue;
            _logger.LogInformation("GetCurrentConnectionString: Using DatabaseSettings - Host: {Host}, Port: {Port}, User: {Username}, DB: {Database}, Password IsNullOrEmpty: {PwdEmpty}, Timeout: {Timeout}", 
                currentSettings.Host, 
                currentSettings.Port, 
                currentSettings.Username, 
                currentSettings.Database,
                string.IsNullOrEmpty(currentSettings.Password),
                currentSettings.ConnectionTimeout);
                
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = currentSettings.Host,
                Port = int.Parse(currentSettings.Port), // Consider TryParse for robustness
                Database = currentSettings.Database,
                Username = currentSettings.Username,
                Password = currentSettings.Password,
                CommandTimeout = currentSettings.ConnectionTimeout,
                Timeout = currentSettings.ConnectionTimeout
            };

            return builder.ToString();
        }

        /// <summary>
        /// Tests the database connection
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();
            try
            {
                _logger.LogInformation("Testing database connection to {Host}:{Port}", currentSettings.Host, currentSettings.Port);
                _logger.LogDebug("Connection details - Database: {Database}, Username: {Username}, Timeout: {Timeout}s", 
                    currentSettings.Database, currentSettings.Username, currentSettings.ConnectionTimeout);
                
                using var connection = new NpgsqlConnection(connectionString);
                _logger.LogDebug("Opening database connection...");
                await connection.OpenAsync();
                _logger.LogInformation("Database connection test successful");
                _logger.LogDebug("Server version: {ServerVersion}", connection.ServerVersion);
                return true;
            }
            catch (Exception ex)
            {
                string errorMessage = ex.Message;
                
                // Provide more user-friendly messages for common database errors
                if (ex is Npgsql.PostgresException pgEx)
                {
                    if (pgEx.SqlState == "28P01") // Authentication error
                    {
                        errorMessage = "Authentication failed. Check your username and password.";
                        _logger.LogDebug("Authentication failed - check username and password configuration");
                    }
                    else if (pgEx.SqlState == "3D000") // Database does not exist
                    {
                        errorMessage = $"Database '{currentSettings.Database}' does not exist.";
                    }
                    else if (pgEx.SqlState == "08001" || pgEx.SqlState == "08006") // Connection error
                    {
                        errorMessage = $"Could not connect to database server at {currentSettings.Host}:{currentSettings.Port}.";
                    }
                }
                
                _logger.LogError(ex, "Database connection test failed: {Message}", errorMessage);

                string connectionStringToLog = connectionString;
                if (!string.IsNullOrEmpty(currentSettings.Password))
                {
                    connectionStringToLog = connectionString.Replace(currentSettings.Password, "********");
                }
                _logger.LogDebug("Connection string used (password redacted): {ConnectionString}", connectionStringToLog);
                return false;
            }
        }

        /// <summary>
        /// Creates the log table if it doesn't exist
        /// </summary>
        public async Task<bool> EnsureTableExistsAsync()
        {
            var (currentSettings, _) = GetSettingsAndConnectionString();
            try
            {
                _logger.LogInformation("Ensuring table {Schema}.{Table} exists", currentSettings.Schema, currentSettings.Table);
                
                // Check if table exists
                bool tableExists = await CheckTableExistsAsync();
                if (tableExists)
                {
                    _logger.LogInformation("Table {Schema}.{Table} already exists", currentSettings.Schema, currentSettings.Table);
                    return true;
                }

                // Create schema if needed
                await CreateSchemaIfNotExistsAsync();

                // Create the table
                return await CreateTableAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create table: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Checks if the log table exists
        /// </summary>
        private async Task<bool> CheckTableExistsAsync()
        {
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            
            string sql = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = @schema 
                    AND table_name = @table
                );";
            
            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("schema", currentSettings.Schema);
            cmd.Parameters.AddWithValue("table", currentSettings.Table);
            
            return (bool?)await cmd.ExecuteScalarAsync() ?? false;
        }

        /// <summary>
        /// Public method to check if the table exists
        /// </summary>
        public async Task<bool> TableExistsAsync()
        {
            return await CheckTableExistsAsync();
        }

        /// <summary>
        /// Creates the schema if it doesn't exist
        /// </summary>
        private async Task CreateSchemaIfNotExistsAsync()
        {
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            
            string sql = $"CREATE SCHEMA IF NOT EXISTS {currentSettings.Schema};"; // Use currentSettings
            
            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Created schema {Schema} if it didn't exist", currentSettings.Schema);
        }

        /// <summary>
        /// Creates the log table
        /// </summary>
        public async Task<bool> CreateTableAsync()
        {
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();
            try
            {
                // Create schema if needed
                await CreateSchemaIfNotExistsAsync(); // This will use current settings internally
                
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                string sql = $@"
                    CREATE TABLE {currentSettings.Schema}.{currentSettings.Table} (
                      message_id TEXT PRIMARY KEY,
                      event_source TEXT,
                      event_datetime TIMESTAMPTZ,
                      event_class TEXT,
                      event_severity TEXT,
                      event_action TEXT,
                      filtering_point TEXT,
                      ip TEXT,
                      sender TEXT,
                      recipients TEXT,
                      msg_subject TEXT,
                      msg_author TEXT,
                      remote_peer TEXT,
                      source_ip TEXT,
                      country TEXT,
                      event_msg TEXT,
                      filename TEXT,
                      processed_at TIMESTAMPTZ DEFAULT NOW()
                    );";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                await cmd.ExecuteNonQueryAsync();
                
                _logger.LogInformation("Table {Schema}.{Table} created successfully", currentSettings.Schema, currentSettings.Table);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create table: {Message}", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Validates if the table structure matches the expected schema
        /// </summary>
        public async Task<bool> ValidateTableStructureAsync()
        {
            try
            {
                string connectionString = GetCurrentConnectionString();
                var currentSettings = _settingsMonitor.CurrentValue;
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                // Query to get table columns
                string sql = @"
                    SELECT column_name, data_type
                    FROM information_schema.columns
                    WHERE table_schema = @schema
                    AND table_name = @table
                    ORDER BY ordinal_position;";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("schema", currentSettings.Schema);
                cmd.Parameters.AddWithValue("table", currentSettings.Table);
                
                using var reader = await cmd.ExecuteReaderAsync();
                
                // Expected columns
                var expectedColumns = new Dictionary<string, string>
                {
                    { "message_id", "text" },
                    { "event_source", "text" },
                    { "event_datetime", "timestamp with time zone" },
                    { "event_class", "text" },
                    { "event_severity", "text" },
                    { "event_action", "text" },
                    { "filtering_point", "text" },
                    { "ip", "text" },
                    { "sender", "text" },
                    { "recipients", "text" },
                    { "msg_subject", "text" },
                    { "msg_author", "text" },
                    { "remote_peer", "text" },
                    { "source_ip", "text" },
                    { "country", "text" },
                    { "event_msg", "text" },
                    { "filename", "text" },
                    { "processed_at", "timestamp with time zone" }
                };
                
                // Check if all columns exist with correct data types
                var actualColumns = new Dictionary<string, string>();
                
                while (await reader.ReadAsync())
                {
                    string columnName = reader.GetString(0);
                    string dataType = reader.GetString(1);
                    actualColumns[columnName.ToLower()] = dataType.ToLower();
                }
                
                // Validate primary key
                bool hasPrimaryKey = await CheckPrimaryKeyAsync();
                
                // Verify all expected columns exist with correct type
                foreach (var expected in expectedColumns)
                {
                    if (!actualColumns.TryGetValue(expected.Key, out string? actualType) ||
                        !actualType.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Column mismatch: Expected {ColumnName} as {ExpectedType}, got {ActualType}",
                            expected.Key, expected.Value, actualColumns.TryGetValue(expected.Key, out string? type) ? type : "missing");
                        return false;
                    }
                }
                
                // Check if there are any unexpected columns
                foreach (var actual in actualColumns)
                {
                    if (!expectedColumns.ContainsKey(actual.Key))
                    {
                        _logger.LogWarning("Unexpected column found: {ColumnName} with type {DataType}", 
                            actual.Key, actual.Value);
                        // Extra columns are allowed, but we log them
                    }
                }
                
                _logger.LogInformation("Table structure validation passed with {ExpectedCount} expected columns and {ActualCount} actual columns",
                    expectedColumns.Count, actualColumns.Count);
                
                return hasPrimaryKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating table structure: {Message}", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Check if the primary key is defined correctly
        /// </summary>
        private async Task<bool> CheckPrimaryKeyAsync()
        {
            try
            {
                string connectionString = GetCurrentConnectionString();
                var currentSettings = _settingsMonitor.CurrentValue;
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                string sql = @"
                    SELECT a.attname as column_name
                    FROM pg_index i
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                    WHERE i.indrelid = (@schema || '.' || @table)::regclass
                    AND i.indisprimary;";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("schema", currentSettings.Schema);
                cmd.Parameters.AddWithValue("table", currentSettings.Table);
                
                using var reader = await cmd.ExecuteReaderAsync();
                
                // Check if message_id is the primary key
                bool foundPrimaryKey = false;
                while (await reader.ReadAsync())
                {
                    string columnName = reader.GetString(0);
                    if (columnName.Equals("message_id", StringComparison.OrdinalIgnoreCase))
                    {
                        foundPrimaryKey = true;
                        break;
                    }
                }
                
                if (!foundPrimaryKey)
                {
                    _logger.LogWarning("Primary key on message_id column not found");
                }
                
                return foundPrimaryKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking primary key: {Message}", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Drops the table if it exists
        /// </summary>
        public async Task<bool> DropTableAsync()
        {
            try
            {
                var (currentSettings, connectionString) = GetSettingsAndConnectionString();
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                string sql = $"DROP TABLE IF EXISTS {currentSettings.Schema}.{currentSettings.Table};";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                await cmd.ExecuteNonQueryAsync();
                
                _logger.LogInformation("Table {Schema}.{Table} dropped successfully", currentSettings.Schema, currentSettings.Table);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to drop table: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Saves a batch of log entries to the database
        /// </summary>
        public async Task<int> SaveEntriesAsync(IEnumerable<OrfLogEntry> entries, CancellationToken cancellationToken)
        {
            int savedCount = 0;
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();

            _logger.LogDebug("Attempting to save {Count} entries to {Schema}.{Table}", 
                entries.Count(), currentSettings.Schema, currentSettings.Table);
            
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                _logger.LogDebug("Database connection opened for saving entries.");

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await using var cmd = new NpgsqlCommand($@"
                        INSERT INTO {currentSettings.Schema}.{currentSettings.Table} 
                        (message_id, event_source, event_datetime, event_class, 
                        event_severity, event_action, filtering_point, ip, 
                        sender, recipients, msg_subject, msg_author, 
                        remote_peer, source_ip, country, event_msg, filename, processed_at)
                        VALUES (@message_id, @event_source, @event_datetime, @event_class,
                        @event_severity, @event_action, @filtering_point, @ip,
                        @sender, @recipients, @msg_subject, @msg_author,
                        @remote_peer, @source_ip, @country, @event_msg, @filename, @processed_at)
                        ON CONFLICT (message_id) DO NOTHING;", connection);

                        cmd.Parameters.AddWithValue("message_id", entry.MessageId);
                        cmd.Parameters.AddWithValue("event_source", entry.EventSource);
                        cmd.Parameters.AddWithValue("event_datetime", entry.EventDateTime);
                        cmd.Parameters.AddWithValue("event_class", entry.EventClass);
                        cmd.Parameters.AddWithValue("event_severity", entry.EventSeverity);
                        cmd.Parameters.AddWithValue("event_action", entry.EventAction);
                        cmd.Parameters.AddWithValue("filtering_point", entry.FilteringPoint);
                        cmd.Parameters.AddWithValue("ip", entry.IP);
                        cmd.Parameters.AddWithValue("sender", entry.Sender);
                        cmd.Parameters.AddWithValue("recipients", entry.Recipients);
                        cmd.Parameters.AddWithValue("msg_subject", entry.MsgSubject);
                        cmd.Parameters.AddWithValue("msg_author", entry.MsgAuthor);
                        cmd.Parameters.AddWithValue("remote_peer", entry.RemotePeer);
                        cmd.Parameters.AddWithValue("source_ip", entry.SourceIP);
                        cmd.Parameters.AddWithValue("country", entry.Country);
                        cmd.Parameters.AddWithValue("event_msg", entry.EventMsg);
                        cmd.Parameters.AddWithValue("filename", entry.SourceFilename);
                        cmd.Parameters.AddWithValue("processed_at", entry.ProcessedAt);

                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                        savedCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("SaveEntriesAsync was cancelled during saving entries.");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error saving entry: {Message}", ex.Message);
                    }
                }

                _logger.LogInformation("Saved {Count} entries to {Schema}.{Table}", savedCount, currentSettings.Schema, currentSettings.Table);
                return savedCount;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SaveEntriesAsync was cancelled before or during saving entries.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving entries to database: {Message}", ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Gets the count of rows in the log table
        /// </summary>
        public async Task<long> GetRowCountAsync()
        {
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {currentSettings.Schema}.{currentSettings.Table}", connection);
                object? result = await cmd.ExecuteScalarAsync();
                return result is DBNull ? 0 : Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting row count: {Message}", ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Gets sample data from the log table
        /// </summary>
        public async Task<List<OrfLogEntry>> GetSampleDataAsync(int limit = 100)
        {
            var entries = new List<OrfLogEntry>();
            var (currentSettings, connectionString) = GetSettingsAndConnectionString();

            try
            {
                _logger.LogDebug("Fetching up to {Limit} sample log entries from {Schema}.{Table}", limit, currentSettings.Schema, currentSettings.Table);
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                
                string sql = $@"
                    SELECT 
                        message_id, event_source, event_datetime, event_class, event_severity, 
                        event_action, filtering_point, ip, sender, recipients, 
                        msg_subject, msg_author, remote_peer, source_ip, country, 
                        event_msg, filename, processed_at
                    FROM {currentSettings.Schema}.{currentSettings.Table}
                    ORDER BY event_datetime DESC
                    LIMIT @limit;";
                
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("limit", limit);
                
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var entry = new OrfLogEntry
                    {
                        MessageId = reader.IsDBNull(0) ? string.Empty : (reader.GetString(0) ?? string.Empty),
                        EventSource = reader.IsDBNull(1) ? string.Empty : (reader.GetString(1) ?? string.Empty),
                        EventDateTime = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                        EventClass = reader.IsDBNull(3) ? string.Empty : (reader.GetString(3) ?? string.Empty),
                        EventSeverity = reader.IsDBNull(4) ? string.Empty : (reader.GetString(4) ?? string.Empty),
                        EventAction = reader.IsDBNull(5) ? string.Empty : (reader.GetString(5) ?? string.Empty),
                        FilteringPoint = reader.IsDBNull(6) ? string.Empty : (reader.GetString(6) ?? string.Empty),
                        IP = reader.IsDBNull(7) ? string.Empty : (reader.GetString(7) ?? string.Empty),
                        Sender = reader.IsDBNull(8) ? string.Empty : (reader.GetString(8) ?? string.Empty),
                        Recipients = reader.IsDBNull(9) ? string.Empty : (reader.GetString(9) ?? string.Empty),
                        MsgSubject = reader.IsDBNull(10) ? string.Empty : (reader.GetString(10) ?? string.Empty),
                        MsgAuthor = reader.IsDBNull(11) ? string.Empty : (reader.GetString(11) ?? string.Empty),
                        RemotePeer = reader.IsDBNull(12) ? string.Empty : (reader.GetString(12) ?? string.Empty),
                        SourceIP = reader.IsDBNull(13) ? string.Empty : (reader.GetString(13) ?? string.Empty),
                        Country = reader.IsDBNull(14) ? string.Empty : (reader.GetString(14) ?? string.Empty),
                        EventMsg = reader.IsDBNull(15) ? string.Empty : (reader.GetString(15) ?? string.Empty),
                        SourceFilename = reader.IsDBNull(16) ? string.Empty : (reader.GetString(16) ?? string.Empty),
                        ProcessedAt = reader.IsDBNull(17) ? DateTime.MinValue : reader.GetDateTime(17)
                    };
                    entries.Add(entry);
                }
                _logger.LogDebug("Fetched {Count} sample entries", entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sample data: {Message}", ex.Message);
            }
            return entries;
        }

        // Helper method to get current settings and connection string based on initialization path
        private (DatabaseSettings settings, string connectionString) GetSettingsAndConnectionString()
        {
            DatabaseSettings currentSettings = _settingsMonitor.CurrentValue;
            // More detailed logging for diagnosing configuration update issues
            _logger.LogInformation("GetSettingsAndConnectionString: Using DatabaseSettings - Host: {Host}, Port: {Port}, User: {Username}, DB: {Database}, Password IsNullOrEmpty: {PwdEmpty}, Timeout: {Timeout}", 
                currentSettings.Host, 
                currentSettings.Port, 
                currentSettings.Username, 
                currentSettings.Database,
                string.IsNullOrEmpty(currentSettings.Password),
                currentSettings.ConnectionTimeout);

            // Build connection string from these settings
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = currentSettings.Host,
                Port = int.TryParse(currentSettings.Port, out int port) ? port : 5432, // Use TryParse with default
                Database = currentSettings.Database,
                Username = currentSettings.Username,
                Password = currentSettings.Password,
                CommandTimeout = currentSettings.ConnectionTimeout,
                Timeout = currentSettings.ConnectionTimeout
            };
            string connectionString = builder.ToString();

            _logger.LogDebug("Using settings - host: {Host}, port: {Port}, db: {Db}, user: {User}. ConnString built.", 
                currentSettings.Host, currentSettings.Port, currentSettings.Database, currentSettings.Username);

            return (currentSettings, connectionString);
        }
    }

    /// <summary>
    /// PostgreSQL database settings
    /// </summary>
    public class DatabaseSettings
    {
        public string Host { get; set; } = "localhost";
        public string Port { get; set; } = "5432";
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Database { get; set; } = "postgres";
        public string Schema { get; set; } = "public";
        public string Table { get; set; } = "orf_logs";
        public int ConnectionTimeout { get; set; } = 30;
    }
} 
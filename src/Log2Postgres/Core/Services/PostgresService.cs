using Log2Postgres.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Service for handling PostgreSQL database operations
    /// </summary>
    public class PostgresService
    {
        private readonly ILogger<PostgresService> _logger;
        private readonly DatabaseSettings _settings;
        private string _connectionString;

        public PostgresService(ILogger<PostgresService> logger, IOptions<DatabaseSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;
            
            // DEBUG ONLY - Log the password received from DI container - REMOVE IN PRODUCTION
            _logger.LogWarning("DEBUG PURPOSE ONLY - PostgresService constructor received password: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", _settings.Password);
            
            BuildConnectionString();
        }

        /// <summary>
        /// Builds the connection string from settings
        /// </summary>
        private void BuildConnectionString()
        {
            // Build the connection string based on settings
            _logger.LogDebug("Building connection string with host: {Host}, port: {Port}, database: {Database}, username: {Username}", 
                _settings.Host, _settings.Port, _settings.Database, _settings.Username);
                
            // DEBUG ONLY - Log the password in cleartext - REMOVE IN PRODUCTION
            _logger.LogWarning("DEBUG PURPOSE ONLY - Password being used: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", _settings.Password);
                
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = _settings.Host,
                Port = int.Parse(_settings.Port),
                Database = _settings.Database,
                Username = _settings.Username,
                Password = _settings.Password,
                CommandTimeout = _settings.ConnectionTimeout,
                Timeout = _settings.ConnectionTimeout
            };

            _connectionString = builder.ToString();
            _logger.LogDebug("Connection string built successfully");
        }

        /// <summary>
        /// Tests the database connection
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                _logger.LogInformation("Testing database connection to {Host}:{Port}", _settings.Host, _settings.Port);
                _logger.LogDebug("Connection details - Database: {Database}, Username: {Username}, Timeout: {Timeout}s", 
                    _settings.Database, _settings.Username, _settings.ConnectionTimeout);
                
                // DEBUG ONLY - Log the password in cleartext - REMOVE IN PRODUCTION
                _logger.LogWarning("DEBUG PURPOSE ONLY - Password being used for connection test: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", _settings.Password);
                
                using var connection = new NpgsqlConnection(_connectionString);
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
                        // DEBUG ONLY - Log the password in cleartext - REMOVE IN PRODUCTION
                        _logger.LogWarning("DEBUG PURPOSE ONLY - Failed authentication with password: '{Password}' [SECURITY RISK - REMOVE THIS LOG]", _settings.Password);
                    }
                    else if (pgEx.SqlState == "3D000") // Database does not exist
                    {
                        errorMessage = $"Database '{_settings.Database}' does not exist.";
                    }
                    else if (pgEx.SqlState == "08001" || pgEx.SqlState == "08006") // Connection error
                    {
                        errorMessage = $"Could not connect to database server at {_settings.Host}:{_settings.Port}.";
                    }
                }
                
                _logger.LogError(ex, "Database connection test failed: {Message}", errorMessage);
                _logger.LogDebug("Connection string used: {ConnectionString}", 
                    _connectionString.Replace(_settings.Password, "********"));
                return false;
            }
        }

        /// <summary>
        /// Creates the log table if it doesn't exist
        /// </summary>
        public async Task<bool> EnsureTableExistsAsync()
        {
            try
            {
                _logger.LogInformation("Ensuring table {Schema}.{Table} exists", _settings.Schema, _settings.Table);
                
                // Check if table exists
                bool tableExists = await CheckTableExistsAsync();
                if (tableExists)
                {
                    _logger.LogInformation("Table {Schema}.{Table} already exists", _settings.Schema, _settings.Table);
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
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            string sql = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = @schema 
                    AND table_name = @table
                );";
            
            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("schema", _settings.Schema);
            cmd.Parameters.AddWithValue("table", _settings.Table);
            
            return (bool)await cmd.ExecuteScalarAsync();
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
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            string sql = $"CREATE SCHEMA IF NOT EXISTS {_settings.Schema};";
            
            using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Created schema {Schema} if it didn't exist", _settings.Schema);
        }

        /// <summary>
        /// Creates the log table
        /// </summary>
        public async Task<bool> CreateTableAsync()
        {
            try
            {
                // Create schema if needed
                await CreateSchemaIfNotExistsAsync();
                
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                string sql = $@"
                    CREATE TABLE {_settings.Schema}.{_settings.Table} (
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
                
                _logger.LogInformation("Table {Schema}.{Table} created successfully", _settings.Schema, _settings.Table);
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
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Query to get table columns
                string sql = @"
                    SELECT column_name, data_type
                    FROM information_schema.columns
                    WHERE table_schema = @schema
                    AND table_name = @table
                    ORDER BY ordinal_position;";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("schema", _settings.Schema);
                cmd.Parameters.AddWithValue("table", _settings.Table);
                
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
                    if (!actualColumns.TryGetValue(expected.Key, out string actualType) || 
                        !actualType.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Column mismatch: Expected {ColumnName} as {ExpectedType}, got {ActualType}",
                            expected.Key, expected.Value, actualColumns.TryGetValue(expected.Key, out string type) ? type : "missing");
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
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                string sql = @"
                    SELECT a.attname as column_name
                    FROM pg_index i
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                    WHERE i.indrelid = (@schema || '.' || @table)::regclass
                    AND i.indisprimary;";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("schema", _settings.Schema);
                cmd.Parameters.AddWithValue("table", _settings.Table);
                
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
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                string sql = $"DROP TABLE IF EXISTS {_settings.Schema}.{_settings.Table};";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                await cmd.ExecuteNonQueryAsync();
                
                _logger.LogInformation("Table {Schema}.{Table} dropped successfully", _settings.Schema, _settings.Table);
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
        public async Task<int> SaveEntriesAsync(IEnumerable<OrfLogEntry> entries)
        {
            var entriesList = entries.ToList();
            _logger.LogDebug("Attempting to save {Count} log entries to database", entriesList.Count);
            
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                _logger.LogDebug("Opening database connection for batch insert");
                await connection.OpenAsync();
                
                int count = 0;
                using (var transaction = await connection.BeginTransactionAsync())
                {
                    _logger.LogDebug("Transaction started for batch insert");
                    try
                    {
                        foreach (var entry in entriesList)
                        {
                            // Skip system messages (no message ID)
                            if (entry.IsSystemMessage)
                            {
                                _logger.LogTrace("Skipping system message entry");
                                continue;
                            }
                            
                            _logger.LogTrace("Processing entry with ID: {MessageId}, Date: {Date}", 
                                entry.MessageId, entry.EventDateTime);
                            
                            string sql = $@"
                                INSERT INTO {_settings.Schema}.{_settings.Table} (
                                    message_id, event_source, event_datetime, event_class, 
                                    event_severity, event_action, filtering_point, ip, 
                                    sender, recipients, msg_subject, msg_author, 
                                    remote_peer, source_ip, country, event_msg, filename, processed_at
                                ) VALUES (
                                    @message_id, @event_source, @event_datetime, @event_class,
                                    @event_severity, @event_action, @filtering_point, @ip,
                                    @sender, @recipients, @msg_subject, @msg_author,
                                    @remote_peer, @source_ip, @country, @event_msg, @filename, @processed_at
                                )
                                ON CONFLICT (message_id) DO NOTHING;";
                            
                            using var cmd = new NpgsqlCommand(sql, connection, transaction as NpgsqlTransaction);
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
                            
                            await cmd.ExecuteNonQueryAsync();
                            count++;
                            
                            if (count % 100 == 0)
                            {
                                _logger.LogDebug("Inserted {Count} entries so far", count);
                            }
                        }
                        
                        _logger.LogDebug("Committing transaction with {Count} entries", count);
                        await transaction.CommitAsync();
                        _logger.LogInformation("Saved {Count} log entries to database", count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Error during batch insert, rolling back transaction");
                        await transaction.RollbackAsync();
                        throw new Exception($"Error saving entries, transaction rolled back: {ex.Message}", ex);
                    }
                }
                
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving log entries to database: {Message}", ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Gets the count of rows in the log table
        /// </summary>
        public async Task<long> GetRowCountAsync()
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                string sql = $"SELECT COUNT(*) FROM {_settings.Schema}.{_settings.Table};";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                var result = await cmd.ExecuteScalarAsync();
                
                return Convert.ToInt64(result);
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
            
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                
                string sql = $@"
                    SELECT message_id, event_source, event_datetime, event_class, 
                           event_severity, event_action, filtering_point, ip, 
                           sender, recipients, msg_subject, msg_author, 
                           remote_peer, source_ip, country, event_msg, filename, processed_at
                    FROM {_settings.Schema}.{_settings.Table}
                    ORDER BY event_datetime DESC
                    LIMIT @limit;";
                
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("limit", limit);
                
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var entry = new OrfLogEntry
                    {
                        MessageId = reader["message_id"].ToString(),
                        EventSource = reader["event_source"].ToString(),
                        EventDateTime = reader.GetDateTime(reader.GetOrdinal("event_datetime")),
                        EventClass = reader["event_class"].ToString(),
                        EventSeverity = reader["event_severity"].ToString(),
                        EventAction = reader["event_action"].ToString(),
                        FilteringPoint = reader["filtering_point"].ToString(),
                        IP = reader["ip"].ToString(),
                        Sender = reader["sender"].ToString(),
                        Recipients = reader["recipients"].ToString(),
                        MsgSubject = reader["msg_subject"].ToString(),
                        MsgAuthor = reader["msg_author"].ToString(),
                        RemotePeer = reader["remote_peer"].ToString(),
                        SourceIP = reader["source_ip"].ToString(),
                        Country = reader["country"].ToString(),
                        EventMsg = reader["event_msg"].ToString(),
                        SourceFilename = reader["filename"].ToString(),
                        ProcessedAt = reader.GetDateTime(reader.GetOrdinal("processed_at"))
                    };
                    
                    entries.Add(entry);
                }
                
                _logger.LogInformation("Retrieved {Count} sample rows from database", entries.Count);
                return entries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sample data: {Message}", ex.Message);
                return entries;
            }
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
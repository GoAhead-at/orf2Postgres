using Log2Postgres.Core.Models;
using Log2Postgres.Core.Services;
using Log2Postgres.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Log2Postgres.Infrastructure.Database
{
    /// <summary>
    /// Enhanced PostgreSQL repository with resilience patterns and optimized operations
    /// </summary>
    public interface IPostgresRepository
    {
        Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
        Task<bool> TableExistsAsync(CancellationToken cancellationToken = default);
        Task<bool> ValidateTableStructureAsync(CancellationToken cancellationToken = default);
        Task CreateTableAsync(CancellationToken cancellationToken = default);
        Task<int> InsertEntriesAsync(IEnumerable<OrfLogEntry> entries, CancellationToken cancellationToken = default);
        Task<int> GetEntriesCountAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<OrfLogEntry>> GetSampleEntriesAsync(int limit = 100, CancellationToken cancellationToken = default);
        Task<DateTime?> GetLastProcessedTimestampAsync(CancellationToken cancellationToken = default);
    }

    public class PostgresRepository : IPostgresRepository, IDisposable
    {
        private readonly ILogger<PostgresRepository> _logger;
        private readonly IOptionsMonitor<DatabaseSettings> _settingsMonitor;
        private readonly IResilienceService _resilienceService;
        private readonly SemaphoreSlim _connectionSemaphore;
        private readonly int _maxConcurrentConnections;

        private DatabaseSettings CurrentSettings => _settingsMonitor.CurrentValue;

        public PostgresRepository(
            ILogger<PostgresRepository> logger, 
            IOptionsMonitor<DatabaseSettings> settingsMonitor,
            IResilienceService resilienceService)
        {
            _logger = logger;
            _settingsMonitor = settingsMonitor;
            _resilienceService = resilienceService;
            _maxConcurrentConnections = Environment.ProcessorCount * 2;
            _connectionSemaphore = new SemaphoreSlim(_maxConcurrentConnections, _maxConcurrentConnections);
        }

        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    using var command = new NpgsqlCommand("SELECT 1", connection);
                    await command.ExecuteScalarAsync(cancellationToken);
                    
                    _logger.LogDebug("Database connection test successful");
                    return true;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "TestConnection", cancellationToken);
        }

        public async Task<bool> TableExistsAsync(CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = @"
                        SELECT EXISTS (
                            SELECT FROM information_schema.tables 
                            WHERE table_schema = @schema 
                            AND table_name = @table
                        )";
                    
                    using var command = new NpgsqlCommand(sql, connection);
                    command.Parameters.AddWithValue("@schema", CurrentSettings.Schema);
                    command.Parameters.AddWithValue("@table", CurrentSettings.Table);
                    
                    var exists = (bool)await command.ExecuteScalarAsync(cancellationToken);
                    _logger.LogDebug("Table {Schema}.{Table} exists: {Exists}", CurrentSettings.Schema, CurrentSettings.Table, exists);
                    return exists;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "TableExists", cancellationToken);
        }

        public async Task<bool> ValidateTableStructureAsync(CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = @"
                        SELECT column_name, data_type, is_nullable
                        FROM information_schema.columns
                        WHERE table_schema = @schema AND table_name = @table
                        ORDER BY ordinal_position";
                    
                    using var command = new NpgsqlCommand(sql, connection);
                    command.Parameters.AddWithValue("@schema", CurrentSettings.Schema);
                    command.Parameters.AddWithValue("@table", CurrentSettings.Table);
                    
                    var expectedColumns = GetExpectedTableStructure();
                    var actualColumns = new Dictionary<string, (string dataType, bool isNullable)>();
                    
                    using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var columnName = reader.GetString(0); // column_name
                        var dataType = reader.GetString(1);   // data_type
                        var isNullable = reader.GetString(2) == "YES"; // is_nullable
                        actualColumns[columnName] = (dataType, isNullable);
                    }
                    
                    // Validate structure
                    foreach (var expected in expectedColumns)
                    {
                        if (!actualColumns.TryGetValue(expected.Key, out var actual))
                        {
                            _logger.LogWarning("Missing column: {Column}", expected.Key);
                            return false;
                        }
                        
                        if (!IsCompatibleDataType(expected.Value.dataType, actual.dataType))
                        {
                            _logger.LogWarning("Column {Column} has incompatible data type. Expected: {Expected}, Actual: {Actual}", 
                                expected.Key, expected.Value.dataType, actual.dataType);
                            return false;
                        }
                    }
                    
                    _logger.LogDebug("Table structure validation successful");
                    return true;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "ValidateTableStructure", cancellationToken);
        }

        public async Task CreateTableAsync(CancellationToken cancellationToken = default)
        {
            await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = $@"
                        CREATE TABLE IF NOT EXISTS ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}"" (
                            message_id TEXT PRIMARY KEY,
                            event_source TEXT,
                            event_datetime TIMESTAMP WITH TIME ZONE NOT NULL,
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
                            processed_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                        );
                        
                        CREATE INDEX IF NOT EXISTS idx_{CurrentSettings.Table}_event_datetime 
                        ON ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}"" (event_datetime);
                        
                        CREATE INDEX IF NOT EXISTS idx_{CurrentSettings.Table}_event_action 
                        ON ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}"" (event_action);
                        
                        CREATE INDEX IF NOT EXISTS idx_{CurrentSettings.Table}_processed_at 
                        ON ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}"" (processed_at);";
                    
                    using var command = new NpgsqlCommand(sql, connection);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    
                    _logger.LogInformation("Table {Schema}.{Table} created successfully with indexes", 
                        CurrentSettings.Schema, CurrentSettings.Table);
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "CreateTable", cancellationToken);
        }

        public async Task<int> InsertEntriesAsync(IEnumerable<OrfLogEntry> entries, CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithCircuitBreakerAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = $@"
                        INSERT INTO ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}"" 
                        (message_id, event_source, event_datetime, event_class, event_severity, event_action, filtering_point, ip, sender, recipients, msg_subject, msg_author, remote_peer, source_ip, country, event_msg, filename)
                        VALUES (@message_id, @event_source, @event_datetime, @event_class, @event_severity, @event_action, @filtering_point, @ip, @sender, @recipients, @msg_subject, @msg_author, @remote_peer, @source_ip, @country, @event_msg, @filename)";
                    
                    var insertCount = 0;
                    using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    
                    try
                    {
                        foreach (var entry in entries)
                        {
                            using var command = new NpgsqlCommand(sql, connection, transaction);
                            AddParameters(command, entry);
                            await command.ExecuteNonQueryAsync(cancellationToken);
                            insertCount++;
                        }
                        
                        await transaction.CommitAsync(cancellationToken);
                        _logger.LogDebug("Successfully inserted {Count} entries", insertCount);
                        return insertCount;
                    }
                    catch
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "InsertEntries", cancellationToken);
        }

        public async Task<int> GetEntriesCountAsync(CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = $@"SELECT COUNT(*) FROM ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}""";
                    using var command = new NpgsqlCommand(sql, connection);
                    
                    var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    _logger.LogDebug("Total entries count: {Count}", count);
                    return count;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "GetEntriesCount", cancellationToken);
        }

        public async Task<IEnumerable<OrfLogEntry>> GetSampleEntriesAsync(int limit = 100, CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = $@"
                        SELECT message_id, event_source, event_datetime, event_class, event_severity, event_action, filtering_point, ip, sender, recipients, msg_subject, msg_author, remote_peer, source_ip, country, event_msg, filename, processed_at
                        FROM ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}""
                        ORDER BY event_datetime DESC
                        LIMIT @limit";
                    
                    using var command = new NpgsqlCommand(sql, connection);
                    command.Parameters.AddWithValue("@limit", limit);
                    
                    var entries = new List<OrfLogEntry>();
                    using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        entries.Add(MapToOrfLogEntry(reader));
                    }
                    
                    _logger.LogDebug("Retrieved {Count} sample entries", entries.Count);
                    return entries;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "GetSampleEntries", cancellationToken);
        }

        public async Task<DateTime?> GetLastProcessedTimestampAsync(CancellationToken cancellationToken = default)
        {
            return await _resilienceService.ExecuteWithRetryAsync(async () =>
            {
                await _connectionSemaphore.WaitAsync(cancellationToken);
                try
                {
                    using var connection = new NpgsqlConnection(GetConnectionString());
                    await connection.OpenAsync(cancellationToken);
                    
                    var sql = $@"
                        SELECT MAX(event_datetime) 
                        FROM ""{CurrentSettings.Schema}"".""{CurrentSettings.Table}""";
                    
                    using var command = new NpgsqlCommand(sql, connection);
                    var result = await command.ExecuteScalarAsync(cancellationToken);
                    
                    var timestamp = result as DateTime?;
                    _logger.LogDebug("Last processed timestamp: {Timestamp}", timestamp);
                    return timestamp;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }, "GetLastProcessedTimestamp", cancellationToken);
        }

        private string GetConnectionString()
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = CurrentSettings.Host,
                Port = int.TryParse(CurrentSettings.Port, out int port) ? port : 5432,
                Database = CurrentSettings.Database,
                Username = CurrentSettings.Username,
                Password = CurrentSettings.Password,
                CommandTimeout = CurrentSettings.ConnectionTimeout,
                Timeout = CurrentSettings.ConnectionTimeout,
                Pooling = true,
                MinPoolSize = 1,
                MaxPoolSize = _maxConcurrentConnections
            };

            return builder.ToString();
        }

        private static void AddParameters(NpgsqlCommand command, OrfLogEntry entry)
        {
            command.Parameters.AddWithValue("@message_id", entry.MessageId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@event_source", entry.EventSource ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@event_datetime", entry.EventDateTime);
            command.Parameters.AddWithValue("@event_class", entry.EventClass ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@event_severity", entry.EventSeverity ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@event_action", entry.EventAction ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@filtering_point", entry.FilteringPoint ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ip", entry.IP ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sender", entry.Sender ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@recipients", entry.Recipients ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@msg_subject", entry.MsgSubject ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@msg_author", entry.MsgAuthor ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@remote_peer", entry.RemotePeer ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@source_ip", entry.SourceIP ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@country", entry.Country ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@event_msg", entry.EventMsg ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@filename", entry.SourceFilename ?? (object)DBNull.Value);
        }

        private static OrfLogEntry MapToOrfLogEntry(NpgsqlDataReader reader)
        {
            return new OrfLogEntry
            {
                MessageId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                EventSource = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                EventDateTime = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                EventClass = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                EventSeverity = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                EventAction = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                FilteringPoint = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                IP = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                Sender = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                Recipients = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                MsgSubject = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                MsgAuthor = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                RemotePeer = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                SourceIP = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                Country = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                EventMsg = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                SourceFilename = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                ProcessedAt = reader.IsDBNull(17) ? DateTime.Now : reader.GetDateTime(17)
            };
        }

        private static Dictionary<string, (string dataType, bool isNullable)> GetExpectedTableStructure()
        {
            return new Dictionary<string, (string, bool)>
            {
                ["message_id"] = ("text", false),
                ["event_source"] = ("text", true),
                ["event_datetime"] = ("timestamp with time zone", false),
                ["event_class"] = ("text", true),
                ["event_severity"] = ("text", true),
                ["event_action"] = ("text", true),
                ["filtering_point"] = ("text", true),
                ["ip"] = ("text", true),
                ["sender"] = ("text", true),
                ["recipients"] = ("text", true),
                ["msg_subject"] = ("text", true),
                ["msg_author"] = ("text", true),
                ["remote_peer"] = ("text", true),
                ["source_ip"] = ("text", true),
                ["country"] = ("text", true),
                ["event_msg"] = ("text", true),
                ["filename"] = ("text", true),
                ["processed_at"] = ("timestamp with time zone", true)
            };
        }

        private static bool IsCompatibleDataType(string expected, string actual)
        {
            return expected switch
            {
                "text" => actual == "text",
                "timestamp with time zone" => actual == "timestamp with time zone",
                _ => expected == actual
            };
        }

        public void Dispose()
        {
            _connectionSemaphore?.Dispose();
        }
    }
} 
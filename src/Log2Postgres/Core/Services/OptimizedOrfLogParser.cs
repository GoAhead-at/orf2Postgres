using Log2Postgres.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// High-performance, memory-efficient ORF log parser with async support and optimizations
    /// </summary>
    public class OptimizedOrfLogParser : IDisposable
    {
        private readonly ILogger<OptimizedOrfLogParser> _logger;
        private readonly List<string> _fieldOrder = new();
        private readonly Dictionary<string, int> _fieldIndexMap = new();
        private bool _isHeaderParsed = false;
        
        // Performance optimizations
        private readonly ArrayPool<char> _charPool = ArrayPool<char>.Shared;
        private readonly ArrayPool<string> _stringPool = ArrayPool<string>.Shared;
        private const int DefaultBufferSize = 8192;
        private const int BatchSize = 1000;
        
        // Reusable StringBuilder for string operations
        private readonly StringBuilder _stringBuilder = new(1024);
        
        // Pre-compiled field name mappings for performance
        private static readonly Dictionary<string, Action<OrfLogEntry, string>> FieldSetters = new()
        {
            ["x-message-id"] = (entry, value) => entry.MessageId = value,
            ["x-event-source"] = (entry, value) => entry.EventSource = value,
            ["x-event-class"] = (entry, value) => entry.EventClass = value,
            ["x-event-severity"] = (entry, value) => entry.EventSeverity = value,
            ["x-event-action"] = (entry, value) => entry.EventAction = value,
            ["x-filtering-point"] = (entry, value) => entry.FilteringPoint = value,
            ["x-ip"] = (entry, value) => entry.IP = value,
            ["x-sender"] = (entry, value) => entry.Sender = value,
            ["x-recipients"] = (entry, value) => entry.Recipients = value,
            ["x-msg-subject"] = (entry, value) => entry.MsgSubject = value,
            ["x-msg-author"] = (entry, value) => entry.MsgAuthor = value,
            ["x-remote-peer"] = (entry, value) => entry.RemotePeer = value,
            ["x-source-ip"] = (entry, value) => entry.SourceIP = value,
            ["x-country"] = (entry, value) => entry.Country = value,
            ["x-event-msg"] = (entry, value) => entry.EventMsg = value
        };

        public OptimizedOrfLogParser(ILogger<OptimizedOrfLogParser> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Asynchronously parses a log file from the given position with batched processing
        /// </summary>
        /// <param name="filePath">Path to the log file</param>
        /// <param name="startPosition">Position in bytes to start reading from</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple of (list of parsed entries, new position)</returns>
        public async Task<(List<OrfLogEntry> Entries, long NewPosition)> ParseLogFileAsync(
            string filePath, 
            long startPosition, 
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Parsing log file {FilePath} from position {StartPosition}", filePath, startPosition);
            
            var entries = new List<OrfLogEntry>();
            long newPosition = startPosition;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, DefaultBufferSize);
                using var reader = new StreamReader(fs, Encoding.UTF8, true, DefaultBufferSize);

                // Skip to the starting position if needed
                if (startPosition > 0)
                {
                    fs.Seek(startPosition, SeekOrigin.Begin);
                    reader.DiscardBufferedData();
                }

                var batch = new List<OrfLogEntry>(BatchSize);
                var lineBuffer = new StringBuilder(512);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // Track the position after reading the line
                    newPosition = fs.Position;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Process header lines to determine field order
                    if (line[0] == '#')
                    {
                        ProcessHeaderLine(line);
                        continue;
                    }

                    // Parse the log entry line
                    var entry = ParseLogEntryOptimized(line.AsSpan(), Path.GetFileName(filePath));
                    if (entry != null)
                    {
                        batch.Add(entry);

                        // Process in batches for better memory management
                        if (batch.Count >= BatchSize)
                        {
                            entries.AddRange(batch);
                            batch.Clear();
                            
                            // Yield control periodically for better responsiveness
                            await Task.Yield();
                        }
                    }
                }

                // Add remaining entries
                if (batch.Count > 0)
                {
                    entries.AddRange(batch);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing log file {FilePath}: {Message}", filePath, ex.Message);
            }

            _logger.LogDebug("Parsed {EntryCount} entries from {FilePath}", entries.Count, filePath);
            return (entries, newPosition);
        }

        /// <summary>
        /// Synchronous version for compatibility, but with optimizations
        /// </summary>
        public (List<OrfLogEntry> Entries, long NewPosition) ParseLogFile(string filePath, long startPosition)
        {
            return ParseLogFileAsync(filePath, startPosition, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Process a header line to extract field order with optimization
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessHeaderLine(ReadOnlySpan<char> headerLine)
        {
            // If we've already processed fields, don't do it again
            if (_isHeaderParsed) return;

            // Look for the Fields header line
            const string fieldsPrefix = "#Fields:";
            if (headerLine.StartsWith(fieldsPrefix.AsSpan()))
            {
                _fieldOrder.Clear();
                _fieldIndexMap.Clear();

                // Extract field names using span operations
                var fieldsPart = headerLine.Slice(fieldsPrefix.Length).Trim();
                
                int fieldIndex = 0;
                int start = 0;
                
                for (int i = 0; i <= fieldsPart.Length; i++)
                {
                    if (i == fieldsPart.Length || fieldsPart[i] == ' ')
                    {
                        if (i > start)
                        {
                            var field = fieldsPart.Slice(start, i - start).ToString();
                            _fieldOrder.Add(field);
                            _fieldIndexMap[field] = fieldIndex++;
                        }
                        start = i + 1;
                    }
                }

                _isHeaderParsed = true;
                _logger.LogInformation("Parsed field order from header: {FieldCount} fields", _fieldOrder.Count);
            }
        }

        /// <summary>
        /// Process a header line to extract field order (string overload)
        /// </summary>
        private void ProcessHeaderLine(string headerLine)
        {
            ProcessHeaderLine(headerLine.AsSpan());
        }

        /// <summary>
        /// Parse a single log entry line with memory and performance optimizations
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OrfLogEntry? ParseLogEntryOptimized(ReadOnlySpan<char> line, string sourceFilename)
        {
            // Make sure we've parsed the header first
            if (!_isHeaderParsed)
            {
                _logger.LogWarning("Attempted to parse log entry before header was processed");
                return null;
            }

            var entry = new OrfLogEntry
            {
                SourceFilename = sourceFilename,
                ProcessedAt = DateTime.UtcNow
            };

            try
            {
                // Use stackalloc for small arrays to avoid heap allocation
                Span<Range> ranges = stackalloc Range[32]; // Most log entries won't have more than 32 fields
                int rangeCount = line.Split(ranges, ' ', StringSplitOptions.RemoveEmptyEntries);

                // Handle the special case for system messages (0-0)
                if (rangeCount > 0 && line[ranges[0]].SequenceEqual("0-0".AsSpan()))
                {
                    return ParseSystemMessage(line, ranges, rangeCount, sourceFilename);
                }

                // Map fields based on the field order from the header
                if (rangeCount >= _fieldOrder.Count)
                {
                    ParseRegularLogEntry(line, ranges, rangeCount, entry);
                }
                else
                {
                    _logger.LogWarning("Log entry has fewer parts ({PartsCount}) than expected fields ({FieldCount})", 
                        rangeCount, _fieldOrder.Count);
                }

                return entry;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing log entry: {Line}", line.ToString());
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OrfLogEntry ParseSystemMessage(ReadOnlySpan<char> line, Span<Range> ranges, int rangeCount, string sourceFilename)
        {
            var entry = new OrfLogEntry
            {
                SourceFilename = sourceFilename,
                MessageId = line[ranges[0]].ToString(),
                ProcessedAt = DateTime.UtcNow
            };

            if (rangeCount > 1) entry.EventSource = line[ranges[1]].ToString();

            // Parse datetime from a system message (third component)
            if (rangeCount > 2)
            {
                var dateTimeSpan = line[ranges[2]];
                if (DateTime.TryParseExact(dateTimeSpan, "yyyy-MM-dd'T'HH:mm:ss", 
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                {
                    entry.EventDateTime = dt;
                }
            }

            if (rangeCount > 3) entry.EventClass = line[ranges[3]].ToString();
            if (rangeCount > 4) entry.EventSeverity = line[ranges[4]].ToString();

            // Find the message by looking for the pattern of dashes
            for (int i = 5; i < rangeCount - 1; i++)
            {
                if (line[ranges[i]].SequenceEqual("-".AsSpan()) && 
                    line[ranges[i + 1]].SequenceEqual("-".AsSpan()))
                {
                    if (i + 2 < rangeCount)
                    {
                        // Efficiently join remaining parts
                        _stringBuilder.Clear();
                        for (int j = i + 2; j < rangeCount; j++)
                        {
                            if (j > i + 2) _stringBuilder.Append(' ');
                            _stringBuilder.Append(line[ranges[j]]);
                        }
                        entry.EventMsg = _stringBuilder.ToString();
                    }
                    break;
                }
            }

            return entry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ParseRegularLogEntry(ReadOnlySpan<char> line, Span<Range> ranges, int rangeCount, OrfLogEntry entry)
        {
            int currentIndex = 0;

            for (int i = 0; i < _fieldOrder.Count && currentIndex < rangeCount; i++)
            {
                string fieldName = _fieldOrder[i];
                ReadOnlySpan<char> valueSpan;

                // For the last field, take all remaining parts
                if (i == _fieldOrder.Count - 1)
                {
                    _stringBuilder.Clear();
                    for (int j = currentIndex; j < rangeCount; j++)
                    {
                        if (j > currentIndex) _stringBuilder.Append(' ');
                        _stringBuilder.Append(line[ranges[j]]);
                    }
                    SetFieldValue(entry, fieldName, _stringBuilder.ToString());
                }
                else
                {
                    valueSpan = line[ranges[currentIndex]];
                    SetFieldValue(entry, fieldName, valueSpan.ToString());
                    currentIndex++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetFieldValue(OrfLogEntry entry, string fieldName, string value)
        {
            // Special handling for datetime field
            if (fieldName == "x-event-datetime")
            {
                if (DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", 
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                {
                    entry.EventDateTime = dt;
                }
                return;
            }

            // Use pre-compiled field setters for performance
            if (FieldSetters.TryGetValue(fieldName, out var setter))
            {
                setter(entry, value);
            }
        }

        public void Dispose()
        {
            _stringBuilder?.Clear();
        }
    }
} 
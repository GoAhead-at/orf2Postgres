using Log2Postgres.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Parses ORF log files and extracts log entries
    /// </summary>
    public class OrfLogParser
    {
        private readonly ILogger<OrfLogParser> _logger;
        
        // The order of fields in the log file, as defined in the header
        private readonly List<string> _fieldOrder = new();
        private bool _isHeaderParsed = false;
        
        public OrfLogParser(ILogger<OrfLogParser> logger)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// Parses a log file from the given position and extracts log entries
        /// </summary>
        /// <param name="filePath">Path to the log file</param>
        /// <param name="startPosition">Position in bytes to start reading from</param>
        /// <returns>Tuple of (list of parsed entries, new position)</returns>
        public (List<OrfLogEntry> Entries, long NewPosition) ParseLogFile(string filePath, long startPosition)
        {
            _logger.LogDebug("Parsing log file {FilePath} from position {StartPosition}", filePath, startPosition);
            
            List<OrfLogEntry> entries = new();
            long newPosition = startPosition;
            
            try
            {
                using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(fs, Encoding.UTF8);
                
                // Skip to the starting position if needed
                if (startPosition > 0)
                {
                    fs.Seek(startPosition, SeekOrigin.Begin);
                    reader.DiscardBufferedData();
                }
                
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Track the position after reading the line
                    newPosition = fs.Position;
                    
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    
                    // Skip empty lines
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    
                    // Process header lines to determine field order
                    if (line.StartsWith("#"))
                    {
                        ProcessHeaderLine(line);
                        continue;
                    }
                    
                    // Parse the log entry line
                    var entry = ParseLogEntry(line, Path.GetFileName(filePath));
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
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
        /// Process a header line to extract field order
        /// </summary>
        private void ProcessHeaderLine(string headerLine)
        {
            // If we've already processed fields, don't do it again
            if (_isHeaderParsed) return;
            
            // Look for the Fields header line
            if (headerLine.StartsWith("#Fields:"))
            {
                _fieldOrder.Clear();
                
                // Extract field names
                string fieldsPart = headerLine.Substring("#Fields:".Length).Trim();
                string[] fields = fieldsPart.Split(' ');
                
                foreach (var field in fields)
                {
                    _fieldOrder.Add(field.Trim());
                }
                
                _isHeaderParsed = true;
                _logger.LogInformation("Parsed field order from header: {FieldCount} fields", _fieldOrder.Count);
            }
        }
        
        /// <summary>
        /// Parse a single log entry line
        /// </summary>
        private OrfLogEntry? ParseLogEntry(string line, string sourceFilename)
        {
            // Make sure we've parsed the header first
            if (!_isHeaderParsed)
            {
                _logger.LogWarning("Attempted to parse log entry before header was processed");
                return null;
            }
            
            var entry = new OrfLogEntry
            {
                SourceFilename = sourceFilename
            };
            
            try
            {
                // Split by spaces, but handle the fields properly
                string[] parts = line.Split(' ');
                _logger.LogTrace("Parsed log line into {Count} parts", parts.Length);
                
                // Handle the special case for system messages (0-0)
                if (parts.Length > 0 && parts[0] == "0-0")
                {
                    // System message format is different (0-0 - dateTime System Info - ...)
                    entry.MessageId = parts[0];
                    entry.EventSource = parts[1]; // Usually "-"
                    
                    // Parse datetime from a system message (third component)
                    if (parts.Length > 2 && DateTime.TryParseExact(parts[2], "yyyy-MM-dd'T'HH:mm:ss", 
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                    {
                        entry.EventDateTime = dt;
                    }
                    
                    if (parts.Length > 3) entry.EventClass = parts[3]; // Usually "System"
                    if (parts.Length > 4) entry.EventSeverity = parts[4]; // Usually "Info"
                    
                    // The event message is typically at the end after several dashes
                    // Find the message by looking for the pattern of dashes and then taking the rest
                    int msgIndex = -1;
                    for (int i = 5; i < parts.Length - 1; i++)
                    {
                        if (parts[i] == "-" && parts[i+1] == "-")
                        {
                            msgIndex = i + 2;
                            break;
                        }
                    }
                    
                    if (msgIndex > 0 && msgIndex < parts.Length)
                    {
                        entry.EventMsg = string.Join(" ", parts.Skip(msgIndex));
                    }
                    
                    return entry;
                }
                
                // Map fields based on the field order from the header
                // We need to handle this carefully since some fields may contain spaces
                if (parts.Length >= _fieldOrder.Count)
                {
                    // Extract fields according to the structure defined in the header
                    int currentIndex = 0;
                    
                    for (int i = 0; i < _fieldOrder.Count; i++)
                    {
                        string fieldName = _fieldOrder[i];
                        string value;
                        
                        // For the last field, take all remaining parts
                        if (i == _fieldOrder.Count - 1)
                        {
                            value = string.Join(" ", parts.Skip(currentIndex));
                        }
                        else
                        {
                            value = parts[currentIndex];
                            currentIndex++;
                        }
                        
                        // Process each field
                        switch (fieldName)
                        {
                            case "x-message-id":
                                entry.MessageId = value;
                                break;
                            case "x-event-source":
                                entry.EventSource = value;
                                break;
                            case "x-event-datetime":
                                if (DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", 
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                                {
                                    entry.EventDateTime = dt;
                                }
                                break;
                            case "x-event-class":
                                entry.EventClass = value;
                                break;
                            case "x-event-severity":
                                entry.EventSeverity = value;
                                break;
                            case "x-event-action":
                                entry.EventAction = value;
                                break;
                            case "x-filtering-point":
                                entry.FilteringPoint = value;
                                break;
                            case "x-ip":
                                entry.IP = value;
                                break;
                            case "x-sender":
                                entry.Sender = value;
                                break;
                            case "x-recipients":
                                entry.Recipients = value;
                                break;
                            case "x-msg-subject":
                                entry.MsgSubject = value;
                                break;
                            case "x-msg-author":
                                entry.MsgAuthor = value;
                                break;
                            case "x-remote-peer":
                                entry.RemotePeer = value;
                                break;
                            case "x-source-ip":
                                entry.SourceIP = value;
                                break;
                            case "x-country":
                                entry.Country = value;
                                break;
                            case "x-event-msg":
                                entry.EventMsg = value;
                                break;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Log entry has fewer parts ({PartsCount}) than expected fields ({FieldCount}): {Line}", 
                        parts.Length, _fieldOrder.Count, line);
                }
                
                return entry;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing log entry: {Line}", line);
                return null;
            }
        }
    }
} 
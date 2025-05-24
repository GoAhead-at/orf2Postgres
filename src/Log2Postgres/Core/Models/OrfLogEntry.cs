using System;

namespace Log2Postgres.Core.Models
{
    /// <summary>
    /// Represents a single entry from an ORF log file
    /// </summary>
    public class OrfLogEntry
    {
        /// <summary>
        /// The unique message ID (x-message-id)
        /// </summary>
        public string MessageId { get; set; } = string.Empty;
        
        /// <summary>
        /// Source of the event (x-event-source)
        /// </summary>
        public string EventSource { get; set; } = string.Empty;
        
        /// <summary>
        /// Timestamp of the event (x-event-datetime)
        /// </summary>
        public DateTime EventDateTime { get; set; }
        
        /// <summary>
        /// Class of event (x-event-class)
        /// </summary>
        public string EventClass { get; set; } = string.Empty;
        
        /// <summary>
        /// Severity level (x-event-severity)
        /// </summary>
        public string EventSeverity { get; set; } = string.Empty;
        
        /// <summary>
        /// Action taken (x-event-action)
        /// </summary>
        public string EventAction { get; set; } = string.Empty;
        
        /// <summary>
        /// Filtering point (x-filtering-point)
        /// </summary>
        public string FilteringPoint { get; set; } = string.Empty;
        
        /// <summary>
        /// IP address (x-ip)
        /// </summary>
        public string IP { get; set; } = string.Empty;
        
        /// <summary>
        /// Email sender (x-sender)
        /// </summary>
        public string Sender { get; set; } = string.Empty;
        
        /// <summary>
        /// Email recipients (x-recipients)
        /// </summary>
        public string Recipients { get; set; } = string.Empty;
        
        /// <summary>
        /// Email subject (x-msg-subject)
        /// </summary>
        public string MsgSubject { get; set; } = string.Empty;
        
        /// <summary>
        /// Email author (x-msg-author)
        /// </summary>
        public string MsgAuthor { get; set; } = string.Empty;
        
        /// <summary>
        /// Remote peer (x-remote-peer)
        /// </summary>
        public string RemotePeer { get; set; } = string.Empty;
        
        /// <summary>
        /// Source IP address (x-source-ip)
        /// </summary>
        public string SourceIP { get; set; } = string.Empty;
        
        /// <summary>
        /// Country code (x-country)
        /// </summary>
        public string Country { get; set; } = string.Empty;
        
        /// <summary>
        /// Event message (x-event-msg)
        /// </summary>
        public string EventMsg { get; set; } = string.Empty;
        
        /// <summary>
        /// Original file that contained this log entry
        /// </summary>
        public string SourceFilename { get; set; } = string.Empty;
        
        /// <summary>
        /// DateTime when this entry was processed
        /// </summary>
        public DateTime ProcessedAt { get; set; } = DateTime.Now;
        
        /// <summary>
        /// Determines if the log entry is a system message
        /// </summary>
        public bool IsSystemMessage => MessageId == "0-0" || MessageId.StartsWith("SYS-");
    }
} 
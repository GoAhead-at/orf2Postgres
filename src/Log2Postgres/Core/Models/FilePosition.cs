using System;

namespace Log2Postgres.Core.Models
{
    /// <summary>
    /// Tracks the position in a log file to avoid duplicate processing
    /// </summary>
    public class FilePosition
    {
        /// <summary>
        /// The full path to the log file
        /// </summary>
        public string FilePath { get; set; } = string.Empty;
        
        /// <summary>
        /// The last position read in the file (in bytes)
        /// </summary>
        public long Position { get; set; }
        
        /// <summary>
        /// The last time this file was processed
        /// </summary>
        public DateTime LastProcessed { get; set; }
        
        /// <summary>
        /// The size of the file when last processed
        /// </summary>
        public long LastFileSize { get; set; }
        
        /// <summary>
        /// The date this file corresponds to (extracted from filename)
        /// </summary>
        public DateTime FileDate { get; set; }
    }
} 
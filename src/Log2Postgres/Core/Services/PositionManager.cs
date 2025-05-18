using Log2Postgres.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// Manages file position tracking to avoid duplicate processing
    /// </summary>
    public class PositionManager
    {
        private readonly ILogger<PositionManager> _logger;
        private readonly string _positionsFilePath;
        private Dictionary<string, FilePosition> _positions = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        
        // Event to notify when positions are loaded or reset
        public event Action<bool>? PositionsLoaded;
        
        public PositionManager(ILogger<PositionManager> logger)
        {
            _logger = logger;
            
            // Store positions.json in the application directory
            string appDirectory = AppContext.BaseDirectory;
            
            _positionsFilePath = Path.Combine(appDirectory, "positions.json");
            _logger.LogInformation("Using positions file: {PositionsFile}", _positionsFilePath);
            
            LoadPositions();
        }
        
        /// <summary>
        /// Gets the last known position for a file
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <returns>Last known position (0 if not previously tracked)</returns>
        public async Task<long> GetPositionAsync(string filePath)
        {
            // First check if positions file exists, if not recreate it
            if (!PositionsFileExists())
            {
                _logger.LogWarning("Positions file does not exist when getting position. Creating a new one.");
                await EnsurePositionsFileExistsAsync();
            }
            
            await _lock.WaitAsync();
            try
            {
                string key = GetNormalizedPath(filePath);
                if (_positions.TryGetValue(key, out FilePosition? position))
                {
                    return position.Position;
                }
                return 0;
            }
            finally
            {
                _lock.Release();
            }
        }
        
        /// <summary>
        /// Checks if the positions file exists
        /// </summary>
        /// <returns>True if the positions file exists, false otherwise</returns>
        public bool PositionsFileExists()
        {
            return File.Exists(_positionsFilePath);
        }
        
        /// <summary>
        /// Ensures the positions file exists, creating it with default values if it doesn't
        /// </summary>
        /// <returns>Task representing the async operation</returns>
        private async Task EnsurePositionsFileExistsAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(_positionsFilePath))
                {
                    _logger.LogInformation("Positions file does not exist. Creating a new one with default values.");
                    
                    // Reset positions dictionary to empty
                    _positions = new Dictionary<string, FilePosition>();
                    
                    // Save the empty positions file
                    await SavePositionsAsync();
                    
                    // Notify that positions were reset
                    PositionsLoaded?.Invoke(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring positions file exists: {Message}", ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }
        
        /// <summary>
        /// Reset and reload positions from the JSON file
        /// </summary>
        /// <returns>True if positions were loaded, false if reset to empty</returns>
        public bool LoadPositions()
        {
            bool positionsLoaded = false;
            try
            {
                // Make sure there is a lock when loading positions
                _lock.Wait();
                
                try
                {
                    _logger.LogDebug("Loading positions from {FilePath}", _positionsFilePath);
                    
                    if (File.Exists(_positionsFilePath))
                    {
                        try
                        {
                            string json = File.ReadAllText(_positionsFilePath);
                            
                            // Make sure the JSON is valid
                            if (string.IsNullOrWhiteSpace(json))
                            {
                                _logger.LogWarning("Positions file exists but is empty. Creating a new empty positions dictionary.");
                                _positions = new Dictionary<string, FilePosition>();
                            }
                            else
                            {
                                try
                                {
                                    var positions = JsonSerializer.Deserialize<List<FilePosition>>(json);
                                    if (positions != null && positions.Count > 0)
                                    {
                                        _positions = positions.ToDictionary(p => GetNormalizedPath(p.FilePath));
                                        _logger.LogInformation("Loaded {Count} file positions from {FilePath}", 
                                            _positions.Count, _positionsFilePath);
                                        positionsLoaded = true;
                                    }
                                    else
                                    {
                                        _logger.LogInformation("Positions file exists but is empty or invalid");
                                        _positions = new Dictionary<string, FilePosition>();
                                    }
                                }
                                catch (JsonException jsonEx)
                                {
                                    _logger.LogError(jsonEx, "Error deserializing positions file: {Message}. Creating a backup and starting with a new file.", 
                                        jsonEx.Message);
                                        
                                    // Backup the corrupted file
                                    string backupPath = _positionsFilePath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                                    try
                                    {
                                        File.Copy(_positionsFilePath, backupPath);
                                        _logger.LogInformation("Created backup of corrupted positions file at {BackupPath}", backupPath);
                                    }
                                    catch (Exception backupEx)
                                    {
                                        _logger.LogWarning(backupEx, "Failed to create backup of corrupted positions file: {Message}", backupEx.Message);
                                    }
                                    
                                    // Start with a new empty dictionary
                                    _positions = new Dictionary<string, FilePosition>();
                                }
                            }
                        }
                        catch (IOException ioEx)
                        {
                            _logger.LogError(ioEx, "IO error reading positions file: {Message}. Will use empty positions.", ioEx.Message);
                            _positions = new Dictionary<string, FilePosition>();
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Positions file does not exist, using empty positions");
                        _positions = new Dictionary<string, FilePosition>();
                        
                        // Create the positions file with default values
                        try
                        {
                            SavePositionsAsync().Wait();
                            _logger.LogInformation("Created new positions file at {FilePath}", _positionsFilePath);
                        }
                        catch (Exception saveEx)
                        {
                            _logger.LogError(saveEx, "Failed to create new positions file: {Message}", saveEx.Message);
                        }
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error loading positions from {FilePath}: {Message}", 
                    _positionsFilePath, ex.Message);
                _positions = new Dictionary<string, FilePosition>();
            }
            
            // Notify subscribers
            try
            {
                PositionsLoaded?.Invoke(positionsLoaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PositionsLoaded event handler: {Message}", ex.Message);
            }
            
            return positionsLoaded;
        }
        
        /// <summary>
        /// Updates the position for a file
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <param name="position">New position in bytes</param>
        /// <returns>Task representing the async operation</returns>
        public async Task UpdatePositionAsync(string filePath, long position)
        {
            // First check if positions file exists, if not recreate it
            if (!PositionsFileExists())
            {
                _logger.LogWarning("Positions file does not exist when updating position. Creating a new one.");
                await EnsurePositionsFileExistsAsync();
            }
            
            await _lock.WaitAsync();
            try
            {
                string key = GetNormalizedPath(filePath);
                
                // Extract date from filename (assuming format orfee-yyyy-MM-dd.log)
                DateTime fileDate = DateTime.Now;
                string filename = Path.GetFileName(filePath);
                if (filename.StartsWith("orfee-") && filename.EndsWith(".log"))
                {
                    string dateStr = filename.Substring(6, 10);
                    if (DateTime.TryParse(dateStr, out DateTime parsed))
                    {
                        fileDate = parsed;
                    }
                }
                
                var fileInfo = new FileInfo(filePath);
                
                if (_positions.TryGetValue(key, out FilePosition? existingPosition))
                {
                    existingPosition.Position = position;
                    existingPosition.LastProcessed = DateTime.Now;
                    existingPosition.LastFileSize = fileInfo.Exists ? fileInfo.Length : 0;
                    existingPosition.FileDate = fileDate;
                }
                else
                {
                    _positions[key] = new FilePosition
                    {
                        FilePath = filePath,
                        Position = position,
                        LastProcessed = DateTime.Now,
                        LastFileSize = fileInfo.Exists ? fileInfo.Length : 0,
                        FileDate = fileDate
                    };
                }
                
                await SavePositionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating position for {FilePath}: {Message}", filePath, ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }
        
        /// <summary>
        /// Gets a list of all tracked file positions
        /// </summary>
        public async Task<IReadOnlyList<FilePosition>> GetAllPositionsAsync()
        {
            // First check if positions file exists, if not recreate it
            if (!PositionsFileExists())
            {
                _logger.LogWarning("Positions file does not exist when getting all positions. Creating a new one.");
                await EnsurePositionsFileExistsAsync();
            }
            
            await _lock.WaitAsync();
            try
            {
                return _positions.Values.ToList().AsReadOnly();
            }
            finally
            {
                _lock.Release();
            }
        }
        
        /// <summary>
        /// Removes tracking for a file
        /// </summary>
        public async Task RemoveFileAsync(string filePath)
        {
            // First check if positions file exists, if not recreate it
            if (!PositionsFileExists())
            {
                _logger.LogWarning("Positions file does not exist when removing file. Creating a new one.");
                await EnsurePositionsFileExistsAsync();
            }
            
            await _lock.WaitAsync();
            try
            {
                string key = GetNormalizedPath(filePath);
                if (_positions.Remove(key))
                {
                    await SavePositionsAsync();
                    _logger.LogInformation("Removed tracking for file {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing file {FilePath}: {Message}", filePath, ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }
        
        /// <summary>
        /// Save positions to the JSON file
        /// </summary>
        private async Task SavePositionsAsync()
        {
            try
            {
                string json = JsonSerializer.Serialize(_positions.Values.ToList(), 
                    new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_positionsFilePath, json);
                _logger.LogDebug("Saved {Count} file positions to {FilePath}", 
                    _positions.Count, _positionsFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving positions to {FilePath}: {Message}", 
                    _positionsFilePath, ex.Message);
            }
        }
        
        /// <summary>
        /// Normalize a file path for consistent dictionary keys
        /// </summary>
        private string GetNormalizedPath(string path)
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
    }
} 
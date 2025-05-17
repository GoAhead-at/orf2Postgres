using Log2Postgres.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        
        public PositionManager(ILogger<PositionManager> logger)
        {
            _logger = logger;
            
            string appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Log2Postgres");
                
            // Ensure directory exists
            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }
            
            _positionsFilePath = Path.Combine(appDataPath, "positions.json");
            LoadPositions();
        }
        
        /// <summary>
        /// Gets the last known position for a file
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <returns>Last known position (0 if not previously tracked)</returns>
        public async Task<long> GetPositionAsync(string filePath)
        {
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
        /// Updates the position for a file
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <param name="position">New position in bytes</param>
        /// <returns>Task representing the async operation</returns>
        public async Task UpdatePositionAsync(string filePath, long position)
        {
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
        /// Load positions from the JSON file
        /// </summary>
        private void LoadPositions()
        {
            try
            {
                if (File.Exists(_positionsFilePath))
                {
                    string json = File.ReadAllText(_positionsFilePath);
                    var positions = JsonSerializer.Deserialize<List<FilePosition>>(json);
                    if (positions != null)
                    {
                        _positions = positions.ToDictionary(p => GetNormalizedPath(p.FilePath));
                        _logger.LogInformation("Loaded {Count} file positions from {FilePath}", 
                            _positions.Count, _positionsFilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading positions from {FilePath}: {Message}", 
                    _positionsFilePath, ex.Message);
                _positions = new Dictionary<string, FilePosition>();
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
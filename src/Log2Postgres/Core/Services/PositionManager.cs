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
using System.Text.RegularExpressions;

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
        
        // Diagnostic property to expose the path
        public string PositionsFilePathForDiagnostics => _positionsFilePath;
        
        public PositionManager(ILogger<PositionManager> logger)
        {
            _logger = logger;
            
            string appDirectory = AppContext.BaseDirectory;
            _positionsFilePath = Path.Combine(appDirectory, "positions.json");

            // Ensure the directory for the positions file exists (mainly for consistency, AppContext.BaseDirectory should exist)
            // This step is somewhat redundant if _positionsFilePath is directly in AppContext.BaseDirectory,
            // but good practice if _positionsFilePath were in a subdirectory of AppContext.BaseDirectory.
            try
            {
                string? directoryName = Path.GetDirectoryName(_positionsFilePath);
                if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                    _logger.LogInformation("Ensured application directory exists: {AppDirectory}", directoryName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ensure application directory exists: {AppDirectory}. Positions may not be saved correctly.", Path.GetDirectoryName(_positionsFilePath));
            }

            _logger.LogInformation("Using positions file: {PositionsFile}", _positionsFilePath);
            
            // LoadPositionsAsync is now called from InitializeAsync
            // LoadPositionsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        
        /// <summary>
        /// Initializes the PositionManager by loading positions.
        /// This should be called after the instance is created.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async initialization operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await LoadPositionsAsync(cancellationToken);
        }
        
        /// <summary>
        /// Gets the last known position for a file
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Last known position (0 if not previously tracked)</returns>
        public async Task<long> GetPositionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        private async Task EnsurePositionsFileExistsAsync(CancellationToken cancellationToken = default)
        {
            // This method is now primarily for diagnostics or specific recovery scenarios
            // not covered by the initial LoadPositions. It should be less destructive.
            // We still use a lock to prevent concurrent file access if multiple parts were to call this,
            // though its primary callers (GetPositionAsync, UpdatePositionAsync) have been removed.
            await _lock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(_positionsFilePath))
                {
                    _logger.LogWarning("EnsurePositionsFileExistsAsync: Positions file {FilePath} was found to be missing. " +
                                     "The in-memory positions will NOT be reset by this method. " +
                                     "A new file will be created by SavePositionsAsync if/when positions are updated.", 
                                     _positionsFilePath);
                    // DO NOT RESET _positions here
                    // DO NOT SAVE an empty file here
                    // PositionsLoaded?.Invoke(false); // This would also be misleading now
                }
                // else, file exists, do nothing.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in EnsurePositionsFileExistsAsync check for {FilePath}: {Message}", _positionsFilePath, ex.Message);
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
        public async Task<bool> LoadPositionsAsync(CancellationToken cancellationToken = default)
        {
            bool positionsWereLoadedSuccessfully = false;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("Loading positions from {FilePath}", _positionsFilePath);
                
                if (File.Exists(_positionsFilePath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(_positionsFilePath, cancellationToken);
                        
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
                                    positionsWereLoadedSuccessfully = true;
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
                                    File.Copy(_positionsFilePath, backupPath, true); // Allow overwrite if somehow a backup with exact same name exists
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
                    _logger.LogInformation("Positions file does not exist, creating a new one with empty positions.");
                    _positions = new Dictionary<string, FilePosition>();
                    
                    // Create the positions file with default values (empty list)
                    try
                    {
                        await SavePositionsAsync(cancellationToken); // Now correctly awaited
                        _logger.LogInformation("Created new positions file at {FilePath}", _positionsFilePath);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(saveEx, "Error creating initial positions file: {Message}", saveEx.Message);
                        // If saving fails, _positions remains empty, which is acceptable.
                    }
                    // positionsWereLoadedSuccessfully remains false, as we initialized a new file.
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Loading positions was cancelled.");
                // Depending on requirements, you might want to throw or handle differently.
                // For now, _positions will retain its state before cancellation (likely empty or previous state).
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during LoadPositionsAsync. Positions may be empty or outdated.");
                _positions = new Dictionary<string, FilePosition>(); // Fallback to empty on severe error
            }
            finally
            {
                _lock.Release();
            }
            
            PositionsLoaded?.Invoke(positionsWereLoadedSuccessfully);
            return positionsWereLoadedSuccessfully;
        }
        
        /// <summary>
        /// Updates the position for a file
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <param name="position">New position in bytes</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        public async Task UpdatePositionAsync(string filePath, long position, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = GetNormalizedPath(filePath);
                
                // Extract date from filename (assuming format orfee-yyyy-MM-dd.log)
                DateTime fileDate = DateTime.Now;
                string filename = Path.GetFileName(filePath);
                var match = Regex.Match(filename, @"(\d{4}-\d{2}-\d{2})");
                if (match.Success && DateTime.TryParse(match.Groups[1].Value, out DateTime parsed))
                {
                    fileDate = parsed;
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
                
                await SavePositionsAsync(cancellationToken);
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
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of all tracked file positions</returns>
        public async Task<IReadOnlyList<FilePosition>> GetAllPositionsAsync(CancellationToken cancellationToken = default)
        {
            // First check if positions file exists, if not recreate it
            if (!PositionsFileExists())
            {
                _logger.LogWarning("Positions file does not exist when getting all positions. Creating a new one.");
                await EnsurePositionsFileExistsAsync();
            }
            
            await _lock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        /// <param name="filePath">Full path to the file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            // First check if positions file exists, if not recreate it
            if (!PositionsFileExists())
            {
                _logger.LogWarning("Positions file does not exist when removing file. Creating a new one.");
                await EnsurePositionsFileExistsAsync();
            }
            
            await _lock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the async operation</returns>
        private async Task SavePositionsAsync(CancellationToken cancellationToken = default)
        {
            string tempFilePath = string.Empty;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var positionsList = _positions.Values.ToList();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(positionsList, options);

                // Create a unique temporary file path in the same directory
                string? directory = Path.GetDirectoryName(_positionsFilePath);
                if (string.IsNullOrEmpty(directory))
                {
                    // Fallback or error if directory is somehow null or empty
                    // This case should ideally not happen if _positionsFilePath is valid
                    _logger.LogError("Cannot determine directory for positions file path: {FilePath}", _positionsFilePath);
                    tempFilePath = _positionsFilePath + "." + Path.GetRandomFileName() + ".tmp"; // Fallback to make it somewhat unique
                }
                else
                {
                    tempFilePath = Path.Combine(directory, Path.GetRandomFileName());
                }
                

                // Write to the temporary file
                await File.WriteAllTextAsync(tempFilePath, json, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested(); // Check after async operation

                // Replace the original file with the temporary file
                File.Move(tempFilePath, _positionsFilePath, overwrite: true);
                
                _logger.LogDebug("Saved {Count} positions to {FilePath}", positionsList.Count, _positionsFilePath);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SavePositionsAsync was cancelled.");
                // Clean up the temporary file if cancellation occurred
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch (Exception exDelete)
                    {
                        _logger.LogWarning(exDelete, "Failed to delete temporary positions file {TempFilePath} during cancellation.", tempFilePath);
                    }
                }
                throw; // Re-throw the cancellation exception
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving positions to {FilePath}: {Message}", _positionsFilePath, ex.Message);
                // Clean up the temporary file if an error occurred
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch (Exception exDelete)
                    {
                        _logger.LogWarning(exDelete, "Failed to delete temporary positions file {TempFilePath} after error.", tempFilePath);
                    }
                }
                // Optionally, re-throw or handle more gracefully depending on requirements
                // For now, just logging as the impact might be that positions aren't saved,
                // but the app might continue running with in-memory positions.
            }
        }
        
        /// <summary>
        /// Normalize a file path for consistent dictionary keys
        /// </summary>
        private string GetNormalizedPath(string path)
        {
            return Path.GetFullPath(path).ToUpperInvariant();
        }
    }
} 
using Log2Postgres.Core.Models;
using Log2Postgres.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Log2Postgres.Core.Services
{
    /// <summary>
    /// High-performance log file processor with async patterns, batching, and memory optimization
    /// </summary>
    public interface IOptimizedLogFileProcessor
    {
        Task<ProcessingResult> ProcessFileAsync(string filePath, long startPosition, CancellationToken cancellationToken = default);
        Task<BatchProcessingResult> ProcessMultipleFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);
        Task<MemoryUsageStats> GetMemoryUsageStatsAsync();
    }

    public class ProcessingResult
    {
        public int EntriesProcessed { get; set; }
        public int EntriesSaved { get; set; }
        public long NewPosition { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string? ErrorMessage { get; set; }
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }

    public class BatchProcessingResult
    {
        public int TotalFiles { get; set; }
        public int SuccessfulFiles { get; set; }
        public int TotalEntriesProcessed { get; set; }
        public int TotalEntriesSaved { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class MemoryUsageStats
    {
        public long WorkingSetBytes { get; set; }
        public long GcMemoryBytes { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
    }

    public class OptimizedLogFileProcessor : IOptimizedLogFileProcessor, IDisposable
    {
        private readonly ILogger<OptimizedLogFileProcessor> _logger;
        private readonly OptimizedOrfLogParser _parser;
        private readonly PositionManager _positionManager;
        private readonly PostgresService _postgresService;
        private readonly IResilienceService _resilienceService;
        private readonly IOptionsMonitor<LogMonitorSettings> _optionsMonitor;
        
        // Performance monitoring
        private readonly Channel<ProcessingMetric> _metricsChannel;
        private readonly ChannelWriter<ProcessingMetric> _metricsWriter;
        private readonly ChannelReader<ProcessingMetric> _metricsReader;
        
        // Configuration
        private const int MaxConcurrentFiles = 3;
        private const int BatchSize = 1000;
        private const int MaxMemoryThresholdMB = 500;
        
        private LogMonitorSettings CurrentSettings => _optionsMonitor.CurrentValue;

        public OptimizedLogFileProcessor(
            ILogger<OptimizedLogFileProcessor> logger,
            OptimizedOrfLogParser parser,
            PositionManager positionManager,
            PostgresService postgresService,
            IResilienceService resilienceService,
            IOptionsMonitor<LogMonitorSettings> optionsMonitor)
        {
            _logger = logger;
            _parser = parser;
            _positionManager = positionManager;
            _postgresService = postgresService;
            _resilienceService = resilienceService;
            _optionsMonitor = optionsMonitor;
            
            // Initialize metrics channel for performance monitoring
            var channelOptions = new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            _metricsChannel = Channel.CreateBounded<ProcessingMetric>(channelOptions);
            _metricsWriter = _metricsChannel.Writer;
            _metricsReader = _metricsChannel.Reader;
        }

        public async Task<ProcessingResult> ProcessFileAsync(string filePath, long startPosition, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            var result = new ProcessingResult();

            try
            {
                _logger.LogDebug("Starting optimized processing of {FilePath} from position {Position}", filePath, startPosition);

                // Check memory usage before processing
                var memoryStats = await GetMemoryUsageStatsAsync();
                if (memoryStats.WorkingSetBytes > MaxMemoryThresholdMB * 1024 * 1024)
                {
                    _logger.LogWarning("High memory usage detected ({MemoryMB} MB), forcing garbage collection", 
                        memoryStats.WorkingSetBytes / 1024 / 1024);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                // Parse file with optimized parser
                var (entries, newPosition) = await _resilienceService.ExecuteWithRetryAsync(
                    () => _parser.ParseLogFileAsync(filePath, startPosition, cancellationToken),
                    $"ParseFile_{Path.GetFileName(filePath)}",
                    cancellationToken);

                result.EntriesProcessed = entries.Count;
                result.NewPosition = newPosition;

                if (entries.Count > 0)
                {
                    // Process entries in batches to manage memory
                    var savedCount = await ProcessEntriesInBatchesAsync(entries, cancellationToken);
                    result.EntriesSaved = savedCount;

                    // Update position only if all entries were saved successfully
                    if (savedCount == entries.Count)
                    {
                        await _positionManager.UpdatePositionAsync(filePath, newPosition, cancellationToken);
                        _logger.LogDebug("Updated position for {FilePath} to {Position}", filePath, newPosition);
                    }
                    else
                    {
                        result.ErrorMessage = $"Only {savedCount} of {entries.Count} entries were saved successfully";
                        _logger.LogWarning("Partial save for {FilePath}: {SavedCount}/{TotalCount} entries", 
                            filePath, savedCount, entries.Count);
                    }
                }

                result.ProcessingTime = DateTime.UtcNow - startTime;
                
                // Record metrics
                await RecordMetricsAsync(new ProcessingMetric
                {
                    FilePath = filePath,
                    EntriesProcessed = result.EntriesProcessed,
                    ProcessingTimeMs = (int)result.ProcessingTime.TotalMilliseconds,
                    MemoryUsageMB = (int)(GC.GetTotalMemory(false) / 1024 / 1024),
                    Success = result.Success
                });

                _logger.LogInformation("Completed processing {FilePath}: {Entries} entries in {Time}ms", 
                    filePath, result.EntriesProcessed, result.ProcessingTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                result.ProcessingTime = DateTime.UtcNow - startTime;
                
                _logger.LogError(ex, "Error processing file {FilePath}: {Message}", filePath, ex.Message);
                return result;
            }
        }

        public async Task<BatchProcessingResult> ProcessMultipleFilesAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            var result = new BatchProcessingResult();
            var files = filePaths.ToList();
            
            result.TotalFiles = files.Count;
            _logger.LogInformation("Starting batch processing of {FileCount} files", files.Count);

            try
            {
                // Process files with controlled concurrency
                var semaphore = new SemaphoreSlim(MaxConcurrentFiles, MaxConcurrentFiles);
                var tasks = files.Select(async filePath =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var startPosition = await _positionManager.GetPositionAsync(filePath, cancellationToken);
                        return await ProcessFileAsync(filePath, startPosition, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToArray();

                var results = await Task.WhenAll(tasks);

                // Aggregate results
                foreach (var fileResult in results)
                {
                    if (fileResult.Success)
                    {
                        result.SuccessfulFiles++;
                    }
                    else if (!string.IsNullOrEmpty(fileResult.ErrorMessage))
                    {
                        result.Errors.Add(fileResult.ErrorMessage);
                    }
                    
                    result.TotalEntriesProcessed += fileResult.EntriesProcessed;
                    result.TotalEntriesSaved += fileResult.EntriesSaved;
                }

                result.TotalProcessingTime = DateTime.UtcNow - startTime;
                
                _logger.LogInformation("Batch processing completed: {SuccessfulFiles}/{TotalFiles} files, " +
                    "{TotalEntries} entries processed in {Time}ms", 
                    result.SuccessfulFiles, result.TotalFiles, 
                    result.TotalEntriesProcessed, result.TotalProcessingTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Batch processing error: {ex.Message}");
                result.TotalProcessingTime = DateTime.UtcNow - startTime;
                
                _logger.LogError(ex, "Error in batch processing: {Message}", ex.Message);
                return result;
            }
        }

        public async Task<MemoryUsageStats> GetMemoryUsageStatsAsync()
        {
            return await Task.FromResult(new MemoryUsageStats
            {
                WorkingSetBytes = Environment.WorkingSet,
                GcMemoryBytes = GC.GetTotalMemory(false),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2)
            });
        }

        private async Task<int> ProcessEntriesInBatchesAsync(List<OrfLogEntry> entries, CancellationToken cancellationToken)
        {
            int totalSaved = 0;
            
            for (int i = 0; i < entries.Count; i += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var batch = entries.Skip(i).Take(BatchSize).ToList();
                
                try
                {
                                         var savedCount = await _resilienceService.ExecuteWithCircuitBreakerAsync(                         () => _postgresService.SaveEntriesAsync(batch, cancellationToken),                         "SaveLogEntriesBatch",                         cancellationToken);
                    
                    totalSaved += savedCount;
                    
                    _logger.LogDebug("Saved batch {BatchNumber}: {SavedCount}/{BatchSize} entries", 
                        (i / BatchSize) + 1, savedCount, batch.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving batch {BatchNumber}: {Message}", (i / BatchSize) + 1, ex.Message);
                    
                    // Try to save entries individually as fallback
                    var individualSaved = await SaveEntriesIndividuallyAsync(batch, cancellationToken);
                    totalSaved += individualSaved;
                }
                
                // Yield control periodically for better responsiveness
                if (i % (BatchSize * 5) == 0)
                {
                    await Task.Yield();
                }
            }

            return totalSaved;
        }

        private async Task<int> SaveEntriesIndividuallyAsync(List<OrfLogEntry> entries, CancellationToken cancellationToken)
        {
            int savedCount = 0;
            
            foreach (var entry in entries)
            {
                try
                {
                                         var result = await _postgresService.SaveEntriesAsync(new[] { entry }, cancellationToken);
                    if (result > 0) savedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving individual entry {MessageId}: {Message}", 
                        entry.MessageId, ex.Message);
                }
                
                cancellationToken.ThrowIfCancellationRequested();
            }
            
            _logger.LogDebug("Individual save fallback: {SavedCount}/{TotalCount} entries saved", 
                savedCount, entries.Count);
            
            return savedCount;
        }

        private async Task RecordMetricsAsync(ProcessingMetric metric)
        {
            try
            {
                await _metricsWriter.WriteAsync(metric);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to record processing metric");
            }
        }

        public void Dispose()
        {
            _metricsWriter?.Complete();
            _parser?.Dispose();
        }

        private class ProcessingMetric
        {
            public string FilePath { get; set; } = string.Empty;
            public int EntriesProcessed { get; set; }
            public int ProcessingTimeMs { get; set; }
            public int MemoryUsageMB { get; set; }
            public bool Success { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }
    }
} 
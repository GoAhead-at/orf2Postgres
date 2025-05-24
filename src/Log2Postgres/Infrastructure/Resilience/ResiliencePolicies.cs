using Microsoft.Extensions.Logging;using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Sockets;using System.Threading;using System.Threading.Tasks;

namespace Log2Postgres.Infrastructure.Resilience
{
    /// <summary>
    /// Resilience policies for handling transient failures and implementing retry logic
    /// </summary>
    public interface IResilienceService
    {
        Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default);
        Task ExecuteWithRetryAsync(Func<Task> operation, string operationName, CancellationToken cancellationToken = default);
        Task<T> ExecuteWithCircuitBreakerAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default);
        void ResetCircuitBreaker(string operationName);
    }

    public class ResilienceService : IResilienceService
    {
        private readonly ILogger<ResilienceService> _logger;
        private readonly ResilienceOptions _options;
        private readonly Dictionary<string, CircuitBreakerState> _circuitBreakers = new();
        private readonly object _circuitBreakerLock = new();

        public ResilienceService(ILogger<ResilienceService> logger, ResilienceOptions? options = null)
        {
            _logger = logger;
            _options = options ?? new ResilienceOptions();
        }

        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default)
        {
            var retryCount = 0;
            var delay = _options.InitialDelay;

            while (retryCount <= _options.MaxRetryAttempts)
            {
                try
                {
                    _logger.LogDebug("Executing operation {OperationName}, attempt {Attempt}", operationName, retryCount + 1);
                    return await operation();
                }
                catch (Exception ex) when (retryCount < _options.MaxRetryAttempts && IsTransientException(ex))
                {
                    retryCount++;
                    _logger.LogWarning(ex, "Operation {OperationName} failed on attempt {Attempt}, retrying in {Delay}ms", 
                        operationName, retryCount, delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * _options.BackoffMultiplier, _options.MaxDelay.TotalMilliseconds));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Operation {OperationName} failed permanently after {Attempts} attempts", operationName, retryCount + 1);
                    throw;
                }
            }

            throw new InvalidOperationException($"Operation {operationName} failed after {_options.MaxRetryAttempts} attempts");
        }

        public async Task ExecuteWithRetryAsync(Func<Task> operation, string operationName, CancellationToken cancellationToken = default)
        {
            await ExecuteWithRetryAsync(async () =>
            {
                await operation();
                return true; // Return dummy value for void operations
            }, operationName, cancellationToken);
        }

        public async Task<T> ExecuteWithCircuitBreakerAsync<T>(Func<Task<T>> operation, string operationName, CancellationToken cancellationToken = default)
        {
            var circuitBreaker = GetOrCreateCircuitBreaker(operationName);

            lock (_circuitBreakerLock)
            {
                switch (circuitBreaker.State)
                {
                    case CircuitState.Open:
                        if (DateTime.UtcNow - circuitBreaker.LastFailureTime < _options.CircuitBreakerTimeout)
                        {
                            _logger.LogWarning("Circuit breaker is OPEN for operation {OperationName}. Request rejected.", operationName);
                            throw new CircuitBreakerOpenException($"Circuit breaker is open for operation {operationName}");
                        }
                        // Transition to Half-Open
                        circuitBreaker.State = CircuitState.HalfOpen;
                        _logger.LogInformation("Circuit breaker transitioning to HALF-OPEN for operation {OperationName}", operationName);
                        break;

                    case CircuitState.HalfOpen:
                        if (circuitBreaker.ConsecutiveFailures > 0)
                        {
                            _logger.LogWarning("Circuit breaker is HALF-OPEN with recent failures for operation {OperationName}. Request rejected.", operationName);
                            throw new CircuitBreakerOpenException($"Circuit breaker is half-open with failures for operation {operationName}");
                        }
                        break;
                }
            }

            try
            {
                var result = await operation();
                
                lock (_circuitBreakerLock)
                {
                    // Success - reset circuit breaker
                    circuitBreaker.ConsecutiveFailures = 0;
                    circuitBreaker.State = CircuitState.Closed;
                    _logger.LogDebug("Operation {OperationName} succeeded. Circuit breaker reset to CLOSED.", operationName);
                }

                return result;
            }
            catch (Exception ex)
            {
                lock (_circuitBreakerLock)
                {
                    circuitBreaker.ConsecutiveFailures++;
                    circuitBreaker.LastFailureTime = DateTime.UtcNow;

                    if (circuitBreaker.ConsecutiveFailures >= _options.CircuitBreakerFailureThreshold)
                    {
                        circuitBreaker.State = CircuitState.Open;
                        _logger.LogError(ex, "Circuit breaker OPENED for operation {OperationName} after {Failures} consecutive failures", 
                            operationName, circuitBreaker.ConsecutiveFailures);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Operation {OperationName} failed ({Failures}/{Threshold} failures)", 
                            operationName, circuitBreaker.ConsecutiveFailures, _options.CircuitBreakerFailureThreshold);
                    }
                }

                throw;
            }
        }

        public void ResetCircuitBreaker(string operationName)
        {
            lock (_circuitBreakerLock)
            {
                if (_circuitBreakers.TryGetValue(operationName, out var circuitBreaker))
                {
                    circuitBreaker.State = CircuitState.Closed;
                    circuitBreaker.ConsecutiveFailures = 0;
                    _logger.LogInformation("Circuit breaker manually reset for operation {OperationName}", operationName);
                }
            }
        }

        private CircuitBreakerState GetOrCreateCircuitBreaker(string operationName)
        {
            lock (_circuitBreakerLock)
            {
                if (!_circuitBreakers.TryGetValue(operationName, out var circuitBreaker))
                {
                    circuitBreaker = new CircuitBreakerState();
                    _circuitBreakers[operationName] = circuitBreaker;
                }
                return circuitBreaker;
            }
        }

        private static bool IsTransientException(Exception ex)
        {
            // Common transient exceptions that should be retried
            return ex is TimeoutException ||
                   ex is TaskCanceledException ||
                   ex is HttpRequestException ||
                   (ex is InvalidOperationException && ex.Message.Contains("connection")) ||
                   (ex is System.Data.Common.DbException dbEx && IsTransientDbException(dbEx)) ||
                   ex is SocketException;
        }

        private static bool IsTransientDbException(System.Data.Common.DbException dbEx)
        {
            // PostgreSQL specific transient error codes
            var errorCode = dbEx.ErrorCode;
            return errorCode switch
            {
                // Connection errors
                08000 or 08003 or 08006 or 08001 or 08004 => true,
                // System errors that might be transient
                53300 or 53400 => true,
                // Lock timeout
                40001 => true,
                _ => false
            };
        }
    }

    public class ResilienceOptions
    {
        public int MaxRetryAttempts { get; set; } = 3;
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
        public double BackoffMultiplier { get; set; } = 2.0;
        
        public int CircuitBreakerFailureThreshold { get; set; } = 5;
        public TimeSpan CircuitBreakerTimeout { get; set; } = TimeSpan.FromMinutes(1);
    }

    public class CircuitBreakerState
    {
        public CircuitState State { get; set; } = CircuitState.Closed;
        public int ConsecutiveFailures { get; set; }
        public DateTime LastFailureTime { get; set; }
    }

    public enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }

    public class CircuitBreakerOpenException : Exception
    {
        public CircuitBreakerOpenException(string message) : base(message) { }
        public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException) { }
    }
} 
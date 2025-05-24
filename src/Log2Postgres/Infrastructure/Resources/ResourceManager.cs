using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Log2Postgres.Infrastructure.Resources
{
    /// <summary>
    /// Manages application resources and ensures proper disposal
    /// </summary>
    public interface IResourceManager
    {
        void RegisterResource<T>(T resource) where T : IDisposable;
        void RegisterAsyncResource<T>(T resource) where T : IAsyncDisposable;
        Task DisposeAllAsync();
        void DisposeAll();
    }

    public class ResourceManager : IResourceManager, IAsyncDisposable, IDisposable
    {
        private readonly ILogger<ResourceManager> _logger;
        private readonly ConcurrentBag<IDisposable> _disposableResources = new();
        private readonly ConcurrentBag<IAsyncDisposable> _asyncDisposableResources = new();
        private bool _disposed;

        public ResourceManager(ILogger<ResourceManager> logger)
        {
            _logger = logger;
        }

        public void RegisterResource<T>(T resource) where T : IDisposable
        {
            if (resource != null && !_disposed)
            {
                _disposableResources.Add(resource);
                _logger.LogDebug("Registered disposable resource of type {ResourceType}", typeof(T).Name);
            }
        }

        public void RegisterAsyncResource<T>(T resource) where T : IAsyncDisposable
        {
            if (resource != null && !_disposed)
            {
                _asyncDisposableResources.Add(resource);
                _logger.LogDebug("Registered async disposable resource of type {ResourceType}", typeof(T).Name);
            }
        }

        public async Task DisposeAllAsync()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Disposing all managed resources asynchronously");

            // Dispose async resources first
            foreach (var asyncResource in _asyncDisposableResources)
            {
                try
                {
                    await asyncResource.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing async resource of type {ResourceType}", 
                        asyncResource.GetType().Name);
                }
            }

            // Then dispose regular resources
            foreach (var resource in _disposableResources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing resource of type {ResourceType}", 
                        resource.GetType().Name);
                }
            }

            _disposed = true;
            _logger.LogInformation("All managed resources disposed");
        }

        public void DisposeAll()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Disposing all managed resources synchronously");

            // Dispose async resources (sync version)
            foreach (var asyncResource in _asyncDisposableResources)
            {
                try
                {
                    if (asyncResource is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    else
                    {
                        // Fallback - this isn't ideal but necessary for sync disposal
                        asyncResource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing async resource of type {ResourceType}", 
                        asyncResource.GetType().Name);
                }
            }

            // Dispose regular resources
            foreach (var resource in _disposableResources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing resource of type {ResourceType}", 
                        resource.GetType().Name);
                }
            }

            _disposed = true;
            _logger.LogInformation("All managed resources disposed");
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAllAsync();
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            DisposeAll();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Extension methods for easier resource registration
    /// </summary>
    public static class ResourceManagerExtensions
    {
        public static T RegisterForDisposal<T>(this T resource, IResourceManager resourceManager) where T : IDisposable
        {
            resourceManager.RegisterResource(resource);
            return resource;
        }

        public static T RegisterForAsyncDisposal<T>(this T resource, IResourceManager resourceManager) where T : IAsyncDisposable
        {
            resourceManager.RegisterAsyncResource(resource);
            return resource;
        }
    }
} 
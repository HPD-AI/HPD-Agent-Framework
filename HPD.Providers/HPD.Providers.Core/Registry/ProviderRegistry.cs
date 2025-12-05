// HPD.Providers.Core/Registry/ProviderRegistry.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HPD.Providers.Core;

/// <summary>
/// Default implementation of IProviderRegistry.
/// Thread-safe singleton for provider registration across HPD ecosystem.
/// Used by both HPD-Agent and HPD-Agent.Memory.
/// </summary>
public class ProviderRegistry : IProviderRegistry
{
    private static readonly Lazy<ProviderRegistry> _instance = new(() => new ProviderRegistry());

    /// <summary>
    /// Singleton instance used by ModuleInitializers across the ecosystem.
    /// </summary>
    public static ProviderRegistry Instance => _instance.Value;

    private readonly Dictionary<string, IProviderFeatures> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
/// Prevents external instantiation and enforces the singleton lifecycle for ProviderRegistry.
/// </summary>
private ProviderRegistry() { }

    /// <summary>
    /// Registers or updates provider features in the registry using the features' ProviderKey.
    /// </summary>
    /// <param name="features">The provider features to register; <see cref="IProviderFeatures.ProviderKey"/> must be non-empty and non-whitespace.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="features"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="features"/> has a null, empty, or whitespace <see cref="IProviderFeatures.ProviderKey"/>.</exception>
    public void Register(IProviderFeatures features)
    {
        if (features == null)
            throw new ArgumentNullException(nameof(features));

        if (string.IsNullOrWhiteSpace(features.ProviderKey))
            throw new ArgumentException("ProviderKey cannot be empty", nameof(features));

        _lock.EnterWriteLock();
        try
        {
            _providers[features.ProviderKey] = features;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Retrieves the features associated with a registered provider key.
    /// </summary>
    /// <param name="providerKey">The provider key to look up; null, empty, or whitespace is considered invalid.</param>
    /// <returns>The <see cref="IProviderFeatures"/> associated with the key, or <c>null</c> if the key is invalid or no provider is registered for it.</returns>
    public IProviderFeatures? GetProvider(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return null;

        _lock.EnterReadLock();
        try
        {
            return _providers.TryGetValue(providerKey, out var features) ? features : null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Determines whether a provider with the specified key is registered.
    /// </summary>
    /// <param name="providerKey">The provider key to check; trimmed null/empty/whitespace keys are treated as not registered.</param>
    /// <returns>`true` if a provider with the given key is registered, `false` otherwise.</returns>
    public bool IsRegistered(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return false;

        _lock.EnterReadLock();
        try
        {
            return _providers.ContainsKey(providerKey);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets a snapshot of all registered provider keys.
    /// </summary>
    /// <returns>A read-only collection containing the provider keys that are currently registered; empty if no providers are registered.</returns>
    public IReadOnlyCollection<string> GetRegisteredProviders()
    {
        _lock.EnterReadLock();
        try
        {
            return _providers.Keys.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes all registered providers from the registry in a thread-safe manner.
    /// </summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _providers.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
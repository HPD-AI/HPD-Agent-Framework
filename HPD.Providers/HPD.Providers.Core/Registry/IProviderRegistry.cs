// HPD.Providers.Core/Registry/IProviderRegistry.cs
using System.Collections.Generic;

namespace HPD.Providers.Core;

/// <summary>
/// Registry for provider features across HPD ecosystem (Agent + Memory + Future products).
/// Providers self-register via ModuleInitializers on assembly load.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>
    /// Register a provider's features.
    /// Called by provider ModuleInitializers during assembly load.
    /// </summary>
    /// <summary>
/// Registers a provider's features in the central provider registry.
/// </summary>
/// <param name="features">The provider's features implementation to register; typically invoked by the provider's ModuleInitializer during assembly load.</param>
    void Register(IProviderFeatures features);

    /// <summary>
    /// Get provider features by key (case-insensitive).
    /// </summary>
    /// <param name="providerKey">Provider identifier (e.g., "openai", "anthropic", "qdrant")</param>
    /// <summary>
/// Retrieves the features for a registered provider by its key (case-insensitive).
/// </summary>
/// <param name="providerKey">Provider identifier (for example, "openai", "anthropic", "qdrant"); lookup is case-insensitive.</param>
/// <returns>The provider's <see cref="IProviderFeatures"/> if registered, or null if no matching provider is found.</returns>
    IProviderFeatures? GetProvider(string providerKey);

    /// <summary>
    /// Check if a provider is registered.
    /// </summary>
    /// <param name="providerKey">Provider identifier</param>
    /// <summary>
/// Determines whether a provider with the specified key is registered in the registry.
/// </summary>
/// <param name="providerKey">Provider identifier (case-insensitive), for example "openai", "anthropic", or "qdrant".</param>
/// <returns>`true` if a provider with the given key is registered, `false` otherwise.</returns>
    bool IsRegistered(string providerKey);

    /// <summary>
    /// Get all registered provider keys.
    /// </summary>
    /// <summary>
/// Retrieves all currently registered provider keys.
/// </summary>
/// <returns>An IReadOnlyCollection&lt;string&gt; containing the registered provider identifiers (for example "openai", "anthropic", "qdrant"); keys are the identifiers as registered and lookup is case-insensitive.</returns>
    IReadOnlyCollection<string> GetRegisteredProviders();

    /// <summary>
    /// Clear all registrations (for testing only).
    /// <summary>
/// Removes all provider registrations from the registry; intended for tests to reset global registry state.
/// </summary>
    void Clear();
}
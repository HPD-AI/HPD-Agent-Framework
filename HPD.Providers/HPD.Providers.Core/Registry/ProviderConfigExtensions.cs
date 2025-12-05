// HPD.Providers.Core/Registry/ProviderConfigExtensions.cs
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HPD.Providers.Core;

/// <summary>
/// Extension methods for ProviderConfig to support typed configuration.
/// </summary>
public static class ProviderConfigExtensions
{
    private static readonly ConditionalWeakTable<ProviderConfig, StrongBox<object?>> _cache = new();

    /// <summary>
    /// Gets the provider-specific configuration using the registered deserializer.
    /// Prefers ProviderOptionsJson (FFI-friendly), falls back to AdditionalProperties.
    /// Uses the provider's registered deserializer from ProviderConfigRegistry for AOT compatibility.
    ///
    /// Usage in providers:
    /// <code>
    /// var myConfig = config.GetTypedProviderConfig&lt;AnthropicProviderConfig&gt;();
    /// </code>
    /// </summary>
    /// <typeparam name="T">The strongly-typed configuration class</typeparam>
    /// <param name="config">The provider config instance</param>
    /// <summary>
    /// Retrieves a strongly-typed provider configuration instance for the given provider config.
    /// </summary>
    /// <param name="config">The provider configuration to read the typed settings from.</param>
    /// <returns>The parsed configuration of type <typeparamref name="T"/>, or <c>null</c> if no configuration is present.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Prefers deserializing <see cref="ProviderConfig.ProviderOptionsJson"/> using a registered provider deserializer; if that is not available or not appropriate for <typeparamref name="T"/>, falls back to deserializing <see cref="ProviderConfig.AdditionalProperties"/>. Results are cached per <see cref="ProviderConfig"/> instance to avoid repeated deserialization. Dictionary-based fallback deserialization requires runtime type information and may not be suitable for AOT scenarios.
    /// </remarks>
    public static T? GetTypedProviderConfig<T>(this ProviderConfig config) where T : class
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        // Return cached value if available and correct type
        if (_cache.TryGetValue(config, out var box) && box.Value is T cached)
            return cached;

        // Priority 1: Use ProviderOptionsJson with registered deserializer
        if (!string.IsNullOrWhiteSpace(config.ProviderOptionsJson))
        {
            var registration = ProviderConfigRegistry.GetRegistration(config.ProviderKey);
            if (registration != null && registration.ConfigType == typeof(T))
            {
                var result = registration.Deserialize(config.ProviderOptionsJson) as T;
                _cache.GetValue(config, _ => new StrongBox<object?>()).Value = result;
                return result;
            }
        }

        // Priority 2: Fall back to AdditionalProperties (legacy)
        var legacyConfig = GetProviderConfigFromDictionary<T>(config.AdditionalProperties);
        _cache.GetValue(config, _ => new StrongBox<object?>()).Value = legacyConfig;
        return legacyConfig;
    }

    /// <summary>
    /// Sets the provider-specific configuration and updates ProviderOptionsJson.
    /// Uses the provider's registered serializer from ProviderConfigRegistry for AOT compatibility.
    /// </summary>
    /// <typeparam name="T">The strongly-typed configuration class</typeparam>
    /// <param name="config">The provider config instance</param>
    /// <summary>
    /// Sets and caches a strongly-typed provider configuration on the given ProviderConfig.
    /// </summary>
    /// <param name="config">The ProviderConfig instance to update.</param>
    /// <param name="providerConfig">The configuration object to set.</param>
    /// <remarks>
    /// If a registration exists for the config's ProviderKey whose ConfigType equals T, the method serializes
    /// <paramref name="providerConfig"/> into <see cref="ProviderConfig.ProviderOptionsJson"/> using that registration's serializer.
    /// This supports AOT-friendly scenarios by preferring the provider-specific serialized representation when available.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    public static void SetTypedProviderConfig<T>(this ProviderConfig config, T providerConfig) where T : class
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        _cache.GetValue(config, _ => new StrongBox<object?>()).Value = providerConfig;

        // Serialize using registered serializer
        var registration = ProviderConfigRegistry.GetRegistration(config.ProviderKey);
        if (registration != null && registration.ConfigType == typeof(T))
        {
            config.ProviderOptionsJson = registration.Serialize(providerConfig);
        }
    }

    /// <summary>
    /// Deserializes AdditionalProperties to a strongly-typed configuration class.
    /// Legacy method - prefer GetTypedProviderConfig for FFI/AOT compatibility.
    /// </summary>
    /// <typeparam name="T">The strongly-typed configuration class</typeparam>
    /// <param name="additionalProperties">Dictionary of additional properties</param>
    /// <summary>
    /// Deserialize a dictionary of additional properties into an instance of the specified provider configuration type.
    /// </summary>
    /// <param name="additionalProperties">A dictionary representing configuration properties; expected to match the structure of <typeparamref name="T"/>.</param>
    /// <returns>The deserialized configuration of type <typeparamref name="T"/>, or <c>null</c> if <paramref name="additionalProperties"/> is null or empty.</returns>
    /// <exception cref="InvalidOperationException">Thrown when JSON serialization or deserialization fails or an unexpected error occurs while parsing the dictionary into <typeparamref name="T"/>.</exception>
    /// <remarks>This method relies on runtime type information for generic deserialization and may not be suitable for AOT scenarios.</remarks>
    [RequiresUnreferencedCode("Generic deserialization requires runtime type information. Use typed provider config methods for AOT.")]
    private static T? GetProviderConfigFromDictionary<T>(Dictionary<string, object>? additionalProperties) where T : class
    {
        if (additionalProperties == null || additionalProperties.Count == 0)
            return null;

        try
        {
            // Convert dictionary to JSON
            var json = JsonSerializer.Serialize(additionalProperties);

            // Deserialize to target type
            var result = JsonSerializer.Deserialize<T>(json);
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse provider configuration for {typeof(T).Name}. " +
                $"Please check that your AdditionalProperties match the expected structure. " +
                $"Error: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unexpected error parsing provider configuration for {typeof(T).Name}: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Helper class for weak reference caching.
/// </summary>
internal class StrongBox<T>
{
    public T? Value { get; set; }
}
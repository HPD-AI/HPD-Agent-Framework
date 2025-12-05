// HPD.Providers.Core/Registry/ProviderConfigRegistry.cs
using System;
using System.Collections.Generic;

namespace HPD.Providers.Core;

/// <summary>
/// Global registry for provider-specific configuration types.
/// Enables type-safe serialization/deserialization for FFI scenarios without core knowing concrete types.
/// Provider packages register their config types via ModuleInitializer.
/// </summary>
public static class ProviderConfigRegistry
{
    private static readonly Dictionary<string, ProviderConfigRegistration> _configTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// Registers a provider-specific configuration type for FFI serialization.
    /// Called by provider package ModuleInitializers.
    /// </summary>
    /// <typeparam name="TConfig">The provider-specific config type (e.g., AnthropicProviderConfig)</typeparam>
    /// <param name="providerKey">Provider key (e.g., "anthropic")</param>
    /// <param name="deserializer">Function to deserialize JSON to the config type</param>
    /// <param name="serializer">Function to serialize the config type to JSON</param>
    /// <example>
    /// <code>
    /// // In AnthropicProviderModule.Initialize():
    /// ProviderConfigRegistry.Register&lt;AnthropicProviderConfig&gt;(
    ///     "anthropic",
    ///     json => JsonSerializer.Deserialize(json, AnthropicJsonContext.Default.AnthropicProviderConfig),
    ///     config => JsonSerializer.Serialize(config, AnthropicJsonContext.Default.AnthropicProviderConfig));
    /// </code>
    /// <summary>
    /// Registers a provider-specific configuration type along with JSON serializer and deserializer functions under the given provider key.
    /// </summary>
    /// <param name="providerKey">Identifier for the provider used to look up the registration.</param>
    /// <param name="deserializer">Function that converts JSON into an instance of <typeparamref name="TConfig"/>; may return null.</param>
    /// <param name="serializer">Function that converts an instance of <typeparamref name="TConfig"/> into JSON.</param>
    public static void Register<TConfig>(
        string providerKey,
        Func<string, TConfig?> deserializer,
        Func<TConfig, string> serializer) where TConfig : class
    {
        lock (_lock)
        {
            _configTypes[providerKey] = new ProviderConfigRegistration(
                typeof(TConfig),
                json => deserializer(json),
                obj => serializer((TConfig)obj));
        }
    }

    /// <summary>
    /// Gets the registered config type for a provider.
    /// </summary>
    /// <param name="providerKey">Provider key (e.g., "anthropic")</param>
    /// <summary>
    /// Retrieves the registration for the specified provider key from the registry.
    /// </summary>
    /// <param name="providerKey">The provider identifier used when registering the config type.</param>
    /// <returns>The <see cref="ProviderConfigRegistration"/> for the provider, or <c>null</c> if no registration exists.</returns>
    public static ProviderConfigRegistration? GetRegistration(string providerKey)
    {
        lock (_lock)
        {
            return _configTypes.TryGetValue(providerKey, out var registration) ? registration : null;
        }
    }

    /// <summary>
    /// Deserializes provider-specific config from JSON using the registered deserializer.
    /// </summary>
    /// <param name="providerKey">Provider key (e.g., "anthropic")</param>
    /// <param name="json">JSON string to deserialize</param>
    /// <summary>
    /// Deserializes provider-specific JSON into the registered provider config object.
    /// </summary>
    /// <param name="providerKey">The provider identifier used when the config type was registered.</param>
    /// <param name="json">The JSON representation of the provider config; if null or whitespace, no deserialization is performed.</param>
    /// <returns>The deserialized provider config object, or null if the provider is not registered or the JSON is null/empty.</returns>
    public static object? Deserialize(string providerKey, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var registration = GetRegistration(providerKey);
        return registration?.Deserialize(json);
    }

    /// <summary>
    /// Serializes provider-specific config to JSON using the registered serializer.
    /// </summary>
    /// <param name="providerKey">Provider key (e.g., "anthropic")</param>
    /// <param name="config">Config object to serialize</param>
    /// <summary>
    /// Serializes a provider-specific configuration object to JSON.
    /// </summary>
    /// <param name="providerKey">The provider key used to locate the registered config type.</param>
    /// <param name="config">The provider-specific configuration object; should be an instance of the registered config type.</param>
    /// <returns>The JSON string representing the config, or null if the provider is not registered or if <paramref name="config"/> is null.</returns>
    public static string? Serialize(string providerKey, object? config)
    {
        if (config == null)
            return null;

        var registration = GetRegistration(providerKey);
        return registration?.Serialize(config);
    }

    /// <summary>
    /// Gets all registered provider config types.
    /// Used by FFI layer for schema discovery.
    /// <summary>
    /// Gets a snapshot of all registered provider config registrations keyed by provider key.
    /// </summary>
    /// <returns>A read-only dictionary mapping provider keys to their ProviderConfigRegistration instances; the returned dictionary is a copy of the registry at the time of the call.</returns>
    public static IReadOnlyDictionary<string, ProviderConfigRegistration> GetAll()
    {
        lock (_lock)
        {
            return new Dictionary<string, ProviderConfigRegistration>(_configTypes);
        }
    }

    /// <summary>
    /// For testing: clear registry.
    /// <summary>
    /// Clears all provider config registrations from the global registry.
    /// </summary>
    /// <remarks>
    /// Intended for use by tests to reset global registry state; the operation is performed under a lock.
    /// </remarks>
    internal static void ClearForTesting()
    {
        lock (_lock)
        {
            _configTypes.Clear();
        }
    }
}

/// <summary>
/// Registration info for a provider-specific configuration type.
/// Enables type-safe serialization/deserialization without core knowing the concrete type.
/// </summary>
public class ProviderConfigRegistration
{
    /// <summary>
    /// The CLR type of the provider config (e.g., typeof(AnthropicProviderConfig)).
    /// </summary>
    public Type ConfigType { get; }

    private readonly Func<string, object?> _deserializer;
    private readonly Func<object, string> _serializer;

    /// <summary>
    /// Creates a registration that associates a provider-specific config type with JSON (de)serialization delegates.
    /// </summary>
    /// <param name="configType">The concrete provider config type being registered.</param>
    /// <param name="deserializer">A delegate that deserializes JSON into an instance of the config type (may return null).</param>
    /// <param name="serializer">A delegate that serializes a config instance to its JSON representation.</param>
    public ProviderConfigRegistration(
        Type configType,
        Func<string, object?> deserializer,
        Func<object, string> serializer)
    {
        ConfigType = configType;
        _deserializer = deserializer;
        _serializer = serializer;
    }

    /// <summary>
    /// Deserializes JSON to the provider config type.
    /// <summary>
/// Deserializes the given JSON into an instance of the registered provider config type.
/// </summary>
/// <param name="json">JSON representing the provider-specific configuration.</param>
/// <returns>The deserialized config object, or null if the JSON deserializes to null.</returns>
    public object? Deserialize(string json) => _deserializer(json);

    /// <summary>
    /// Serializes the provider config to JSON.
    /// <summary>
/// Serializes a provider-specific configuration object to its JSON representation.
/// </summary>
/// <param name="config">The provider configuration instance whose type must match the registration's ConfigType.</param>
/// <returns>The JSON representation of the provided configuration.</returns>
    public string Serialize(object config) => _serializer(config);
}
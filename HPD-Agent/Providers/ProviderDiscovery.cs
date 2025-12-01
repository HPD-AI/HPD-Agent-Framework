// HPD-Agent/Providers/ProviderDiscovery.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HPD.Agent.Providers;

/// <summary>
/// Global discovery mechanism for provider packages.
/// ModuleInitializers register here, AgentBuilder copies to instance registry.
/// Also supports provider-specific configuration type registration for FFI serialization.
/// </summary>
public static class ProviderDiscovery
{
    private static readonly List<Func<IProviderFeatures>> _factories = new();
    private static readonly Dictionary<string, ProviderConfigRegistration> _configTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// Called by provider package ModuleInitializers to register a provider.
    /// </summary>
    public static void RegisterProviderFactory(Func<IProviderFeatures> factory)
    {
        lock (_lock)
        {
            _factories.Add(factory);
        }
    }

    /// <summary>
    /// Get all discovered provider factories.
    /// Called by AgentBuilder to populate its instance registry.
    /// </summary>
    internal static IEnumerable<Func<IProviderFeatures>> GetFactories()
    {
        lock (_lock)
        {
            return _factories.ToList(); // Return copy for thread safety
        }
    }

    /// <summary>
    /// For testing: clear discovery registry.
    /// </summary>
    internal static void ClearForTesting()
    {
        lock (_lock)
        {
            _factories.Clear();
        }
    }

    /// <summary>
    /// Explicitly loads a provider package to trigger its ModuleInitializer.
    /// Required for Native AOT scenarios where automatic assembly loading is not available.
    /// In non-AOT scenarios, AgentBuilder automatically discovers and loads provider assemblies.
    /// </summary>
    /// <typeparam name="TProviderModule">The provider module type (e.g., HPD_Agent.Providers.OpenRouter.OpenRouterProviderModule)</typeparam>
    /// <example>
    /// <code>
    /// // Native AOT: Explicitly load providers before creating AgentBuilder
    /// ProviderDiscovery.LoadProvider&lt;HPD_Agent.Providers.OpenRouter.OpenRouterProviderModule&gt;();
    /// var agent = new AgentBuilder(config).Build();
    /// </code>
    /// </example>
    public static void LoadProvider<TProviderModule>() where TProviderModule : class
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TProviderModule).Module.ModuleHandle);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PROVIDER CONFIG TYPE REGISTRATION (For FFI/JSON serialization)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a provider-specific configuration type for FFI serialization.
    /// Called by provider package ModuleInitializers alongside RegisterProviderFactory.
    /// </summary>
    /// <typeparam name="TConfig">The provider-specific config type (e.g., AnthropicProviderConfig)</typeparam>
    /// <param name="providerKey">Provider key (e.g., "anthropic")</param>
    /// <param name="deserializer">Function to deserialize JSON to the config type</param>
    /// <param name="serializer">Function to serialize the config type to JSON</param>
    /// <example>
    /// <code>
    /// // In AnthropicProviderModule.Initialize():
    /// ProviderDiscovery.RegisterProviderConfigType&lt;AnthropicProviderConfig&gt;(
    ///     "anthropic",
    ///     json => JsonSerializer.Deserialize(json, AnthropicJsonContext.Default.AnthropicProviderConfig),
    ///     config => JsonSerializer.Serialize(config, AnthropicJsonContext.Default.AnthropicProviderConfig));
    /// </code>
    /// </example>
    public static void RegisterProviderConfigType<TConfig>(
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
    /// <returns>Registration info, or null if not registered</returns>
    public static ProviderConfigRegistration? GetProviderConfigType(string providerKey)
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
    /// <returns>Deserialized config object, or null if provider not registered or JSON is empty</returns>
    public static object? DeserializeProviderConfig(string providerKey, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var registration = GetProviderConfigType(providerKey);
        return registration?.Deserialize(json);
    }

    /// <summary>
    /// Serializes provider-specific config to JSON using the registered serializer.
    /// </summary>
    /// <param name="providerKey">Provider key (e.g., "anthropic")</param>
    /// <param name="config">Config object to serialize</param>
    /// <returns>JSON string, or null if provider not registered or config is null</returns>
    public static string? SerializeProviderConfig(string providerKey, object? config)
    {
        if (config == null)
            return null;

        var registration = GetProviderConfigType(providerKey);
        return registration?.Serialize(config);
    }

    /// <summary>
    /// Gets all registered provider config types.
    /// Used by FFI layer for schema discovery.
    /// </summary>
    public static IReadOnlyDictionary<string, ProviderConfigRegistration> GetAllConfigTypes()
    {
        lock (_lock)
        {
            return new Dictionary<string, ProviderConfigRegistration>(_configTypes);
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
    /// </summary>
    public object? Deserialize(string json) => _deserializer(json);

    /// <summary>
    /// Serializes the provider config to JSON.
    /// </summary>
    public string Serialize(object config) => _serializer(config);
}

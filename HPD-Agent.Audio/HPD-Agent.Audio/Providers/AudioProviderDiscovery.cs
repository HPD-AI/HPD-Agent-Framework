// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Runtime.CompilerServices;

namespace HPD.Agent.Audio.Providers;

/// <summary>
/// Global discovery mechanism for audio provider packages.
/// ModuleInitializers register here, AgentBuilder/AudioPipelineMiddleware use this for discovery.
/// Also supports audio provider-specific configuration type registration for FFI serialization.
/// </summary>
public static class AudioProviderDiscovery
{
    private static readonly List<Func<IAudioProviderFeatures>> _factories = new();
    private static readonly Dictionary<string, AudioProviderConfigRegistration> _configTypes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// Called by audio provider package ModuleInitializers to register a provider.
    /// </summary>
    /// <param name="factory">Factory function that creates provider instance</param>
    /// <example>
    /// <code>
    /// [ModuleInitializer]
    /// public static void Initialize()
    /// {
    ///     AudioProviderDiscovery.RegisterProviderFactory(() => new OpenAIAudioProvider());
    /// }
    /// </code>
    /// </example>
    public static void RegisterProviderFactory(Func<IAudioProviderFeatures> factory)
    {
        lock (_lock)
        {
            _factories.Add(factory);
        }
    }

    /// <summary>
    /// Get all discovered audio provider factories.
    /// Called by AgentBuilder or AudioPipelineMiddleware to populate provider registry.
    /// </summary>
    /// <returns>Enumerable of provider factory functions</returns>
    public static IEnumerable<Func<IAudioProviderFeatures>> GetFactories()
    {
        lock (_lock)
        {
            return _factories.ToList(); // Return copy for thread safety
        }
    }

    /// <summary>
    /// Get all discovered audio providers (materialized instances).
    /// </summary>
    /// <returns>Enumerable of provider instances</returns>
    public static IEnumerable<IAudioProviderFeatures> GetProviders()
    {
        lock (_lock)
        {
            return _factories.Select(f => f()).ToList();
        }
    }

    /// <summary>
    /// Find a specific audio provider by key.
    /// </summary>
    /// <param name="providerKey">Provider key (case-insensitive)</param>
    /// <returns>Provider instance, or null if not found</returns>
    public static IAudioProviderFeatures? GetProvider(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return null;

        lock (_lock)
        {
            foreach (var factory in _factories)
            {
                var provider = factory();
                if (string.Equals(provider.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
                    return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Explicitly loads an audio provider package to trigger its ModuleInitializer.
    /// Required for Native AOT scenarios where automatic assembly loading is not available.
    /// </summary>
    /// <typeparam name="TProviderModule">The audio provider module type</typeparam>
    /// <example>
    /// <code>
    /// // Native AOT: Explicitly load providers before creating agent
    /// AudioProviderDiscovery.LoadProvider&lt;OpenAIAudioProviderModule&gt;();
    /// var agent = new AgentBuilder(config).Build();
    /// </code>
    /// </example>
    public static void LoadProvider<TProviderModule>() where TProviderModule : class
    {
        RuntimeHelpers.RunModuleConstructor(typeof(TProviderModule).Module.ModuleHandle);
    }

    /// <summary>
    /// For testing: clear discovery registry.
    /// </summary>
    internal static void ClearForTesting()
    {
        lock (_lock)
        {
            _factories.Clear();
            _configTypes.Clear();
        }
    }

    /// <summary>
    /// For testing: get count of registered providers.
    /// </summary>
    internal static int GetProviderCount()
    {
        lock (_lock)
        {
            return _factories.Count;
        }
    }

    //
    // AUDIO PROVIDER CONFIG TYPE REGISTRATION (For FFI/JSON serialization)
    //

    /// <summary>
    /// Registers an audio provider-specific configuration type for FFI serialization.
    /// Called by audio provider package ModuleInitializers alongside RegisterProviderFactory.
    /// </summary>
    /// <typeparam name="TConfig">The provider-specific config type</typeparam>
    /// <param name="providerKey">Provider key (e.g., "openai-audio")</param>
    /// <param name="deserializer">Function to deserialize JSON to config object</param>
    /// <param name="serializer">Function to serialize config object to JSON</param>
    /// <example>
    /// <code>
    /// AudioProviderDiscovery.RegisterProviderConfigType&lt;OpenAIAudioConfig&gt;(
    ///     "openai-audio",
    ///     json => JsonSerializer.Deserialize&lt;OpenAIAudioConfig&gt;(json),
    ///     config => JsonSerializer.Serialize(config)
    /// );
    /// </code>
    /// </example>
    public static void RegisterProviderConfigType<TConfig>(
        string providerKey,
        Func<string, TConfig?> deserializer,
        Func<TConfig, string> serializer)
    {
        lock (_lock)
        {
            _configTypes[providerKey] = new AudioProviderConfigRegistration(
                typeof(TConfig),
                json => deserializer(json),
                obj => serializer((TConfig)obj)
            );
        }
    }

    /// <summary>
    /// Get registered config type for a provider.
    /// </summary>
    /// <param name="providerKey">Provider key</param>
    /// <returns>Config registration, or null if not found</returns>
    public static AudioProviderConfigRegistration? GetProviderConfigType(string providerKey)
    {
        lock (_lock)
        {
            return _configTypes.TryGetValue(providerKey, out var registration) ? registration : null;
        }
    }

    /// <summary>
    /// Deserialize provider-specific config from JSON.
    /// </summary>
    /// <param name="providerKey">Provider key</param>
    /// <param name="json">JSON string</param>
    /// <returns>Deserialized config object, or null if provider not registered</returns>
    public static object? DeserializeProviderConfig(string providerKey, string json)
    {
        var registration = GetProviderConfigType(providerKey);
        return registration?.Deserialize(json);
    }

    /// <summary>
    /// Serialize provider-specific config to JSON.
    /// </summary>
    /// <param name="providerKey">Provider key</param>
    /// <param name="config">Config object</param>
    /// <returns>JSON string</returns>
    public static string? SerializeProviderConfig(string providerKey, object config)
    {
        var registration = GetProviderConfigType(providerKey);
        return registration?.Serialize(config);
    }
}

/// <summary>
/// Registration for an audio provider-specific configuration type.
/// Enables FFI/JSON serialization of provider configs.
/// </summary>
public class AudioProviderConfigRegistration
{
    /// <summary>
    /// The CLR type of the provider config (e.g., typeof(OpenAIAudioConfig)).
    /// </summary>
    public Type ConfigType { get; }

    private readonly Func<string, object?> _deserializer;
    private readonly Func<object, string> _serializer;

    public AudioProviderConfigRegistration(
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

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using Azure.AI.Inference;
using Azure;
using OllamaSharp;
using System.Diagnostics;

/// <summary>
/// Builder for creating dual interface agents with sophisticated capabilities
/// This is your equivalent of the AgentBuilder from Semantic Kernel, but for the new architecture
/// </summary>
public class AgentBuilder
{
    // The new central configuration object
    private readonly AgentConfig _config;

    // Fields that are NOT part of the serializable config remain
    internal IChatClient? _baseClient;
    internal IConfiguration? _configuration;
    internal readonly PluginManager _pluginManager = new();
    internal IPluginMetadataContext? _defaultContext;
    // store individual plugin contexts
    internal readonly Dictionary<string, IPluginMetadataContext?> _pluginContexts = new();
    private readonly List<IAiFunctionFilter> _globalFilters = new();
    internal readonly ScopedFilterManager _scopedFilterManager = new();
    internal readonly BuilderScopeContext _scopeContext = new();
    internal readonly List<IPromptFilter> _promptFilters = new();
    internal IAiFunctionFilter? _permissionFilter; // Dedicated permission filter

    // Audio capability fields (runtime only) - made internal for extensions
    internal ISpeechToTextClient? _sttClient;
    internal ITextToSpeechClient? _ttsClient;
    internal readonly Dictionary<Type, object> _providerConfigs = new();
    internal IServiceProvider? _serviceProvider;
    internal ILoggerFactory? _logger;

    // Memory Injected Memory runtime fields
    internal AgentInjectedMemoryManager? _memoryInjectedManager;  // track externally provided manager

    // MCP runtime fields
    internal MCPClientManager? _mcpClientManager;

    // Permission system integration
    private ContinuationPermissionManager? _continuationPermissionManager;

    /// <summary>
    /// Creates a new builder with a default configuration.
    /// </summary>
    public AgentBuilder()
    {
        _config = new AgentConfig();
    }

    /// <summary>
    /// Creates a new builder from a pre-existing configuration object.
    /// </summary>
    /// <param name="config">The configuration to use.</param>
    public AgentBuilder(AgentConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Sets the system instructions/persona for the agent
    /// </summary>
    public AgentBuilder WithInstructions(string instructions)
    {
        _config.SystemInstructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        return this;
    }

    /// <summary>
    /// Provides the service provider for resolving dependencies.
    /// This is required if you use `UseRegisteredEmbeddingGenerator()` for contextual functions.
    /// </summary>
    public AgentBuilder WithServiceProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetService<ILoggerFactory>();
        return this;
    }


    /// <summary>
    /// Configures the maximum number of function calls allowed in a single conversation turn
    /// </summary>
    /// <param name="maxFunctionCalls">Maximum number of function calls (default: 10)</param>
    public AgentBuilder WithMaxFunctionCalls(int maxFunctionCalls)
    {
        if (maxFunctionCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFunctionCalls), "Maximum function calls must be greater than 0");

        _config.MaxFunctionCalls = maxFunctionCalls;
        return this;
    }

    /// <summary>
    /// Wraps the base chat client with OpenTelemetry middleware to enable standardized telemetry.
    /// This should be called after the base client has been configured via WithProvider() or WithBaseClient().
    /// </summary>
    /// <param name="sourceName">An optional source name for the telemetry data. Defaults to "Experimental.Microsoft.Extensions.AI".</param>
    /// <param name="configure">An optional callback to configure the OpenTelemetryChatClient instance.</param>
    public AgentBuilder WithOpenTelemetry(string? sourceName = null, Action<OpenTelemetryChatClient>? configure = null)
    {
        // This method must be called after a base client is available.
        if (_baseClient == null)
        {
            throw new InvalidOperationException("WithOpenTelemetry() must be called after WithProvider() or WithBaseClient().");
        }

        // The AgentBuilder needs access to an ILoggerFactory to pass to the telemetry client.
        // We can get this from the IServiceProvider if one was provided.
        var loggerFactory = _serviceProvider?.GetService<ILoggerFactory>();

        // Use the AsBuilder() and UseOpenTelemetry() extension methods from Microsoft.Extensions.AI
        // to wrap the current _baseClient in the telemetry middleware.
        var builder = new ChatClientBuilder(_baseClient);
        builder.UseOpenTelemetry(loggerFactory, sourceName, configure);

        // Replace the existing base client with the newly built pipeline that includes telemetry.
        _baseClient = builder.Build(_serviceProvider);

        return this;
    }


    /// <summary>
    /// Sets the agent name
    /// </summary>
    public AgentBuilder WithName(string name)
    {
        _config.Name = name ?? throw new ArgumentNullException(nameof(name));
        return this;
    }

    /// <summary>
    /// Sets default chat options (including backend tools)
    /// </summary>
    public AgentBuilder WithDefaultOptions(ChatOptions options)
    {
        if (_config.Provider == null)
            _config.Provider = new ProviderConfig();
        _config.Provider.DefaultChatOptions = options;
        return this;
    }





    /// <summary>
    /// Builds the dual interface agent
    /// </summary>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public Agent Build()
    {
        // === START: VALIDATION LOGIC ===
        // Validate the core agent configuration
        var agentConfigValidator = new AgentConfigValidator();
        agentConfigValidator.ValidateAndThrow(_config);

        // Validate provider-specific configurations if they exist
        if (_config.WebSearch?.Tavily != null)
        {
            var tavilyValidator = new TavilyConfigValidator();
            tavilyValidator.ValidateAndThrow(_config.WebSearch.Tavily);
        }
        // TODO: Add similar validation blocks for Brave, Bing, ElevenLabs, AzureSpeech etc.
        // === END: VALIDATION LOGIC ===

        // Ensure base client is available by creating from provider if needed
        this.EnsureBaseClientFromProvider();

        if (_baseClient == null)
            throw new InvalidOperationException("Base client must be provided using WithBaseClient() or WithProvider()");

        // Register Memory Injected Memory prompt filter if configured
        if (_config.InjectedMemory != null && MemoryInjectedManager != null)
        {
            var memoryOptions = new AgentInjectedMemoryOptions
            {
                StorageDirectory = _config.InjectedMemory.StorageDirectory,
                MaxTokens = _config.InjectedMemory.MaxTokens,
                EnableAutoEviction = _config.InjectedMemory.EnableAutoEviction,
                AutoEvictionThreshold = _config.InjectedMemory.AutoEvictionThreshold
            };
            var memoryFilter = new AgentInjectedMemoryFilter(memoryOptions, _logger?.CreateLogger<AgentInjectedMemoryFilter>());
            _promptFilters.Add(memoryFilter);
        }

        // Automatically finalize web search configuration if providers were configured
        AgentBuilderWebSearchExtensions.FinalizeWebSearch(this);

        // Create plugin functions using per-plugin contexts and merge with default options
        var pluginFunctions = new List<AIFunction>();
        foreach (var registration in _pluginManager.GetPluginRegistrations())
        {
            var pluginName = registration.PluginType.Name;
            _pluginContexts.TryGetValue(pluginName, out var ctx);
            var functions = registration.ToAIFunctions(ctx ?? _defaultContext);
            pluginFunctions.AddRange(functions);
        }

        // Register function-to-plugin mappings for scoped filters
        RegisterFunctionPluginMappings(pluginFunctions);

        // Load MCP tools if configured
        if (McpClientManager != null)
        {
            try
            {
                List<AIFunction> mcpTools;
                if (_config.Mcp != null && !string.IsNullOrEmpty(_config.Mcp.ManifestPath))
                {
                    // Check if this is actually content vs path based on if it starts with '{'
                    if (_config.Mcp.ManifestPath.TrimStart().StartsWith("{"))
                    {
                        mcpTools = McpClientManager.LoadToolsFromManifestContentAsync(_config.Mcp.ManifestPath).GetAwaiter().GetResult();
                    }
                    else
                    {
                        mcpTools = McpClientManager.LoadToolsFromManifestAsync(_config.Mcp.ManifestPath).GetAwaiter().GetResult();
                    }
                }
                else
                {
                    throw new InvalidOperationException("MCP client manager is configured but no manifest path or content provided");
                }

                pluginFunctions.AddRange(mcpTools);
                _logger?.CreateLogger<AgentBuilder>().LogInformation("Successfully integrated {Count} MCP tools into agent", mcpTools.Count);
            }
            catch (Exception ex)
            {
                _logger?.CreateLogger<AgentBuilder>().LogError(ex, "Failed to load MCP tools: {Error}", ex.Message);
                throw new InvalidOperationException("Failed to initialize MCP integration", ex);
            }
        }

        var mergedOptions = MergePluginFunctions(_config.Provider?.DefaultChatOptions, pluginFunctions);


        // Create agent using the new, cleaner constructor with AgentConfig
        var agent = new Agent(
            _config,
            _baseClient,
            mergedOptions, // Pass the merged options directly
            _promptFilters,
            _scopedFilterManager,
            _permissionFilter,
            _continuationPermissionManager);

        // Attach audio capability if configured
        var audioCapability = this.CreateAudioCapability(agent);
        if (audioCapability != null)
        {
            agent.AddCapability("Audio", audioCapability);
        }

        // Attach MCP capability if configured
        if (McpClientManager != null)
        {
            agent.AddCapability("MCP", McpClientManager);
        }

        return agent;
    }

    #region Helper Methods



    /// <summary>
    /// Registers function-to-plugin mappings for scoped filter support
    /// </summary>
    [RequiresUnreferencedCode("This method uses reflection to call generated plugin registration code.")]
    private void RegisterFunctionPluginMappings(List<AIFunction> pluginFunctions)
    {
        // Map functions to plugins for scoped filter support, using per-plugin contexts
        var pluginRegistrations = _pluginManager.GetPluginRegistrations();
        foreach (var registration in pluginRegistrations)
        {
            try
            {
                var pluginName = registration.PluginType.Name;
                _pluginContexts.TryGetValue(pluginName, out var ctx);
                var functions = registration.ToAIFunctions(ctx ?? _defaultContext);
                foreach (var function in functions)
                {
                    _scopedFilterManager.RegisterFunctionPlugin(function.Name, pluginName);
                }
            }
            catch (Exception)
            {
                // Ignore errors during mapping - filters will still work at global/function level
            }
        }
    }

    /// <summary>
    /// Merges plugin functions into chat options.
    /// </summary>
    private ChatOptions? MergePluginFunctions(ChatOptions? defaultOptions, List<AIFunction> pluginFunctions)
    {
        if (pluginFunctions.Count == 0)
            return defaultOptions;

        var options = defaultOptions ?? new ChatOptions();

        // Add plugin functions to existing tools
        var allTools = new List<AITool>(options.Tools ?? []);
        allTools.AddRange(pluginFunctions);

        return new ChatOptions
        {
            Tools = allTools,
            ToolMode = ChatToolMode.Auto, // Enable function calling!
            MaxOutputTokens = options.MaxOutputTokens,
            Temperature = options.Temperature,
            TopP = options.TopP,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            ResponseFormat = options.ResponseFormat,
            Seed = options.Seed,
            StopSequences = options.StopSequences,
            ModelId = options.ModelId,
            AdditionalProperties = options.AdditionalProperties
        };
    }


    /// <summary>
    /// Creates a new builder instance
    /// </summary>
    public static AgentBuilder Create() => new();


    // Public properties for extension methods
    /// <summary>
    /// Gets the agent name for use in extension methods
    /// </summary>
    public string AgentName => _config.Name;

    /// <summary>
    /// Internal access to configuration for extension methods
    /// </summary>
    internal IConfiguration? Configuration => _configuration;

    /// <summary>
    /// Internal access to config object for extension methods
    /// </summary>
    internal AgentConfig Config => _config;

    /// <summary>
    /// Internal access to base client for extension methods
    /// </summary>
    internal IChatClient? BaseClient
    {
        get => _baseClient;
        set => _baseClient = value;
    }

    /// <summary>
    /// Internal access to STT client for extension methods
    /// </summary>
    internal ISpeechToTextClient? SttClient
    {
        get => _sttClient;
        set => _sttClient = value;
    }

    /// <summary>
    /// Internal access to TTS client for extension methods
    /// </summary>
    internal ITextToSpeechClient? TtsClient
    {
        get => _ttsClient;
        set => _ttsClient = value;
    }

    /// <summary>
    /// Internal access to provider configs for extension methods
    /// </summary>
    internal Dictionary<Type, object> ProviderConfigs => _providerConfigs;

    /// <summary>
    /// Internal access to service provider for extension methods
    /// </summary>
    internal IServiceProvider? ServiceProvider => _serviceProvider;

    /// <summary>
    /// Internal access to logger for extension methods
    /// </summary>
    internal ILoggerFactory? Logger => _logger;

    /// <summary>
    /// Internal access to scoped filter manager for extension methods
    /// </summary>
    internal ScopedFilterManager ScopedFilterManager => _scopedFilterManager;

    /// <summary>
    /// Internal access to plugin manager for extension methods
    /// </summary>
    internal PluginManager PluginManager => _pluginManager;

    /// <summary>
    /// Internal access to default plugin context for extension methods
    /// </summary>
    internal IPluginMetadataContext? DefaultContext
    {
        get => _defaultContext;
        set => _defaultContext = value;
    }

    /// <summary>
    /// Internal access to plugin contexts for extension methods
    /// </summary>
    internal Dictionary<string, IPluginMetadataContext?> PluginContexts => _pluginContexts;

    /// <summary>
    /// Internal access to scope context for extension methods
    /// </summary>
    internal BuilderScopeContext ScopeContext => _scopeContext;

    /// <summary>
    /// Internal access to prompt filters for extension methods
    /// </summary>
    internal List<IPromptFilter> PromptFilters => _promptFilters;

    /// <summary>
    /// Internal access to permission filter for extension methods
    /// </summary>
    internal IAiFunctionFilter? PermissionFilter
    {
        get => _permissionFilter;
        set => _permissionFilter = value;
    }

    /// <summary>
    /// Internal access to MCP client manager for extension methods
    /// </summary>
    internal MCPClientManager? McpClientManager
    {
        get => _mcpClientManager;
        set => _mcpClientManager = value;
    }

    /// <summary>
    /// Internal access to memory injected manager for extension methods
    /// </summary>
    internal AgentInjectedMemoryManager? MemoryInjectedManager
    {
        get => _memoryInjectedManager;
        set => _memoryInjectedManager = value;
    }

    #endregion

    public AgentBuilder WithContinuationManager(ContinuationPermissionManager manager)
    {
        _continuationPermissionManager = manager;
        return this;
    }
}

#region Audio Extensions
/// <summary>
/// Extension methods for configuring Audio (TTS/STT) capabilities for the AgentBuilder.
/// </summary>
public static class AgentBuilderAudioExtensions
{
    /// <summary>Register STT client directly</summary>
    public static AgentBuilder WithSpeechToText(this AgentBuilder builder, ISpeechToTextClient sttClient)
    {
        builder.SttClient = sttClient ?? throw new ArgumentNullException(nameof(sttClient));
        return builder;
    }

    /// <summary>Register TTS client directly</summary>
    public static AgentBuilder WithTextToSpeech(this AgentBuilder builder, ITextToSpeechClient ttsClient)
    {
        builder.TtsClient = ttsClient ?? throw new ArgumentNullException(nameof(ttsClient));
        return builder;
    }

    /// <summary>Configure audio capability options</summary>
    public static AgentBuilder WithAudioOptions(this AgentBuilder builder, Action<AudioCapabilityOptions> configure)
    {
        if (builder.Config.Audio == null)
            builder.Config.Audio = new AudioConfig();
        builder.Config.Audio.Options ??= new AudioCapabilityOptions();
        configure(builder.Config.Audio.Options);
        return builder;
    }

    /// <summary>ElevenLabs STT with configuration object</summary>
    public static AgentBuilder WithElevenLabsSpeechToText(this AgentBuilder builder, ElevenLabsConfig config)
    {
        var client = new ElevenLabsSpeechToTextClient(config, null, builder.Logger?.CreateLogger<ElevenLabsSpeechToTextClient>());
        return builder.WithSpeechToText(client);
    }

    /// <summary>ElevenLabs TTS with configuration object</summary>
    public static AgentBuilder WithElevenLabsTextToSpeech(this AgentBuilder builder, ElevenLabsConfig config, string? voiceId = null)
    {
        var client = new ElevenLabsTextToSpeechClient(config, voiceId, null,
            builder.Logger?.CreateLogger<ElevenLabsTextToSpeechClient>());
        return builder.WithTextToSpeech(client);
    }

    /// <summary>ElevenLabs complete pipeline</summary>
    public static AgentBuilder WithElevenLabsAudio(this AgentBuilder builder, string? apiKey = null, string? voiceId = null)
    {
        var config = builder.ResolveOrCreateConfig<ElevenLabsConfig>();
        if (!string.IsNullOrEmpty(apiKey))
            config.ApiKey = apiKey;
        if (!string.IsNullOrEmpty(voiceId))
            config.DefaultVoiceId = voiceId;

        // Store in config
        if (builder.Config.Audio == null)
            builder.Config.Audio = new AudioConfig();
        builder.Config.Audio.ElevenLabs = config;

        return builder.WithElevenLabsSpeechToText(config)
               .WithElevenLabsTextToSpeech(config, voiceId);
    }

    /// <summary>Azure Speech STT with configuration object</summary>
    public static AgentBuilder WithAzureSpeechToText(this AgentBuilder builder, AzureSpeechConfig config)
    {
        config.Validate();
        var client = new AzureSpeechToTextClient(config, builder.Logger?.CreateLogger<AzureSpeechToTextClient>());
        return builder.WithSpeechToText(client);
    }

    /// <summary>Azure Speech STT with key parameters</summary>
    public static AgentBuilder WithAzureSpeechToText(this AgentBuilder builder, string apiKey, string region, string? language = null)
    {
        var config = new AzureSpeechConfig
        {
            ApiKey = apiKey,
            Region = region,
            DefaultLanguage = language ?? "en-US"
        };
        return builder.WithAzureSpeechToText(config);
    }

    /// <summary>Azure Speech TTS with configuration object</summary>
    public static AgentBuilder WithAzureTextToSpeech(this AgentBuilder builder, AzureSpeechConfig config)
    {
        config.Validate();
        var client = new AzureTextToSpeechClient(config, builder.Logger?.CreateLogger<AzureTextToSpeechClient>());
        return builder.WithTextToSpeech(client);
    }

    /// <summary>Azure Speech complete pipeline</summary>
    public static AgentBuilder WithAzureSpeechAudio(this AgentBuilder builder, string apiKey, string region, string? voice = null)
    {
        var config = new AzureSpeechConfig
        {
            ApiKey = apiKey,
            Region = region,
            DefaultVoice = voice ?? "en-US-AriaNeural"
        };

        // Store in config
        if (builder.Config.Audio == null)
            builder.Config.Audio = new AudioConfig();
        builder.Config.Audio.AzureSpeech = config;

        return builder.WithAzureSpeechToText(config)
               .WithAzureTextToSpeech(config);
    }

    /// <summary>Create audio capability during build</summary>
    internal static AudioCapability? CreateAudioCapability(this AgentBuilder builder, global::Agent agent)
    {
        // Skip if no audio clients configured
        if (builder.SttClient == null && builder.TtsClient == null)
            return null;

        var options = builder.Config.Audio?.Options ?? new AudioCapabilityOptions();

        return new AudioCapability(
            agent: agent,
            options: options,
            sttClient: builder.SttClient,
            ttsClient: builder.TtsClient,
            filterManager: builder.ScopedFilterManager,
            logger: builder.Logger?.CreateLogger<AudioCapability>());
    }

    private static T ResolveOrCreateConfig<T>(this AgentBuilder builder) where T : class, new()
    {
        return AgentBuilderHelpers.ResolveOrCreateConfig<T>(builder.ProviderConfigs);
    }


    private static ElevenLabsConfig? CreateElevenLabsConfigFromEnvironment()
    {
        var config = AgentBuilderHelpers.CreateElevenLabsConfigFromEnvironment();
        return string.IsNullOrEmpty(config.ApiKey) ? null : config;
    }

    private static AzureSpeechConfig? CreateAzureConfigFromEnvironment()
    {
        var config = AgentBuilderHelpers.CreateAzureConfigFromEnvironment();
        return string.IsNullOrEmpty(config.ApiKey) ? null : config;
    }

}
#endregion

#region Filter Extensions
/// <summary>
/// Extension methods for configuring prompt and function filters for the AgentBuilder.
/// </summary>
public static class AgentBuilderFilterExtensions
{
    /// <summary>
    /// Adds Function Invocation filters that apply to all tool calls in conversations
    /// </summary>
    public static AgentBuilder WithFunctionInvokationFilters(this AgentBuilder builder, params IAiFunctionFilter[] filters)
    {
        if (filters != null)
        {
            foreach (var filter in filters)
            {
                builder.ScopedFilterManager.AddFilter(filter, builder.ScopeContext.CurrentScope, builder.ScopeContext.CurrentTarget);
            }
        }
        return builder;
    }

    /// <summary>
    /// Adds an Function Invocation filter by type (will be instantiated)
    /// </summary>
    public static AgentBuilder WithFunctionInvocationFilter<T>(this AgentBuilder builder) where T : IAiFunctionFilter, new()
    {
        var filter = new T();
        builder.ScopedFilterManager.AddFilter(filter, builder.ScopeContext.CurrentScope, builder.ScopeContext.CurrentTarget);
        return builder;
    }

    /// <summary>
    /// Adds an function filter instance
    /// </summary>
    public static AgentBuilder WithFilter(this AgentBuilder builder, IAiFunctionFilter filter)
    {
        if (filter is FunctionPermissionFilter pFilter)
        {
            // Store permission filter separately
            builder.PermissionFilter = pFilter;
        }
        else if (filter != null)
        {
            builder.ScopedFilterManager.AddFilter(filter, builder.ScopeContext.CurrentScope, builder.ScopeContext.CurrentTarget);
        }
        return builder;
    }

    /// <summary>
    /// Adds a prompt filter instance
    /// </summary>
    public static AgentBuilder WithPromptFilter(this AgentBuilder builder, IPromptFilter filter)
    {
        if (filter != null)
        {
            builder.PromptFilters.Add(filter);
        }
        return builder;
    }

    /// <summary>
    /// Adds a prompt filter by type (will be instantiated)
    /// </summary>
    public static AgentBuilder WithPromptFilter<T>(this AgentBuilder builder) where T : IPromptFilter, new()
        => builder.WithPromptFilter(new T());

    /// <summary>
    /// Adds multiple prompt filters
    /// </summary>
    public static AgentBuilder WithPromptFilters(this AgentBuilder builder, params IPromptFilter[] filters)
    {
        if (filters != null)
        {
            foreach (var f in filters)
                builder.PromptFilters.Add(f);
        }
        return builder;
    }
}

#endregion

#region MCP Extensions
/// <summary>
/// Extension methods for configuring Model Context Protocol (MCP) capabilities for the AgentBuilder.
/// </summary>
public static class AgentBuilderMcpExtensions
{
    /// <summary>
    /// Enables MCP support with the specified manifest file
    /// </summary>
    /// <param name="manifestPath">Path to the MCP manifest JSON file</param>
    /// <param name="options">Optional MCP configuration options</param>
    public static AgentBuilder WithMCP(this AgentBuilder builder, string manifestPath, MCPOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Manifest path cannot be null or empty", nameof(manifestPath));

        builder.Config.Mcp = new McpConfig
        {
            ManifestPath = manifestPath,
            Options = options
        };
        builder.McpClientManager = new MCPClientManager(builder.Logger?.CreateLogger<MCPClientManager>() ?? NullLogger<MCPClientManager>.Instance, options);

        return builder;
    }

    /// <summary>
    /// Enables MCP support with fluent configuration
    /// </summary>
    /// <param name="manifestPath">Path to the MCP manifest JSON file</param>
    /// <param name="configure">Configuration action for MCP options</param>
    public static AgentBuilder WithMCP(this AgentBuilder builder, string manifestPath, Action<MCPOptions> configure)
    {
        var options = new MCPOptions();
        configure(options);
        return builder.WithMCP(manifestPath, options);
    }

    /// <summary>
    /// Enables MCP support with manifest content directly
    /// </summary>
    /// <param name="manifestContent">JSON content of the MCP manifest</param>
    /// <param name="options">Optional MCP configuration options</param>
    public static AgentBuilder WithMCPContent(this AgentBuilder builder, string manifestContent, MCPOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(manifestContent))
            throw new ArgumentException("Manifest content cannot be null or empty", nameof(manifestContent));

        // Store content in ManifestPath for now - we might need a separate property for content
        builder.Config.Mcp = new McpConfig
        {
            ManifestPath = manifestContent, // This represents content, not path
            Options = options
        };
        builder.McpClientManager = new MCPClientManager(builder.Logger?.CreateLogger<MCPClientManager>() ?? NullLogger<MCPClientManager>.Instance, options);

        return builder;
    }
}

#endregion

#region Memory Extensions
/// <summary>
/// Extension methods for configuring agent-specific memory capabilities.
/// </summary>
public static class AgentBuilderMemoryExtensions
{
    /// <summary>
    /// Configures the agent's deep, static, read-only knowledge base.
    /// This utilizes an Indexed Retrieval (RAG) system for the agent's core expertise.
    /// </summary>
    /// 
    /*
    public static AgentBuilder WithKnowledgeBase(this AgentBuilder builder)
    {

        return builder;
    }
    */

    /// <summary>
    /// Configures the agent's dynamic, editable working memory.
    /// This enables a Full Text Injection (formerly CAG) system and provides the agent
    /// with tools to manage its own persistent facts.
    /// </summary>
    public static AgentBuilder WithInjectedMemory(this AgentBuilder builder, Action<AgentInjectedMemoryOptions> configure)
    {
        var options = new AgentInjectedMemoryOptions();
        configure(options);

        // Set the config on the builder
        builder.Config.InjectedMemory = new InjectedMemoryConfig
        {
            StorageDirectory = options.StorageDirectory,
            MaxTokens = options.MaxTokens,
            EnableAutoEviction = options.EnableAutoEviction,
            AutoEvictionThreshold = options.AutoEvictionThreshold
        };

        var manager = new AgentInjectedMemoryManager(options.StorageDirectory);
        builder.MemoryInjectedManager = manager; // Set the internal property on the builder

        var plugin = new AgentInjectedMemoryPlugin(manager, builder.AgentName);
        var filter = new AgentInjectedMemoryFilter(options);

        // Register plugin and filter directly without cross-extension dependencies
        RegisterInjectedMemoryPlugin(builder, plugin);
        RegisterInjectedMemoryFilter(builder, filter);

        return builder;
    }

    /// <summary>
    /// Registers the memory plugin directly with the builder's plugin manager
    /// Avoids dependency on AgentBuilderPluginExtensions
    /// </summary>
    private static void RegisterInjectedMemoryPlugin(AgentBuilder builder, AgentInjectedMemoryPlugin plugin)
    {
        builder.PluginManager.RegisterPlugin(plugin);
        var pluginName = typeof(AgentInjectedMemoryPlugin).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = null; // No special context needed for memory plugin
    }

    /// <summary>
    /// Registers the memory filter directly with the builder's filter manager
    /// Avoids dependency on AgentBuilderFilterExtensions
    /// </summary>
    private static void RegisterInjectedMemoryFilter(AgentBuilder builder, AgentInjectedMemoryFilter filter)
    {
        builder.PromptFilters.Add(filter);
    }
}
#endregion

#region Plugin Extensions


/// <summary>
/// Extension methods for configuring plugins for the AgentBuilder.
/// </summary>
public static class AgentBuilderPluginExtensions
{
    /// <summary>
    /// Registers a plugin by type with optional execution context.
    /// </summary>
    public static AgentBuilder WithPlugin<T>(this AgentBuilder builder, IPluginMetadataContext? context = null) where T : class, new()
    {
        builder.PluginManager.RegisterPlugin<T>();
        var pluginName = typeof(T).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = context;
        return builder;
    }

    /// <summary>
    /// Registers a plugin using an instance with optional execution context.
    /// </summary>
    public static AgentBuilder WithPlugin<T>(this AgentBuilder builder, T instance, IPluginMetadataContext? context = null) where T : class
    {
        builder.PluginManager.RegisterPlugin(instance);
        var pluginName = typeof(T).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = context;
        return builder;
    }

    /// <summary>
    /// Registers a plugin by Type with optional execution context.
    /// </summary>
    public static AgentBuilder WithPlugin(this AgentBuilder builder, Type pluginType, IPluginMetadataContext? context = null)
    {
        builder.PluginManager.RegisterPlugin(pluginType);
        var pluginName = pluginType.Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = context;
        return builder;
    }
}

#endregion

#region Provider Extensions

public static class AgentBuilderProviderExtensions
{
    /// <summary>
    /// Configures the agent to use a specific provider with model name
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="provider">The chat provider to use</param>
    /// <param name="modelName">The specific model name (e.g., "gpt-4", "anthropic/claude-3.5-sonnet")</param>
    /// <param name="apiKey">API key for the provider (optional - will fallback to configuration/environment)</param>
    public static AgentBuilder WithProvider(this AgentBuilder builder, ChatProvider provider, string modelName, string? apiKey = null)
    {
        builder.Config.Provider = new ProviderConfig
        {
            Provider = provider,
            ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName)),
            ApiKey = apiKey
        };
        return builder;
    }

    /// <summary>
    /// Sets the configuration source for reading API keys and other settings
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="configuration">Configuration instance (e.g., from appsettings.json)</param>
    public static AgentBuilder WithAPIConfiguration(this AgentBuilder builder, IConfiguration configuration)
    {
        builder._configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        return builder;
    }

    /// <summary>
    /// Sets the base IChatClient that provides the core LLM functionality
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="baseClient">The base chat client.</param>
    public static AgentBuilder WithBaseClient(this AgentBuilder builder, IChatClient baseClient)
    {
        builder._baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        return builder;
    }

    internal static string ResolveApiKey(this AgentBuilder builder, ChatProvider provider, string? explicitApiKey = null)
    {
        // 1. Use explicitly provided API key if available
        if (!string.IsNullOrEmpty(explicitApiKey))
            return explicitApiKey;

        // 2. Try configuration (appsettings.json)
        var configKey = GetConfigurationKey(provider);
        var apiKeyFromConfig = builder._configuration?[configKey];
        if (!string.IsNullOrEmpty(apiKeyFromConfig))
            return apiKeyFromConfig;

        // 3. Try environment variable
        var envKey = GetEnvironmentVariableName(provider);
        var apiKeyFromEnv = AgentBuilderHelpers.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(apiKeyFromEnv))
            return apiKeyFromEnv;

        // 4. Fallback to generic environment variable names (AOT-safe)
        var genericEnvKey = GetGenericEnvironmentVariableName(provider);
        var genericApiKey = AgentBuilderHelpers.GetEnvironmentVariable(genericEnvKey);
        if (!string.IsNullOrEmpty(genericApiKey))
            return genericApiKey;

        throw new InvalidOperationException($"API key for {provider} not found. Provide it via:" +
            $"\n1. WithProvider() method parameter" +
            $"\n2. Configuration key: '{configKey}'" +
            $"\n3. Environment variable: '{envKey}' or '{genericEnvKey}'");
    }

    private static string GetConfigurationKey(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OpenRouter:ApiKey",
        ChatProvider.OpenAI => "OpenAI:ApiKey",
        ChatProvider.AzureOpenAI => "AzureOpenAI:ApiKey",
        ChatProvider.Ollama => "Ollama:ApiKey",
        ChatProvider.AppleIntelligence => "AppleIntelligence:ApiKey",
        _ => "Unknown:ApiKey" // AOT-safe fallback
    };

    private static string GetEnvironmentVariableName(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OPENROUTER_API_KEY",
        ChatProvider.OpenAI => "OPENAI_API_KEY",
        ChatProvider.AzureOpenAI => "AZURE_OPENAI_API_KEY",
        ChatProvider.Ollama => "OLLAMA_API_KEY",
        ChatProvider.AppleIntelligence => "APPLE_INTELLIGENCE_API_KEY",
        _ => "UNKNOWN_API_KEY" // AOT-safe fallback
    };

    private static string GetGenericEnvironmentVariableName(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OPENROUTER_API_KEY",
        ChatProvider.OpenAI => "OPENAI_API_KEY",
        ChatProvider.AzureOpenAI => "AZUREOPENAI_API_KEY",
        ChatProvider.Ollama => "OLLAMA_API_KEY",
        ChatProvider.AppleIntelligence => "APPLEINTELLIGENCE_API_KEY",
        _ => "GENERIC_API_KEY" // AOT-safe fallback
    };

    /// <summary>
    /// Ensures a base client is available by creating one from provider configuration if needed
    /// </summary>
    internal static void EnsureBaseClientFromProvider(this AgentBuilder builder)
    {
        // If no base client provided but provider info is available, create the client
        if (builder.BaseClient == null && builder.Config.Provider != null && !string.IsNullOrEmpty(builder.Config.Provider.ModelName))
        {
            // Handle Apple Intelligence as a special case since it doesn't need an API key
            if (builder.Config.Provider.Provider == ChatProvider.AppleIntelligence)
            {
                builder.BaseClient = builder.CreateClientFromProvider(builder.Config.Provider.Provider, builder.Config.Provider.ModelName, null);
            }
            else
            {
                var apiKey = builder.ResolveApiKey(builder.Config.Provider.Provider, builder.Config.Provider.ApiKey);
                builder.BaseClient = builder.CreateClientFromProvider(builder.Config.Provider.Provider, builder.Config.Provider.ModelName, apiKey);
            }
        }
    }

    internal static IChatClient CreateClientFromProvider(this AgentBuilder builder, ChatProvider provider, string modelName, string? apiKey)
    {
        return provider switch
        {
            ChatProvider.OpenRouter => new OpenRouterChatClient(new OpenRouterConfig
            {
                ApiKey = apiKey!,
                ModelName = modelName,
                Temperature = 0.7,
                MaxTokens = 2048
            }),
            ChatProvider.AppleIntelligence => new AppleIntelligenceChatClient(
                new AppleIntelligenceConfig
                {
                    ModelId = modelName,
                    // Add other config options as needed
                }),
            ChatProvider.OpenAI => new ChatClient(modelName, apiKey!).AsIChatClient(),
            ChatProvider.AzureOpenAI => new ChatCompletionsClient(
                new Uri("https://{your-resource-name}.openai.azure.com/openai/deployments/{yourDeployment}"),
                new AzureKeyCredential(apiKey!)).AsIChatClient(modelName),
            ChatProvider.Ollama => new OllamaApiClient(new Uri("http://localhost:11434"), modelName),
            _ => throw new NotSupportedException($"Provider {provider} is not supported."),
        };
    }

}

#endregion

#region Helper Methods

/// <summary>
/// Shared utility methods for AgentBuilder extension classes.
/// Contains common helper methods to avoid duplication across extension files.
/// </summary>
internal static class AgentBuilderHelpers
{
    /// <summary>
    /// Helper method to get environment variables (isolated to avoid analyzer warnings)
    /// </summary>
#pragma warning disable RS1035 // Environment access is valid in application code, not analyzer code
    internal static string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }
#pragma warning restore RS1035

    /// <summary>
    /// Simple heuristic to detect analyzer context
    /// </summary>
    internal static bool IsAnalyzerContext()
    {
        return Debugger.IsAttached == false;
    }

    /// <summary>
    /// Resolves or creates a configuration object from the provider configs dictionary
    /// AOT-safe implementation without reflection
    /// </summary>
    internal static T ResolveOrCreateConfig<T>(Dictionary<Type, object> providerConfigs) where T : new()
    {
        if (providerConfigs.TryGetValue(typeof(T), out var existing))
        {
            return (T)existing;
        }

        var config = new T();
        providerConfigs[typeof(T)] = config;
        return config;
    }

    /// <summary>
    /// Creates ElevenLabs configuration from environment variables
    /// </summary>
    internal static ElevenLabsConfig CreateElevenLabsConfigFromEnvironment()
    {
        return new ElevenLabsConfig
        {
            ApiKey = GetEnvironmentVariable("ELEVENLABS_API_KEY"),
            DefaultVoiceId = GetEnvironmentVariable("ELEVENLABS_VOICE_ID") ?? "21m00Tcm4TlvDq8ikWAM", // Default voice
            BaseUrl = GetEnvironmentVariable("ELEVENLABS_BASE_URL") ?? "https://api.elevenlabs.io"
        };
    }

    /// <summary>
    /// Creates Azure Speech configuration from environment variables
    /// </summary>
    internal static AzureSpeechConfig CreateAzureConfigFromEnvironment()
    {
        return new AzureSpeechConfig
        {
            ApiKey = GetEnvironmentVariable("AZURE_SPEECH_KEY"),
            Region = GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? "eastus",
            DefaultVoice = GetEnvironmentVariable("AZURE_SPEECH_VOICE") ?? "en-US-AriaNeural"
        };
    }
}

#endregion
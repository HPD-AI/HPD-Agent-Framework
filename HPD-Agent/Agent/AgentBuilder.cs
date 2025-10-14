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
using Anthropic.SDK;
using GenerativeAI;
using GenerativeAI.Microsoft;
using HuggingFace;
using Amazon.BedrockRuntime;
using Amazon;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using Microsoft.ML.OnnxRuntimeGenAI;
using Mistral.SDK;
using HPD_Agent.Memory.Agent.PlanMode;

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
    internal readonly List<IPermissionFilter> _permissionFilters = new(); // Permission filters
    internal readonly List<IMessageTurnFilter> _messageTurnFilters = new(); // Message turn filters

    internal readonly Dictionary<Type, object> _providerConfigs = new();
    internal IServiceProvider? _serviceProvider;
    internal ILoggerFactory? _logger;
    private ActivitySource? _activitySource; // OpenTelemetry ActivitySource for observability
    private Meter? _meter; // OpenTelemetry Meter for metrics

    // MCP runtime fields
    internal MCPClientManager? _mcpClientManager;

    // Microsoft.Extensions.AI middleware pipeline
    private readonly List<Func<IChatClient, IServiceProvider, IChatClient>> _middlewares = new();

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
    /// Creates a new builder from a JSON configuration file.
    /// </summary>
    /// <param name="jsonFilePath">Path to the JSON file containing AgentConfig data.</param>
    /// <exception cref="ArgumentException">Thrown when the file path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="JsonException">Thrown when the JSON is invalid or cannot be deserialized.</exception>
    public AgentBuilder(string jsonFilePath)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
            throw new ArgumentException("JSON file path cannot be null or empty.", nameof(jsonFilePath));

        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException($"Configuration file not found: {jsonFilePath}");

        try
        {
            var jsonContent = File.ReadAllText(jsonFilePath);
            _config = JsonSerializer.Deserialize<AgentConfig>(jsonContent, HPDJsonContext.Default.AgentConfig)
                ?? throw new JsonException("Failed to deserialize AgentConfig from JSON - result was null.");
        }
        catch (JsonException)
        {
            throw; // Re-throw JSON exceptions as-is
        }
        catch (Exception ex)
        {
            throw new JsonException($"Failed to load or parse configuration file '{jsonFilePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a new builder from a JSON configuration file.
    /// </summary>
    /// <param name="jsonFilePath">Path to the JSON file containing AgentConfig data.</param>
    /// <returns>A new AgentBuilder instance configured from the JSON file.</returns>
    /// <exception cref="ArgumentException">Thrown when the file path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="JsonException">Thrown when the JSON is invalid or cannot be deserialized.</exception>
    public static AgentBuilder FromJsonFile(string jsonFilePath)
    {
        return new AgentBuilder(jsonFilePath);
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
    /// Configures the maximum number of turns the agent can take to call functions before requiring continuation permission
    /// </summary>
    /// <param name="maxTurns">Maximum number of function-calling turns (default: 10)</param>
    public AgentBuilder WithMaxFunctionCallTurns(int maxTurns)
    {
        if (maxTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "Maximum function call turns must be greater than 0");

        _config.MaxAgenticIterations = maxTurns;
        return this;
    }

    /// <summary>
    /// Legacy method - use WithMaxFunctionCallTurns instead
    /// </summary>
    [Obsolete("Use WithMaxFunctionCallTurns instead - this better reflects that we're limiting turns, not individual function calls")]
    public AgentBuilder WithMaxFunctionCalls(int maxFunctionCalls) => WithMaxFunctionCallTurns(maxFunctionCalls);

    /// <summary>
    /// Configures how many additional turns to allow when user chooses to continue beyond the limit
    /// </summary>
    /// <param name="extensionAmount">Additional turns to allow (default: 3)</param>
    public AgentBuilder WithContinuationExtensionAmount(int extensionAmount)
    {
        if (extensionAmount <= 0)
            throw new ArgumentOutOfRangeException(nameof(extensionAmount), "Continuation extension amount must be greater than 0");

        _config.ContinuationExtensionAmount = extensionAmount;
        return this;
    }

    /// <summary>
    /// Wraps the base chat client with OpenTelemetry middleware to enable standardized telemetry for LLM calls,
    /// and adds a filter to create detailed traces and metrics for tool calls.
    /// This enables complete observability: Agent Turn traces, LLM Call traces, Tool Call traces, and Tool Call metrics.
    /// Can be called at any point in the builder chain - telemetry will be applied during Build().
    /// </summary>
    /// <param name="sourceName">An optional source name for the telemetry data. Defaults to "HPD.Agent".</param>
    /// <param name="configure">An optional callback to configure the OpenTelemetryChatClient instance.</param>
    public AgentBuilder WithOpenTelemetry(string? sourceName = "HPD.Agent", Action<OpenTelemetryChatClient>? configure = null)
    {
        // Add telemetry as a middleware that will be applied during Build()
        _middlewares.Add((client, services) =>
        {
            var loggerFactory = services.GetService<ILoggerFactory>();
            var builder = new ChatClientBuilder(client);
            builder.UseOpenTelemetry(loggerFactory, sourceName, configure);
            return builder.Build(services);
        });

        // === Add Tool Call Tracing and Metrics ===
        // Create or reuse the ActivitySource and Meter
        _activitySource ??= new ActivitySource(sourceName ?? "HPD.Agent");
        _meter ??= new Meter(sourceName ?? "HPD.Agent");

        // Create and register the observability filter with both tracing and metrics support
        var observabilityFilter = new ObservabilityAiFunctionFilter(_activitySource, _meter);
        this.WithFilter(observabilityFilter);

        return this;
    }

    /// <summary>
    /// Adds distributed caching to reduce redundant LLM calls
    /// </summary>
    public AgentBuilder WithCaching(IDistributedCache? cache = null, Action<DistributedCachingChatClient>? configure = null)
    {
        _middlewares.Add((client, services) =>
        {
            var cacheInstance = cache ?? services.GetService<IDistributedCache>();
            if (cacheInstance == null)
            {
                _logger?.CreateLogger<AgentBuilder>().LogWarning("Caching requested but no IDistributedCache available");
                return client;
            }
            var cachingClient = new DistributedCachingChatClient(client, cacheInstance);
            configure?.Invoke(cachingClient);
            return cachingClient;
        });
        return this;
    }

    /// <summary>
    /// Adds message reduction to handle long conversation histories
    /// Note: This method will be fully functional when Microsoft.Extensions.AI includes the ReducingChatClient
    /// </summary>
    public AgentBuilder WithMessageReducer(object? reducer = null, Action<object>? configure = null)
    {
        _middlewares.Add((client, services) =>
        {
            // TODO: Implement when ReducingChatClient is available in Microsoft.Extensions.AI
            _logger?.CreateLogger<AgentBuilder>().LogWarning("Message reduction not yet implemented - ReducingChatClient not available");
            return client;
        });
        return this;
    }

    /// <summary>
    /// Adds comprehensive logging for all operations (chat + functions)
    /// </summary>
    public AgentBuilder WithLogging(
        ILoggerFactory? loggerFactory = null, 
        bool includeChats = true,
        bool includeFunctions = true,
        Action<LoggingChatClient>? configureChat = null,
        Action<LoggingAiFunctionFilter>? configureFunction = null)
    {
        // Add chat logging middleware
        if (includeChats)
        {
            _middlewares.Add((client, services) =>
            {
                var factory = loggerFactory ?? _logger ?? services.GetService<ILoggerFactory>();
                if (factory == null || factory == NullLoggerFactory.Instance)
                {
                    return client; // Skip if no real logger available
                }
                var loggingClient = new LoggingChatClient(client, factory.CreateLogger<LoggingChatClient>());
                configureChat?.Invoke(loggingClient);
                return loggingClient;
            });
        }
        
        // Add function logging filter using the same approach as permission filters
        if (includeFunctions)
        {
            var factory = loggerFactory ?? _logger;
            var functionFilter = new LoggingAiFunctionFilter(factory);
            configureFunction?.Invoke(functionFilter);
            
            // Use the same registration approach as permission filters
            this.WithFilter(functionFilter);
        }
        
        return this;
    }

    /// <summary>
    /// Adds function invocation logging for advanced users who want explicit control
    /// </summary>
    public AgentBuilder WithFunctionLogging(ILoggerFactory? loggerFactory = null, Action<LoggingAiFunctionFilter>? configure = null)
    {
        var factory = loggerFactory ?? _logger;
        var filter = new LoggingAiFunctionFilter(factory);
        configure?.Invoke(filter);
        
        // Use the same registration approach as permission filters
        return this.WithFilter(filter);
    }

    /// <summary>
    /// Adds options configuration middleware
    /// </summary>
    public AgentBuilder WithOptionsConfiguration(Action<ChatOptions> configureOptions)
    {
        _middlewares.Add((client, services) =>
        {
            return new ConfigureOptionsChatClient(client, configureOptions);
        });
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

        // Apply middleware pipeline in order
        var clientToUse = _baseClient;
        if (_middlewares.Count > 0)
        {
            var serviceProvider = _serviceProvider ?? EmptyServiceProvider.Instance;
            foreach (var middleware in _middlewares)
            {
                clientToUse = middleware(clientToUse, serviceProvider);
                if (clientToUse == null)
                {
                    throw new InvalidOperationException("Middleware returned null client");
                }
            }
        }

        // Dynamic Memory registration is handled by WithDynamicMemory() extension method
        // No need to register here in Build() - the extension already adds filter and plugin

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

        // Filter out container functions if plugin scoping is disabled
        // Container functions are only needed when scoping is enabled for the two-turn expansion flow
        if (_config.PluginScoping?.Enabled != true)
        {
            pluginFunctions = pluginFunctions.Where(f =>
                !(f.AdditionalProperties?.TryGetValue("IsContainer", out var isContainer) == true &&
                  isContainer is bool isCont && isCont)
            ).ToList();
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
                    // Get scoping configuration
                    var enableMCPScoping = _config.PluginScoping?.ScopeMCPTools ?? false;
                    var maxFunctionNames = _config.PluginScoping?.MaxFunctionNamesInDescription ?? 10;

                    // Check if this is actually content vs path based on if it starts with '{'
                    if (_config.Mcp.ManifestPath.TrimStart().StartsWith("{"))
                    {
                        mcpTools = McpClientManager.LoadToolsFromManifestContentAsync(
                            _config.Mcp.ManifestPath,
                            enableMCPScoping,
                            maxFunctionNames).GetAwaiter().GetResult();
                    }
                    else
                    {
                        mcpTools = McpClientManager.LoadToolsFromManifestAsync(
                            _config.Mcp.ManifestPath,
                            enableMCPScoping,
                            maxFunctionNames).GetAwaiter().GetResult();
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
            clientToUse,
            mergedOptions, // Pass the merged options directly
            _promptFilters,
            _scopedFilterManager,
            _permissionFilters,
            _globalFilters,
            _messageTurnFilters);

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

        // Translate ToolSelectionConfig to ChatToolMode (FFI-friendly → M.E.AI)
        var toolMode = TranslateToolMode(_config.ToolSelection);

        return new ChatOptions
        {
            Tools = allTools,
            ToolMode = toolMode,
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
    /// Translates FFI-friendly ToolSelectionConfig to Microsoft.Extensions.AI ChatToolMode.
    /// This keeps foreign language bindings (Python, JS, etc.) free from M.E.AI dependencies.
    /// </summary>
    private static ChatToolMode TranslateToolMode(ToolSelectionConfig? toolSelection)
    {
        if (toolSelection == null)
            return ChatToolMode.Auto;

        return toolSelection.ToolMode switch
        {
            "None" => ChatToolMode.None,
            "RequireAny" => ChatToolMode.RequireAny,
            "RequireSpecific" when !string.IsNullOrEmpty(toolSelection.RequiredFunctionName)
                => ChatToolMode.RequireSpecific(toolSelection.RequiredFunctionName),
            "RequireSpecific"
                => throw new InvalidOperationException("ToolMode 'RequireSpecific' requires RequiredFunctionName to be set."),
            "Auto" => ChatToolMode.Auto,
            _ => throw new InvalidOperationException($"Unknown ToolMode: '{toolSelection.ToolMode}'. Valid values: 'Auto', 'None', 'RequireAny', 'RequireSpecific'.")
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
    /// Internal access to permission filters for extension methods
    /// </summary>
    internal List<IPermissionFilter> PermissionFilters => _permissionFilters;

    /// <summary>
    /// Internal access to MCP client manager for extension methods
    /// </summary>
    internal MCPClientManager? McpClientManager
    {
        get => _mcpClientManager;
        set => _mcpClientManager = value;
    }

    #endregion

    /// <summary>
    /// Adds a Rust function to the agent (used by FFI layer)
    /// </summary>
    internal void AddRustFunction(AIFunction function)
    {
        // Get or create default chat options
        if (_config.Provider == null)
            _config.Provider = new ProviderConfig();
            
        if (_config.Provider.DefaultChatOptions == null)
            _config.Provider.DefaultChatOptions = new ChatOptions();
            
        // Add to tools list
        var tools = _config.Provider.DefaultChatOptions.Tools?.ToList() ?? new List<AITool>();
        tools.Add(function);
        _config.Provider.DefaultChatOptions.Tools = tools;
        
        // Enable auto tool mode if not already set
        if (_config.Provider.DefaultChatOptions.ToolMode == null)
            _config.Provider.DefaultChatOptions.ToolMode = ChatToolMode.Auto;
    }
}


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
        if (filter != null)
        {
            builder.ScopedFilterManager.AddFilter(filter, builder.ScopeContext.CurrentScope, builder.ScopeContext.CurrentTarget);
        }
        return builder;
    }

    /// <summary>
    /// Adds a permission filter instance
    /// </summary>
    public static AgentBuilder WithPermissionFilter(this AgentBuilder builder, IPermissionFilter filter)
    {
        if (filter != null)
        {
            builder.PermissionFilters.Add(filter);
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

    /// <summary>
    /// Adds a message turn filter to process completed turns
    /// </summary>
    public static AgentBuilder WithMessageTurnFilter(this AgentBuilder builder, IMessageTurnFilter filter)
    {
        builder._messageTurnFilters.Add(filter);
        return builder;
    }

    /// <summary>
    /// Adds a message turn filter of the specified type (creates new instance)
    /// </summary>
    public static AgentBuilder WithMessageTurnFilter<T>(this AgentBuilder builder) where T : IMessageTurnFilter, new()
    {
        builder._messageTurnFilters.Add(new T());
        return builder;
    }

    /// <summary>
    /// Adds multiple message turn filters
    /// </summary>
    public static AgentBuilder WithMessageTurnFilters(this AgentBuilder builder, params IMessageTurnFilter[] filters)
    {
        if (filters != null)
        {
            foreach (var f in filters)
                builder._messageTurnFilters.Add(f);
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
    

    /// <summary>
    /// Configures the agent's dynamic, editable working memory.
    /// This enables a Full Text Injection (formerly CAG) system and provides the agent
    /// with tools to manage its own persistent facts.
    /// </summary>
    public static AgentBuilder WithDynamicMemory(this AgentBuilder builder, Action<DynamicMemoryOptions> configure)
    {
        var options = new DynamicMemoryOptions();
        configure(options);

        // Set the config on the builder
        builder.Config.DynamicMemory = new DynamicMemoryConfig
        {
            StorageDirectory = options.StorageDirectory,
            MaxTokens = options.MaxTokens,
            EnableAutoEviction = options.EnableAutoEviction,
            AutoEvictionThreshold = options.AutoEvictionThreshold
        };

        // Use custom store if provided, otherwise create JsonDynamicMemoryStore (default)
        var store = options.Store ?? new JsonDynamicMemoryStore(
            options.StorageDirectory,
            builder.Logger?.CreateLogger<JsonDynamicMemoryStore>());

        // Use MemoryId if provided, otherwise fall back to agent name
        var memoryId = options.MemoryId ?? builder.AgentName;
        var plugin = new DynamicMemoryPlugin(store, memoryId, builder.Logger?.CreateLogger<DynamicMemoryPlugin>());
        var filter = new DynamicMemoryFilter(store, options, builder.Logger?.CreateLogger<DynamicMemoryFilter>());

        // Register plugin and filter directly without cross-extension dependencies
        RegisterDynamicMemoryPlugin(builder, plugin);
        RegisterDynamicMemoryFilter(builder, filter);

        return builder;
    }

    /// <summary>
    /// Registers the memory plugin directly with the builder's plugin manager
    /// Avoids dependency on AgentBuilderPluginExtensions
    /// </summary>
    private static void RegisterDynamicMemoryPlugin(AgentBuilder builder, DynamicMemoryPlugin plugin)
    {
        builder.PluginManager.RegisterPlugin(plugin);
        var pluginName = typeof(DynamicMemoryPlugin).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = null; // No special context needed for memory plugin
    }

    /// <summary>
    /// Registers the memory filter directly with the builder's filter manager
    /// Avoids dependency on AgentBuilderFilterExtensions
    /// </summary>
    private static void RegisterDynamicMemoryFilter(AgentBuilder builder, DynamicMemoryFilter filter)
    {
        builder.PromptFilters.Add(filter);
    }

    /// <summary>
    /// Configures conversation history reduction to manage context window size.
    /// Supports message-based reduction (keeps last N messages) or summarization (uses LLM to compress old messages).
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configure">Configuration action for history reduction settings</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// builder.WithHistoryReduction(config => {
    ///     config.Enabled = true;
    ///     config.Strategy = HistoryReductionStrategy.Summarizing;
    ///     config.TargetMessageCount = 30;
    ///     config.SummarizationThreshold = 10;
    /// });
    /// </code>
    /// </example>
    public static AgentBuilder WithHistoryReduction(this AgentBuilder builder, Action<HistoryReductionConfig>? configure = null)
    {
        var config = builder.Config.HistoryReduction ?? new HistoryReductionConfig();
        configure?.Invoke(config);

        builder.Config.HistoryReduction = config;
        return builder;
    }

    /// <summary>
    /// Enables simple message counting reduction (keeps last N messages).
    /// Quick setup method for basic history management.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="targetMessageCount">Number of messages to keep (default: 20)</param>
    /// <param name="threshold">Extra messages allowed before reduction triggers (default: 5)</param>
    /// <returns>The builder for method chaining</returns>
    public static AgentBuilder WithMessageCountingReduction(this AgentBuilder builder, int targetMessageCount = 20, int threshold = 5)
    {
        return builder.WithHistoryReduction(config =>
        {
            config.Enabled = true;
            config.Strategy = HistoryReductionStrategy.MessageCounting;
            config.TargetMessageCount = targetMessageCount;
            config.SummarizationThreshold = threshold;
        });
    }

    /// <summary>
    /// Enables summarizing reduction (uses LLM to compress old messages).
    /// Provides better context retention than message counting but requires additional LLM calls.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="targetMessageCount">Number of messages to keep (default: 20)</param>
    /// <param name="threshold">Extra messages before summarization triggers (default: 5)</param>
    /// <param name="customPrompt">Optional custom summarization prompt</param>
    /// <returns>The builder for method chaining</returns>
    public static AgentBuilder WithSummarizingReduction(this AgentBuilder builder, int targetMessageCount = 20, int threshold = 5, string? customPrompt = null)
    {
        return builder.WithHistoryReduction(config =>
        {
            config.Enabled = true;
            config.Strategy = HistoryReductionStrategy.Summarizing;
            config.TargetMessageCount = targetMessageCount;
            config.SummarizationThreshold = threshold;
            config.CustomSummarizationPrompt = customPrompt;
        });
    }

    /// <summary>
    /// Configures a separate LLM provider for summarization to optimize costs.
    /// Use a cheaper/faster model for summaries while keeping your main model for responses.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="provider">The provider to use for summarization (e.g., OpenAI, Anthropic)</param>
    /// <param name="modelName">The model name (e.g., "gpt-4o-mini", "claude-3-haiku-20240307")</param>
    /// <param name="apiKey">Optional API key (uses main provider's key if not specified)</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// builder
    ///     .WithOpenAI(apiKey, "gpt-4") // Main agent uses GPT-4
    ///     .WithSummarizingReduction()
    ///     .WithSummarizerProvider(ChatProvider.OpenAI, "gpt-4o-mini"); // Summaries use mini
    /// </code>
    /// </example>
    public static AgentBuilder WithSummarizerProvider(this AgentBuilder builder, ChatProvider provider, string modelName, string? apiKey = null)
    {
        var config = builder.Config.HistoryReduction ?? new HistoryReductionConfig { Enabled = true };

        config.SummarizerProvider = new ProviderConfig
        {
            Provider = provider,
            ModelName = modelName,
            ApiKey = apiKey ?? builder.Config.Provider?.ApiKey
        };

        builder.Config.HistoryReduction = config;
        return builder;
    }

    /// <summary>
    /// Configures a separate LLM provider for summarization with full provider configuration.
    /// Use this for advanced scenarios requiring custom endpoints or options.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configureSummarizer">Action to configure the summarizer provider</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// builder
    ///     .WithSummarizingReduction()
    ///     .WithSummarizerProvider(config => {
    ///         config.Provider = ChatProvider.Ollama;
    ///         config.ModelName = "llama3.2";
    ///         config.Endpoint = "http://localhost:11434";
    ///     });
    /// </code>
    /// </example>
    public static AgentBuilder WithSummarizerProvider(this AgentBuilder builder, Action<ProviderConfig> configureSummarizer)
    {
        var historyConfig = builder.Config.HistoryReduction ?? new HistoryReductionConfig { Enabled = true };
        var summarizerConfig = new ProviderConfig();

        configureSummarizer(summarizerConfig);
        historyConfig.SummarizerProvider = summarizerConfig;

        builder.Config.HistoryReduction = historyConfig;
        return builder;
    }

    /// <summary>
    /// Configures the agent's static knowledge base with FullTextInjection or IndexedRetrieval strategy.
    /// This is read-only domain expertise (e.g., Python docs, design patterns, API references).
    /// Different from DynamicMemory (which is dynamic and editable).
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configure">Configuration action for agent knowledge settings</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// builder.WithKnowledge(opts => {
    ///     opts.Strategy = AgentKnowledgeStrategy.FullTextInjection;
    ///     opts.StorageDirectory = "./knowledge/python-expert";
    ///     opts.MaxTokens = 8000;
    ///     opts.AddDocument("./docs/python-best-practices.md");
    ///     opts.AddDocument("./docs/fastapi-patterns.md");
    /// });
    /// </code>
    /// </example>
    public static AgentBuilder WithStaticMemory(this AgentBuilder builder, Action<StaticMemoryOptions> configure)
    {
        var options = new StaticMemoryOptions();
        configure(options);

        // Determine the knowledge ID (priority: KnowledgeId > AgentName > builder.AgentName)
        var knowledgeId = options.KnowledgeId ?? options.AgentName ?? builder.AgentName;

        // Create store if not provided
        if (options.Store == null)
        {
            var textExtractor = new HPD_Agent.TextExtraction.TextExtractionUtility();
            options.Store = new JsonStaticMemoryStore(
                options.StorageDirectory,
                textExtractor,
                builder.Logger?.CreateLogger<JsonStaticMemoryStore>());
        }

        // Add documents specified at build time
        if (options.DocumentsToAdd.Any())
        {
            var store = options.Store;
            // Synchronously add documents (blocking - consider async BuildAsync in future)
            foreach (var doc in options.DocumentsToAdd)
            {
                if (store is JsonStaticMemoryStore jsonStore)
                {
                    if (doc.PathOrUrl.StartsWith("http://") || doc.PathOrUrl.StartsWith("https://"))
                    {
                        jsonStore.AddDocumentFromUrlAsync(knowledgeId, doc.PathOrUrl, doc.Description, doc.Tags).GetAwaiter().GetResult();
                    }
                    else
                    {
                        jsonStore.AddDocumentFromFileAsync(knowledgeId, doc.PathOrUrl, doc.Description, doc.Tags).GetAwaiter().GetResult();
                    }
                }
            }
        }

        // Only register filter for FullTextInjection strategy
        if (options.Strategy == MemoryStrategy.FullTextInjection)
        {
            var filter = new StaticMemoryFilter(
                options.Store,
                knowledgeId,
                options.MaxTokens,
                builder.Logger?.CreateLogger<StaticMemoryFilter>());
            RegisterStaticMemoryFilter(builder, filter);
        }
        else if (options.Strategy == MemoryStrategy.IndexedRetrieval)
        {
            // TODO: Future implementation for vector store integration
            throw new NotImplementedException(
                "IndexedRetrieval strategy is not yet implemented. " +
                "Please use FullTextInjection for now.");
        }

        return builder;
    }

    /// <summary>
    /// Registers the knowledge filter directly with the builder's filter manager.
    /// Avoids dependency on AgentBuilderFilterExtensions.
    /// </summary>
    private static void RegisterStaticMemoryFilter(AgentBuilder builder, StaticMemoryFilter filter)
    {
        builder.PromptFilters.Add(filter);
    }

    /// <summary>
    /// Enables plan mode for the agent, allowing it to create and manage execution plans.
    /// Plan mode provides AIFunctions for creating plans, updating steps, and tracking progress.
    /// </summary>
    public static AgentBuilder WithPlanMode(this AgentBuilder builder, Action<PlanModeOptions>? configure = null)
    {
        var options = new PlanModeOptions();
        configure?.Invoke(options);

        if (!options.Enabled)
        {
            return builder;
        }

        // Determine which store to use
        AgentPlanStore store;
        if (options.Store != null)
        {
            // Use custom store provided by user
            store = options.Store;
        }
        else if (options.EnablePersistence)
        {
            // Use JSON file-based storage for persistence
            store = new JsonAgentPlanStore(
                options.StorageDirectory,
                builder.Logger?.CreateLogger<JsonAgentPlanStore>());
        }
        else
        {
            // Default to in-memory storage (non-persistent)
            store = new InMemoryAgentPlanStore(
                builder.Logger?.CreateLogger<InMemoryAgentPlanStore>());
        }

        // Create plugin and filter with store
        var plugin = new AgentPlanPlugin(store, builder.Logger?.CreateLogger<AgentPlanPlugin>());
        var filter = new AgentPlanFilter(store, builder.Logger?.CreateLogger<AgentPlanFilter>());

        // Register plugin directly
        builder.PluginManager.RegisterPlugin(plugin);
        var pluginName = typeof(AgentPlanPlugin).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = null;

        // Register filter directly
        builder.PromptFilters.Add(filter);

        // Update config for backwards compatibility
        var config = new PlanModeConfig
        {
            Enabled = options.Enabled,
            CustomInstructions = options.CustomInstructions
        };
        builder.Config.PlanMode = config;

        return builder;
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
    /// Sets the configuration source for reading API keys and other settings from a JSON file
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="jsonFilePath">Path to the JSON configuration file (e.g., appsettings.json)</param>
    /// <param name="optional">Whether the file is optional (default: false)</param>
    /// <param name="reloadOnChange">Whether to reload configuration when file changes (default: true)</param>
    /// <exception cref="ArgumentException">Thrown when the file path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist and optional is false.</exception>
    public static AgentBuilder WithAPIConfiguration(this AgentBuilder builder, string jsonFilePath, bool optional = false, bool reloadOnChange = true)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
            throw new ArgumentException("JSON file path cannot be null or empty.", nameof(jsonFilePath));

        if (!optional && !File.Exists(jsonFilePath))
            throw new FileNotFoundException($"Configuration file not found: {jsonFilePath}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(jsonFilePath, optional: optional, reloadOnChange: reloadOnChange)
                .Build();

            builder._configuration = configuration;
            return builder;
        }
        catch (Exception ex) when (!(ex is ArgumentException || ex is FileNotFoundException))
        {
            throw new InvalidOperationException($"Failed to load configuration from '{jsonFilePath}': {ex.Message}", ex);
        }
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
        ChatProvider.AzureAIInference => "AzureAIInference:Endpoint",
        ChatProvider.Ollama => "Ollama:ApiKey",
        ChatProvider.Anthropic => "Anthropic:ApiKey",
        ChatProvider.GoogleAI => "GoogleAI:ApiKey",
        ChatProvider.VertexAI => "VertexAI:ProjectId",
        ChatProvider.HuggingFace => "HuggingFace:ApiKey",
        ChatProvider.Bedrock => "AWS:Region", // Primary config is region
        ChatProvider.OnnxRuntime => "OnnxRuntime:ModelPath",
        ChatProvider.Mistral => "Mistral:ApiKey",
        // Apple Intelligence removed
        _ => "Unknown:ApiKey" // AOT-safe fallback
    };

    private static string GetEnvironmentVariableName(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OPENROUTER_API_KEY",
        ChatProvider.OpenAI => "OPENAI_API_KEY",
        ChatProvider.AzureOpenAI => "AZURE_OPENAI_API_KEY",
        ChatProvider.AzureAIInference => "AZURE_AI_INFERENCE_ENDPOINT",
        ChatProvider.Ollama => "OLLAMA_API_KEY",
        ChatProvider.Anthropic => "ANTHROPIC_API_KEY",
        ChatProvider.GoogleAI => "GOOGLE_API_KEY",
        ChatProvider.VertexAI => "GOOGLE_CLOUD_PROJECT",
        ChatProvider.HuggingFace => "HF_TOKEN",
        ChatProvider.Bedrock => "AWS_REGION", // Standard AWS region variable
        ChatProvider.OnnxRuntime => "ONNX_MODEL_PATH",
        ChatProvider.Mistral => "MISTRAL_API_KEY",
        // Apple Intelligence removed
        _ => "UNKNOWN_API_KEY" // AOT-safe fallback
    };

    private static string GetGenericEnvironmentVariableName(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OPENROUTER_API_KEY",
        ChatProvider.OpenAI => "OPENAI_API_KEY",
        ChatProvider.AzureOpenAI => "AZUREOPENAI_API_KEY",
        ChatProvider.AzureAIInference => "AZURE_AI_INFERENCE_ENDPOINT",
        ChatProvider.Ollama => "OLLAMA_API_KEY",
        ChatProvider.Anthropic => "ANTHROPIC_API_KEY",
        ChatProvider.GoogleAI => "GOOGLE_API_KEY",
        ChatProvider.VertexAI => "GOOGLE_CLOUD_PROJECT",
        ChatProvider.HuggingFace => "HF_TOKEN",
        ChatProvider.Bedrock => "AWS_REGION",
        ChatProvider.OnnxRuntime => "ONNX_MODEL_PATH",
        ChatProvider.Mistral => "MISTRAL_API_KEY",
        // Apple Intelligence removed
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
            // Apple Intelligence removed - handle all providers the same way
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
            ChatProvider.OpenRouter => CreateOpenRouterClient(modelName, apiKey!),
            ChatProvider.OpenAI => new ChatClient(modelName, apiKey!).AsIChatClient(),
            ChatProvider.AzureOpenAI => new ChatCompletionsClient(
                new Uri("https://{your-resource-name}.openai.azure.com/openai/deployments/{yourDeployment}"),
                new AzureKeyCredential(apiKey!)).AsIChatClient(modelName),
            ChatProvider.AzureAIInference => CreateAzureAIInferenceClient(builder, modelName),
            ChatProvider.Ollama => new OllamaApiClient(new Uri("http://localhost:11434"), modelName),
            ChatProvider.Anthropic => new AnthropicClient(apiKey).Messages,
            ChatProvider.GoogleAI => new GenerativeAIChatClient(apiKey!, modelName),
            ChatProvider.VertexAI => CreateVertexAIClient(builder, modelName),
            ChatProvider.HuggingFace => new HuggingFaceClient(apiKey!),
            ChatProvider.Bedrock => CreateBedrockChatClient(builder, modelName),
            ChatProvider.OnnxRuntime => CreateOnnxRuntimeChatClient(builder, modelName),
            ChatProvider.Mistral => new MistralClient(apiKey!).Completions,
            _ => throw new NotSupportedException($"Provider {provider} is not supported."),
        };
    }

    /// <summary>
    /// Creates an OpenRouter client using our custom OpenRouterChatClient.
    /// Properly exposes reasoning content from OpenRouter's reasoning_details field.
    /// </summary>
    private static IChatClient CreateOpenRouterClient(string modelName, string apiKey)
    {
        // Create HttpClient configured for OpenRouter
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/")
        };

        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/your-repo"); // Optional: for rankings
        httpClient.DefaultRequestHeaders.Add("X-Title", "HPD-Agent"); // Optional: for rankings

        // Create our custom OpenRouterChatClient that properly handles reasoning_details
        return new HPD_Agent.Providers.OpenRouter.OpenRouterChatClient(httpClient, modelName);
    }

    /// <summary>
    /// Creates a Vertex AI client using the project ID and region from configuration
    /// </summary>
    private static IChatClient CreateVertexAIClient(AgentBuilder builder, string modelName)
    {
        var providerSpecific = builder.Config.Provider?.ProviderSpecific;
        var projectId = providerSpecific?.VertexAI?.ProjectId 
            ?? builder._configuration?["VertexAI:ProjectId"] 
            ?? AgentBuilderHelpers.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");

        var region = providerSpecific?.VertexAI?.Region
            ?? builder._configuration?["VertexAI:Region"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("GOOGLE_CLOUD_REGION");

        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException(
                "For the VertexAI provider, ProjectId and Region must be configured via WithProvider<VertexAISettings>(...) or environment variables (GOOGLE_CLOUD_PROJECT, GOOGLE_CLOUD_REGION).");
        }

        // Create VertexAI platform adapter (this will use Application Default Credentials)
        var platformAdapter = new VertextPlatformAdapter(projectId, region);
        
        // Create the IChatClient using the GenerativeAIChatClient with the VertexAI platform adapter
        return new GenerativeAIChatClient(platformAdapter, modelName);
    }

    /// <summary>
    /// Creates an AWS Bedrock client using the region and credentials from configuration
    /// </summary>
    private static IChatClient CreateBedrockChatClient(AgentBuilder builder, string modelName)
    {
        var settings = builder.Config.Provider?.ProviderSpecific?.Bedrock;

        var region = settings?.Region
            ?? builder._configuration?["AWS:Region"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("AWS_REGION");

        var accessKey = settings?.AccessKeyId
            ?? builder._configuration?["AWS:AccessKeyId"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");

        var secretKey = settings?.SecretAccessKey
            ?? builder._configuration?["AWS:SecretAccessKey"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            
        if (string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException(
                "For the Bedrock provider, the AWS Region must be configured via WithProvider<BedrockSettings>(...) or the AWS_REGION environment variable.");
        }

        // Create the IAmazonBedrockRuntime client
        IAmazonBedrockRuntime bedrockRuntime;
        
        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
        {
            // Use provided credentials
            var regionEndpoint = RegionEndpoint.GetBySystemName(region);
            bedrockRuntime = new AmazonBedrockRuntimeClient(accessKey, secretKey, regionEndpoint);
        }
        else
        {
            // Use default credential chain
            var regionEndpoint = RegionEndpoint.GetBySystemName(region);
            bedrockRuntime = new AmazonBedrockRuntimeClient(regionEndpoint);
        }

        // Use the extension method from the Bedrock MEAI library to get the IChatClient
        return bedrockRuntime.AsIChatClient(modelName);
    }

    /// <summary>
    /// Creates an Azure AI Inference client using the endpoint and API key from configuration
    /// </summary>
    private static IChatClient CreateAzureAIInferenceClient(AgentBuilder builder, string modelName)
    {
        var settings = builder.Config.Provider?.ProviderSpecific?.AzureAIInference;

        var endpoint = settings?.Endpoint
            ?? builder._configuration?["AzureAIInference:Endpoint"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("AZURE_AI_INFERENCE_ENDPOINT");

        var apiKey = settings?.ApiKey
            ?? builder._configuration?["AzureAIInference:ApiKey"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("AZURE_AI_INFERENCE_API_KEY");

        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("For AzureAIInference, the Endpoint must be configured.");
        }
        
        if (string.IsNullOrEmpty(apiKey))
        {
             throw new InvalidOperationException("For AzureAIInference, the ApiKey must be configured.");
        }

        // Create the ChatCompletionsClient and use the built-in AsIChatClient extension method
        var client = new ChatCompletionsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        return client.AsIChatClient(modelName);
    }

    /// <summary>
    /// Creates an ONNX Runtime client using the model path from configuration
    /// </summary>
    private static IChatClient CreateOnnxRuntimeChatClient(AgentBuilder builder, string modelName)
    {
        var settings = builder.Config.Provider?.ProviderSpecific?.OnnxRuntime;

        var modelPath = settings?.ModelPath
            ?? builder._configuration?["OnnxRuntime:ModelPath"]
            ?? AgentBuilderHelpers.GetEnvironmentVariable("ONNX_MODEL_PATH");

        if (string.IsNullOrEmpty(modelPath))
        {
            throw new InvalidOperationException(
                "For the OnnxRuntime provider, the ModelPath must be configured.");
        }

        // Create configuration for the client with enhanced options
        var options = new OnnxRuntimeGenAIChatClientOptions
        {
            StopSequences = settings?.StopSequences,
            EnableCaching = settings?.EnableCaching ?? false,
            PromptFormatter = settings?.PromptFormatter
        };
        
        return new OnnxRuntimeGenAIChatClient(modelPath, options);
    }

    /// <summary>
    /// Generic method for configuring any provider with its specific settings in a single call.
    /// This is a type-safe, flexible approach that combines provider selection and configuration.
    /// </summary>
    /// <typeparam name="TProviderConfig">The provider-specific configuration type (e.g., AnthropicSettings, OpenAISettings)</typeparam>
    /// <param name="builder">The agent builder.</param>
    /// <param name="provider">The chat provider to use</param>
    /// <param name="modelName">The specific model name</param>
    /// <param name="configure">Configuration action for the provider settings</param>
    /// <param name="apiKey">Optional API key (will fallback to configuration/environment if not provided)</param>
    /// <returns>The agent builder for method chaining</returns>
    /// <exception cref="InvalidOperationException">Thrown when the provider type doesn't match the configuration type</exception>
    public static AgentBuilder WithProvider<TProviderConfig>(
        this AgentBuilder builder, 
        ChatProvider provider, 
        string modelName,
        Action<TProviderConfig> configure,
        string? apiKey = null) 
        where TProviderConfig : class, new()
    {
        // First set the basic provider configuration
        builder.Config.Provider = new ProviderConfig
        {
            Provider = provider,
            ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName)),
            ApiKey = apiKey
        };

        // Then configure the provider-specific settings
        var providerSpecific = GetOrCreateProviderSpecificSettings(builder);
        var settings = GetOrCreateProviderSettings<TProviderConfig>(providerSpecific, provider);
        configure(settings);

        return builder;
    }

    /// <summary>
    /// Gets or creates the ProviderSpecificConfig object
    /// </summary>
    private static ProviderSpecificConfig GetOrCreateProviderSpecificSettings(AgentBuilder builder)
    {
        if (builder.Config.Provider!.ProviderSpecific == null)
        {
            builder.Config.Provider.ProviderSpecific = new ProviderSpecificConfig();
        }
        return builder.Config.Provider.ProviderSpecific;
    }

    /// <summary>
    /// Generic helper to get or create provider-specific settings for the generic WithProvider method
    /// </summary>
    private static TProviderConfig GetOrCreateProviderSettings<TProviderConfig>(
        ProviderSpecificConfig providerSpecific, 
        ChatProvider provider) 
        where TProviderConfig : class, new()
    {
        // Use type matching to set the correct property on ProviderSpecificConfig
        if (typeof(TProviderConfig) == typeof(AnthropicSettings))
        {
            if (provider != ChatProvider.Anthropic)
                throw new InvalidOperationException($"AnthropicSettings can only be used with {ChatProvider.Anthropic} provider. Current provider is {provider}.");
            
            return (providerSpecific.Anthropic ??= new AnthropicSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create AnthropicSettings");
        }
        else if (typeof(TProviderConfig) == typeof(OpenAISettings))
        {
            if (provider != ChatProvider.OpenAI)
                throw new InvalidOperationException($"OpenAISettings can only be used with {ChatProvider.OpenAI} provider. Current provider is {provider}.");
            
            return (providerSpecific.OpenAI ??= new OpenAISettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create OpenAISettings");
        }
        else if (typeof(TProviderConfig) == typeof(AzureOpenAISettings))
        {
            if (provider != ChatProvider.AzureOpenAI)
                throw new InvalidOperationException($"AzureOpenAISettings can only be used with {ChatProvider.AzureOpenAI} provider. Current provider is {provider}.");
            
            return (providerSpecific.AzureOpenAI ??= new AzureOpenAISettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create AzureOpenAISettings");
        }
        else if (typeof(TProviderConfig) == typeof(OllamaSettings))
        {
            if (provider != ChatProvider.Ollama)
                throw new InvalidOperationException($"OllamaSettings can only be used with {ChatProvider.Ollama} provider. Current provider is {provider}.");
            
            return (providerSpecific.Ollama ??= new OllamaSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create OllamaSettings");
        }
        else if (typeof(TProviderConfig) == typeof(OpenRouterSettings))
        {
            if (provider != ChatProvider.OpenRouter)
                throw new InvalidOperationException($"OpenRouterSettings can only be used with {ChatProvider.OpenRouter} provider. Current provider is {provider}.");
            
            return (providerSpecific.OpenRouter ??= new OpenRouterSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create OpenRouterSettings");
        }
        else if (typeof(TProviderConfig) == typeof(GoogleAISettings))
        {
            if (provider != ChatProvider.GoogleAI)
                throw new InvalidOperationException($"GoogleAISettings can only be used with {ChatProvider.GoogleAI} provider. Current provider is {provider}.");
            
            return (providerSpecific.GoogleAI ??= new GoogleAISettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create GoogleAISettings");
        }
        else if (typeof(TProviderConfig) == typeof(VertexAISettings))
        {
            if (provider != ChatProvider.VertexAI)
                throw new InvalidOperationException($"VertexAISettings can only be used with {ChatProvider.VertexAI} provider. Current provider is {provider}.");

            return (providerSpecific.VertexAI ??= new VertexAISettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create VertexAISettings");
        }
        else if (typeof(TProviderConfig) == typeof(HuggingFaceSettings))
        {
            if (provider != ChatProvider.HuggingFace)
                throw new InvalidOperationException($"HuggingFaceSettings can only be used with {ChatProvider.HuggingFace} provider. Current provider is {provider}.");

            return (providerSpecific.HuggingFace ??= new HuggingFaceSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create HuggingFaceSettings");
        }
        else if (typeof(TProviderConfig) == typeof(BedrockSettings))
        {
            if (provider != ChatProvider.Bedrock)
                throw new InvalidOperationException($"BedrockSettings can only be used with the {ChatProvider.Bedrock} provider.");
            
            return (providerSpecific.Bedrock ??= new BedrockSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create BedrockSettings");
        }
        else if (typeof(TProviderConfig) == typeof(AzureAIInferenceSettings))
        {
            if (provider != ChatProvider.AzureAIInference)
                throw new InvalidOperationException($"AzureAIInferenceSettings can only be used with the {ChatProvider.AzureAIInference} provider.");
            
            return (providerSpecific.AzureAIInference ??= new AzureAIInferenceSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create AzureAIInferenceSettings");
        }
        else if (typeof(TProviderConfig) == typeof(OnnxRuntimeSettings))
        {
            if (provider != ChatProvider.OnnxRuntime)
                throw new InvalidOperationException($"OnnxRuntimeSettings can only be used with the {ChatProvider.OnnxRuntime} provider.");
            
            return (providerSpecific.OnnxRuntime ??= new OnnxRuntimeSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create OnnxRuntimeSettings");
        }
        else if (typeof(TProviderConfig) == typeof(MistralSettings))
        {
            if (provider != ChatProvider.Mistral)
                throw new InvalidOperationException($"MistralSettings can only be used with the {ChatProvider.Mistral} provider.");
            
            return (providerSpecific.Mistral ??= new MistralSettings()) as TProviderConfig
                ?? throw new InvalidOperationException("Failed to create MistralSettings");
        }
        else
        {
            throw new InvalidOperationException($"Unsupported provider configuration type: {typeof(TProviderConfig).Name}. " +
                "Supported types: AnthropicSettings, OpenAISettings, AzureOpenAISettings, OllamaSettings, OpenRouterSettings, GoogleAISettings, VertexAISettings, HuggingFaceSettings, BedrockSettings, AzureAIInferenceSettings, OnnxRuntimeSettings, MistralSettings.");
        }
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
    /// Resolves the provider URI based on provider configuration, following Microsoft.Extensions.AI patterns
    /// </summary>
    /// <param name="provider">Provider configuration</param>
    /// <returns>Provider URI if resolvable, otherwise null</returns>
    internal static Uri? ResolveProviderUri(ProviderConfig? provider)
    {
        if (provider?.Endpoint != null)
        {
            try
            {
                return new Uri(provider.Endpoint);
            }
            catch (UriFormatException)
            {
                // Invalid URI format, return null
                return null;
            }
        }

        return provider?.Provider switch
        {
            ChatProvider.OpenAI => new Uri("https://api.openai.com"),
            ChatProvider.OpenRouter => new Uri("https://openrouter.ai/api"),
            ChatProvider.Ollama => new Uri("http://localhost:11434"),
            ChatProvider.Mistral => new Uri("https://api.mistral.ai"),
            ChatProvider.Anthropic => new Uri("https://api.anthropic.com"),
            ChatProvider.GoogleAI => new Uri("https://generativelanguage.googleapis.com"),
            ChatProvider.VertexAI => new Uri("https://us-central1-aiplatform.googleapis.com"), // Default region
            ChatProvider.HuggingFace => new Uri("https://api-inference.huggingface.co"),
            ChatProvider.Bedrock => new Uri("https://bedrock-runtime.us-east-1.amazonaws.com"), // Default region
            ChatProvider.AzureOpenAI => null, // Requires specific endpoint
            ChatProvider.AzureAIInference => null, // Requires specific endpoint  
            ChatProvider.OnnxRuntime => null, // Local model, no URI
            _ => null
        };
    }

    /// <summary>
    /// Creates an IChatClient from a ProviderConfig.
    /// Used by Agent's history reduction to create a separate summarizer client.
    /// </summary>
    internal static IChatClient CreateClientFromProviderConfig(ProviderConfig providerConfig)
    {
        // Use a dummy builder to access extension methods
        var builder = new AgentBuilder();
        return builder.CreateClientFromProvider(
            providerConfig.Provider,
            providerConfig.ModelName,
            providerConfig.ApiKey);
    }
}

#endregion
#region Error Handling Policy

/// <summary>
/// Error handling policy that normalizes exceptions across different providers
/// into consistent ErrorContent format following Microsoft.Extensions.AI patterns.
/// </summary>
public class ErrorHandlingPolicy
{
    /// <summary>
    /// Whether to normalize provider-specific errors into standard formats
    /// </summary>
    public bool NormalizeProviderErrors { get; set; } = true;

    /// <summary>
    /// Whether to include provider-specific details in error messages
    /// </summary>
    public bool IncludeProviderDetails { get; set; } = false;

    /// <summary>
    /// Maximum number of retries for transient errors
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Normalizes an exception into a consistent ErrorContent format
    /// </summary>
    /// <param name="ex">The exception to normalize</param>
    /// <param name="provider">The provider that generated the exception</param>
    /// <returns>Normalized ErrorContent</returns>
    public ErrorContent NormalizeError(Exception ex, ChatProvider provider)
    {
        if (!NormalizeProviderErrors)
        {
            return new ErrorContent(ex.Message);
        }

        var normalizedMessage = ex.Message;
        var errorCode = "UnknownError";

        // Provider-specific error normalization
        switch (provider)
        {
            case ChatProvider.OpenAI:
                (normalizedMessage, errorCode) = NormalizeOpenAIError(ex);
                break;
            case ChatProvider.OpenRouter:
                (normalizedMessage, errorCode) = NormalizeOpenRouterError(ex);
                break;
            case ChatProvider.AzureOpenAI:
                (normalizedMessage, errorCode) = NormalizeAzureError(ex);
                break;
            case ChatProvider.AzureAIInference:
                (normalizedMessage, errorCode) = NormalizeAzureAIInferenceError(ex);
                break;
            case ChatProvider.Anthropic:
                (normalizedMessage, errorCode) = NormalizeAnthropicError(ex);
                break;
            case ChatProvider.Ollama:
                (normalizedMessage, errorCode) = NormalizeOllamaError(ex);
                break;
            case ChatProvider.GoogleAI:
                (normalizedMessage, errorCode) = NormalizeGoogleAIError(ex);
                break;
            case ChatProvider.VertexAI:
                (normalizedMessage, errorCode) = NormalizeVertexAIError(ex);
                break;
            case ChatProvider.HuggingFace:
                (normalizedMessage, errorCode) = NormalizeHuggingFaceError(ex);
                break;
            case ChatProvider.Bedrock:
                (normalizedMessage, errorCode) = NormalizeBedrockError(ex);
                break;
            case ChatProvider.OnnxRuntime:
                (normalizedMessage, errorCode) = NormalizeOnnxRuntimeError(ex);
                break;
            case ChatProvider.Mistral:
                (normalizedMessage, errorCode) = NormalizeMistralError(ex);
                break;
            default:
                normalizedMessage = ex.Message;
                errorCode = "ProviderError";
                break;
        }

        var errorContent = new ErrorContent(normalizedMessage)
        {
            ErrorCode = errorCode
        };

        // Add provider details if requested
        if (IncludeProviderDetails)
        {
            errorContent.AdditionalProperties ??= new();
            errorContent.AdditionalProperties["Provider"] = provider.ToString();
            errorContent.AdditionalProperties["OriginalMessage"] = ex.Message;
            errorContent.AdditionalProperties["ExceptionType"] = ex.GetType().Name;
        }

        return errorContent;
    }

    /// <summary>
    /// Determines if an error is transient and should be retried
    /// </summary>
    /// <param name="ex">The exception to check</param>
    /// <param name="provider">The provider that generated the exception</param>
    /// <returns>True if the error is transient</returns>
    public bool IsTransientError(Exception ex, ChatProvider provider)
    {
        var message = ex.Message.ToLowerInvariant();

        // Common transient error patterns
        if (message.Contains("rate limit") ||
            message.Contains("timeout") ||
            message.Contains("503") ||
            message.Contains("502") ||
            message.Contains("network") ||
            message.Contains("connection"))
        {
            return true;
        }

        // Provider-specific transient patterns
        return provider switch
        {
            ChatProvider.OpenAI => message.Contains("overloaded") || message.Contains("429"),
            ChatProvider.OpenRouter => message.Contains("queue") || message.Contains("busy"),
            ChatProvider.AzureOpenAI => message.Contains("deployment busy") || message.Contains("throttling"),
            ChatProvider.AzureAIInference => message.Contains("throttling") || message.Contains("resource busy"),
            ChatProvider.Anthropic => message.Contains("overloaded") || message.Contains("rate_limit"),
            ChatProvider.Ollama => message.Contains("loading") || message.Contains("busy"),
            ChatProvider.GoogleAI => message.Contains("quota exceeded") || message.Contains("backend error"),
            ChatProvider.VertexAI => message.Contains("quota exceeded") || message.Contains("backend error"),
            ChatProvider.HuggingFace => message.Contains("model loading") || message.Contains("estimated_time"),
            ChatProvider.Bedrock => message.Contains("throttling") || message.Contains("model busy"),
            ChatProvider.OnnxRuntime => message.Contains("model loading") || message.Contains("initialization"),
            ChatProvider.Mistral => message.Contains("rate limit") || message.Contains("overloaded"),
            _ => false
        };
    }

    private static (string message, string code) NormalizeOpenAIError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("rate limit") => ("Rate limit exceeded. Please try again later.", "RateLimit"),
            var m when m.Contains("insufficient quota") => ("API quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("invalid api key") => ("Invalid API key provided.", "InvalidApiKey"),
            var m when m.Contains("model_not_found") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("refused") => ("Request was refused by the model.", "Refusal"),
            var m when m.Contains("context_length_exceeded") => ("Input exceeds maximum context length.", "ContextTooLong"),
            var m when m.Contains("content filter") => ("Content was filtered due to policy violations.", "ContentFiltered"),
            _ => (message, "OpenAIError")
        };
    }

    private static (string message, string code) NormalizeOpenRouterError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("rate limit") => ("Rate limit exceeded. Please try again later.", "RateLimit"),
            var m when m.Contains("credits") => ("Insufficient credits.", "InsufficientCredits"),
            var m when m.Contains("queue") => ("Request queued due to high demand.", "Queued"),
            var m when m.Contains("model unavailable") => ("Model is currently unavailable.", "ModelUnavailable"),
            _ => (message, "OpenRouterError")
        };
    }

    private static (string message, string code) NormalizeAzureError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("unauthorized") => ("Authentication failed. Check your API key.", "AuthenticationFailed"),
            var m when m.Contains("deployment not found") => ("Model deployment not found.", "DeploymentNotFound"),
            var m when m.Contains("quota") => ("Deployment quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("content filter") => ("Content filtered by Azure policies.", "ContentFiltered"),
            _ => (message, "AzureError")
        };
    }

    private static (string message, string code) NormalizeOllamaError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("model not found") => ("Model not found. Please pull the model first.", "ModelNotFound"),
            var m when m.Contains("connection refused") => ("Cannot connect to Ollama server.", "ConnectionFailed"),
            var m when m.Contains("loading") => ("Model is still loading. Please wait.", "ModelLoading"),
            var m when m.Contains("out of memory") => ("Insufficient memory to run model.", "OutOfMemory"),
            _ => (message, "OllamaError")
        };
    }

    private static (string message, string code) NormalizeAzureAIInferenceError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("unauthorized") => ("Authentication failed. Check your API key.", "AuthenticationFailed"),
            var m when m.Contains("quota") => ("API quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("throttling") => ("Request was throttled. Please retry after some time.", "Throttled"),
            var m when m.Contains("model not found") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("resource busy") => ("Azure AI resource is busy. Please try again later.", "ResourceBusy"),
            var m when m.Contains("content filter") => ("Content filtered by Azure policies.", "ContentFiltered"),
            _ => (message, "AzureAIInferenceError")
        };
    }

    private static (string message, string code) NormalizeAnthropicError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("rate_limit_error") => ("Rate limit exceeded. Please try again later.", "RateLimit"),
            var m when m.Contains("invalid_request_error") => ("Invalid request. Check your parameters.", "InvalidRequest"),
            var m when m.Contains("authentication_error") => ("Authentication failed. Check your API key.", "AuthenticationFailed"),
            var m when m.Contains("permission_error") => ("Permission denied. Check your access rights.", "PermissionDenied"),
            var m when m.Contains("not_found_error") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("overloaded_error") => ("Service is overloaded. Please try again later.", "ServiceOverloaded"),
            var m when m.Contains("api_error") => ("Internal API error occurred.", "InternalApiError"),
            _ => (message, "AnthropicError")
        };
    }

    private static (string message, string code) NormalizeGoogleAIError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("invalid_api_key") => ("Invalid API key provided.", "InvalidApiKey"),
            var m when m.Contains("quota_exceeded") => ("API quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("resource_exhausted") => ("Resource quota exhausted.", "ResourceExhausted"),
            var m when m.Contains("model_not_found") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("permission_denied") => ("Permission denied. Check your access rights.", "PermissionDenied"),
            var m when m.Contains("backend_error") => ("Backend service error. Please try again later.", "BackendError"),
            var m when m.Contains("safety") => ("Content was blocked due to safety policies.", "SafetyBlocked"),
            _ => (message, "GoogleAIError")
        };
    }

    private static (string message, string code) NormalizeVertexAIError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("unauthenticated") => ("Authentication failed. Check your credentials.", "AuthenticationFailed"),
            var m when m.Contains("permission_denied") => ("Permission denied. Check your access rights.", "PermissionDenied"),
            var m when m.Contains("quota_exceeded") => ("Project quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("resource_exhausted") => ("Resource quota exhausted.", "ResourceExhausted"),
            var m when m.Contains("model_not_found") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("backend_error") => ("Backend service error. Please try again later.", "BackendError"),
            var m when m.Contains("safety") => ("Content was blocked due to safety policies.", "SafetyBlocked"),
            _ => (message, "VertexAIError")
        };
    }

    private static (string message, string code) NormalizeHuggingFaceError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("authorization") => ("Authentication failed. Check your API key.", "AuthenticationFailed"),
            var m when m.Contains("model loading") => ("Model is still loading. Please wait.", "ModelLoading"),
            var m when m.Contains("estimated_time") => ("Model loading in progress. Please wait.", "ModelLoading"),
            var m when m.Contains("rate limit") => ("Rate limit exceeded. Please try again later.", "RateLimit"),
            var m when m.Contains("model_not_found") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("service_unavailable") => ("Service temporarily unavailable.", "ServiceUnavailable"),
            var m when m.Contains("bad_request") => ("Invalid request. Check your parameters.", "InvalidRequest"),
            _ => (message, "HuggingFaceError")
        };
    }

    private static (string message, string code) NormalizeBedrockError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("accessdenied") => ("Access denied. Check your AWS credentials and permissions.", "AccessDenied"),
            var m when m.Contains("throttling") => ("Request was throttled. Please retry after some time.", "Throttled"),
            var m when m.Contains("model_not_ready") => ("Model is not ready. Please try again later.", "ModelNotReady"),
            var m when m.Contains("validation") => ("Invalid request. Check your parameters.", "ValidationError"),
            var m when m.Contains("service_quota") => ("Service quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("model_error") => ("Model execution error occurred.", "ModelError"),
            var m when m.Contains("content_policy") => ("Content was blocked due to content policies.", "ContentBlocked"),
            _ => (message, "BedrockError")
        };
    }

    private static (string message, string code) NormalizeOnnxRuntimeError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("model not found") => ("ONNX model file not found.", "ModelNotFound"),
            var m when m.Contains("initialization") => ("Model initialization failed.", "InitializationFailed"),
            var m when m.Contains("invalid input") => ("Invalid input format or dimensions.", "InvalidInput"),
            var m when m.Contains("out of memory") => ("Insufficient memory to run model.", "OutOfMemory"),
            var m when m.Contains("session") => ("Model session error occurred.", "SessionError"),
            var m when m.Contains("provider") => ("Execution provider error.", "ProviderError"),
            var m when m.Contains("graph") => ("Model graph error occurred.", "GraphError"),
            _ => (message, "OnnxRuntimeError")
        };
    }

    private static (string message, string code) NormalizeMistralError(Exception ex)
    {
        var message = ex.Message;

        return message.ToLowerInvariant() switch
        {
            var m when m.Contains("unauthorized") => ("Authentication failed. Check your API key.", "AuthenticationFailed"),
            var m when m.Contains("rate limit") => ("Rate limit exceeded. Please try again later.", "RateLimit"),
            var m when m.Contains("overloaded") => ("Service is overloaded. Please try again later.", "ServiceOverloaded"),
            var m when m.Contains("model_not_found") => ("Model not found or not accessible.", "ModelNotFound"),
            var m when m.Contains("bad_request") => ("Invalid request. Check your parameters.", "InvalidRequest"),
            var m when m.Contains("quota") => ("API quota exceeded.", "QuotaExceeded"),
            var m when m.Contains("internal_error") => ("Internal server error occurred.", "InternalError"),
            _ => (message, "MistralError")
        };
    }
}


#endregion
/// <summary>
/// Empty service provider for middleware when no service provider is available
/// </summary>
internal class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();
    public object? GetService(Type serviceType) => null;
}
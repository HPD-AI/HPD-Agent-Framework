using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Azure.AI.Inference;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using OllamaSharp;
using FluentValidation;

/// <summary>
/// Builder for creating dual interface agents with sophisticated capabilities
/// This is your equivalent of the AgentBuilder from Semantic Kernel, but for the new architecture
/// </summary>
public class AgentBuilder
{
    // The new central configuration object
    private readonly AgentConfig _config;

    // Fields that are NOT part of the serializable config remain
    private IChatClient? _baseClient;
    private IConfiguration? _configuration;
    private readonly PluginManager _pluginManager = new();
    private IPluginMetadataContext? _defaultContext;
    // store individual plugin contexts
    private readonly Dictionary<string, IPluginMetadataContext?> _pluginContexts = new();
    private readonly List<IAiFunctionFilter> _globalFilters = new();
    private readonly ScopedFilterManager _scopedFilterManager = new();
    private readonly BuilderScopeContext _scopeContext = new();
    private readonly List<IPromptFilter> _promptFilters = new();
    private IAiFunctionFilter? _permissionFilter; // Dedicated permission filter

    // Audio capability fields (runtime only)
    private ISpeechToTextClient? _sttClient;
    private ITextToSpeechClient? _ttsClient;
    private readonly Dictionary<Type, object> _providerConfigs = new();
    private IServiceProvider? _serviceProvider;
    private ILoggerFactory? _logger;

    // Memory Injected Memory runtime fields
    private AgentInjectedMemoryManager? _memoryInjectedManager;  // track externally provided manager

    // MCP runtime fields
    private MCPClientManager? _mcpClientManager;

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
    /// Adds Function Invocation filters that apply to all tool calls in conversations
    /// </summary>
    public AgentBuilder WithFunctionInvokationFilters(params IAiFunctionFilter[] filters)
    {
        if (filters != null)
        {
            foreach (var filter in filters)
            {
                _scopedFilterManager.AddFilter(filter, _scopeContext.CurrentScope, _scopeContext.CurrentTarget);
            }
        }
        return this;
    }
    
    /// <summary>
    /// Adds an Function Invocation filter by type (will be instantiated)
    /// </summary>
    public AgentBuilder WithFunctionInvocationFilter<T>() where T : IAiFunctionFilter, new()
    {
        var filter = new T();
        _scopedFilterManager.AddFilter(filter, _scopeContext.CurrentScope, _scopeContext.CurrentTarget);
        return this;
    }
    
    /// <summary>
    /// Adds an function filter instance
    /// </summary>
    public AgentBuilder WithFilter(IAiFunctionFilter filter)
    {
        if (filter is FunctionPermissionFilter pFilter)
        {
            // Store permission filter separately
            _permissionFilter = pFilter;
        }
        else if (filter != null)
        {
            _scopedFilterManager.AddFilter(filter, _scopeContext.CurrentScope, _scopeContext.CurrentTarget);
        }
        return this;
    }
    
    // Audio Capability Configurations
    
    /// <summary>Register STT client directly</summary>
    public AgentBuilder WithSpeechToText(ISpeechToTextClient sttClient)
    {
        _sttClient = sttClient ?? throw new ArgumentNullException(nameof(sttClient));
        return this;
    }

    /// <summary>Register TTS client directly</summary>
    public AgentBuilder WithTextToSpeech(ITextToSpeechClient ttsClient)
    {
        _ttsClient = ttsClient ?? throw new ArgumentNullException(nameof(ttsClient));
        return this;
    }

    /// <summary>Configure audio capability options</summary>
    public AgentBuilder WithAudioOptions(Action<AudioCapabilityOptions> configure)
    {
        if (_config.Audio == null)
            _config.Audio = new AudioConfig();
        _config.Audio.Options ??= new AudioCapabilityOptions();
        configure(_config.Audio.Options);
        return this;
    }

    /// <summary>ElevenLabs STT with configuration object</summary>
    public AgentBuilder WithElevenLabsSpeechToText(ElevenLabsConfig config)
    {
        var client = new ElevenLabsSpeechToTextClient(config, null, _logger?.CreateLogger<ElevenLabsSpeechToTextClient>());
        return WithSpeechToText(client);
    }

    /// <summary>ElevenLabs TTS with configuration object</summary>
    public AgentBuilder WithElevenLabsTextToSpeech(ElevenLabsConfig config, string? voiceId = null)
    {
        var client = new ElevenLabsTextToSpeechClient(config, voiceId, null, 
            _logger?.CreateLogger<ElevenLabsTextToSpeechClient>());
        return WithTextToSpeech(client);
    }

    /// <summary>ElevenLabs complete pipeline</summary>
    public AgentBuilder WithElevenLabsAudio(string? apiKey = null, string? voiceId = null)
    {
        var config = ResolveOrCreateConfig<ElevenLabsConfig>();
        if (!string.IsNullOrEmpty(apiKey))
            config.ApiKey = apiKey;
        if (!string.IsNullOrEmpty(voiceId))
            config.DefaultVoiceId = voiceId;
        
        // Store in config
        if (_config.Audio == null)
            _config.Audio = new AudioConfig();
        _config.Audio.ElevenLabs = config;
        
        return WithElevenLabsSpeechToText(config)
               .WithElevenLabsTextToSpeech(config, voiceId);
    }

    #region Azure Speech Integration

    /// <summary>Azure Speech STT with configuration object</summary>
    public AgentBuilder WithAzureSpeechToText(AzureSpeechConfig config)
    {
        config.Validate();
        var client = new AzureSpeechToTextClient(config, _logger?.CreateLogger<AzureSpeechToTextClient>());
        return WithSpeechToText(client);
    }

    /// <summary>Azure Speech STT with key parameters</summary>
    public AgentBuilder WithAzureSpeechToText(string apiKey, string region, string? language = null)
    {
        var config = new AzureSpeechConfig
        {
            ApiKey = apiKey,
            Region = region,
            DefaultLanguage = language ?? "en-US"
        };
        return WithAzureSpeechToText(config);
    }

    /// <summary>Azure Speech TTS with configuration object</summary>
    public AgentBuilder WithAzureTextToSpeech(AzureSpeechConfig config)
    {
        config.Validate();
        var client = new AzureTextToSpeechClient(config, _logger?.CreateLogger<AzureTextToSpeechClient>());
        return WithTextToSpeech(client);
    }

    /// <summary>Azure Speech complete pipeline</summary>
    public AgentBuilder WithAzureSpeechAudio(string apiKey, string region, string? voice = null)
    {
        var config = new AzureSpeechConfig
        {
            ApiKey = apiKey,
            Region = region,
            DefaultVoice = voice ?? "en-US-AriaNeural"
        };
        
        // Store in config
        if (_config.Audio == null)
            _config.Audio = new AudioConfig();
        _config.Audio.AzureSpeech = config;
        
        return WithAzureSpeechToText(config)
               .WithAzureTextToSpeech(config);
    }

    #endregion

    /// <summary>
    /// Adds a prompt filter instance
    /// </summary>
    public AgentBuilder WithPromptFilter(IPromptFilter filter)
    {
        if (filter != null)
        {
            _promptFilters.Add(filter);
        }
        return this;
    }

    /// <summary>
    /// Adds a prompt filter by type (will be instantiated)
    /// </summary>
    public AgentBuilder WithPromptFilter<T>() where T : IPromptFilter, new()
        => WithPromptFilter(new T());

    /// <summary>
    /// Adds multiple prompt filters
    /// </summary>
    public AgentBuilder WithPromptFilters(params IPromptFilter[] filters)
    {
        if (filters != null)
        {
            foreach (var f in filters) _promptFilters.Add(f);
        }
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
    /// Configures the agent to use a specific provider with model name
    /// </summary>
    /// <param name="provider">The chat provider to use</param>
    /// <param name="modelName">The specific model name (e.g., "gpt-4", "anthropic/claude-3.5-sonnet")</param>
    /// <param name="apiKey">API key for the provider (optional - will fallback to configuration/environment)</param>
    public AgentBuilder WithProvider(ChatProvider provider, string modelName, string? apiKey = null)
    {
        _config.Provider = new ProviderConfig
        {
            Provider = provider,
            ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName)),
            ApiKey = apiKey
        };
        return this;
    }
    
    /// <summary>
    /// Sets the configuration source for reading API keys and other settings
    /// </summary>
    /// <param name="configuration">Configuration instance (e.g., from appsettings.json)</param>
    public AgentBuilder WithAPIConfiguration(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        return this;
    }
    
    /// <summary>
    /// Sets the base IChatClient that provides the core LLM functionality
    /// </summary>
    public AgentBuilder WithBaseClient(IChatClient baseClient)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
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
    /// Registers a plugin by type with optional execution context.
    /// </summary>
    public AgentBuilder WithPlugin<T>(IPluginMetadataContext? context = null) where T : class, new()
    {
        _pluginManager.RegisterPlugin<T>();
        var pluginName = typeof(T).Name;
        _scopeContext.SetPluginScope(pluginName);
        _pluginContexts[pluginName] = context;
        return this;
    }
    
    /// <summary>
    /// Registers a plugin using an instance with optional execution context.
    /// </summary>
    public AgentBuilder WithPlugin<T>(T instance, IPluginMetadataContext? context = null) where T : class
    {
        _pluginManager.RegisterPlugin(instance);
        var pluginName = typeof(T).Name;
        _scopeContext.SetPluginScope(pluginName);
        _pluginContexts[pluginName] = context;
        return this;
    }
    
    /// <summary>
    /// Registers a plugin by Type with optional execution context.
    /// </summary>
    public AgentBuilder WithPlugin(Type pluginType, IPluginMetadataContext? context = null)
    {
        _pluginManager.RegisterPlugin(pluginType);
        var pluginName = pluginType.Name;
        _scopeContext.SetPluginScope(pluginName);
        _pluginContexts[pluginName] = context;
        return this;
    }

    /// <summary>
    /// Registers a raw function directly (manual registration, not via plugin), using HPDAIFunctionFactory for enhanced metadata.
    /// Note: Filters are now applied at the conversation level, not per function.
    /// </summary>
    public AgentBuilder WithFunction(Delegate method, string? name = null, string? description = null, Dictionary<string, string>? parameterDescriptions = null)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        // Get or create default chat options
        if (_config.Provider == null)
            _config.Provider = new ProviderConfig();
        if (_config.Provider.DefaultChatOptions == null)
            _config.Provider.DefaultChatOptions = new ChatOptions();
        var tools = _config.Provider.DefaultChatOptions.Tools?.ToList() ?? new List<AITool>();
        var options = new HPDAIFunctionFactoryOptions
        {
            Name = name ?? method.Method.Name,
            Description = description,
            ParameterDescriptions = parameterDescriptions
        };
        
        var function = HPDAIFunctionFactory.Create(method, options);
        tools.Add(function);
        _config.Provider.DefaultChatOptions.Tools = tools;
        
        // Set scope context for subsequent filter registrations
        var functionName = name ?? method.Method.Name;
        _scopeContext.SetFunctionScope(functionName);
        
        return this;
    }
    
    // MCP Integration Methods
    
    /// <summary>
    /// Enables MCP support with the specified manifest file
    /// </summary>
    /// <param name="manifestPath">Path to the MCP manifest JSON file</param>
    /// <param name="options">Optional MCP configuration options</param>
    public AgentBuilder WithMCP(string manifestPath, MCPOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Manifest path cannot be null or empty", nameof(manifestPath));

        _config.Mcp = new McpConfig
        {
            ManifestPath = manifestPath,
            Options = options
        };
        _mcpClientManager = new MCPClientManager(_logger?.CreateLogger<MCPClientManager>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MCPClientManager>.Instance, options);
        
        return this;
    }

    /// <summary>
    /// Enables MCP support with fluent configuration
    /// </summary>
    /// <param name="manifestPath">Path to the MCP manifest JSON file</param>
    /// <param name="configure">Configuration action for MCP options</param>
    public AgentBuilder WithMCP(string manifestPath, Action<MCPOptions> configure)
    {
        var options = new MCPOptions();
        configure(options);
        return WithMCP(manifestPath, options);
    }

    /// <summary>
    /// Enables MCP support with manifest content directly
    /// </summary>
    /// <param name="manifestContent">JSON content of the MCP manifest</param>
    /// <param name="options">Optional MCP configuration options</param>
    public AgentBuilder WithMCPContent(string manifestContent, MCPOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(manifestContent))
            throw new ArgumentException("Manifest content cannot be null or empty", nameof(manifestContent));

        // Store content in ManifestPath for now - we might need a separate property for content
        _config.Mcp = new McpConfig
        {
            ManifestPath = manifestContent, // This represents content, not path
            Options = options
        };
        _mcpClientManager = new MCPClientManager(_logger?.CreateLogger<MCPClientManager>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MCPClientManager>.Instance, options);
        
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

        // If no base client provided but provider info is available, create the client
        if (_baseClient == null && _config.Provider != null && !string.IsNullOrEmpty(_config.Provider.ModelName))
        {
            // Handle Apple Intelligence as a special case since it doesn't need an API key
            if (_config.Provider.Provider == ChatProvider.AppleIntelligence)
            {
                _baseClient = CreateClientFromProvider(_config.Provider.Provider, _config.Provider.ModelName, null);
            }
            else
            {
                var apiKey = ResolveApiKey(_config.Provider.Provider, _config.Provider.ApiKey);
                _baseClient = CreateClientFromProvider(_config.Provider.Provider, _config.Provider.ModelName, apiKey);
            }
        }

        if (_baseClient == null)
            throw new InvalidOperationException("Base client must be provided using WithBaseClient() or WithProvider()");

        // Register Memory Injected Memory prompt filter if configured
        if (_config.InjectedMemory != null && _memoryInjectedManager != null)
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
        if (_mcpClientManager != null)
        {
            try
            {
                List<AIFunction> mcpTools;
                if (_config.Mcp != null && !string.IsNullOrEmpty(_config.Mcp.ManifestPath))
                {
                    // Check if this is actually content vs path based on if it starts with '{'
                    if (_config.Mcp.ManifestPath.TrimStart().StartsWith("{"))
                    {
                        mcpTools = _mcpClientManager.LoadToolsFromManifestContentAsync(_config.Mcp.ManifestPath).GetAwaiter().GetResult();
                    }
                    else
                    {
                        mcpTools = _mcpClientManager.LoadToolsFromManifestAsync(_config.Mcp.ManifestPath).GetAwaiter().GetResult();
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
        var audioCapability = CreateAudioCapability(agent);
        if (audioCapability != null)
        {
            agent.AddCapability("Audio", audioCapability);
        }

        // Attach MCP capability if configured
        if (_mcpClientManager != null)
        {
            agent.AddCapability("MCP", _mcpClientManager);
        }

        return agent;
    }
    
    #region Helper Methods
    
    /// <summary>Create audio capability during build</summary>
    private AudioCapability? CreateAudioCapability(Agent agent)
    {
        // Skip if no audio clients configured
        if (_sttClient == null && _ttsClient == null)
            return null;

        var options = _config.Audio?.Options ?? new AudioCapabilityOptions();
        
        return new AudioCapability(
            agent: agent,
            options: options,
            sttClient: _sttClient,
            ttsClient: _ttsClient,
            filterManager: _scopedFilterManager,
            logger: _logger?.CreateLogger<AudioCapability>());
    }
    
    private T ResolveOrCreateConfig<T>() where T : class, new()
    {
        // 1. Try explicitly stored config
        if (_providerConfigs.TryGetValue(typeof(T), out var stored))
            return (T)stored;
        
        // 2. Try DI container
        var fromDi = _serviceProvider?.GetService<T>();
        if (fromDi != null) return fromDi;
        
        // 3. Try creating from environment
        var fromEnv = CreateConfigFromEnvironment<T>();
        if (fromEnv != null) return fromEnv;
        
        // 4. Create default
        return new T();
    }

    private T? CreateConfigFromEnvironment<T>() where T : class
    {
        // Only create from environment in non-analyzer context
        if (IsAnalyzerContext())
            return null;
            
        return typeof(T).Name switch
        {
            nameof(ElevenLabsConfig) => CreateElevenLabsConfigFromEnvironment() as T,
            nameof(AzureSpeechConfig) => CreateAzureConfigFromEnvironment() as T,
            _ => null
        };
    }
    
    private ElevenLabsConfig? CreateElevenLabsConfigFromEnvironment()
    {
        var apiKey = GetEnvironmentVariable("ELEVENLABS_API_KEY");
        var voiceId = GetEnvironmentVariable("ELEVENLABS_VOICE_ID");
        
        if (string.IsNullOrEmpty(apiKey))
            return null;
            
        return new ElevenLabsConfig
        {
            ApiKey = apiKey,
            DefaultVoiceId = voiceId
        };
    }
    
    private AzureSpeechConfig? CreateAzureConfigFromEnvironment()
    {
        var apiKey = GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var region = GetEnvironmentVariable("AZURE_SPEECH_REGION");
        var language = GetEnvironmentVariable("AZURE_SPEECH_LANGUAGE");
        
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(region))
            return null;
            
        return new AzureSpeechConfig
        {
            ApiKey = apiKey,
            Region = region,
            DefaultLanguage = language ?? "en-US"
        };
    }
    
    private static bool IsAnalyzerContext()
    {
        // Simple heuristic to detect analyzer context
        return System.Diagnostics.Debugger.IsAttached == false;
    }
    
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
    
    private string ResolveApiKey(ChatProvider provider, string? explicitApiKey = null)
    {
        // 1. Use explicitly provided API key if available
        if (!string.IsNullOrEmpty(explicitApiKey))
            return explicitApiKey;
        
        // 2. Try configuration (appsettings.json)
        var configKey = GetConfigurationKey(provider);
        var apiKeyFromConfig = _configuration?[configKey];
        if (!string.IsNullOrEmpty(apiKeyFromConfig))
            return apiKeyFromConfig;
        
        // 3. Try environment variable
        var envKey = GetEnvironmentVariableName(provider);
        var apiKeyFromEnv = GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(apiKeyFromEnv))
            return apiKeyFromEnv;
        
        // 4. Fallback to generic environment variable names (AOT-safe)
        var genericEnvKey = GetGenericEnvironmentVariableName(provider);
        var genericApiKey = GetEnvironmentVariable(genericEnvKey);
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

    /// <summary>
    /// AOT-safe method to get generic environment variable names
    /// </summary>
    private static string GetGenericEnvironmentVariableName(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OPENROUTER_API_KEY",
        ChatProvider.OpenAI => "OPENAI_API_KEY",
        ChatProvider.AzureOpenAI => "AZUREOPENAI_API_KEY",
        ChatProvider.Ollama => "OLLAMA_API_KEY",
        ChatProvider.AppleIntelligence => "APPLEINTELLIGENCE_API_KEY",
        _ => "GENERIC_API_KEY" // AOT-safe fallback
    };
    
    private IChatClient CreateClientFromProvider(ChatProvider provider, string modelName, string? apiKey)
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
    
    /// <summary>
    /// Creates a new builder instance
    /// </summary>
    public static AgentBuilder Create() => new();
    
    /// <summary>
    /// Helper method to get environment variables (isolated to avoid analyzer warnings)
    /// </summary>
#pragma warning disable RS1035 // Environment access is valid in application code, not analyzer code
    private static string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }
#pragma warning restore RS1035

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

    #endregion

    public AgentBuilder WithContinuationManager(ContinuationPermissionManager manager)
    {
        _continuationPermissionManager = manager;
        return this;
    }
}

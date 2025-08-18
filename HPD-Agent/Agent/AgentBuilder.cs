using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Azure.AI.Inference;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using Microsoft.KernelMemory;
using OllamaSharp;
using HPD_Agent.MemoryRAG;
using HPD_Agent.MCP;

/// <summary>
/// Builder for creating dual interface agents with sophisticated capabilities
/// This is your equivalent of the AgentBuilder from Semantic Kernel, but for the new architecture
/// </summary>
public class AgentBuilder
{
    private IChatClient? _baseClient;
    private string _agentName = "HPD-Agent";
    private ChatOptions? _defaultChatOptions;
    private string? _providerApiKey;
    private ChatProvider? _provider;
    private string? _modelName;
    private IConfiguration? _configuration;
    private readonly PluginManager _pluginManager = new();
    private IPluginMetadataContext? _defaultContext;
    // store individual plugin contexts
    private readonly Dictionary<string, IPluginMetadataContext?> _pluginContexts = new();
    private string? _systemInstructions;
    private readonly List<IAiFunctionFilter> _globalFilters = new();
    private readonly ScopedFilterManager _scopedFilterManager = new();
    private readonly BuilderScopeContext _scopeContext = new();
    private ContextualFunctionConfig? _contextualConfig;
    private readonly List<IPromptFilter> _promptFilters = new();
    
    // Function calling configuration
    private int _maxFunctionCalls = 10; // Default to 10

    // Audio capability fields
    private ISpeechToTextClient? _sttClient;
    private ITextToSpeechClient? _ttsClient;
    private AudioCapabilityOptions? _audioOptions;
    private readonly Dictionary<Type, object> _providerConfigs = new();
    private IServiceProvider? _serviceProvider;
    private ILoggerFactory? _logger;

    // Memory Injected Memory configuration fields (formerly CAG)
    private AgentInjectedMemoryOptions? _memoryInjectedOptions;
    private AgentInjectedMemoryManager? _memoryInjectedManager;  // track externally provided manager

    // RAG configuration fields
    private AgentMemoryBuilder? _agentMemoryBuilder;
    private RetrievalStrategy _ragStrategy = RetrievalStrategy.Push; // Default to Push
    private RAGConfiguration _ragConfiguration = new();

    // MCP configuration fields
    private MCPClientManager? _mcpClientManager;
    private string? _mcpManifestPath;
    private string? _mcpManifestContent;

    /// <summary>
    /// Sets the system instructions/persona for the agent
    /// </summary>
    public AgentBuilder WithInstructions(string instructions)
    {
        _systemInstructions = instructions;
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
        if (filter != null)
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
        _audioOptions ??= new AudioCapabilityOptions();
        configure(_audioOptions);
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
        
        return WithAzureSpeechToText(config)
               .WithAzureTextToSpeech(config);
    }

    #endregion
    
    // Future capability configurations will go here:
    // - Memory system configuration
    // - Permission system configuration
    // - A2A delegation configuration
    
    /// <summary>
    /// Configures contextual function selection using vector similarity search
    /// </summary>
    /// <param name="configure">Configuration action for contextual function selection</param>
    public AgentBuilder WithContextualFunctions(Action<ContextualFunctionConfig> configure)
    {
        _contextualConfig = new ContextualFunctionConfig();
        configure(_contextualConfig);
        return this;
    }

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
        
        _maxFunctionCalls = maxFunctionCalls;
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
        _provider = provider;
        _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _providerApiKey = apiKey; // Can be null - will be resolved later
        return this;
    }
    
    /// <summary>
    /// Sets the configuration source for reading API keys and other settings
    /// </summary>
    /// <param name="configuration">Configuration instance (e.g., from appsettings.json)</param>
    public AgentBuilder WithConfiguration(IConfiguration configuration)
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
        _agentName = name ?? throw new ArgumentNullException(nameof(name));
        return this;
    }
    
    /// <summary>
    /// Sets default chat options (including backend tools)
    /// </summary>
    public AgentBuilder WithDefaultOptions(ChatOptions options)
    {
        _defaultChatOptions = options;
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
        if (_defaultChatOptions == null)
            _defaultChatOptions = new ChatOptions();
        var tools = _defaultChatOptions.Tools?.ToList() ?? new List<AITool>();
        var options = new HPDAIFunctionFactoryOptions
        {
            Name = name ?? method.Method.Name,
            Description = description,
            ParameterDescriptions = parameterDescriptions
        };
        
        var function = HPDAIFunctionFactory.Create(method, options);
        tools.Add(function);
        _defaultChatOptions.Tools = tools;
        
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

        _mcpManifestPath = manifestPath;
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

        _mcpManifestContent = manifestContent;
        _mcpClientManager = new MCPClientManager(_logger?.CreateLogger<MCPClientManager>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MCPClientManager>.Instance, options);
        
        return this;
    }
    
    // Future capability methods:
    // public DualInterfaceAgentBuilder WithAudioCapabilities(AudioConfiguration config) { ... }
    // public DualInterfaceAgentBuilder WithMemorySystem(MemoryConfiguration config) { ... }
    // public DualInterfaceAgentBuilder WithPermissions(PermissionConfiguration config) { ... }
    // public DualInterfaceAgentBuilder WithContextualFunctionSelection(CfsConfiguration config) { ... }
    // public DualInterfaceAgentBuilder WithA2ACommunication(A2AConfiguration config) { ... }
    
    /// <summary>
    /// Builds the dual interface agent
    /// </summary>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public Agent Build()
    {
        // If no base client provided but provider info is available, create the client
        if (_baseClient == null && _provider.HasValue && !string.IsNullOrEmpty(_modelName))
        {
            // Handle Apple Intelligence as a special case since it doesn't need an API key
            if (_provider.Value == ChatProvider.AppleIntelligence)
            {
                _baseClient = CreateClientFromProvider(_provider.Value, _modelName, null);
            }
            else
            {
                var apiKey = ResolveApiKey(_provider.Value);
                _baseClient = CreateClientFromProvider(_provider.Value, _modelName, apiKey);
            }
        }

        if (_baseClient == null)
            throw new InvalidOperationException("Base client must be provided using WithBaseClient() or WithProvider()");

        // Register Memory Injected Memory prompt filter if configured
        if (_memoryInjectedOptions != null && _memoryInjectedManager != null)
        {
            var memoryFilter = new AgentInjectedMemoryFilter(_memoryInjectedOptions, _logger?.CreateLogger<AgentInjectedMemoryFilter>());
            _promptFilters.Add(memoryFilter);
        }

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
        // Register function-to-plugin mappings for scoped filters using same contexts
        RegisterFunctionPluginMappings(pluginFunctions);
        
        // Load MCP tools if configured
        if (_mcpClientManager != null)
        {
            try
            {
                List<AIFunction> mcpTools;
                if (!string.IsNullOrEmpty(_mcpManifestPath))
                {
                    mcpTools = _mcpClientManager.LoadToolsFromManifestAsync(_mcpManifestPath).GetAwaiter().GetResult();
                }
                else if (!string.IsNullOrEmpty(_mcpManifestContent))
                {
                    mcpTools = _mcpClientManager.LoadToolsFromManifestContentAsync(_mcpManifestContent).GetAwaiter().GetResult();
                }
                else
                {
                    throw new InvalidOperationException("MCP client manager is configured but no manifest path or content provided");
                }
                
                // Add MCP tools to plugin functions list for consistent handling
                pluginFunctions.AddRange(mcpTools);
                
                var logger = _logger?.CreateLogger<AgentBuilder>();
                logger?.LogInformation("Successfully integrated {Count} MCP tools into agent", mcpTools.Count);
            }
            catch (Exception ex)
            {
                var logger = _logger?.CreateLogger<AgentBuilder>();
                logger?.LogError(ex, "Failed to load MCP tools: {Error}", ex.Message);
                throw new InvalidOperationException("Failed to initialize MCP integration", ex);
            }
        }
        
        var mergedOptions = MergePluginFunctions(_defaultChatOptions, pluginFunctions);

        // Create Memory RAG-based contextual function selector if configured
        ContextualFunctionSelector? selector = null;
        if (_contextualConfig != null)
        {
            // Build a simple kernel memory store (default in-memory)
            var kernelBuilder = new KernelMemoryBuilder();
            var functionMemory = kernelBuilder.Build<MemoryServerless>();

            selector = new ContextualFunctionSelector(
                functionMemory,
                _contextualConfig,
                pluginFunctions,
                _logger?.CreateLogger<ContextualFunctionSelector>());

            _ = Task.Run(async () =>
            {
                try
                {
                    await selector.InitializeAsync();

                    var pluginRegistrations = _pluginManager.GetPluginRegistrations();
                    foreach (var registration in pluginRegistrations)
                    {
                        foreach (var function in pluginFunctions.Where(f => f.Name.StartsWith(registration.PluginType.Name)))
                        {
                            selector.RegisterFunctionPlugin(function.Name, registration.PluginType.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to initialize contextual function selector: {ex.Message}");
                }
            });
        }

        // DO NOT wrap with FunctionInvokingChatClient - we handle function calls manually in Agent
        // This allows our Function Invocation filters to work properly
        var agent = new Agent(
            _baseClient,
            _agentName,
            mergedOptions,
            _systemInstructions,
            _promptFilters,
            _scopedFilterManager, 
            selector,
            _maxFunctionCalls);

        // Inject memory builder if it has been configured
        if (_agentMemoryBuilder != null)
        {
            agent.SetMemoryBuilder(_agentMemoryBuilder);
        }

        // Attach RAG components based on the selected strategy
        AttachRAGComponents(agent);

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
    
    #region Audio Helper Methods
    
    /// <summary>Create audio capability during build</summary>
    private AudioCapability? CreateAudioCapability(Agent agent)
    {
        // Skip if no audio clients configured
        if (_sttClient == null && _ttsClient == null)
            return null;

        var options = _audioOptions ?? new AudioCapabilityOptions();
        
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
    
    #endregion

    #region RAG Helper Methods
    
    /// <summary>
    /// Attach RAG components based on the configured strategy
    /// </summary>
    private void AttachRAGComponents(Agent agent)
    {
        // A helper function to create the capability on demand
        RAGMemoryCapability CreateRAGCapability(Agent agentInstance) => new RAGMemoryCapability(agentInstance, _ragConfiguration);

        RAGMemoryCapability? ragCapability = null;

        // Push or Hybrid strategy requires the RAGMemoryCapability
        if (_ragStrategy == RetrievalStrategy.Push || _ragStrategy == RetrievalStrategy.Hybrid)
        {
            ragCapability = CreateRAGCapability(agent);
            agent.AddCapability("RAG", ragCapability);
        }
        
        // Pull or Hybrid strategy requires the RAGPlugin
        if (_ragStrategy == RetrievalStrategy.Pull || _ragStrategy == RetrievalStrategy.Hybrid)
        {
            // If the capability wasn't created for Push, create it now for the plugin
            ragCapability ??= CreateRAGCapability(agent);
            
            var ragPlugin = new RAGPlugin(ragCapability);
            WithPlugin(ragPlugin); // WithPlugin is an existing method in AgentBuilder
        }
    }
    
    #endregion
    
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
    
    private string ResolveApiKey(ChatProvider provider)
    {
        // 1. Use explicitly provided API key if available
        if (!string.IsNullOrEmpty(_providerApiKey))
            return _providerApiKey;
        
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
        
        // 4. Fallback to generic environment variable names
        var genericEnvKey = $"{provider.ToString().ToUpper()}_API_KEY";
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
        _ => $"{provider}:ApiKey"
    };
    
    private static string GetEnvironmentVariableName(ChatProvider provider) => provider switch
    {
        ChatProvider.OpenRouter => "OPENROUTER_API_KEY",
        ChatProvider.OpenAI => "OPENAI_API_KEY",
        ChatProvider.AzureOpenAI => "AZURE_OPENAI_API_KEY", 
        ChatProvider.Ollama => "OLLAMA_API_KEY",
        _ => $"{provider.ToString().ToUpper()}_API_KEY"
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
    /// Provider integration examples:
    /// 
    /// OpenRouter with API key from configuration:
    ///   // appsettings.json: { "OpenRouter": { "ApiKey": "your-key" } }
    ///   var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
    ///   var agent = DualInterfaceAgentBuilder.Create()
    ///       .WithConfiguration(config)
    ///       .WithProvider(ChatProvider.OpenRouter, "anthropic/claude-3.5-sonnet")
    ///       .Build();
    /// 
    /// OpenRouter with environment variable:
    ///   // Set environment variable: OPENROUTER_API_KEY=your-key
    ///   var agent = DualInterfaceAgentBuilder.Create()
    ///       .WithProvider(ChatProvider.OpenRouter, "anthropic/claude-3.5-sonnet")
    ///       .Build();
    /// 
    /// OpenRouter with explicit API key:
    ///   var agent = DualInterfaceAgentBuilder.Create()
    ///       .WithProvider(ChatProvider.OpenRouter, "anthropic/claude-3.5-sonnet", "your-api-key")
    ///       .Build();
    /// 
    /// Configuration priority (highest to lowest):
    /// 1. Explicit API key parameter
    /// 2. Configuration (appsettings.json): "OpenRouter:ApiKey"
    /// 3. Environment variable: "OPENROUTER_API_KEY" 
    /// 4. Generic environment variable: "OPENROUTER_API_KEY"
    /// 
    /// Other providers (requires appropriate NuGet packages):
    /// 
    /// OpenAI:
    ///   Install-Package Microsoft.Extensions.AI.OpenAI
    ///   var client = new OpenAIClient("api-key").AsChatClient("gpt-4");
    /// 
    /// Azure OpenAI:
    ///   Install-Package Microsoft.Extensions.AI.AzureAIInference  
    ///   var client = new AzureOpenAIClient(endpoint, credential).AsChatClient("gpt-4");
    /// 
    /// For other providers, use WithBaseClient():
    ///   var agent = DualInterfaceAgentBuilder.Create()
    ///       .WithBaseClient(client)
    ///       .WithName("MyAgent")
    ///       .Build();
    /// </summary>
    
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
    public string AgentName => _agentName;

    /// <summary>
    /// Internal method to set memory builder (used by extension methods)
    /// </summary>
    internal AgentBuilder SetMemoryBuilder(AgentMemoryBuilder builder)
    {
        _agentMemoryBuilder = builder;
        return this;
    }

    /// <summary>
    /// Configure RAG memory for the agent
    /// </summary>
    public AgentBuilder WithMemory(Action<AgentMemoryBuilder> configure)
    {
        _agentMemoryBuilder ??= new AgentMemoryBuilder(_agentName);
        configure(_agentMemoryBuilder);
        return this;
    }

    /// <summary>
    /// Set the RAG retrieval strategy (Push, Pull, or Hybrid)
    /// </summary>
    public AgentBuilder WithRAGStrategy(RetrievalStrategy strategy)
    {
        _ragStrategy = strategy;
        return this;
    }

    /// <summary>
    /// Configure RAG settings
    /// </summary>
    public AgentBuilder WithRAGConfiguration(Action<RAGConfiguration> configure)
    {
        configure(_ragConfiguration);
        return this;
    }
}
using HPD.Agent.Providers;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Collections.Immutable;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using HPD_Agent.Memory.Agent.PlanMode;
using Microsoft.Agents.AI;

namespace HPD.Agent;

// NOTE: Project filter classes are defined in the global namespace with the Project class

/// <summary>
/// Dependencies needed for agent construction
/// </summary>
internal record AgentBuildDependencies(
    IChatClient ClientToUse,
    ChatOptions? MergedOptions,
    HPD.Agent.ErrorHandling.IProviderErrorHandler ErrorHandler);

/// <summary>
/// Builder for creating dual interface agents with sophisticated capabilities
/// This is your equivalent of the AgentBuilder from Semantic Kernel, but for the new architecture
/// </summary>
public class AgentBuilder
{
    // The new central configuration object
    private readonly AgentConfig _config;
    private readonly IProviderRegistry _providerRegistry;

    // Fields that are NOT part of the serializable config remain
    internal IChatClient? _baseClient;
    internal IConfiguration? _configuration;
    internal readonly PluginManager _pluginManager = new();
    internal IPluginMetadataContext? _defaultContext;
    // store individual plugin contexts
    internal readonly Dictionary<string, IPluginMetadataContext?> _pluginContexts = new();
    // Phase 5: Document store for skill instruction documents
    internal HPD_Agent.Skills.DocumentStore.IInstructionDocumentStore? _documentStore;
    // Track explicitly registered plugins (for scoping manager)
    internal readonly HashSet<string> _explicitlyRegisteredPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IAiFunctionFilter> _globalFilters = new();
    internal readonly ScopedFilterManager _scopedFilterManager = new();
    internal readonly BuilderScopeContext _scopeContext = new();
    internal readonly List<IPromptFilter> _promptFilters = new();
    internal readonly List<IPermissionFilter> _permissionFilters = new(); // Permission filters
    internal readonly List<IMessageTurnFilter> _messageTurnFilters = new(); // Message turn filters

    internal readonly Dictionary<Type, object> _providerConfigs = new();
    internal IServiceProvider? _serviceProvider;
    internal ILoggerFactory? _logger;

    // MCP runtime fields
    internal MCPClientManager? _mcpClientManager;

    // AIContextProvider factory (Microsoft protocol only)
    private Func<HPD.Agent.Microsoft.AIContextProviderFactoryContext, AIContextProvider>? _contextProviderFactory;

    /// <summary>
    /// Creates a new builder with default configuration.
    /// Provider assemblies are automatically discovered via ProviderAutoDiscovery ModuleInitializer.
    /// </summary>
    public AgentBuilder()
    {
        _config = new AgentConfig();
        _providerRegistry = new ProviderRegistry();
        RegisterDiscoveredProviders();
    }

    /// <summary>
    /// Creates a builder from existing configuration.
    /// Provider assemblies are automatically discovered via ProviderAutoDiscovery ModuleInitializer.
    /// </summary>
    public AgentBuilder(AgentConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _providerRegistry = new ProviderRegistry();
        RegisterDiscoveredProviders();
    }

    /// <summary>
    /// Creates a builder with custom provider registry (for testing).
    /// </summary>
    public AgentBuilder(AgentConfig config, IProviderRegistry providerRegistry)
    {
        _config = config;
        _providerRegistry = providerRegistry;
    }

    /// <summary>
    /// Registers all providers that were discovered by ProviderAutoDiscovery ModuleInitializer.
    /// Provider assemblies are loaded and their ModuleInitializers run before this is called.
    /// </summary>
    private void RegisterDiscoveredProviders()
    {
        foreach (var factory in ProviderDiscovery.GetFactories())
        {
            try
            {
                var provider = factory();
                _providerRegistry.Register(provider);
            }
            catch (Exception ex)
            {
                _logger?.CreateLogger<AgentBuilder>().LogWarning(ex, "Failed to register provider from discovery");
            }
        }
    }

    /// <summary>
    /// Creates a new builder from a JSON configuration file.
    /// </summary>
    /// <param name="jsonFilePath">Path to the JSON file containing AgentConfig data.</param>
    /// <exception cref="ArgumentException">Thrown when the file path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="JsonException">Thrown when the JSON is invalid or cannot be deserialized.</exception>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Provider assembly loading uses reflection in non-AOT scenarios")]
    public AgentBuilder(string jsonFilePath)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
            throw new ArgumentException("JSON file path cannot be null or empty.", nameof(jsonFilePath));

        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException($"Configuration file not found: {jsonFilePath}");

        _providerRegistry = new ProviderRegistry();

        try
        {
            var jsonContent = File.ReadAllText(jsonFilePath);
            _config = JsonSerializer.Deserialize<AgentConfig>(jsonContent, HPDJsonContext.Default.AgentConfig)
                ?? throw new JsonException("Failed to deserialize AgentConfig from JSON - result was null.");
            
            RegisterDiscoveredProviders();
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
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Provider assembly loading uses reflection in non-AOT scenarios")]
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
    /// Sets the document store for skill instruction documents.
    /// Phase 5: Required for skills that use AddDocument() or AddDocumentFromFile().
    /// Documents are uploaded and validated during Build().
    /// </summary>
    /// <param name="documentStore">Document store implementation (InMemoryInstructionStore, FileSystemInstructionStore, etc.)</param>
    public AgentBuilder WithDocumentStore(HPD_Agent.Skills.DocumentStore.IInstructionDocumentStore documentStore)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        return this;
    }

    /// <summary>
    /// Provides the service provider for resolving dependencies.
    /// Required for:
    /// - Observability: ILoggerFactory (for structured logging), IDistributedCache (for response caching)
    /// - Contextual functions: Embedding generators via UseRegisteredEmbeddingGenerator()
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

    // ══════════════════════════════════════════════════════════════════════════════
    // AIContextProvider CONFIGURATION (Microsoft Protocol Only)
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets the AI context provider factory for Microsoft protocol agents.
    /// Factory creates fresh provider instances per thread with optional state restoration.
    /// </summary>
    /// <param name="factory">Factory function that creates AIContextProvider instances</param>
    /// <returns>This builder for fluent chaining</returns>
    /// <remarks>
    /// The factory is invoked for each new thread created via <see cref="HPD.Agent.Microsoft.Agent.GetNewThread"/>.
    /// For state restoration (deserialization), check <see cref="HPD.Agent.Microsoft.AIContextProviderFactoryContext.SerializedState"/>.
    /// <para><b>Example - Stateless Provider:</b></para>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithContextProviderFactory(ctx => new MyMemoryProvider())
    ///     .BuildMicrosoftAgent();
    /// </code>
    /// <para><b>Example - Stateful Provider with Restoration:</b></para>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithContextProviderFactory(ctx =>
    ///     {
    ///         // Check if we're restoring from saved state
    ///         if (ctx.SerializedState.ValueKind != JsonValueKind.Undefined &amp;&amp;
    ///             ctx.SerializedState.ValueKind != JsonValueKind.Null)
    ///         {
    ///             return new MyMemoryProvider(ctx.SerializedState, ctx.JsonSerializerOptions);
    ///         }
    ///         return new MyMemoryProvider();
    ///     })
    ///     .BuildMicrosoftAgent();
    /// </code>
    /// </remarks>
    public AgentBuilder WithContextProviderFactory(
        Func<HPD.Agent.Microsoft.AIContextProviderFactoryContext, AIContextProvider> factory)
    {
        _contextProviderFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// Convenience method for stateless AIContextProvider types.
    /// Creates a new instance for each thread without state restoration.
    /// </summary>
    /// <typeparam name="T">AIContextProvider type with parameterless constructor</typeparam>
    /// <returns>This builder for fluent chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithContextProvider&lt;MyMemoryProvider&gt;()
    ///     .BuildMicrosoftAgent();
    /// </code>
    /// </example>
    public AgentBuilder WithContextProvider<T>() where T : AIContextProvider, new()
    {
        _contextProviderFactory = _ => new T();
        return this;
    }

    /// <summary>
    /// Convenience method for singleton AIContextProvider (shared across all threads).
    /// </summary>
    /// <param name="provider">Provider instance to share across all threads</param>
    /// <returns>This builder for fluent chaining</returns>
    /// <remarks>
    /// <b>WARNING:</b> Use only for stateless providers or when sharing state is intentional.
    /// All threads will share the same provider instance and its state.
    /// <para>For per-thread isolation, use <see cref="WithContextProviderFactory"/> or <see cref="WithContextProvider{T}"/> instead.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var sharedProvider = new MyStatelessProvider();
    /// var agent = new AgentBuilder()
    ///     .WithSharedContextProvider(sharedProvider)
    ///     .BuildMicrosoftAgent();
    /// </code>
    /// </example>
    public AgentBuilder WithSharedContextProvider(AIContextProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _contextProviderFactory = _ => provider;
        return this;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // DUAL-LAYER OBSERVABILITY ARCHITECTURE
    // ══════════════════════════════════════════════════════════════════════════════
    // HPD-Agent implements a dual-layer observability model that combines:
    // 1. LLM-level instrumentation (Microsoft.Extensions.AI middleware)
    // 2. Agent-level instrumentation (HPD's specialized services)
    //
    // ┌────────────────────────────────────────────────────────────────────────┐
    // │ LAYER 1: LLM-LEVEL OBSERVABILITY (Microsoft Middleware)               │
    // ├────────────────────────────────────────────────────────────────────────┤
    // │ • OpenTelemetryChatClient:                                             │
    // │   - Token usage histograms (prompt/completion/total tokens)            │
    // │   - Operation duration histograms (request latency)                    │
    // │   - Distributed traces (LLM call spans)                                │
    // │   - Gen AI Semantic Conventions v1.38 compliance                       │
    // │                                                                         │
    // │ • LoggingChatClient:                                                   │
    // │   - LLM invocation logging (GetResponseAsync/GetStreamingResponseAsync)│
    // │   - Request/response logging at Trace level (sensitive data)           │
    // │   - Error and cancellation logging                                     │
    // │                                                                         │
    // │ • DistributedCachingChatClient:                                        │
    // │   - Response caching with IDistributedCache (Redis, Memory, etc.)      │
    // │   - Cache key generation from messages + options                       │
    // │   - Streaming response coalescing                                      │
    // │                                                                         │
    // │ Applied: Automatically in AgentTurn.RunAsyncCore() on each LLM call    │
    // │ Wrapping: Base → Caching → Logging → Telemetry (Russian doll pattern) │
    // └────────────────────────────────────────────────────────────────────────┘
    //
    // ┌────────────────────────────────────────────────────────────────────────┐
    // │ LAYER 2: AGENT-LEVEL OBSERVABILITY (HPD Services)                     │
    // ├────────────────────────────────────────────────────────────────────────┤
    // │ • AgentTelemetryService:                                               │
    // │   - Agent decision tracking (CallLLM, Complete, Terminate)             │
    // │   - Circuit breaker trigger counting                                   │
    // │   - Iteration histograms per orchestration run                         │
    // │   - State-aware distributed tracing (AgentLoopState context)           │
    // │                                                                         │
    // │ • AgentLoggingService:                                                 │
    // │   - Agent decision logging with structured data                        │
    // │   - Circuit breaker warnings                                           │
    // │   - State snapshots at key orchestration points                        │
    // │   - Completion logging with iteration counts                           │
    // │                                                                         │
    // │ Applied: Created in Agent constructor, invoked throughout orchestration│
    // │ Scope: Agent orchestration loop, not individual LLM calls              │
    // └────────────────────────────────────────────────────────────────────────┘
    //
    // WHY DUAL-LAYER?
    // ────────────────────────────────────────────────────────────────────────────
    // Microsoft middleware cannot access agent-specific context:
    //   ✗ Agent decisions (CallLLM vs Complete vs Terminate)
    //   ✗ Circuit breaker state
    //   ✗ Iteration tracking across multiple LLM calls
    //   ✗ AgentLoopState for rich contextual tracing
    //
    // HPD services cannot instrument LLM client internals:
    //   ✗ Token usage (requires IChatClient instrumentation)
    //   ✗ Provider-specific metadata (model, server address)
    //   ✗ Cache hit/miss tracking
    //
    // Together, they provide complete observability:
    //   ✓ "Why did the agent call the LLM?" (HPD)
    //   ✓ "What did the LLM say and how much did it cost?" (Microsoft)
    //   ✓ "Was the response cached?" (Microsoft)
    //   ✓ "How many iterations did the agent take?" (HPD)
    //
    // DEVELOPER EXPERIENCE:
    // ────────────────────────────────────────────────────────────────────────────
    // .WithTelemetry()  → Automatic: Microsoft middleware + HPD service
    // .WithLogging()    → Automatic: Microsoft middleware + HPD service
    // .WithCaching()    → Automatic: Microsoft middleware only
    //
    // Result: Zero boilerplate, production-grade observability at both layers.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enables dual-layer telemetry tracking for complete observability:
    /// <list type="bullet">
    /// <item><description><b>LLM-level (Microsoft):</b> Token usage, duration, distributed traces for LLM calls</description></item>
    /// <item><description><b>Agent-level (HPD):</b> Decision tracking, circuit breaker, iteration histograms</description></item>
    /// </list>
    /// </summary>
    /// <param name="sourceName">ActivitySource/Meter name (default: "HPD.Agent")</param>
    /// <param name="enableSensitiveData">Include prompts/responses in traces (default: false)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This method automatically registers both layers of telemetry:
    /// <list type="number">
    /// <item><description>Microsoft's <c>OpenTelemetryChatClient</c> middleware for LLM instrumentation</description></item>
    /// <item><description>HPD's <c>AgentTelemetryService</c> for agent orchestration instrumentation</description></item>
    /// </list>
    /// </para>
    /// <para><b>Requirements:</b> Call <c>WithServiceProvider()</c> with an <c>ILoggerFactory</c> registered.</para>
    /// <para><b>Metrics Emitted:</b></para>
    /// <list type="bullet">
    /// <item><description><c>gen_ai.client.token.usage</c> - Token consumption per LLM call (Microsoft)</description></item>
    /// <item><description><c>gen_ai.client.operation.duration</c> - LLM call latency (Microsoft)</description></item>
    /// <item><description><c>hpd.agent.decision.count</c> - Agent decisions (CallLLM/Complete/Terminate) (HPD)</description></item>
    /// <item><description><c>hpd.agent.circuit_breaker.triggered</c> - Circuit breaker activations (HPD)</description></item>
    /// <item><description><c>hpd.agent.iteration.count</c> - Iterations per orchestration run (HPD)</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithServiceProvider(services)  // ILoggerFactory required
    ///     .WithTelemetry(sourceName: "MyApp.Agent", enableSensitiveData: false)
    ///     .WithOpenAI(apiKey, "gpt-4")
    ///     .Build();
    ///
    /// // Automatically instruments:
    /// // - LLM token usage, duration, traces (Microsoft)
    /// // - Agent decisions, iterations, circuit breaker (HPD)
    /// </code>
    /// </example>
    public AgentBuilder WithTelemetry(string? sourceName = null, bool enableSensitiveData = false)
    {
        var effectiveSourceName = sourceName ?? "HPD.Agent";

        // Configure agent-level telemetry service for HPD-specific orchestration tracing
        _config.Telemetry = new TelemetryConfig
        {
            Enabled = true,
            SourceName = effectiveSourceName,
            EnableSensitiveData = enableSensitiveData
        };

        // Automatically register Microsoft's OpenTelemetryChatClient middleware
        // This provides LLM-level tracing (token usage, duration, model calls)
        this.UseChatClientMiddleware((client, services) =>
        {
            var loggerFactory = services?.GetService<ILoggerFactory>();
            var telemetryClient = new OpenTelemetryChatClient(
                client,
                loggerFactory?.CreateLogger(typeof(OpenTelemetryChatClient)),
                effectiveSourceName);

            telemetryClient.EnableSensitiveData = enableSensitiveData;

            return telemetryClient;
        });

        return this;
    }

    /// <summary>
    /// Enables dual-layer structured logging for complete observability:
    /// <list type="bullet">
    /// <item><description><b>LLM-level (Microsoft):</b> LLM invocation logging (requests/responses/errors)</description></item>
    /// <item><description><b>Agent-level (HPD):</b> Decision logging, state snapshots, circuit breaker warnings</description></item>
    /// <item><description><b>Function-level (Optional):</b> Function invocation logging via filter</description></item>
    /// </list>
    /// </summary>
    /// <param name="enableSensitiveData">Include prompts/responses at Trace level (default: false)</param>
    /// <param name="includeFunctionInvocations">Also log function invocations via LoggingAiFunctionFilter (default: true)</param>
    /// <param name="configureFunctionFilter">Optional callback to configure the function logging filter</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This method automatically registers both layers of logging:
    /// <list type="number">
    /// <item><description>Microsoft's <c>LoggingChatClient</c> middleware for LLM invocation logging</description></item>
    /// <item><description>HPD's <c>AgentLoggingService</c> for agent orchestration logging</description></item>
    /// <item><description>(Optional) <c>LoggingAiFunctionFilter</c> for function call logging</description></item>
    /// </list>
    /// </para>
    /// <para><b>Requirements:</b> Call <c>WithServiceProvider()</c> with an <c>ILoggerFactory</c> registered.</para>
    /// <para><b>Log Levels:</b></para>
    /// <list type="bullet">
    /// <item><description><c>Debug</c> - LLM invocations, agent decisions, completions</description></item>
    /// <item><description><c>Information</c> - Agent completion summaries</description></item>
    /// <item><description><c>Warning</c> - Circuit breaker triggers, missing dependencies</description></item>
    /// <item><description><c>Trace</c> - Full message/response content (sensitive data)</description></item>
    /// <item><description><c>Error</c> - LLM errors, agent errors</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithServiceProvider(services)  // ILoggerFactory required
    ///     .WithLogging(
    ///         enableSensitiveData: false,  // Don't log prompts/responses
    ///         includeFunctionInvocations: true,
    ///         configureFunctionFilter: filter => {
    ///             filter.LogParameters = true;
    ///             filter.LogResults = false;
    ///         })
    ///     .WithOpenAI(apiKey, "gpt-4")
    ///     .Build();
    ///
    /// // Automatically logs:
    /// // - LLM requests/responses (Microsoft)
    /// // - Agent decisions, iterations (HPD)
    /// // - Function calls with parameters (filter)
    /// </code>
    /// </example>
    public AgentBuilder WithLogging(
        bool enableSensitiveData = false,
        bool includeFunctionInvocations = true)
    {
        // Configure agent-level logging service for HPD-specific orchestration logging
        _config.Logging = new LoggingConfig
        {
            Enabled = true,
            EnableSensitiveData = enableSensitiveData
        };

        // Automatically register Microsoft's LoggingChatClient middleware
        // This provides LLM-level invocation logging (requests/responses)
        this.UseChatClientMiddleware((client, services) =>
        {
            var loggerFactory = services?.GetService<ILoggerFactory>();
            if (loggerFactory == null || loggerFactory == NullLoggerFactory.Instance)
            {
                // Log warning but don't fail - logging is optional
                _logger?.CreateLogger<AgentBuilder>().LogWarning(
                    "Logging is enabled but ILoggerFactory is not registered in service provider. LLM-level logging will be skipped.");
                return client;
            }

            var loggingClient = new LoggingChatClient(
                client,
                loggerFactory.CreateLogger(typeof(LoggingChatClient)));

            // Configure JSON serialization options to match HPD settings
            loggingClient.JsonSerializerOptions = AIJsonUtilities.DefaultOptions;

            return loggingClient;
        });

        // Optionally add function invocation logging filter
        if (includeFunctionInvocations)
        {
            var functionFilter = new LoggingAiFunctionFilter(_logger);
            this.WithFilter(functionFilter);
        }

        return this;
    }

    /// <summary>
    /// Enables distributed caching for LLM response caching.
    /// Dramatically reduces latency and cost for repeated queries.
    /// Automatically applies Microsoft's <c>DistributedCachingChatClient</c> middleware.
    /// </summary>
    /// <param name="cacheExpiration">Cache TTL (default: 30 minutes)</param>
    /// <param name="cacheStatefulConversations">Allow caching with ConversationId (default: false)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This method automatically registers Microsoft's <c>DistributedCachingChatClient</c> middleware
    /// which caches LLM responses in <c>IDistributedCache</c> (Redis, Memory, SQL, etc.).
    /// </para>
    /// <para><b>Requirements:</b> Call <c>WithServiceProvider()</c> with an <c>IDistributedCache</c> registered.</para>
    /// <para><b>How It Works:</b></para>
    /// <list type="number">
    /// <item><description>Generates cache key from messages + options (uses JSON serialization)</description></item>
    /// <item><description>Checks cache before making LLM call (cache hit = skip LLM entirely)</description></item>
    /// <item><description>Stores LLM response in cache for future requests (coalesces streaming responses)</description></item>
    /// <item><description>Respects <paramref name="cacheExpiration"/> TTL</description></item>
    /// </list>
    /// <para><b>Performance Impact:</b></para>
    /// <list type="bullet">
    /// <item><description>Cache hit: ~1-5ms (vs 500-5000ms LLM call)</description></item>
    /// <item><description>Cost savings: 100% for cached responses (no LLM API call)</description></item>
    /// <item><description>Best for: Repeated queries, testing, demo environments</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Setup in your DI container:
    /// services.AddDistributedMemoryCache();  // Or Redis, SQL, etc.
    ///
    /// var agent = new AgentBuilder()
    ///     .WithServiceProvider(services)  // IDistributedCache required
    ///     .WithCaching(
    ///         cacheExpiration: TimeSpan.FromHours(1),
    ///         cacheStatefulConversations: false)  // Don't cache with ConversationId
    ///     .WithOpenAI(apiKey, "gpt-4")
    ///     .Build();
    ///
    /// // First call: Cache miss → LLM call → Store in cache
    /// await agent.RunAsync("What is 2+2?");
    ///
    /// // Second call: Cache hit → Return from cache (no LLM call!)
    /// await agent.RunAsync("What is 2+2?");
    /// </code>
    /// </example>
    public AgentBuilder WithCaching(TimeSpan? cacheExpiration = null, bool cacheStatefulConversations = false)
    {
        _config.Caching = new CachingConfig
        {
            Enabled = true,
            CacheExpiration = cacheExpiration ?? TimeSpan.FromMinutes(30),
            CacheStatefulConversations = cacheStatefulConversations,
            CoalesceStreamingUpdates = true
        };

        // Automatically register Microsoft's DistributedCachingChatClient middleware
        // This provides LLM-level response caching
        this.UseChatClientMiddleware((client, services) =>
        {
            var cache = services?.GetService<IDistributedCache>();
            if (cache == null)
            {
                // Log warning but don't fail - caching is optional
                _logger?.CreateLogger<AgentBuilder>().LogWarning(
                    "Caching is enabled but IDistributedCache is not registered in service provider. Caching will be skipped.");
                return client;
            }

            return new DistributedCachingChatClient(client, cache);
        });

        return this;
    }

    /// <summary>
    /// Disables agent-level telemetry (opt-out).
    /// By default, telemetry is enabled when WithServiceProvider() is called with ILoggerFactory.
    /// </summary>
    public AgentBuilder WithoutTelemetry()
    {
        _config.Telemetry = new TelemetryConfig { Enabled = false };
        return this;
    }

    /// <summary>
    /// Disables agent-level logging (opt-out).
    /// By default, logging is enabled when WithServiceProvider() is called with ILoggerFactory.
    /// </summary>
    public AgentBuilder WithoutLogging()
    {
        _config.Logging = new LoggingConfig { Enabled = false };
        return this;
    }

    /// <summary>
    /// Configures a callback to transform ChatOptions before each LLM call.
    /// This allows dynamic runtime configuration without middleware complexity.
    /// </summary>
    /// <param name="configureOptions">Callback to modify ChatOptions before each request</param>
    /// <example>
    /// <code>
    /// builder.WithOptionsConfiguration(opts =>
    /// {
    ///     opts.Temperature = Math.Min(opts.Temperature ?? 1.0f, 0.8f);
    ///     opts.AdditionalProperties ??= new();
    ///     opts.AdditionalProperties["request_id"] = Guid.NewGuid().ToString();
    /// });
    /// </code>
    /// </example>
    public AgentBuilder WithOptionsConfiguration(Action<ChatOptions> configureOptions)
    {
        _config.ConfigureOptions = configureOptions ?? throw new ArgumentNullException(nameof(configureOptions));
        return this;
    }

    /// <summary>
    /// Adds middleware to wrap the IChatClient for custom processing.
    /// Middleware is applied dynamically on each request, so runtime provider switching still works.
    /// </summary>
    /// <param name="middleware">Function that wraps an IChatClient with custom behavior</param>
    /// <returns>The builder instance for chaining</returns>
    /// <remarks>
    /// <para>
    /// Unlike traditional middleware that wraps at build time, this middleware is applied
    /// on every request. This means runtime provider switching automatically applies your
    /// middleware to the new provider.
    /// </para>
    /// <para>
    /// Middleware is applied in the order added (first added = outermost wrapper).
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder
    ///     .UseChatClientMiddleware((client, services) =>
    ///         new RateLimitingChatClient(client, maxRequestsPerMinute: 60))
    ///     .UseChatClientMiddleware((client, services) =>
    ///         new CostTrackingChatClient(client, services?.GetService&lt;ICostTracker&gt;()));
    /// </code>
    /// </example>
    public AgentBuilder UseChatClientMiddleware(Func<IChatClient, IServiceProvider?, IChatClient> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _config.ChatClientMiddleware ??= new();
        _config.ChatClientMiddleware.Add(middleware);
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
    /// Configures validation behavior for provider configuration during agent building.
    /// </summary>
    /// <param name="enableAsync">Whether to perform async validation (network calls)</param>
    public AgentBuilder WithValidation(bool enableAsync)
    {
        _config.Validation = new ValidationConfig
        {
            EnableAsyncValidation = enableAsync
        };
        return this;
    }





    /// <summary>
    /// Builds the dual interface agent asynchronously.
    /// Validation behavior is controlled by the ValidationConfig (see WithValidation()).
    /// Returns HPD.Agent.Microsoft.Agent for Microsoft protocol compatibility.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public async Task<Microsoft.Agent> BuildAsync(CancellationToken cancellationToken = default)
    {
        var buildData = await BuildDependenciesAsync(cancellationToken).ConfigureAwait(false);

        // Wrap in Microsoft protocol adapter
        return new Microsoft.Agent(
            _config!,
            buildData.ClientToUse,
            buildData.MergedOptions,
            _promptFilters,
            _scopedFilterManager!,
            buildData.ErrorHandler,
            _permissionFilters,
            _globalFilters,
            _messageTurnFilters,
            _serviceProvider,
            _contextProviderFactory);
    }

    /// <summary>
    /// Builds the dual interface agent synchronously (blocks thread until complete).
    /// Always uses sync validation for performance.
    /// Returns HPD.Agent.Microsoft.Agent by default for backwards compatibility.
    /// </summary>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public Microsoft.Agent Build()
    {
        var buildData = BuildDependenciesAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Set explicitly registered plugins in config for scoping manager
        _config.ExplicitlyRegisteredPlugins = _explicitlyRegisteredPlugins
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        // Wrap in Microsoft protocol adapter
        return new Microsoft.Agent(
            _config!,
            buildData.ClientToUse,
            buildData.MergedOptions,
            _promptFilters,
            _scopedFilterManager!,
            buildData.ErrorHandler,
            _permissionFilters,
            _globalFilters,
            _messageTurnFilters,
            _serviceProvider,
            _contextProviderFactory);
    }

    /// <summary>
    /// Builds the AGUI protocol agent asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public async Task<AGUI.Agent> BuildAGUIAsync(CancellationToken cancellationToken = default)
    {
        var buildData = await BuildDependenciesAsync(cancellationToken).ConfigureAwait(false);
        
        // Set explicitly registered plugins in config for scoping manager
        _config.ExplicitlyRegisteredPlugins = _explicitlyRegisteredPlugins
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Wrap in AGUI protocol adapter
        return new AGUI.Agent(
            _config!,
            buildData.ClientToUse,
            buildData.MergedOptions,
            _promptFilters,
            _scopedFilterManager!,
            buildData.ErrorHandler,
            _permissionFilters,
            _globalFilters,
            _messageTurnFilters,
            _serviceProvider);
    }

    /// <summary>
    /// Builds the AGUI protocol agent synchronously.
    /// </summary>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public AGUI.Agent BuildAGUI()
    {
        var buildData = BuildDependenciesAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Set explicitly registered plugins in config for scoping manager
        _config.ExplicitlyRegisteredPlugins = _explicitlyRegisteredPlugins
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        // Wrap in AGUI protocol adapter  
        return new AGUI.Agent(
            _config!,
            buildData.ClientToUse,
            buildData.MergedOptions,
            _promptFilters,
            _scopedFilterManager!,
            buildData.ErrorHandler,
            _permissionFilters,
            _globalFilters,
            _messageTurnFilters,
            _serviceProvider);
    }

    /// <summary>
    /// Core build logic shared between sync and async paths
    /// </summary>
    internal async Task<Agent> BuildCoreAsync(CancellationToken cancellationToken)
    {
        var buildData = await BuildDependenciesAsync(cancellationToken).ConfigureAwait(false);
        
        // Set explicitly registered plugins in config for scoping manager
        _config.ExplicitlyRegisteredPlugins = _explicitlyRegisteredPlugins
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Create agent using the new, cleaner constructor with AgentConfig
        var agent = new Agent(
            _config,
            buildData.ClientToUse,
            buildData.MergedOptions,
            _promptFilters,
            _scopedFilterManager,
            buildData.ErrorHandler,
            _permissionFilters,
            _globalFilters,
            _messageTurnFilters,
            _serviceProvider);

        return agent;
    }

    /// <summary>
    /// Builds all dependencies needed for agent construction
    /// </summary>
    private async Task<AgentBuildDependencies> BuildDependenciesAsync(CancellationToken cancellationToken)
    {
        // === PHASE 1: PROCESS SKILL DOCUMENTS (Before provider validation) ===
        // This happens early so documents can be uploaded even if provider config is invalid
        // Matches the StaticMemory pattern where documents are uploaded during WithStaticMemory()
        // See: Comparison with StaticMemory in architecture docs
        await ProcessSkillDocumentsAsync(cancellationToken).ConfigureAwait(false);

        // Auto-register document retrieval plugin if document store is present
        if (_documentStore != null)
        {
            _pluginManager.RegisterPlugin<HPD_Agent.Skills.DocumentStore.DocumentRetrievalPlugin>();
        }

        // Auto-register plugins referenced by skills
        AutoRegisterPluginsFromSkills();

        // === TESTING BYPASS: If BaseClient is already set, skip provider resolution ===
        // This allows tests to inject fake clients without configuring a real provider
        if (_baseClient != null)
        {
            // Use generic error handler for testing
            var testErrorHandler = new HPD.Agent.ErrorHandling.GenericErrorHandler();

            return new AgentBuildDependencies(
                _baseClient,
                _config.Provider?.DefaultChatOptions,
                testErrorHandler);
        }

        // === START: VALIDATION LOGIC ===
        var agentConfigValidator = new AgentConfigValidator();
        agentConfigValidator.ValidateAndThrow(_config);

        if (_config.Provider == null)
            throw new InvalidOperationException("Provider configuration is required.");

        // ✨ AUTO-CONFIGURE: If no configuration provided, create default configuration
        // Automatically loads from appsettings.json in the current directory
        if (_configuration == null)
        {
            try
            {
                _configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .Build();
            }
            catch (Exception ex)
            {
                // If auto-configuration fails, we'll continue without it
                // and provide helpful error message later if API key is missing
                Console.WriteLine($"[AgentBuilder] Auto-configuration warning: {ex.Message}");
            }
        }

        // ✨ PRIORITY 1: Try to resolve from connection string first (Microsoft pattern)
        if (_configuration != null)
        {
            // Try standard ConnectionStrings section first
            var connectionString = _configuration.GetConnectionString("Agent")
                                ?? _configuration.GetConnectionString("ChatClient")
                                ?? _configuration.GetConnectionString("Provider");

            if (!string.IsNullOrEmpty(connectionString) && ProviderConnectionInfo.TryParse(connectionString, out var connInfo))
            {
                // Apply connection string values (only if not already set in code)
                if (!string.IsNullOrEmpty(connInfo.Provider) && string.IsNullOrEmpty(_config.Provider.ProviderKey))
                    _config.Provider.ProviderKey = connInfo.Provider;

                if (!string.IsNullOrEmpty(connInfo.AccessKey) && string.IsNullOrEmpty(_config.Provider.ApiKey))
                    _config.Provider.ApiKey = connInfo.AccessKey;

                if (!string.IsNullOrEmpty(connInfo.Model) && string.IsNullOrEmpty(_config.Provider.ModelName))
                    _config.Provider.ModelName = connInfo.Model;

                if (connInfo.Endpoint != null && string.IsNullOrEmpty(_config.Provider.Endpoint))
                    _config.Provider.Endpoint = connInfo.Endpoint.ToString();
            }
        }

        // ✨ PRIORITY 2: Resolve from individual configuration keys (backward compatibility)
        if (string.IsNullOrEmpty(_config.Provider.ApiKey) && _configuration != null)
        {
            var providerKeyForConfig = _config.Provider.ProviderKey;
            if (string.IsNullOrEmpty(providerKeyForConfig))
                providerKeyForConfig = "openai"; // fallback default

            // Try multiple configuration patterns
            var apiKey = _configuration[$"{providerKeyForConfig}:ApiKey"]
                      ?? _configuration[$"{Capitalize(providerKeyForConfig)}:ApiKey"]
                      ?? Environment.GetEnvironmentVariable($"{providerKeyForConfig.ToUpperInvariant()}_API_KEY");

            if (!string.IsNullOrEmpty(apiKey))
            {
                _config.Provider.ApiKey = apiKey;
            }

            // Also try to resolve endpoint if not set
            if (string.IsNullOrEmpty(_config.Provider.Endpoint))
            {
                var endpoint = _configuration[$"{providerKeyForConfig}:Endpoint"]
                            ?? _configuration[$"{Capitalize(providerKeyForConfig)}:Endpoint"];

                if (!string.IsNullOrEmpty(endpoint))
                {
                    _config.Provider.Endpoint = endpoint;
                }
            }
        }

        // Resolve provider from registry
        var providerKey = _config.Provider.ProviderKey;
        if (string.IsNullOrEmpty(providerKey))
            throw new InvalidOperationException("ProviderKey in ProviderConfig cannot be empty.");

        var providerFeatures = _providerRegistry.GetProvider(providerKey);

        if (providerFeatures == null)
        {
            var availableProviders = string.Join(", ", _providerRegistry.GetRegisteredProviders());
            throw new InvalidOperationException(
                $"Provider '{providerKey}' not registered. " +
                $"Available providers: [{availableProviders}]. " +
                $"Did you forget to reference the HPD-Agent.Providers.{Capitalize(providerKey)} package?");
        }

        // Validate provider-specific configuration
        ProviderValidationResult validation;

        // Check if async validation is enabled in configuration
        var enableAsyncValidation = _config.Validation?.EnableAsyncValidation ?? false;

        // Try async validation first if enabled and supported
        if (enableAsyncValidation)
        {
            var asyncValidationTask = providerFeatures.ValidateConfigurationAsync(_config.Provider, cancellationToken);

            // If provider supports async validation (returns non-null Task)
            if (asyncValidationTask != null)
            {
                var asyncValidation = await asyncValidationTask.ConfigureAwait(false);

                // If async validation returned a result, use it; otherwise fall back to sync
                if (asyncValidation != null)
                {
                    validation = asyncValidation;
                    _logger?.CreateLogger<AgentBuilder>().LogDebug(
                        "Used async validation for provider '{ProviderKey}'", providerKey);
                }
                else
                {
                    // Async task completed but returned null, use sync
                    validation = providerFeatures.ValidateConfiguration(_config.Provider);
                }
            }
            else
            {
                // Provider doesn't support async validation (returns null Task), use sync
                validation = providerFeatures.ValidateConfiguration(_config.Provider);
            }
        }
        else
        {
            // Async validation disabled, use sync only
            validation = providerFeatures.ValidateConfiguration(_config.Provider);
        }

        if (!validation.IsValid)
        {
            // Check if this is an API key issue and provide helpful guidance
            var hasApiKeyError = validation.Errors.Any(e =>
                e.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
                e.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                e.Contains("AccessKey", StringComparison.OrdinalIgnoreCase));

            var errorMessage = $"Provider configuration for '{providerKey}' is invalid:\n- {string.Join("\n- ", validation.Errors)}";

            if (hasApiKeyError)
            {
                var providerUpper = providerKey.ToUpperInvariant();
                var providerCapitalized = Capitalize(providerKey);

                errorMessage += $"\n\n💡 Configure your API key using any of these methods:\n\n" +
                    $"1️⃣  CONNECTION STRING (recommended):\n" +
                    $"   appsettings.json → \"ConnectionStrings\": {{\n" +
                    $"     \"Agent\": \"Provider={providerKey};AccessKey=your-api-key;Model=your-model\"\n" +
                    $"   }}\n\n" +
                    $"2️⃣  CONFIGURATION FILE:\n" +
                    $"   appsettings.json → \"{providerCapitalized}\": {{ \"ApiKey\": \"your-api-key\" }}\n\n" +
                    $"3️⃣  ENVIRONMENT VARIABLE:\n" +
                    $"   {providerUpper}_API_KEY=your-api-key\n\n" +
                    $"4️⃣  USER SECRETS (development only):\n" +
                    $"   dotnet user-secrets set \"{providerCapitalized}:ApiKey\" \"your-api-key\"\n\n" +
                    $"5️⃣  CODE (for testing only, not recommended):\n" +
                    $"   Provider = new ProviderConfig {{ ApiKey = \"your-api-key\", ... }}";
            }

            throw new InvalidOperationException(errorMessage);
        }

        // Create chat client and error handler via provider factories
        _baseClient = providerFeatures.CreateChatClient(_config.Provider, _serviceProvider);
        var errorHandler = providerFeatures.CreateErrorHandler();

        if (_baseClient == null)
            throw new InvalidOperationException($"The factory for provider '{providerKey}' returned a null chat client.");
        if (errorHandler == null)
            throw new InvalidOperationException($"The factory for provider '{providerKey}' returned a null error handler.");


        // Use base client directly (no middleware pipeline)
        // Observability (telemetry, logging, caching) is integrated directly into Agent.cs
        // See: Proposals/Urgent/MIDDLEWARE_DIRECT_INTEGRATION.md
        var clientToUse = _baseClient;

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
                        mcpTools = await McpClientManager.LoadToolsFromManifestContentAsync(
                            _config.Mcp.ManifestPath,
                            enableMCPScoping,
                            maxFunctionNames,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        mcpTools = await McpClientManager.LoadToolsFromManifestAsync(
                            _config.Mcp.ManifestPath,
                            enableMCPScoping,
                            maxFunctionNames,
                            cancellationToken).ConfigureAwait(false);
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

        // Note: Old SkillDefinition-based skills have been removed in favor of type-safe Skill class.
        // Skills are now registered via plugins and auto-discovered by the source generator.

        var mergedOptions = MergePluginFunctions(_config.Provider?.DefaultChatOptions, pluginFunctions);

        // Return dependencies instead of creating agent
        return new AgentBuildDependencies(
            clientToUse,
            mergedOptions,
            errorHandler);
    }

    /// <summary>
    /// Phase 5: Process skill documents - upload files and validate references.
    /// Extracts document metadata from skill containers and processes them.
    /// If no document store is configured, creates a default FileSystemInstructionStore.
    /// </summary>
    private async Task ProcessSkillDocumentsAsync(CancellationToken cancellationToken)
    {
        var logger = _logger?.CreateLogger<AgentBuilder>();

        Console.WriteLine("[ProcessSkillDocuments] Starting document processing...");
        logger?.LogInformation("Starting skill document processing");

        // Create default document store if not provided
        if (_documentStore == null)
        {
            var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "agent-skills", "skill-documents");
            var storeLogger = _logger?.CreateLogger<HPD_Agent.Skills.DocumentStore.FileSystemInstructionStore>()
                ?? NullLogger<HPD_Agent.Skills.DocumentStore.FileSystemInstructionStore>.Instance;
            _documentStore = new HPD_Agent.Skills.DocumentStore.FileSystemInstructionStore(
                storeLogger,
                defaultPath);
            Console.WriteLine($"[ProcessSkillDocuments] Created default store at: {defaultPath}");
            logger?.LogInformation(
                "No document store configured. Using default FileSystemInstructionStore at: {Path}",
                defaultPath);
        }
        else
        {
            Console.WriteLine("[ProcessSkillDocuments] Using provided document store");
        }

        // Get all skill containers from registered plugins
        var skillContainers = new List<AIFunction>();
        foreach (var registration in _pluginManager.GetPluginRegistrations())
        {
            try
            {
                _pluginContexts.TryGetValue(registration.PluginType.Name, out var ctx);
                var functions = registration.ToAIFunctions(ctx ?? _defaultContext);

                // Filter to only skill containers
                var skills = functions.Where(f =>
                    f.AdditionalProperties?.TryGetValue("IsSkill", out var isSkill) == true &&
                    isSkill is bool isSkillBool && isSkillBool);

                skillContainers.AddRange(skills);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract skill containers from plugin {PluginType}",
                    registration.PluginType.Name);
            }
        }

        if (skillContainers.Count == 0)
        {
            logger?.LogDebug("No skills with documents found");
            return;
        }

        // Phase 1: Collect all document uploads (AddDocumentFromFile)
        var documentUploads = new List<(string SkillName, string DocumentId, string FilePath, string Description)>();

        foreach (var skillContainer in skillContainers)
        {
            var skillName = skillContainer.Name ?? "Unknown";

            if (skillContainer.AdditionalProperties?.TryGetValue("DocumentUploads", out var uploadsObj) == true &&
                uploadsObj is Array uploadsArray)
            {
                foreach (var upload in uploadsArray)
                {
                    // Source generator creates Dictionary<string, string> for uploads
                    if (upload is Dictionary<string, string> uploadDict)
                    {
                        if (uploadDict.TryGetValue("FilePath", out var filePath) &&
                            uploadDict.TryGetValue("DocumentId", out var documentId) &&
                            uploadDict.TryGetValue("Description", out var description) &&
                            !string.IsNullOrEmpty(filePath) &&
                            !string.IsNullOrEmpty(description))
                        {
                            // If documentId is empty, auto-derive from filePath
                            if (string.IsNullOrEmpty(documentId))
                            {
                                documentId = DeriveDocumentId(filePath);
                            }

                            documentUploads.Add((skillName, documentId, filePath, description));
                        }
                    }
                }
            }
        }

        // Phase 2: Upload documents with deduplication
        // IMPORTANT: This is where we read files from the OS filesystem (not in the store!)
        // The store only cares about content and where to persist it.
        var uploadedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[ProcessSkillDocuments] Found {documentUploads.Count} document uploads to process");

        foreach (var (skillName, documentId, filePath, description) in documentUploads)
        {
            Console.WriteLine($"[ProcessSkillDocuments] Processing: {documentId} from {filePath}");
            // Skip duplicates (same document ID from multiple skills)
            if (uploadedDocuments.Contains(documentId))
            {
                logger?.LogDebug("Skipping duplicate upload for document {DocumentId} (already uploaded)", documentId);
                continue;
            }

            try
            {
                // ✅ Step 1: Resolve the file path with proper error handling
                var resolvedPath = ResolveDocumentPath(filePath, skillName);

                // ✅ Step 2: Read file content (this is AgentBuilder's responsibility)
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(resolvedPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    throw new FileNotFoundException(
                        $"Document file '{filePath}' not found for skill '{skillName}'. " +
                        $"Searched at: {resolvedPath}. " +
                        $"Current working directory: {Directory.GetCurrentDirectory()}",
                        filePath);
                }
                catch (Exception fileEx) when (fileEx is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Failed to read document file '{filePath}' for skill '{skillName}': {fileEx.Message}",
                        fileEx);
                }

                // ✅ Step 3: Pass content (not path) to store
                var metadata = new HPD_Agent.Skills.DocumentStore.DocumentMetadata
                {
                    Name = documentId,
                    Description = description
                };

                await _documentStore!.UploadFromContentAsync(documentId, metadata, content, cancellationToken)
                    .ConfigureAwait(false);

                uploadedDocuments.Add(documentId);
                logger?.LogInformation(
                    "Uploaded document {DocumentId} from {FilePath} (resolved to {ResolvedPath}) for skill {SkillName}",
                    documentId, filePath, resolvedPath, skillName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = $"Failed to upload document '{documentId}' from '{filePath}' for skill '{skillName}': {ex.Message}";
                logger?.LogError(ex, message);
                throw new InvalidOperationException(message, ex);
            }
        }

        // Phase 3: Collect all document references (AddDocument) and validate
        var documentReferences = new List<(string SkillName, string DocumentId, string? DescriptionOverride)>();

        foreach (var skillContainer in skillContainers)
        {
            var skillName = skillContainer.Name ?? "Unknown";

            if (skillContainer.AdditionalProperties?.TryGetValue("DocumentReferences", out var refsObj) == true &&
                refsObj is Array refsArray)
            {
                foreach (var reference in refsArray)
                {
                    // Source generator creates Dictionary<string, string> for references
                    if (reference is Dictionary<string, string> refDict)
                    {
                        if (refDict.TryGetValue("DocumentId", out var documentId) &&
                            !string.IsNullOrEmpty(documentId))
                        {
                            refDict.TryGetValue("DescriptionOverride", out var descriptionOverride);
                            documentReferences.Add((skillName, documentId, descriptionOverride));
                        }
                    }
                }
            }
        }

        // Validate all references exist in store
        foreach (var (skillName, documentId, descriptionOverride) in documentReferences)
        {
            var exists = await _documentStore!.DocumentExistsAsync(documentId, cancellationToken)
                .ConfigureAwait(false);

            if (!exists)
            {
                var message = $"Document '{documentId}' referenced by skill '{skillName}' does not exist in store. " +
                    "Either upload it with AddDocumentFromFile() in another skill, upload externally via CLI/API, " +
                    "or ensure the skill that uploads it is registered.";
                logger?.LogError(message);
                throw new HPD_Agent.Skills.DocumentStore.DocumentNotFoundException(message, documentId);
            }

            logger?.LogDebug("Validated document reference {DocumentId} for skill {SkillName}", documentId, skillName);
        }

        // Phase 4: Link documents to skills in the store
        foreach (var skillContainer in skillContainers)
        {
            var skillName = skillContainer.Name ?? "Unknown";
            var skillNamespace = $"{skillContainer.AdditionalProperties?["ParentSkillContainer"]}";

            // Collect all documents for this skill (uploads + references)
            var skillDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add uploads
            foreach (var (sn, docId, _, _) in documentUploads.Where(u => u.SkillName == skillName))
            {
                skillDocuments.Add(docId);
            }

            // Add references
            foreach (var (sn, docId, _) in documentReferences.Where(r => r.SkillName == skillName))
            {
                skillDocuments.Add(docId);
            }

            // Link each document to the skill
            foreach (var documentId in skillDocuments)
            {
                try
                {
                    // Find description override for this skill-document pair
                    var descriptionOverride = documentReferences
                        .FirstOrDefault(r => r.SkillName == skillName && r.DocumentId == documentId)
                        .DescriptionOverride;

                    // Get default description from store
                    var docMetadata = await _documentStore!.GetDocumentMetadataAsync(documentId, cancellationToken)
                        .ConfigureAwait(false);

                    var skillDocMetadata = new HPD_Agent.Skills.DocumentStore.SkillDocumentMetadata
                    {
                        Description = descriptionOverride ?? docMetadata?.Description ?? "No description"
                    };

                    await _documentStore!.LinkDocumentToSkillAsync(
                        skillNamespace,
                        documentId,
                        skillDocMetadata,
                        cancellationToken).ConfigureAwait(false);

                    logger?.LogDebug("Linked document {DocumentId} to skill {SkillName}", documentId, skillName);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to link document {DocumentId} to skill {SkillName}",
                        documentId, skillName);
                }
            }
        }

        logger?.LogInformation(
            "Successfully processed skill documents: {UploadCount} uploads, {ReferenceCount} references, {SkillCount} skills",
            uploadedDocuments.Count, documentReferences.Count, skillContainers.Count);
    }

    #region Helper Methods

    /// <summary>
    /// Resolves a document file path relative to the current working directory.
    /// This separates concerns: AgentBuilder resolves paths, Store handles persistence.
    /// </summary>
    private string ResolveDocumentPath(string filePath, string skillName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        // If absolute path, use as-is
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        // Resolve relative to current working directory
        var resolvedPath = Path.GetFullPath(filePath);

        return resolvedPath;
    }

    /// <summary>
    /// Derives a document ID from a file path (matches SkillOptions.DeriveDocumentId)
    /// </summary>
    private static string DeriveDocumentId(string filePath)
    {
        // "./docs/debugging-workflow.md" -> "debugging-workflow"
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        // Normalize to lowercase-kebab-case
        return fileName.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }

    public bool IsProviderRegistered(string providerKey) => _providerRegistry.IsRegistered(providerKey);
    
    public IReadOnlyCollection<string> GetAvailableProviders() => _providerRegistry.GetRegisteredProviders();

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];



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
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Provider assembly loading uses reflection in non-AOT scenarios")]
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

    /// <summary>
    /// Auto-registers plugins that are referenced by skills
    /// This enables type-safe skill references without manual plugin registration
    /// </summary>
    [RequiresUnreferencedCode("Auto-registration uses reflection to discover and register plugins.")]
    private void AutoRegisterPluginsFromSkills()
    {
        // Get all currently registered plugins
        var registeredPluginNames = new HashSet<string>(
            _pluginManager.GetPluginRegistrations()
                .Select(r => r.PluginType.Name),
            StringComparer.OrdinalIgnoreCase);

        // Discover referenced plugins from all registered plugins
        var referencedPlugins = DiscoverReferencedPlugins(_pluginManager.GetPluginRegistrations());

        // Auto-register any referenced plugins that aren't already registered
        foreach (var pluginName in referencedPlugins)
        {
            if (!registeredPluginNames.Contains(pluginName))
            {
                _logger?.CreateLogger<AgentBuilder>()
                    .LogInformation("Auto-registering plugin '{PluginName}' (referenced by skills)", pluginName);

                var pluginType = FindPluginTypeByName(pluginName);
                if (pluginType == null)
                {
                    _logger?.CreateLogger<AgentBuilder>()
                        .LogWarning("Plugin '{PluginName}' is referenced by skills but could not be found. Ensure the plugin assembly is referenced.", pluginName);
                    continue;
                }

                // Register the plugin
                _pluginManager.RegisterPlugin(pluginType);
                registeredPluginNames.Add(pluginName);
            }
        }
    }

    /// <summary>
    /// Discovers all plugins referenced by skills in registered plugins
    /// </summary>
    private HashSet<string> DiscoverReferencedPlugins(IEnumerable<PluginRegistration> registrations)
    {
        var referencedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrations)
        {
            // Call generated GetReferencedPlugins() method if it exists
            var registrationType = registration.PluginType.Assembly.GetType(
                $"{registration.PluginType.Namespace}.{registration.PluginType.Name}Registration");

            if (registrationType == null)
                continue;

            var method = registrationType.GetMethod(
                "GetReferencedPlugins",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (method == null)
                continue;

            try
            {
                var plugins = method.Invoke(null, null) as string[];
                if (plugins != null)
                {
                    foreach (var plugin in plugins)
                    {
                        referencedPlugins.Add(plugin);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.CreateLogger<AgentBuilder>()
                    .LogWarning(ex, "Failed to call GetReferencedPlugins() for {PluginType}", registration.PluginType.Name);
            }
        }

        return referencedPlugins;
    }

    /// <summary>
    /// Finds a plugin type by name searching all loaded assemblies
    /// </summary>
    private Type? FindPluginTypeByName(string pluginName)
    {
        // Search all loaded assemblies for the plugin type
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes();
                var pluginType = types.FirstOrDefault(t =>
                    t.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
                    t.IsClass &&
                    t.IsPublic);

                if (pluginType != null)
                    return pluginType;
            }
            catch (System.Reflection.ReflectionTypeLoadException)
            {
                // Skip assemblies we can't load
                continue;
            }
        }

        return null;
    }
}


#region Filter Extensions
/// <summary>
/// Extension methods for configuring prompt and function filters for the AgentBuilder.
/// Internal - not exposed to users. Use AIContextProvider for public API.
/// </summary>
internal static class AgentBuilderFilterExtensions
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
    /// <param name="providerKey">The provider key to use for summarization (e.g., "openai", "anthropic")</param>
    /// <param name="modelName">The model name (e.g., "gpt-4o-mini", "claude-3-haiku-20240307")</param>
    /// <param name="apiKey">Optional API key (uses main provider's key if not specified)</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// builder
    ///     .WithOpenAI(apiKey, "gpt-4") // Main agent uses GPT-4
    ///     .WithSummarizingReduction()
    ///     .WithSummarizerProvider("openai", "gpt-4o-mini"); // Summaries use mini
    /// </code>
    /// </example>
    public static AgentBuilder WithSummarizerProvider(this AgentBuilder builder, string providerKey, string modelName, string? apiKey = null)
    {
        var config = builder.Config.HistoryReduction ?? new HistoryReductionConfig { Enabled = true };

        config.SummarizerProvider = new ProviderConfig
        {
            ProviderKey = providerKey,
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
    ///         config.ProviderKey = "ollama";
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

        var knowledgeId = options.KnowledgeId ?? options.AgentName ?? builder.AgentName;

        if (options.Store == null)
        {
            var textExtractor = new HPD_Agent.TextExtraction.TextExtractionUtility();
            options.Store = new JsonStaticMemoryStore(
                options.StorageDirectory,
                textExtractor,
                builder.Logger?.CreateLogger<JsonStaticMemoryStore>());
        }

        if (options.DocumentsToAdd.Any())
        {
            var store = options.Store;
            // Get existing documents to avoid re-extracting
            var existingDocs = store.GetDocumentsAsync(knowledgeId).GetAwaiter().GetResult();
            
            foreach (var doc in options.DocumentsToAdd)
            {
                if (store is JsonStaticMemoryStore jsonStore)
                {
                    // Check if document with this path already exists
                    var fileName = Path.GetFileName(doc.PathOrUrl);
                    var alreadyExists = existingDocs.Any(d => 
                        d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                        d.OriginalPath.Equals(doc.PathOrUrl, StringComparison.OrdinalIgnoreCase));
                    
                    if (alreadyExists)
                    {
                        // Skip - document already extracted and stored
                        continue;
                    }
                    
                    if (doc.PathOrUrl.StartsWith("http") || doc.PathOrUrl.StartsWith("https"))
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

        if (options.Strategy == MemoryStrategy.FullTextInjection)
        {
            var filter = new StaticMemoryFilter(
                options.Store,
                knowledgeId,
                options.MaxTokens,
                builder.Logger?.CreateLogger<StaticMemoryFilter>());
            builder.WithPromptFilter(filter);
        }
        else if (options.Strategy == MemoryStrategy.IndexedRetrieval)
        {
            // This is the placeholder for the future, more nuanced implementation.
            throw new NotImplementedException(
                "The IndexedRetrieval strategy is not yet implemented. A future version will use a flexible callback system.");
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
    /// Phase 4: Auto-registers referenced plugins from skills via GetReferencedPlugins().
    /// </summary>
    public static AgentBuilder WithPlugin<T>(this AgentBuilder builder, IPluginMetadataContext? context = null) where T : class, new()
    {
        builder.PluginManager.RegisterPlugin<T>();
        var pluginName = typeof(T).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = context;
        
        // Track this as explicitly registered
        builder._explicitlyRegisteredPlugins.Add(pluginName);

        // Phase 4: Auto-register referenced plugins
        AutoRegisterReferencedPlugins(builder, typeof(T));

        return builder;
    }

    /// <summary>
    /// Registers a plugin using an instance with optional execution context.
    /// Phase 4: Auto-registers referenced plugins from skills via GetReferencedPlugins().
    /// </summary>
    public static AgentBuilder WithPlugin<T>(this AgentBuilder builder, T instance, IPluginMetadataContext? context = null) where T : class
    {
        builder.PluginManager.RegisterPlugin(instance);
        var pluginName = typeof(T).Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = context;
        
        // Track this as explicitly registered
        builder._explicitlyRegisteredPlugins.Add(pluginName);

        // Phase 4: Auto-register referenced plugins
        AutoRegisterReferencedPlugins(builder, typeof(T));

        return builder;
    }

    /// <summary>
    /// Registers a plugin by Type with optional execution context.
    /// Phase 4: Auto-registers referenced plugins from skills via GetReferencedPlugins().
    /// </summary>
    public static AgentBuilder WithPlugin(this AgentBuilder builder, Type pluginType, IPluginMetadataContext? context = null)
    {
        builder.PluginManager.RegisterPlugin(pluginType);
        var pluginName = pluginType.Name;
        builder.ScopeContext.SetPluginScope(pluginName);
        builder.PluginContexts[pluginName] = context;
        
        // Track this as explicitly registered
        builder._explicitlyRegisteredPlugins.Add(pluginName);

        // Phase 4: Auto-register referenced plugins
        AutoRegisterReferencedPlugins(builder, pluginType);

        return builder;
    }

    /// <summary>
    /// Registers all plugins from a shared PluginManager instance.
    /// This allows you to create a plugin registry once and reuse it across multiple agents.
    /// Particularly useful with Skills-Only Mode where plugins must be registered for validation
    /// but you want to create multiple agents with different skill configurations.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="pluginManager">A PluginManager instance containing pre-registered plugins</param>
    /// <param name="context">Optional execution context to apply to all plugins</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// // Create shared plugin registry
    /// var sharedPlugins = new PluginManager()
    ///     .RegisterPlugin&lt;FileSystemPlugin&gt;()
    ///     .RegisterPlugin&lt;WebSearchPlugin&gt;()
    ///     .RegisterPlugin&lt;DataAnalysisPlugin&gt;();
    ///
    /// // Reuse across multiple agents with different skills
    /// var dataAgent = new AgentBuilder()
    ///     .WithPlugins(sharedPlugins)
    ///     .AddSkill("DataScience", skill =>
    ///         skill.WithFunctionReferences("DataAnalysisPlugin.Analyze"))
    ///     .EnableSkillsOnlyMode()
    ///     .Build();
    ///
    /// var webAgent = new AgentBuilder()
    ///     .WithPlugins(sharedPlugins)
    ///     .AddSkill("WebResearch", skill =>
    ///         skill.WithFunctionReferences("WebSearchPlugin.Search"))
    ///     .EnableSkillsOnlyMode()
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithPlugins(this AgentBuilder builder, PluginManager pluginManager, IPluginMetadataContext? context = null)
    {
        if (pluginManager == null)
            throw new ArgumentNullException(nameof(pluginManager));

        // Get all registrations from the provided PluginManager
        var registrations = pluginManager.GetPluginRegistrations();

        // Register each plugin in the builder's internal PluginManager
        foreach (var registration in registrations)
        {
            // Register the plugin (either by type or instance)
            if (registration.IsInstance)
            {
                builder.PluginManager.RegisterPlugin(registration.GetOrCreateInstance());
            }
            else
            {
                builder.PluginManager.RegisterPlugin(registration.PluginType);
            }

            // Set up scope context and plugin context
            var pluginName = registration.PluginType.Name;
            builder.ScopeContext.SetPluginScope(pluginName);
            builder.PluginContexts[pluginName] = context;
        }

        return builder;
    }

    /// <summary>
    /// Phase 4: Auto-registers plugins referenced by skills via GetReferencedPlugins() method.
    /// Uses reflection to discover the static GetReferencedPlugins() method generated by the source generator.
    /// Recursively registers referenced plugins to ensure all dependencies are available.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="pluginType">The plugin type to check for skill references</param>
    private static void AutoRegisterReferencedPlugins(AgentBuilder builder, Type pluginType)
    {
        // Use a HashSet to track plugins being processed to prevent infinite loops
        var processingStack = new HashSet<string>();
        AutoRegisterReferencedPluginsRecursive(builder, pluginType, processingStack);
    }

    /// <summary>
    /// Recursive implementation of auto-registration with circular reference detection.
    /// </summary>
    private static void AutoRegisterReferencedPluginsRecursive(
        AgentBuilder builder,
        Type pluginType,
        HashSet<string> processingStack)
    {
        var pluginTypeName = pluginType.Name;

        // Detect circular references
        if (processingStack.Contains(pluginTypeName))
        {
            // Circular reference detected, but this is not an error - just skip
            return;
        }

        // Add to processing stack
        processingStack.Add(pluginTypeName);

        try
        {
            // Phase 4.5: Try to get function-specific references first
            var referencedFunctionsMethod = pluginType.GetMethod(
                "GetReferencedFunctions",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            Dictionary<string, string[]>? functionMap = null;
            if (referencedFunctionsMethod != null)
            {
                functionMap = referencedFunctionsMethod.Invoke(null, null) as Dictionary<string, string[]>;
            }

            // Check if plugin has GetReferencedPlugins() method
            var method = pluginType.GetMethod(
                "GetReferencedPlugins",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (method == null)
            {
                // No GetReferencedPlugins() method - this plugin has no skill references
                return;
            }

            // Invoke GetReferencedPlugins() to get referenced plugin type names
            var referencedPluginNames = method.Invoke(null, null) as string[];

            if (referencedPluginNames == null || referencedPluginNames.Length == 0)
            {
                // No referenced plugins
                return;
            }

            // Get currently registered plugin types for deduplication
            var registeredTypes = builder.PluginManager.GetRegisteredPluginTypes();

            // Register each referenced plugin
            foreach (var referencedPluginName in referencedPluginNames)
            {
                // Check if already registered by name
                if (registeredTypes.Any(t => t.Name == referencedPluginName))
                {
                    // Already registered, skip
                    continue;
                }

                // Resolve type from name
                var referencedPluginType = ResolvePluginType(pluginType, referencedPluginName);

                if (referencedPluginType == null)
                {
                    // Could not resolve type - log warning but don't fail
                    // In production, you might want to use ILogger here
                    System.Diagnostics.Debug.WriteLine(
                        $"Warning: Could not resolve referenced plugin type '{referencedPluginName}' " +
                        $"from plugin '{pluginTypeName}'. Ensure the plugin type is accessible.");
                    continue;
                }

                // Phase 4.5: Register with function filter if available
                if (functionMap != null && functionMap.TryGetValue(referencedPluginName, out var functionNames))
                {
                    // Register only specific functions
                    builder.PluginManager.RegisterPluginFunctions(referencedPluginType, functionNames);
                }
                else
                {
                    // Fallback: register entire plugin
                    builder.PluginManager.RegisterPlugin(referencedPluginType);
                }

                // Recursively register plugins referenced by this plugin
                AutoRegisterReferencedPluginsRecursive(builder, referencedPluginType, processingStack);
            }
        }
        finally
        {
            // Remove from processing stack
            processingStack.Remove(pluginTypeName);
        }
    }

    /// <summary>
    /// Resolves a plugin type name to a Type object.
    /// Searches in the same assembly and namespace as the source plugin.
    /// </summary>
    private static Type? ResolvePluginType(Type sourcePluginType, string pluginTypeName)
    {
        // Try 1: Same assembly, same namespace
        var sourceNamespace = sourcePluginType.Namespace;
        var fullTypeName = string.IsNullOrEmpty(sourceNamespace)
            ? pluginTypeName
            : $"{sourceNamespace}.{pluginTypeName}";

        var resolvedType = sourcePluginType.Assembly.GetType(fullTypeName);
        if (resolvedType != null)
            return resolvedType;

        // Try 2: Same assembly, no namespace (short name)
        resolvedType = sourcePluginType.Assembly.GetType(pluginTypeName);
        if (resolvedType != null)
            return resolvedType;

        // Try 3: Search all types in the assembly
        resolvedType = sourcePluginType.Assembly.GetTypes()
            .FirstOrDefault(t => t.Name == pluginTypeName);
        if (resolvedType != null)
            return resolvedType;

        // Try 4: Search all loaded assemblies (last resort)
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolvedType = assembly.GetTypes()
                .FirstOrDefault(t => t.Name == pluginTypeName);
            if (resolvedType != null)
                return resolvedType;
        }

        return null;
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
    /// <param name="providerKey">The provider key that generated the exception (e.g., "openai", "openrouter")</param>
    /// <returns>Normalized ErrorContent</returns>
    public ErrorContent NormalizeError(Exception ex, string providerKey)
    {
        if (!NormalizeProviderErrors)
        {
            return new ErrorContent(ex.Message);
        }

        var normalizedMessage = ex.Message;
        var errorCode = "UnknownError";

        // Provider-specific error normalization
        switch (providerKey?.ToLowerInvariant())
        {
            case "openai":
                (normalizedMessage, errorCode) = NormalizeOpenAIError(ex);
                break;
            case "openrouter":
                (normalizedMessage, errorCode) = NormalizeOpenRouterError(ex);
                break;
            case "azureopenai":
                (normalizedMessage, errorCode) = NormalizeAzureError(ex);
                break;
            case "azureaiinference":
                (normalizedMessage, errorCode) = NormalizeAzureAIInferenceError(ex);
                break;
            case "anthropic":
                (normalizedMessage, errorCode) = NormalizeAnthropicError(ex);
                break;
            case "ollama":
                (normalizedMessage, errorCode) = NormalizeOllamaError(ex);
                break;
            case "googleai":
                (normalizedMessage, errorCode) = NormalizeGoogleAIError(ex);
                break;
            case "vertexai":
                (normalizedMessage, errorCode) = NormalizeVertexAIError(ex);
                break;
            case "huggingface":
                (normalizedMessage, errorCode) = NormalizeHuggingFaceError(ex);
                break;
            case "bedrock":
                (normalizedMessage, errorCode) = NormalizeBedrockError(ex);
                break;
            case "onnxruntime":
                (normalizedMessage, errorCode) = NormalizeOnnxRuntimeError(ex);
                break;
            case "mistral":
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
            errorContent.AdditionalProperties["Provider"] = providerKey ?? "Unknown";
            errorContent.AdditionalProperties["OriginalMessage"] = ex.Message;
            errorContent.AdditionalProperties["ExceptionType"] = ex.GetType().Name;
        }

        return errorContent;
    }

    /// <summary>
    /// Determines if an error is transient and should be retried
    /// </summary>
    /// <param name="ex">The exception to check</param>
    /// <param name="providerKey">The provider key that generated the exception (e.g., "openai", "openrouter")</param>
    /// <returns>True if the error is transient</returns>
    public bool IsTransientError(Exception ex, string providerKey)
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
        return providerKey?.ToLowerInvariant() switch
        {
            "openai" => message.Contains("overloaded") || message.Contains("429"),
            "openrouter" => message.Contains("queue") || message.Contains("busy"),
            "azureopenai" => message.Contains("deployment busy") || message.Contains("throttling"),
            "azureaiinference" => message.Contains("throttling") || message.Contains("resource busy"),
            "anthropic" => message.Contains("overloaded") || message.Contains("rate_limit"),
            "ollama" => message.Contains("loading") || message.Contains("busy"),
            "googleai" => message.Contains("quota exceeded") || message.Contains("backend error"),
            "vertexai" => message.Contains("quota exceeded") || message.Contains("backend error"),
            "huggingface" => message.Contains("model loading") || message.Contains("estimated_time"),
            "bedrock" => message.Contains("throttling") || message.Contains("model busy"),
            "onnxruntime" => message.Contains("model loading") || message.Contains("initialization"),
            "mistral" => message.Contains("rate limit") || message.Contains("overloaded"),
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

#region Configuration Extensions

public static class AgentBuilderConfigExtensions
{
    /// <summary>
    /// Sets a custom configuration source for reading API keys and other settings.
    ///
    /// ⚠️ OPTIONAL: AgentBuilder automatically loads configuration from:
    ///    - appsettings.json (in current directory)
    ///    - Environment variables
    ///    - User secrets (development only)
    ///
    /// 💡 Only use this method if you need to:
    ///    - Load from a non-standard location
    ///    - Use custom configuration sources
    ///    - Override the default configuration behavior
    ///
    /// Example (custom configuration):
    /// <code>
    /// var customConfig = new ConfigurationBuilder()
    ///     .AddJsonFile("custom.json")
    ///     .AddEnvironmentVariables("MY_APP_")
    ///     .Build();
    ///
    /// var agent = new AgentBuilder(config)
    ///     .WithAPIConfiguration(customConfig)  // Override default
    ///     .Build();
    /// </code>
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="configuration">Configuration instance (e.g., from appsettings.json)</param>
    public static AgentBuilder WithAPIConfiguration(this AgentBuilder builder, IConfiguration configuration)
    {
        builder._configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        return builder;
    }

    /// <summary>
    /// Sets a custom configuration source from a specific JSON file.
    ///
    /// ⚠️ OPTIONAL: AgentBuilder automatically loads appsettings.json from the current directory.
    ///
    /// 💡 Only use this method if you need to load from a different file or location.
    ///
    /// Example:
    /// <code>
    /// var agent = new AgentBuilder(config)
    ///     .WithAPIConfiguration("config/production.json")
    ///     .Build();
    /// </code>
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="jsonFilePath">Path to the JSON configuration file (e.g., appsettings.json)</param>
    /// <param name="optional">Whether the file is optional (default: false)</param>
    /// <param name="reloadOnChange">Whether to reload configuration when file changes (default: true)</param>
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
}

#endregion

#region Provider Extensions

public static class AgentBuilderProviderExtensions
{
    public static AgentBuilder WithProvider(this AgentBuilder builder, string providerKey, string modelName, string? apiKey = null)
    {
        builder.Config.Provider = new ProviderConfig
        {
            ProviderKey = providerKey,
            ModelName = modelName,
            ApiKey = apiKey
        };
        return builder;
    }
}

#endregion


#region Provider

/// <summary>
/// Represents connection information parsed from a connection string.
/// Supports format: "Provider=openrouter;AccessKey=sk-xxx;Model=gpt-4;Endpoint=https://..."
/// </summary>
public class ProviderConnectionInfo
{
    /// <summary>
    /// The provider key (e.g., "openrouter", "openai", "azure", "ollama")
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// The API key or access token
    /// </summary>
    public string? AccessKey { get; init; }

    /// <summary>
    /// The model name (e.g., "gpt-4", "google/gemini-2.5-pro")
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The API endpoint (optional, uses provider default if not specified)
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Tries to parse a connection string into ProviderConnectionInfo
    /// </summary>
    /// <param name="connectionString">Connection string to parse</param>
    /// <param name="info">Parsed connection info if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParse(string? connectionString, [NotNullWhen(true)] out ProviderConnectionInfo? info)
    {
        info = null;

        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            // Use DbConnectionStringBuilder for robust parsing
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            // Provider is required
            if (!builder.ContainsKey("Provider"))
                return false;

            var provider = builder["Provider"].ToString();
            if (string.IsNullOrWhiteSpace(provider))
                return false;

            // Parse optional fields
            string? accessKey = builder.ContainsKey("AccessKey")
                ? builder["AccessKey"].ToString()
                : null;

            string? model = builder.ContainsKey("Model")
                ? builder["Model"].ToString()
                : null;

            Uri? endpoint = null;
            if (builder.ContainsKey("Endpoint"))
            {
                var endpointStr = builder["Endpoint"].ToString();
                if (!string.IsNullOrWhiteSpace(endpointStr))
                {
                    if (!Uri.TryCreate(endpointStr, UriKind.Absolute, out endpoint))
                        return false; // Invalid endpoint URL
                }
            }

            info = new ProviderConnectionInfo
            {
                Provider = provider,
                AccessKey = accessKey,
                Model = model,
                Endpoint = endpoint
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a connection string, throwing an exception if invalid
    /// </summary>
    /// <param name="connectionString">Connection string to parse</param>
    /// <returns>Parsed connection info</returns>
    /// <exception cref="ArgumentException">Thrown if connection string is invalid</exception>
    public static ProviderConnectionInfo Parse(string connectionString)
    {
        if (TryParse(connectionString, out var info))
            return info;

        throw new ArgumentException(
            $"Invalid connection string: '{connectionString}'. " +
            "Expected format: 'Provider=openrouter;AccessKey=sk-xxx;Model=gpt-4;Endpoint=https://...' " +
            "(AccessKey, Model, and Endpoint are optional)",
            nameof(connectionString));
    }
}

#endregion
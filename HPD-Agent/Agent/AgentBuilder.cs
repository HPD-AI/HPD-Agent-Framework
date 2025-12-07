using HPD.Agent.Providers;
using HPD.Agent.Middleware;
using HPD.Agent.Middleware.Function;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace HPD.Agent;

// NOTE: Project Middleware classes are defined in the global namespace with the Project class

/// <summary>
/// Dependencies needed for agent construction
/// </summary>
internal record AgentBuildDependencies(
    IChatClient ClientToUse,
    ChatOptions? MergedOptions,
    ErrorHandling.IProviderErrorHandler ErrorHandler,
    IChatClient? SummarizerClient = null);

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
    internal IPluginMetadataContext? _defaultContext;

    /// <summary>
    /// Instance-based registrations for DI-required plugins (e.g., AgentPlanPlugin, DynamicMemoryPlugin).
    /// These plugins cannot be instantiated via the catalog because they require constructor parameters.
    /// </summary>
    public readonly List<PluginInstanceRegistration> _instanceRegistrations = new();
    // store individual plugin contexts
    internal readonly Dictionary<string, IPluginMetadataContext?> _pluginContexts = new();
    // Phase 5: Document store for skill instruction documents
    internal HPD.Agent.Skills.DocumentStore.IInstructionDocumentStore? _documentStore;
    // Track explicitly registered plugins (for scoping manager)
    internal readonly HashSet<string> _explicitlyRegisteredPlugins = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<Middleware.IAgentMiddleware> _middlewares = new(); // Unified middleware list
    internal readonly HPD.Agent.Permissions.PermissionOverrideRegistry _permissionOverrides = new(); // Permission overrides

    // Function scope tracking for middleware scoping
    internal readonly Dictionary<string, string> _functionToPluginMap = new(); // functionName -> pluginTypeName
    internal readonly Dictionary<string, string> _functionToSkillMap = new(); // functionName -> skillName

    // Internal observers for agent-level observability (developer-only, hidden from users)
    private readonly List<IAgentEventObserver> _observers = new();

    // Event handlers for synchronous, ordered event processing (UI, console, web streams)
    private readonly List<IAgentEventHandler> _eventHandlers = new();

    internal readonly Dictionary<Type, object> _providerConfigs = new();
    internal IServiceProvider? _serviceProvider;
    internal ILoggerFactory? _logger;

    // MCP runtime fields (stored as object to avoid circular reference to HPD-Agent.MCP)
    internal object? _mcpClientManager;

    // AIContextProvider factory (protocol-specific, stored as object for extensibility)
    internal object? _contextProviderFactory;

    // Text extraction utility for document processing (shared instance)
    public HPD.Agent.TextExtraction.TextExtractionUtility? _textExtractor;

    //     
    // AOT-COMPATIBLE PLUGIN REGISTRY (Phase: AOT Plugin Registry Hybrid)
    //     
    // These fields enable reflection-free plugin instantiation in hot paths.
    // The source generator creates a PluginRegistry.All array with direct delegates.

    /// <summary>
    /// Plugin catalog loaded from generated PluginRegistry.All.
    /// Starts with the calling assembly's registry and lazily loads additional assemblies
    /// when plugins from other assemblies are requested via WithPlugin&lt;T&gt;().
    /// </summary>
    internal readonly Dictionary<string, PluginFactory> _availablePlugins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks which assemblies have already been scanned for plugin registries.
    /// Used to avoid repeated reflection calls for the same assembly.
    /// </summary>
    internal readonly HashSet<Assembly> _loadedAssemblies = new();

    /// <summary>
    /// Selected plugins for this agent (from WithPlugin calls).
    /// Only plugins in this list will have their functions created during Build().
    /// </summary>
    internal readonly List<PluginFactory> _selectedPluginFactories = new();

    /// <summary>
    /// Function filters for Phase 4.5 selective registration.
    /// Maps plugin name -> array of function names to include.
    /// When a plugin is auto-registered as a skill dependency, only these functions are included.
    /// </summary>
    internal readonly Dictionary<string, string[]> _pluginFunctionFilters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new builder with default configuration.
    /// Provider assemblies are automatically discovered via ProviderAutoDiscovery ModuleInitializer.
    /// </summary>
#pragma warning disable IL2026
    public AgentBuilder()
    {
        // Capture calling assembly FIRST, before any method calls
        // GetCallingAssembly() returns the immediate caller, not transitive
        var callingAssembly = Assembly.GetCallingAssembly();

        _config = new AgentConfig();
        _providerRegistry = new ProviderRegistry();
        LoadPluginRegistryFromAssembly(callingAssembly);
        RegisterDiscoveredProviders();
    }
#pragma warning restore IL2026

    /// <summary>
    /// Creates a builder from existing configuration.
    /// Provider assemblies are automatically discovered via ProviderAutoDiscovery ModuleInitializer.
    /// </summary>
#pragma warning disable IL2026
    public AgentBuilder(AgentConfig config)
    {
        // Capture calling assembly FIRST, before any method calls
        var callingAssembly = Assembly.GetCallingAssembly();

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _providerRegistry = new ProviderRegistry();
        LoadPluginRegistryFromAssembly(callingAssembly);
        RegisterDiscoveredProviders();
    }
#pragma warning restore IL2026

    /// <summary>
    /// Creates a builder with custom provider registry (for testing).
    /// Optionally accepts an assembly hint for plugin registry discovery.
    /// </summary>
#pragma warning disable IL2026
    public AgentBuilder(AgentConfig config, IProviderRegistry providerRegistry)
    {
        // Capture calling assembly FIRST
        var callingAssembly = Assembly.GetCallingAssembly();

        _config = config;
        _providerRegistry = providerRegistry;
        LoadPluginRegistryFromAssembly(callingAssembly);
    }
#pragma warning restore IL2026

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
    /// Loads plugins from the generated PluginRegistry.All in the specified assembly
    /// and merges them into the _availablePlugins dictionary.
    /// Uses minimal reflection (one GetType call per assembly) to discover the catalog.
    /// Thread-safe: tracks loaded assemblies to avoid duplicate processing.
    /// WARNING: Requires source-generated PluginRegistry type to be preserved in AOT.
    /// </summary>
    /// <param name="assembly">The assembly to search for the generated PluginRegistry</param>
    [RequiresUnreferencedCode("Plugin registry lookup via Assembly.GetType requires PluginRegistry type to be preserved during AOT compilation.")]
    internal void LoadPluginRegistryFromAssembly(Assembly assembly)
    {
        // Skip if already loaded
        if (!_loadedAssemblies.Add(assembly))
        {
            return;
        }

        try
        {
            // ONE reflection call: Look for generated registry in the specified assembly
            // This type name is a constant known at compile time, making it AOT-safe
            var registryType = assembly.GetType("HPD.Agent.Generated.PluginRegistry");

            if (registryType == null)
            {
                // No registry found - no plugins available from this assembly
                return;
            }

            // Get the All field (static readonly array - NOT a property!)
            var allField = registryType.GetField("All", BindingFlags.Public | BindingFlags.Static);
            if (allField == null)
            {
                return;
            }

            // Get the PluginFactory array
            var factories = allField.GetValue(null) as PluginFactory[];
            if (factories == null || factories.Length == 0)
            {
                return;
            }

            // Add to dictionary (new plugins from this assembly)
            foreach (var factory in factories)
            {
                // Use TryAdd to avoid overwriting if plugin with same name already exists
                _availablePlugins.TryAdd(factory.Name, factory);
            }
        }
        catch (Exception ex)
        {
            // Log warning but don't crash - no plugins from this assembly
            _logger?.CreateLogger<AgentBuilder>()
                .LogWarning(ex, "Failed to load PluginRegistry.All from assembly {Assembly}", assembly.FullName);
        }
    }

    /// <summary>
    /// Creates AIFunctions from selected plugins using the catalog (zero reflection in hot path).
    /// Also handles instance-based registrations for DI plugins.
    /// Phase 4.5: Applies function filters for selective registration.
    /// </summary>
    /// <returns>List of AIFunctions from all selected plugins</returns>
    [RequiresUnreferencedCode("Instance-based registrations for DI plugins use reflection.")]
    private List<AIFunction> CreateFunctionsFromCatalog()
    {
        var allFunctions = new List<AIFunction>();

        // Process catalog-based plugins (zero reflection in hot path)
        foreach (var factory in _selectedPluginFactories)
        {
            try
            {
                _pluginContexts.TryGetValue(factory.Name, out var ctx);

                // Create plugin instance (ZERO REFLECTION - direct delegate call!)
                var instance = factory.CreateInstance();

                // Call CreateFunctions delegate (ZERO REFLECTION!)
                var functions = factory.CreateFunctions(instance, ctx ?? _defaultContext);

                // Phase 4.5: Apply function filter if this plugin has selective registration
                if (_pluginFunctionFilters.TryGetValue(factory.Name, out var functionFilter))
                {
                    // Only include functions that are in the filter
                    functions = functions
                        .Where(f => functionFilter.Contains(f.Name))
                        .ToList();
                }

                allFunctions.AddRange(functions);
            }
            catch (Exception ex)
            {
                _logger?.CreateLogger<AgentBuilder>()
                    .LogWarning(ex, "Failed to create functions for plugin {PluginName}", factory.Name);
            }
        }

        // Process instance-based registrations (for DI plugins like AgentPlanPlugin, DynamicMemoryPlugin)
        foreach (var registration in _instanceRegistrations)
        {
            try
            {
                _pluginContexts.TryGetValue(registration.PluginTypeName, out var ctx);
                var functions = CreateFunctionsFromInstance(registration, ctx ?? _defaultContext);

                // Apply function filter if set
                if (registration.FunctionFilter != null && registration.FunctionFilter.Length > 0)
                {
                    functions = functions
                        .Where(f => registration.FunctionFilter.Contains(f.Name))
                        .ToList();
                }

                allFunctions.AddRange(functions);
            }
            catch (Exception ex)
            {
                _logger?.CreateLogger<AgentBuilder>()
                    .LogWarning(ex, "Failed to create functions for instance plugin {PluginName}", registration.PluginTypeName);
            }
        }

        return allFunctions;
    }

    /// <summary>
    /// Creates AIFunctions from an instance-based plugin registration.
    /// Uses the generated Registration class to create functions from the provided instance.
    /// This is used for DI plugins that cannot be instantiated via the catalog.
    /// </summary>
    [RequiresUnreferencedCode("Uses reflection to call generated Registration class for DI plugins.")]
    private List<AIFunction> CreateFunctionsFromInstance(PluginInstanceRegistration registration, IPluginMetadataContext? context)
    {
        var instance = registration.Instance;
        var instanceType = instance.GetType();

        // Look up the generated Registration class
        var registrationTypeName = $"{registration.PluginTypeName}Registration";
        var registrationType = instanceType.Assembly.GetType(registrationTypeName);

        if (registrationType == null)
        {
            throw new InvalidOperationException(
                $"Generated registration class {registrationTypeName} not found for DI plugin. " +
                $"Ensure the plugin has [Function] or [Skill] attributes and the source generator ran successfully.");
        }

        var createPluginMethod = registrationType.GetMethod("CreatePlugin", BindingFlags.Public | BindingFlags.Static);
        if (createPluginMethod == null)
        {
            throw new InvalidOperationException(
                $"CreatePlugin method not found in {registrationTypeName}.");
        }

        // Invoke CreatePlugin with the instance
        var parameters = createPluginMethod.GetParameters();
        object? result;

        if (parameters.Length == 1)
        {
            // Skill-only container: CreatePlugin(IPluginMetadataContext? context)
            result = createPluginMethod.Invoke(null, new object?[] { context });
        }
        else
        {
            // Regular plugin: CreatePlugin(TPlugin instance, IPluginMetadataContext? context)
            result = createPluginMethod.Invoke(null, new object?[] { instance, context });
        }

        return result as List<AIFunction> ?? new List<AIFunction>();
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
        // Capture calling assembly FIRST, before any method calls
        var callingAssembly = Assembly.GetCallingAssembly();

        if (string.IsNullOrWhiteSpace(jsonFilePath))
            throw new ArgumentException("JSON file path cannot be null or empty.", nameof(jsonFilePath));

        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException($"Configuration file not found: {jsonFilePath}");

        _providerRegistry = new ProviderRegistry();
        LoadPluginRegistryFromAssembly(callingAssembly);

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
    /// Phase 5: Required for skills that use AddDocument(), AddDocumentFromFile(), or AddDocumentFromUrl().
    /// Documents are uploaded and validated during Build().
    /// </summary>
    /// <param name="documentStore">Document store implementation (InMemoryInstructionStore, FileSystemInstructionStore, etc.)</param>
    public AgentBuilder WithDocumentStore(HPD.Agent.Skills.DocumentStore.IInstructionDocumentStore documentStore)
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

    //   
    // DURABLE EXECUTION (Crash Recovery)
    //   

    /// <summary>
    /// Enables durable execution with automatic checkpointing for crash recovery.
    /// The agent will automatically save state during execution, allowing recovery after failures.
    /// </summary>
    /// <param name="store">The checkpoint store for persisting state</param>
    /// <param name="frequency">How often to checkpoint (default: PerTurn - after each message turn)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// <b>What is Durable Execution?</b>
    /// Durable execution ensures that long-running agent operations can survive process crashes,
    /// network failures, or restarts. The agent automatically saves checkpoints during execution
    /// and can resume from the last checkpoint after a failure.
    /// </para>
    /// <para>
    /// <b>Checkpoint Frequency Options:</b>
    /// <list type="bullet">
    /// <item><description><c>PerTurn</c> (default): Save after each complete message turn. Good balance of durability and performance.</description></item>
    /// <item><description><c>PerIteration</c>: Save after each tool call cycle. Maximum durability but higher overhead.</description></item>
    /// <item><description><c>Never</c>: No automatic checkpoints. Use for explicit/manual checkpointing only.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Note:</b> For explicit checkpointing without automatic saves (e.g., for branching/message editing),
    /// use <see cref="Checkpointing.BranchingConversation"/> instead. It provides a clean API for
    /// fork-based conversation branching without enabling automatic durable execution.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Enable durable execution with file-based storage
    /// var store = new JsonConversationThreadStore("./checkpoints", CheckpointRetentionMode.LatestOnly);
    /// var agent = new AgentBuilder()
    ///     .WithProvider("openai", "gpt-4")
    ///     .WithDurableExecution(store)
    ///     .Build();
    ///
    /// // Resume after crash
    /// var thread = await store.LoadThreadAsync(threadId);
    /// if (thread?.ExecutionState != null)
    /// {
    ///     // Resume from checkpoint
    ///     await agent.RunAsync(Array.Empty&lt;ChatMessage&gt;(), thread: thread);
    /// }
    /// </code>
    /// </example>
    public AgentBuilder WithDurableExecution(
        Checkpointing.ICheckpointStore store,
        Checkpointing.CheckpointFrequency frequency = Checkpointing.CheckpointFrequency.PerTurn)
    {
        ArgumentNullException.ThrowIfNull(store);
        _config.ThreadStore = store;
        _config.DurableExecutionConfig = new Checkpointing.Services.DurableExecutionConfig
        {
            Enabled = true,
            Frequency = frequency,
            Retention = Checkpointing.Services.RetentionPolicy.LatestOnly
        };
        return this;
    }

    /// <summary>
    /// Enables durable execution with a simple file-based store.
    /// Convenience overload that creates a JsonConversationThreadStore internally.
    /// </summary>
    /// <param name="storagePath">Directory to store checkpoint files</param>
    /// <param name="frequency">How often to checkpoint (default: PerTurn)</param>
    /// <returns>The builder for chaining</returns>
    public AgentBuilder WithDurableExecution(
        string storagePath,
        Checkpointing.CheckpointFrequency frequency = Checkpointing.CheckpointFrequency.PerTurn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        var store = new Checkpointing.JsonConversationThreadStore(storagePath);
        return WithDurableExecution(store, frequency);
    }

    //   
    // PROTOCOL-SPECIFIC CONFIGURATION
    //   
    // Protocol-specific configuration methods (WithContextProviderFactory, etc.) are now
    // provided via extension methods in protocol adapter projects (HPD-Agent.Microsoft, etc.)

    /// <summary>
    /// Internal method to set protocol-specific context provider factory.
    /// Used by protocol adapter extension methods (e.g., HPD.Agent.Microsoft.AgentBuilderExtensions).
    /// </summary>
    internal void SetContextProviderFactory(object factory)
    {
        _contextProviderFactory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Internal method to get protocol-specific context provider factory.
    /// Used by protocol adapter extension methods to retrieve the stored factory.
    /// </summary>
    internal object? GetContextProviderFactory() => _contextProviderFactory;

    //   
    // DUAL-LAYER OBSERVABILITY ARCHITECTURE
    //   
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
    //   

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

        // 1. Register Microsoft's OpenTelemetryChatClient middleware (user-facing LLM observability)
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

        // 2. Internally create TelemetryEventObserver for agent-level observability (developer-only)
        // This tracks agent decisions, iterations, circuit breakers, etc.
        var telemetryObserver = new TelemetryEventObserver(effectiveSourceName);
        _observers.Add(telemetryObserver);

        return this;
    }

    /// <summary>
    /// Enables comprehensive structured logging for observability:
    /// <list type="bullet">
    /// <item><description><b>LLM-level (Microsoft):</b> LLM invocation logging (requests/responses/errors)</description></item>
    /// <item><description><b>Agent-level (HPD):</b> Decision logging, state snapshots, circuit breaker warnings</description></item>
    /// <item><description><b>Unified Middleware:</b> Configurable logging at message turn, iteration, and function levels</description></item>
    /// </list>
    /// </summary>
    /// <param name="enableSensitiveData">Include prompts/responses at Trace level (default: false)</param>
    /// <param name="options">Optional logging middleware options. If null, uses default options (message turn + function logging).</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This method automatically registers multiple layers of logging:
    /// <list type="number">
    /// <item><description>Microsoft's <c>LoggingChatClient</c> middleware for LLM invocation logging</description></item>
    /// <item><description>HPD's <c>LoggingEventObserver</c> for agent orchestration logging</description></item>
    /// <item><description>Unified <c>LoggingMiddleware</c> for configurable agent lifecycle logging</description></item>
    /// </list>
    /// </para>
    /// <para><b>Requirements:</b> Call <c>WithServiceProvider()</c> with an <c>ILoggerFactory</c> registered.</para>
    /// <para><b>Log Levels:</b></para>
    /// <list type="bullet">
    /// <item><description><c>Debug</c> - LLM invocations, agent decisions, completions</description></item>
    /// <item><description><c>Information</c> - Agent completion summaries, middleware logging</description></item>
    /// <item><description><c>Warning</c> - Circuit breaker triggers, missing dependencies</description></item>
    /// <item><description><c>Trace</c> - Full message/response content (sensitive data)</description></item>
    /// <item><description><c>Error</c> - LLM errors, agent errors</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Default logging (message turns + functions)
    /// var agent = new AgentBuilder()
    ///     .WithServiceProvider(services)
    ///     .WithLogging()
    ///     .WithOpenAI(apiKey, "gpt-4")
    ///     .Build();
    ///
    /// // Minimal logging (just function names with timing)
    /// var agent = new AgentBuilder()
    ///     .WithLogging(options: LoggingMiddlewareOptions.Minimal)
    ///     .Build();
    ///
    /// // Verbose logging (everything)
    /// var agent = new AgentBuilder()
    ///     .WithLogging(options: LoggingMiddlewareOptions.Verbose)
    ///     .Build();
    ///
    /// // Custom configuration
    /// var agent = new AgentBuilder()
    ///     .WithLogging(options: new LoggingMiddlewareOptions
    ///     {
    ///         LogFunction = true,
    ///         LogIteration = true,
    ///         IncludeArguments = false,
    ///         MaxStringLength = 500
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public AgentBuilder WithLogging(
        bool enableSensitiveData = false,
        LoggingMiddlewareOptions? options = null)
    {
        // 1. Register Microsoft's LoggingChatClient middleware (user-facing LLM observability)
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

        // 2. Internally create LoggingEventObserver for agent-level observability (developer-only)
        // This tracks agent decisions, state, circuit breakers, etc.
        if (_logger != null)
        {
            var loggingObserver = new LoggingEventObserver(
                _logger.CreateLogger<LoggingEventObserver>(),
                enableSensitiveData);
            _observers.Add(loggingObserver);
        }

        // 3. Add unified LoggingMiddleware with configurable options
        var loggingOptions = options ?? LoggingMiddlewareOptions.Default;
        var loggingMiddleware = new LoggingMiddleware(_logger, loggingOptions);
        _middlewares.Add(loggingMiddleware);

        return this;
    }

    /// <summary>
    /// Enables logging with an explicit logger factory.
    /// Use this when you want to configure logging without using dependency injection.
    /// </summary>
    /// <param name="loggerFactory">The logger factory to use for all logging.</param>
    /// <param name="options">Optional logging middleware options. If null, uses default options.</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    /// var agent = new AgentBuilder()
    ///     .WithLogging(loggerFactory)
    ///     .WithProvider("openai", "gpt-4", apiKey)
    ///     .Build();
    /// </code>
    /// </example>
    public AgentBuilder WithLogging(
        ILoggerFactory loggerFactory,
        LoggingMiddlewareOptions? options = null)
    {
        _logger = loggerFactory;
        return WithLogging(enableSensitiveData: false, options: options);
    }

    /// <summary>
    /// Registers a custom event observer to handle agent events.
    /// Observers receive all internal agent events (AgentEvent) and can process them asynchronously.
    /// Use this to implement custom event handling, logging, metrics collection, or UI updates.
    /// </summary>
    /// <param name="observer">The observer to register. Can use <see cref="IEventHandler"/> or <see cref="IAgentEventObserver"/> - both are equivalent.</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// <b>Observer Pattern Benefits:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Reusable event handling logic across multiple agents</description></item>
    /// <item><description>Fire-and-forget async processing (doesn't block agent execution)</description></item>
    /// <item><description>Automatic circuit breaker protection (failing observers auto-disabled after 10 failures)</description></item>
    /// <item><description>Selective filtering via <c>ShouldProcess()</c> method</description></item>
    /// </list>
    /// <para>
    /// <b>Example - Custom Event Handler:</b>
    /// <code>
    /// public class MyEventHandler : IEventHandler
    /// {
    ///     public bool ShouldProcess(AgentEvent evt)
    ///     {
    ///         // Filter events you care about
    ///         return evt is PermissionRequestEvent or TextDeltaEvent;
    ///     }
    ///
    ///     public async Task OnEventAsync(AgentEvent evt, CancellationToken ct)
    ///     {
    ///         switch (evt)
    ///         {
    ///             case PermissionRequestEvent permReq:
    ///                 await HandlePermissionAsync(permReq, ct);
    ///                 break;
    ///             case TextDeltaEvent textDelta:
    ///                 Console.Write(textDelta.Text);
    ///                 break;
    ///         }
    ///     }
    /// }
    ///
    /// // Register with agent
    /// var agent = new AgentBuilder(config)
    ///     .WithObserver(new MyEventHandler())
    ///     .Build();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Multiple Observers:</b> Call <c>WithObserver()</c> multiple times to register multiple observers.
    /// All observers run in parallel via fire-and-forget pattern.
    /// </para>
    /// <para>
    /// <b>Built-in Observers:</b> The framework automatically registers:
    /// - <c>LoggingEventObserver</c> (when you call <c>WithLogging()</c>)
    /// - <c>TelemetryEventObserver</c> (when you call <c>WithTelemetry()</c>)
    /// </para>
    /// </remarks>
    public AgentBuilder WithObserver(IAgentEventObserver observer)
    {
        if (observer == null)
            throw new ArgumentNullException(nameof(observer));

        _observers.Add(observer);
        return this;
    }

    /// <summary>
    /// Registers an event handler for synchronous, ordered event processing.
    /// Use this for UI handlers (console, web streams) that need guaranteed ordering.
    /// </summary>
    /// <param name="handler">The event handler to register</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="WithObserver"/>, event handlers are awaited synchronously
    /// in the event loop, guaranteeing events are processed in order.
    /// </para>
    /// <para>
    /// <b>Use WithEventHandler for:</b>
    /// <list type="bullet">
    /// <item>Console output that needs correct ordering</item>
    /// <item>Web UI streaming (SSE, WebSockets)</item>
    /// <item>Permission prompts that need user interaction</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Use WithObserver for:</b>
    /// <list type="bullet">
    /// <item>Telemetry and metrics (ordering doesn't matter)</item>
    /// <item>Background logging</item>
    /// <item>Analytics</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder(config)
    ///     .WithEventHandler(new ConsoleEventHandler())  // Synchronous, ordered
    ///     .WithObserver(new TelemetryObserver())        // Fire-and-forget
    ///     .Build();
    /// </code>
    /// </example>
    public AgentBuilder WithEventHandler(IAgentEventHandler handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        _eventHandlers.Add(handler);
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
    /// Sets an existing chat client to use instead of creating one from a provider.
    /// This is useful for SubAgents that want to inherit the parent's chat client.
    /// </summary>
    /// <param name="client">The chat client to use</param>
    /// <returns>The builder for method chaining</returns>
    /// <remarks>
    /// When this is set, the agent will use this client instead of creating a new one
    /// from the Provider configuration. The Provider configuration will still be validated
    /// but won't be used to create a client.
    /// </remarks>
    public AgentBuilder WithChatClient(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _baseClient = client;
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
    /// Builds the protocol-agnostic core agent asynchronously.
    /// Internal method - use protocol-specific Build methods (e.g., BuildMicrosoftAgent()).
    /// Validation behavior is controlled by the ValidationConfig (see WithValidation()).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "RequiresUnreferencedCode declared on method")]
    /// <summary>
    /// Builds the protocol-agnostic core agent asynchronously.
    /// Required for provider validation (LLM connectivity checks) during initialization.
    /// </summary>
    public async Task<Agent> Build(CancellationToken cancellationToken = default)
    {
        var buildData = await BuildDependenciesAsync(cancellationToken).ConfigureAwait(false);
        RegisterAutoMiddleware(buildData);
        return CreateAgent(buildData);
    }

    /// <summary>
    /// Registers all auto-middleware (error handling, history reduction, tool scoping, etc).
    /// Called by both sync and async build paths to eliminate code duplication.
    /// </summary>
    private void RegisterAutoMiddleware(AgentBuildDependencies buildData)
    {
        // Set explicitly registered plugins in config for scoping manager
        _config.ExplicitlyRegisteredPlugins = _explicitlyRegisteredPlugins
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        // Set global config for source-generated code to access (sync path sets this, harmless when called from async)
        AgentConfig.GlobalConfig = _config;

        // Register ContinuationPermissionMiddleware if enabled
        // This requests user permission when iteration limit is reached
        // Only register if we have a reasonable iteration limit set
        if (_config!.MaxAgenticIterations > 0 && _config.MaxAgenticIterations < 1000)
        {
            _middlewares.Add(new ContinuationPermissionMiddleware(
                maxIterations: _config.MaxAgenticIterations,
                extensionAmount: _config.ContinuationExtensionAmount));
        }

        // Register HistoryReductionMiddleware if enabled
        // This reduces conversation history to manage context window size
        if (_config.HistoryReduction?.Enabled == true)
        {
            var chatReducer = CreateChatReducer(buildData.ClientToUse, _config, buildData.SummarizerClient);
            _middlewares.Add(new HistoryReductionMiddleware
            {
                ChatReducer = chatReducer,
                Config = _config.HistoryReduction,
                SystemInstructions = _config.SystemInstructions
            });
        }

        //     
        // AUTO-REGISTER FUNCTION-LEVEL MIDDLEWARE
        //     
        // These are registered in execution order (first = outermost):
        // - FunctionRetryMiddleware wraps timeout (retry the entire timeout operation)
        // - FunctionTimeoutMiddleware wraps execution (timeout individual attempts)

        // Register FunctionRetryMiddleware if retry is enabled
        if (_config.ErrorHandling?.MaxRetries > 0)
        {
            _middlewares.Add(new Middleware.Function.FunctionRetryMiddleware(_config.ErrorHandling));
        }

        // Register FunctionTimeoutMiddleware if timeout is configured
        if (_config.ErrorHandling?.SingleFunctionTimeout != null)
        {
            _middlewares.Add(new Middleware.Function.FunctionTimeoutMiddleware(_config.ErrorHandling.SingleFunctionTimeout.Value));
        }

        // Register ToolScopingMiddleware if enabled
        // This middleware owns the ToolVisibilityManager and handles all tool scoping logic
        if (_config.Scoping?.Enabled == true && buildData.MergedOptions?.Tools != null)
        {
            var scopingMiddleware = new ToolScopingMiddleware(
                buildData.MergedOptions.Tools,
                _explicitlyRegisteredPlugins.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                _config.Scoping);
            _middlewares.Add(scopingMiddleware);
        }

        // Register FrontendToolMiddleware automatically
        // This enables frontend-defined tools without explicit configuration.
        // It's a no-op if no frontend plugins are registered via AgentRunInput.
        // Users can override with WithFrontendTools() to customize config.
        if (!_middlewares.Any(m => m is FrontendTools.FrontendToolMiddleware))
        {
            _middlewares.Add(new FrontendTools.FrontendToolMiddleware());
        }
    }

    /// <summary>
    /// Creates the final Agent instance with all registered middleware and configuration.
    /// Shared by both sync and async build paths to eliminate code duplication.
    /// </summary>
    private Agent CreateAgent(AgentBuildDependencies buildData)
    {
        return new Agent(
            _config!,
            buildData.ClientToUse,
            buildData.MergedOptions,
            buildData.ErrorHandler,
            _functionToPluginMap,
            _functionToSkillMap,
            _middlewares,
            _serviceProvider,
            _observers,
            _eventHandlers);
    }

    /// <summary>
    /// Invokes MCP methods via reflection. This is only called from RequiresUnreferencedCode context,
    /// so the trimmer knows MCPClientManager types must be preserved.
    /// </summary>
#pragma warning disable IL2075
    private async Task<List<AIFunction>> InvokeMCPMethodAsync(
        object mcpManager,
        string methodName,
        string manifestPath,
        int maxFunctionNames,
        CancellationToken cancellationToken)
    {
        var managerType = mcpManager.GetType();
        var method = managerType.GetMethod(
            methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(string), typeof(bool), typeof(int), typeof(System.Threading.CancellationToken) },
            null);

        if (method == null)
            throw new InvalidOperationException($"MCPClientManager does not have {methodName} method");

        var task = method.Invoke(mcpManager, new object[] { manifestPath, false, maxFunctionNames, cancellationToken })
            as System.Threading.Tasks.Task<List<AIFunction>>;

        if (task == null)
            throw new InvalidOperationException($"MCP method {methodName} returned null or wrong type");

        return await task;
    }
#pragma warning restore IL2075

    /// <summary>
    /// Builds all dependencies needed for agent construction.
    /// WARNING: This method uses reflection for MCP tool loading. It requires HPD.Agent.MCP assembly
    /// and its public types to be preserved during AOT compilation. For Native AOT deployment,
    /// ensure MCPClientManager type is explicitly preserved in your rd.xml or RootDescriptor.
    /// </summary>
    [RequiresUnreferencedCode("MCP tool loading via reflection. Requires HPD.Agent.MCP types preserved.")]
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
            var pluginName = "DocumentRetrievalPlugin";
            if (_availablePlugins.TryGetValue(pluginName, out var factory) &&
                !_selectedPluginFactories.Any(f => f.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedPluginFactories.Add(factory);
            }
        }

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

        // ✨ PRIORITY 1: Try to resolve from Providers section (recommended pattern)
        if (_configuration != null && !string.IsNullOrEmpty(_config.Provider.ProviderKey))
        {
            var providerName = _config.Provider.ProviderKey;
            var providerSection = _configuration.GetSection($"Providers:{providerName}")
                               ?? _configuration.GetSection($"Providers:{Capitalize(providerName)}");

            if (providerSection.Exists())
            {
                // Apply provider section values (only if not already set in code)
                var sectionProviderKey = providerSection["ProviderKey"];
                if (!string.IsNullOrEmpty(sectionProviderKey) && string.IsNullOrEmpty(_config.Provider.ProviderKey))
                    _config.Provider.ProviderKey = sectionProviderKey;

                var sectionApiKey = providerSection["ApiKey"];
                if (!string.IsNullOrEmpty(sectionApiKey) && string.IsNullOrEmpty(_config.Provider.ApiKey))
                    _config.Provider.ApiKey = sectionApiKey;

                var sectionModelName = providerSection["ModelName"];
                if (!string.IsNullOrEmpty(sectionModelName) && string.IsNullOrEmpty(_config.Provider.ModelName))
                    _config.Provider.ModelName = sectionModelName;

                var sectionEndpoint = providerSection["Endpoint"];
                if (!string.IsNullOrEmpty(sectionEndpoint) && string.IsNullOrEmpty(_config.Provider.Endpoint))
                    _config.Provider.Endpoint = sectionEndpoint;
            }
        }

        // ✨ PRIORITY 2: Try to resolve from connection string (backward compatibility)
        if (_configuration != null)
        {
            // Try standard ConnectionStrings section
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

        // ✨ PRIORITY 3: Resolve from individual configuration keys (backward compatibility)
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
                    $"1️⃣  PROVIDERS SECTION (recommended for multiple providers):\n" +
                    $"   appsettings.json → \"Providers\": {{\n" +
                    $"     \"{providerCapitalized}\": {{\n" +
                    $"       \"ProviderKey\": \"{providerKey}\",\n" +
                    $"       \"ModelName\": \"your-model\",\n" +
                    $"       \"ApiKey\": \"your-api-key\"\n" +
                    $"     }}\n" +
                    $"   }}\n\n" +
                    $"2️⃣  CONNECTION STRING (legacy, single provider):\n" +
                    $"   appsettings.json → \"ConnectionStrings\": {{\n" +
                    $"     \"Agent\": \"Provider={providerKey};AccessKey=your-api-key;Model=your-model\"\n" +
                    $"   }}\n\n" +
                    $"3️⃣  ENVIRONMENT VARIABLE:\n" +
                    $"   {providerUpper}_API_KEY=your-api-key\n\n" +
                    $"4️⃣  USER SECRETS (development only):\n" +
                    $"   dotnet user-secrets set \"Providers:{providerCapitalized}:ApiKey\" \"your-api-key\"\n\n" +
                    $"5️⃣  CODE (for testing only, not recommended):\n" +
                    $"   Provider = new ProviderConfig {{ ApiKey = \"your-api-key\", ... }}";
            }            throw new InvalidOperationException(errorMessage);
        }

        // Create chat client and error handler via provider factories
        // Skip client creation if WithChatClient() was used (e.g., SubAgent inheriting parent's client)
        if (_baseClient == null)
        {
            _baseClient = providerFeatures.CreateChatClient(_config.Provider, _serviceProvider);

            if (_baseClient == null)
                throw new InvalidOperationException($"The factory for provider '{providerKey}' returned a null chat client.");
        }

        var errorHandler = providerFeatures.CreateErrorHandler();
        if (errorHandler == null)
            throw new InvalidOperationException($"The factory for provider '{providerKey}' returned a null error handler.");


        // Use base client directly (no middleware pipeline)
        // Observability (telemetry, logging, caching) is integrated directly into Agent.cs
        // See: Proposals/Urgent/MIDDLEWARE_DIRECT_INTEGRATION.md
        var clientToUse = _baseClient;

        // Dynamic Memory registration is handled by WithDynamicMemory() extension method
        // No need to register here in Build() - the extension already adds Middleware and plugin

        //     
        // CREATE PLUGIN FUNCTIONS (AOT-Compatible - Zero Reflection in Hot Path)
        //     
        // All plugins are registered via the catalog (PluginRegistry.All) using direct delegate calls.
        // Instance-based plugins (requiring DI) use their own direct delegate calls.
        // No reflection fallback - the catalog is required.

        var pluginFunctions = CreateFunctionsFromCatalog();

        // Middleware out container functions if scoping is disabled
        // Container functions are only needed when scoping is enabled for the two-turn expansion flow
        if (_config.Scoping?.Enabled != true)
        {
            pluginFunctions = pluginFunctions.Where(f =>
                !(f.AdditionalProperties?.TryGetValue("IsContainer", out var isContainer) == true &&
                  isContainer is bool isCont && isCont)
            ).ToList();
        }

        // Load MCP tools if configured
        if (McpClientManager != null)
        {
            try
            {
                List<AIFunction> mcpTools;
                if (_config.Mcp != null && !string.IsNullOrEmpty(_config.Mcp.ManifestPath))
                {
                    // Get max function names configuration (no longer using global MCP scoping - that's per-server now)
                    var maxFunctionNames = _config.Scoping?.MaxFunctionNamesInDescription ?? 10;

                    // Check if this is actually content vs path based on if it starts with '{'
                    if (_config.Mcp.ManifestPath.TrimStart().StartsWith("{"))
                    {
                        // AOT-safe reflection-based method invocation (no dynamic required)
                        mcpTools = await InvokeMCPMethodAsync(
                            McpClientManager,
                            "LoadToolsFromManifestContentAsync",
                            _config.Mcp.ManifestPath,
                            maxFunctionNames,
                            cancellationToken);
                    }
                    else
                    {
                        // AOT-safe reflection-based method invocation (no dynamic required)
                        mcpTools = await InvokeMCPMethodAsync(
                            McpClientManager,
                            "LoadToolsFromManifestAsync",
                            _config.Mcp.ManifestPath,
                            maxFunctionNames,
                            cancellationToken);
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

        // Create custom summarizer client if configured
        IChatClient? summarizerClient = null;
        if (_config.HistoryReduction?.SummarizerProvider != null)
        {
            var summarizerProviderKey = _config.HistoryReduction.SummarizerProvider.ProviderKey;
            var summarizerProviderFeatures = _providerRegistry.GetProvider(summarizerProviderKey);

            if (summarizerProviderFeatures == null)
            {
                var availableProviders = string.Join(", ", _providerRegistry.GetRegisteredProviders());
                throw new InvalidOperationException(
                    $"Unknown provider for summarization: '{summarizerProviderKey}'. " +
                    $"Available providers: [{availableProviders}]");
            }

            summarizerClient = summarizerProviderFeatures.CreateChatClient(
                _config.HistoryReduction.SummarizerProvider,
                _serviceProvider);
        }

        // Return dependencies instead of creating agent
        return new AgentBuildDependencies(
            clientToUse,
            mergedOptions,
            errorHandler,
            summarizerClient);
    }

    /// <summary>
    /// Phase 5: Process skill documents - upload files and validate references.
    /// Extracts document metadata from skill containers and processes them.
    /// If no document store is configured, creates a default FileSystemInstructionStore.
    /// </summary>
    [RequiresUnreferencedCode("Skill document processing uses reflection-based plugin catalog.")]
    private async Task ProcessSkillDocumentsAsync(CancellationToken cancellationToken)
    {
        var logger = _logger?.CreateLogger<AgentBuilder>();

        Console.WriteLine("[ProcessSkillDocuments] Starting document processing...");
        logger?.LogInformation("Starting skill document processing");

        // ========== PHASE 1: Collect skill containers ==========
        // Use the catalog-based function creation (same as Build() uses)
        var skillContainers = new List<AIFunction>();
        var allFunctions = CreateFunctionsFromCatalog();

        // Filter to only skill containers
        foreach (var function in allFunctions)
        {
            if (function.AdditionalProperties?.TryGetValue("IsSkill", out var isSkill) == true &&
                isSkill is bool isSkillBool && isSkillBool)
            {
                skillContainers.Add(function);
            }
        }

        if (skillContainers.Count == 0)
        {
            logger?.LogDebug("No skills found");
            return;
        }

        // ========== PHASE 2: Collect document uploads and references ==========
        var documentUploads = new List<(string SkillName, string DocumentId, string FilePath, string Description)>();
        var documentReferences = new List<(string SkillName, string DocumentId, string? DescriptionOverride)>();

        foreach (var skillContainer in skillContainers)
        {
            var skillName = skillContainer.Name ?? "Unknown";

            // Collect document uploads using type-safe SkillDocuments property
            if (skillContainer is HPDAIFunctionFactory.HPDAIFunction hpdFunction &&
                hpdFunction.SkillDocuments?.Any() == true)
            {
                foreach (var doc in hpdFunction.SkillDocuments)
                {
                    // Handle both file-based and URL-based documents
                    var filePath = doc.FilePath ?? doc.Url;
                    if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(doc.Description))
                    {
                        documentUploads.Add((skillName, doc.DocumentId, filePath, doc.Description));
                    }
                }
            }

            // Collect document references (AddDocument)
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

        // ========== CRITICAL CHECK: Do we have ANY documents? ==========
        if (documentUploads.Count == 0 && documentReferences.Count == 0)
        {
            logger?.LogDebug("No documents found in any skills - document store not needed");
            Console.WriteLine("[ProcessSkillDocuments] No documents found - skipping document store creation");
            // IMPORTANT: Leave _documentStore as null so DocumentRetrievalPlugin is NOT registered
            return;
        }

        Console.WriteLine($"[ProcessSkillDocuments] Found {documentUploads.Count} uploads and {documentReferences.Count} references");

        // ========== PHASE 3: NOW create document store (only if needed) ==========
        if (_documentStore == null)
        {
            var defaultPath = Path.Combine(Directory.GetCurrentDirectory(), "agent-skills", "skill-documents");
            var storeLogger = _logger?.CreateLogger<HPD.Agent.Skills.DocumentStore.FileSystemInstructionStore>()
                ?? NullLogger<HPD.Agent.Skills.DocumentStore.FileSystemInstructionStore>.Instance;
            _documentStore = new HPD.Agent.Skills.DocumentStore.FileSystemInstructionStore(
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

        // ========== PHASE 4: Upload documents with deduplication ==========
        // IMPORTANT: This is where we read files from the OS filesystem (not in the store!)
        // The store only cares about content and where to persist it.
        var uploadedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[ProcessSkillDocuments] Processing {documentUploads.Count} document uploads");

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
                string content;

                // ✅ Step 1: Check if this is a URL or a file path
                if (IsUrl(filePath))
                {
                    // Fetch content from URL
                    try
                    {
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(30);
                        content = await httpClient.GetStringAsync(filePath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (HttpRequestException httpEx)
                    {
                        throw new InvalidOperationException(
                            $"Failed to fetch document from URL '{filePath}' for skill '{skillName}': {httpEx.Message}",
                            httpEx);
                    }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException(
                            $"Timeout fetching document from URL '{filePath}' for skill '{skillName}'");
                    }
                }
                else
                {
                    // ✅ Step 1b: Resolve the file path with proper error handling
                    var resolvedPath = ResolveDocumentPath(filePath, skillName);

                    // ✅ Step 2: Read file content (this is AgentBuilder's responsibility)
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
                }

                // ✅ Step 3: Pass content (not path) to store
                var metadata = new HPD.Agent.Skills.DocumentStore.DocumentMetadata
                {
                    Name = documentId,
                    Description = description
                };

                await _documentStore!.UploadFromContentAsync(documentId, metadata, content, cancellationToken)
                    .ConfigureAwait(false);

                uploadedDocuments.Add(documentId);
                logger?.LogInformation(
                    "Uploaded document {DocumentId} from {Source} for skill {SkillName}",
                    documentId, filePath, skillName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = $"Failed to upload document '{documentId}' from '{filePath}' for skill '{skillName}': {ex.Message}";
                logger?.LogError(ex, message);
                throw new InvalidOperationException(message, ex);
            }
        }

        // ========== PHASE 5: Validate document references ==========
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
                throw new HPD.Agent.Skills.DocumentStore.DocumentNotFoundException(message, documentId);
            }

            logger?.LogDebug("Validated document reference {DocumentId} for skill {SkillName}", documentId, skillName);
        }

        // ========== PHASE 6: Link documents to skills in the store ==========
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

                    var skillDocMetadata = new HPD.Agent.Skills.DocumentStore.SkillDocumentMetadata
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
    /// Checks if a path is a URL (http:// or https://).
    /// </summary>
    private static bool IsUrl(string path)
    {
        return !string.IsNullOrEmpty(path) &&
               (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

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
    /// Gets the configuration instance for this builder (if provided).
    /// Used by extension methods and plugins to access configuration values.
    /// </summary>
    public IConfiguration? Configuration => _configuration;

    /// <summary>
    /// Gets the configuration object for this builder.
    /// Used by provider extension methods to configure provider-specific settings.
    /// </summary>
    public AgentConfig Config => _config;

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
    /// Gets the logger factory for use in extension methods.
    /// Used by MCP and other extension methods to create loggers.
    /// </summary>
    public ILoggerFactory? Logger => _logger;

    /// <summary>
    /// Internal access to scoped Middleware manager for extension methods
    /// </summary>

    /// <summary>
    /// Internal access to default plugin context for extension methods
    /// </summary>
    internal IPluginMetadataContext? DefaultContext
    {
        get => _defaultContext;
        set => _defaultContext = value;
    }

    /// <summary>
    /// Public access to plugin contexts for extension methods and external configuration
    /// </summary>
    public Dictionary<string, IPluginMetadataContext?> PluginContexts => _pluginContexts;

    /// <summary>
    /// Public access to unified middlewares for extension methods and external configuration
    /// </summary>
    public List<Middleware.IAgentMiddleware> Middlewares => _middlewares;

    /// <summary>
    /// Internal access to permission Middlewares for extension methods
    /// </summary>

    /// <summary>
    /// Gets or sets the MCP client manager for extension methods (stored as object to avoid circular reference).
    /// Used by MCP extension methods to initialize and manage MCP server connections.
    /// </summary>
    public object? McpClientManager
    {
        get => _mcpClientManager;
        set => _mcpClientManager = value;
    }

    #endregion

    /// <summary>
    /// Adds a native function to the agent (used by FFI layer for Rust, C++, etc.)
    /// This method is intended primarily for FFI integration with native plugins.
    /// </summary>
    public AgentBuilder WithNativeFunction(AIFunction function)
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
            
        return this;
    }

    /// <summary>
    /// Creates a chat reducer based on the HistoryReductionConfig strategy.
    /// Returns null if history reduction is not enabled.
    /// </summary>
    private static IChatReducer? CreateChatReducer(
        IChatClient baseClient,
        AgentConfig config,
        IChatClient? summarizerClient)
    {
        var historyConfig = config.HistoryReduction;

        if (historyConfig == null || !historyConfig.Enabled)
        {
            return null;
        }

        return historyConfig.Strategy switch
        {
            HistoryReductionStrategy.MessageCounting =>
                new MessageCountingChatReducer(historyConfig.TargetMessageCount),

            HistoryReductionStrategy.Summarizing =>
                CreateSummarizingReducer(baseClient, historyConfig, summarizerClient),

            _ => throw new ArgumentException($"Unknown history reduction strategy: {historyConfig.Strategy}")
        };
    }

    /// <summary>
    /// Creates a SummarizingChatReducer with custom configuration.
    /// Supports using a separate, cheaper model for summarization (cost optimization).
    /// </summary>
    private static SummarizingChatReducer CreateSummarizingReducer(
        IChatClient baseClient,
        HistoryReductionConfig historyConfig,
        IChatClient? summarizerClient)
    {
        // Determine which chat client to use for summarization
        // If a custom summarizer client was provided, use it
        // Otherwise, fall back to the base client
        var clientForSummarization = summarizerClient ?? baseClient;

        var reducer = new SummarizingChatReducer(
            clientForSummarization,
            historyConfig.TargetMessageCount,
            historyConfig.SummarizationThreshold);

        if (!string.IsNullOrEmpty(historyConfig.CustomSummarizationPrompt))
        {
            reducer.SummarizationPrompt = historyConfig.CustomSummarizationPrompt;
        }

        return reducer;
    }
}


#region Middleware Extensions
/// <summary>
/// Extension methods for configuring middleware for the AgentBuilder.
/// </summary>
public static class AgentBuilderMiddlewareExtensions
{
    /// <summary>
    /// Adds a unified agent middleware instance.
    /// Supports scoping via extension methods (.AsGlobal(), .ForPlugin(), .ForSkill(), .ForFunction()).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="middleware">The unified middleware to add</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithMiddleware(this AgentBuilder builder, Middleware.IAgentMiddleware middleware)
    {
        if (middleware != null)
        {
            builder.Middlewares.Add(middleware);
        }
        return builder;
    }

    /// <summary>
    /// Adds a unified agent middleware by type (will be instantiated).
    /// </summary>
    /// <typeparam name="T">The middleware type</typeparam>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithMiddleware<T>(this AgentBuilder builder)
        where T : Middleware.IAgentMiddleware, new()
        => builder.WithMiddleware(new T());

    /// <summary>
    /// Adds multiple unified agent middlewares.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="middlewares">The middlewares to add</param>
    /// <returns>The builder for chaining</returns>
    public static AgentBuilder WithMiddlewares(this AgentBuilder builder, params Middleware.IAgentMiddleware[] middlewares)
    {
        if (middlewares != null)
        {
            foreach (var middleware in middlewares)
            {
                builder.WithMiddleware(middleware);
            }
        }
        return builder;
    }



    /// <summary>
    /// Adds circuit breaker middleware to prevent infinite loops from repeated identical tool calls.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="maxConsecutiveCalls">Maximum consecutive identical calls before triggering (default: 3)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// The circuit breaker detects when the same tool is called with identical arguments
    /// multiple times consecutively, which typically indicates the agent is stuck in a loop.
    /// When triggered, execution terminates with a descriptive message.
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithCircuitBreaker(maxConsecutiveCalls: 3)
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithCircuitBreaker(this AgentBuilder builder, int maxConsecutiveCalls = 3)
    {
        var middleware = new CircuitBreakerMiddleware
        {
            MaxConsecutiveCalls = maxConsecutiveCalls
        };
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds circuit breaker middleware with custom configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure the circuit breaker middleware</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithCircuitBreaker(config =>
    ///     {
    ///         config.MaxConsecutiveCalls = 5;
    ///         config.TerminationMessageTemplate = "Loop detected for {toolName}. Stopping.";
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithCircuitBreaker(this AgentBuilder builder, Action<CircuitBreakerMiddleware> configure)
    {
        var middleware = new CircuitBreakerMiddleware();
        configure(middleware);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds error tracking middleware to detect and handle consecutive tool execution errors.
    /// Terminates execution when errors exceed the specified threshold.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="maxConsecutiveErrors">Maximum consecutive errors before termination (default: 3)</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithErrorTracking(maxConsecutiveErrors: 5)
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithErrorTracking(this AgentBuilder builder, int maxConsecutiveErrors = 3)
    {
        var middleware = new ErrorTrackingMiddleware
        {
            MaxConsecutiveErrors = maxConsecutiveErrors
        };
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds error tracking middleware with custom configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure the error tracking middleware</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithErrorTracking(config =>
    ///     {
    ///         config.MaxConsecutiveErrors = 5;
    ///         config.CustomErrorDetector = result =>
    ///             result.Exception != null ||
    ///             result.Result?.ToString()?.Contains("FATAL") == true;
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithErrorTracking(this AgentBuilder builder, Action<ErrorTrackingMiddleware> configure)
    {
        var middleware = new ErrorTrackingMiddleware();
        configure(middleware);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds total error threshold middleware to protect against gradual degradation.
    /// Tracks total errors across all iterations (regardless of type) and stops when threshold is exceeded.
    /// This complements ErrorTracking (consecutive same errors) and CircuitBreaker (identical calls).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="maxTotalErrors">Maximum total errors before termination (default: 10)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// Use this middleware when you want to protect against:
    /// - Different types of errors occurring progressively
    /// - Total degradation from mixed failure scenarios
    /// - Agents that keep trying despite multiple different problems
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithErrorTracking(maxConsecutiveErrors: 3)           // 3 consecutive same errors
    ///     .WithCircuitBreaker(maxConsecutiveCalls: 3)           // 3 identical tool calls
    ///     .WithTotalErrorThreshold(maxTotalErrors: 10)          // 10 total errors (any type)
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithTotalErrorThreshold(this AgentBuilder builder, int maxTotalErrors = 10)
    {
        var middleware = new TotalErrorThresholdMiddleware
        {
            MaxTotalErrors = maxTotalErrors
        };
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds total error threshold middleware with custom configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure the total error threshold middleware</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithTotalErrorThreshold(config =>
    ///     {
    ///         config.MaxTotalErrors = 15;
    ///         config.CustomErrorDetector = result =>
    ///             result.Exception != null ||
    ///             result.Result?.ToString()?.Contains("CRITICAL") == true;
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithTotalErrorThreshold(this AgentBuilder builder, Action<TotalErrorThresholdMiddleware> configure)
    {
        var middleware = new TotalErrorThresholdMiddleware();
        configure(middleware);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    //      
    // FUNCTION-LEVEL ERROR HANDLING MIDDLEWARE
    //      

    /// <summary>
    /// Adds function retry middleware with provider-aware retry logic.
    /// Uses settings from AgentConfig.ErrorHandling for retry behavior.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This middleware provides intelligent retry logic with a 3-tier priority system:
    /// </para>
    /// <list type="number">
    /// <item><b>Priority 1:</b> Custom retry strategy (if configured via ErrorHandling.CustomRetryStrategy)</item>
    /// <item><b>Priority 2:</b> Provider-aware handling (respects Retry-After headers, error categorization)</item>
    /// <item><b>Priority 3:</b> Exponential backoff fallback (with jitter)</item>
    /// </list>
    /// <para>
    /// <b>Recommended Middleware Order:</b>
    /// </para>
    /// <code>
    /// .WithFunctionRetry()    // Outermost - retry the entire timeout operation
    /// .WithFunctionTimeout()  // Middle - timeout individual attempts
    /// .WithPermissions()      // Innermost - check permissions before execution
    /// </code>
    /// <para>
    /// The middleware uses settings from <c>AgentConfig.ErrorHandling</c>:
    /// - MaxRetries (default: 3)
    /// - RetryDelay (default: 1 second)
    /// - BackoffMultiplier (default: 2.0)
    /// - MaxRetryDelay (default: 30 seconds)
    /// - UseProviderRetryDelays (default: true)
    /// - MaxRetriesByCategory (optional per-category limits)
    /// - CustomRetryStrategy (optional override)
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var config = new AgentConfig
    /// {
    ///     ErrorHandling = new ErrorHandlingConfig
    ///     {
    ///         MaxRetries = 5,
    ///         RetryDelay = TimeSpan.FromSeconds(2)
    ///     }
    /// };
    ///
    /// var agent = new AgentBuilder(config)
    ///     .WithFunctionRetry()  // Uses config.ErrorHandling settings
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFunctionRetry(this AgentBuilder builder)
    {
        var config = builder.Config.ErrorHandling ?? new ErrorHandlingConfig();
        var middleware = new FunctionRetryMiddleware(config);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds function retry middleware with custom error handling configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure error handling settings</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithFunctionRetry(config =>
    ///     {
    ///         config.MaxRetries = 5;
    ///         config.RetryDelay = TimeSpan.FromSeconds(2);
    ///         config.MaxRetriesByCategory = new Dictionary&lt;ErrorCategory, int&gt;
    ///         {
    ///             [ErrorCategory.RateLimitRetryable] = 10,
    ///             [ErrorCategory.ServerError] = 3
    ///         };
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFunctionRetry(this AgentBuilder builder, Action<ErrorHandlingConfig> configure)
    {
        var config = new ErrorHandlingConfig();
        configure(config);
        var middleware = new FunctionRetryMiddleware(config);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds function timeout middleware to enforce execution time limits.
    /// Uses SingleFunctionTimeout from AgentConfig.ErrorHandling (default: 30 seconds).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// If a function takes longer than the configured timeout, it will be cancelled
    /// and a TimeoutException will be thrown.
    /// </para>
    /// <para>
    /// <b>Recommended Middleware Order:</b>
    /// </para>
    /// <code>
    /// .WithFunctionRetry()    // Outermost - retry the entire timeout operation
    /// .WithFunctionTimeout()  // Middle - timeout individual attempts
    /// .WithPermissions()      // Innermost - check permissions before execution
    /// </code>
    /// <para>
    /// When combined with retry middleware, the timeout applies to EACH retry attempt
    /// independently, not to the total time across all attempts.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var config = new AgentConfig
    /// {
    ///     ErrorHandling = new ErrorHandlingConfig
    ///     {
    ///         SingleFunctionTimeout = TimeSpan.FromMinutes(2)
    ///     }
    /// };
    ///
    /// var agent = new AgentBuilder(config)
    ///     .WithFunctionTimeout()  // Uses config.ErrorHandling.SingleFunctionTimeout
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFunctionTimeout(this AgentBuilder builder)
    {
        var timeout = builder.Config.ErrorHandling?.SingleFunctionTimeout ?? TimeSpan.FromSeconds(30);
        var middleware = new FunctionTimeoutMiddleware(timeout);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds function timeout middleware with a custom timeout value.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="timeout">Maximum time allowed for function execution</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithFunctionTimeout(TimeSpan.FromMinutes(5))
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFunctionTimeout(this AgentBuilder builder, TimeSpan timeout)
    {
        var middleware = new FunctionTimeoutMiddleware(timeout);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds error formatting middleware to sanitize function errors before sending to the LLM.
    /// Uses settings from AgentConfig.ErrorHandling.IncludeDetailedErrorsInChat (default: false for security).
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This middleware acts as a security boundary, preventing sensitive information from being
    /// exposed to the LLM through exception messages. By default, it returns sanitized error
    /// messages like "Error: Function 'X' failed." while preserving the full exception in
    /// <c>AgentMiddlewareContext.FunctionException</c> for logging and debugging.
    /// </para>
    /// <para>
    /// <b>Security Note:</b> The default setting (<c>IncludeDetailedErrorsInChat = false</c>) is
    /// recommended for production to prevent exposing:
    /// - Stack traces
    /// - Database connection strings
    /// - File system paths
    /// - API keys or tokens
    /// </para>
    /// <para>
    /// <b>Recommended Middleware Order:</b>
    /// </para>
    /// <code>
    /// .WithFunctionRetry()      // Outermost - retry the entire operation
    /// .WithFunctionTimeout()    // Middle - timeout individual attempts
    /// .WithErrorFormatting()    // Innermost - format errors after all retries exhausted
    /// </code>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Default - sanitized errors (secure)
    /// var agent = new AgentBuilder(config)
    ///     .WithErrorFormatting()
    ///     .Build();
    ///
    /// // Allow detailed errors (only for trusted environments)
    /// var config = new AgentConfig
    /// {
    ///     ErrorHandling = new ErrorHandlingConfig
    ///     {
    ///         IncludeDetailedErrorsInChat = true
    ///     }
    /// };
    /// var agent = new AgentBuilder(config)
    ///     .WithErrorFormatting()
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithErrorFormatting(this AgentBuilder builder)
    {
        var config = builder.Config.ErrorHandling ?? new ErrorHandlingConfig();
        var middleware = new ErrorFormattingMiddleware(config);
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Adds error formatting middleware with explicit configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="includeDetailedErrors">Whether to include detailed exception messages in function results sent to the LLM</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// <b>Security Warning:</b> Setting <paramref name="includeDetailedErrors"/> to <c>true</c>
    /// may expose sensitive information to the LLM. Use only in trusted environments or for debugging.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Debugging/development - include detailed errors
    /// var agent = new AgentBuilder()
    ///     .WithErrorFormatting(includeDetailedErrors: true)
    ///     .Build();
    ///
    /// // Production - sanitized errors (recommended)
    /// var agent = new AgentBuilder()
    ///     .WithErrorFormatting(includeDetailedErrors: false)
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithErrorFormatting(this AgentBuilder builder, bool includeDetailedErrors)
    {
        var middleware = new ErrorFormattingMiddleware
        {
            IncludeDetailedErrorsInChat = includeDetailedErrors
        };
        builder.Middlewares.Add(middleware);
        return builder;
    }

    /// <summary>
    /// Convenience method that registers all error handling middleware in the correct order.
    /// This includes circuit breaker, error tracking, total error threshold, function retry, function timeout, and error formatting.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="maxConsecutiveCalls">Maximum consecutive identical function calls before circuit breaker triggers (default: 5)</param>
    /// <param name="maxConsecutiveErrors">Maximum consecutive errors before termination (default: 3)</param>
    /// <param name="maxTotalErrors">Maximum total errors across all iterations before termination (default: 10)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This convenience method registers middleware in the optimal order:
    /// </para>
    /// <para><b>Iteration-level middleware (outer to inner):</b></para>
    /// <list type="number">
    /// <item><b>CircuitBreakerMiddleware</b> - Detects stuck loops (same function called N times)</item>
    /// <item><b>ErrorTrackingMiddleware</b> - Tracks consecutive errors (resets on success)</item>
    /// <item><b>TotalErrorThresholdMiddleware</b> - Tracks cumulative errors (never resets)</item>
    /// </list>
    /// <para><b>Function-level middleware (onion pattern):</b></para>
    /// <list type="number">
    /// <item><b>FunctionRetryMiddleware</b> - Outermost, retries entire operation</item>
    /// <item><b>FunctionTimeoutMiddleware</b> - Middle, applies timeout to each retry attempt</item>
    /// <item><b>ErrorFormattingMiddleware</b> - Innermost, sanitizes errors for LLM (security boundary)</item>
    /// </list>
    /// <para>
    /// Function-level middleware uses settings from <c>AgentConfig.ErrorHandling</c> for retry/timeout/formatting configuration.
    /// </para>
    /// <para>
    /// <b>Security Note:</b> By default, error messages sent to the LLM are sanitized to prevent exposing
    /// sensitive information. Set <c>ErrorHandlingConfig.IncludeDetailedErrorsInChat = true</c> only in
    /// trusted environments.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple usage with defaults
    /// var agent = new AgentBuilder(config)
    ///     .WithErrorHandling()
    ///     .Build();
    ///
    /// // Custom thresholds
    /// var agent = new AgentBuilder(config)
    ///     .WithErrorHandling(
    ///         maxConsecutiveCalls: 3,
    ///         maxConsecutiveErrors: 5,
    ///         maxTotalErrors: 15)
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithErrorHandling(
        this AgentBuilder builder,
        int maxConsecutiveCalls = 5,
        int maxConsecutiveErrors = 3,
        int maxTotalErrors = 10)
    {
        // Iteration-level middleware (order matters: circuit breaker → error tracking → total threshold)
        builder.WithCircuitBreaker(maxConsecutiveCalls);
        builder.WithErrorTracking(maxConsecutiveErrors);
        builder.WithTotalErrorThreshold(maxTotalErrors);

        // Function-level middleware (onion pattern: retry → timeout → formatting)
        // These use AgentConfig.ErrorHandling for retry/timeout/formatting settings
        builder.WithFunctionRetry();
        builder.WithFunctionTimeout();
        builder.WithErrorFormatting();  // Innermost - sanitizes errors for LLM

        return builder;
    }

    /// <summary>
    /// Convenience method that registers all error handling middleware with advanced configuration options.
    /// Allows fine-grained control over each middleware component.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configureCircuitBreaker">Action to configure circuit breaker middleware</param>
    /// <param name="configureErrorTracking">Optional action to configure error tracking middleware</param>
    /// <param name="configureTotalThreshold">Optional action to configure total error threshold middleware</param>
    /// <param name="configureFunctionRetry">Optional action to configure function retry middleware</param>
    /// <param name="configureFunctionTimeout">Optional timeout for function execution</param>
    /// <param name="includeDetailedErrorsInChat">Optional flag to include detailed error messages in LLM chat (default: false for security)</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// This overload provides maximum flexibility for configuring error handling middleware.
    /// Middleware that are not configured (null actions) will use sensible defaults.
    /// </para>
    /// <para>
    /// <b>NOTE:</b> This overload requires at least the first parameter (configureCircuitBreaker)
    /// to disambiguate from the simple overload. To use all defaults, use the parameterless
    /// <see cref="WithErrorHandling(AgentBuilder, int, int, int)"/> overload instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder(config)
    ///     .WithErrorHandling(
    ///         configureCircuitBreaker: cb =>
    ///         {
    ///             cb.MaxConsecutiveCalls = 3;
    ///             cb.TerminationMessageTemplate = "Loop detected for {toolName}!";
    ///         },
    ///         configureFunctionRetry: retry =>
    ///         {
    ///             retry.MaxRetries = 5;
    ///             retry.RetryDelay = TimeSpan.FromSeconds(2);
    ///             retry.MaxRetriesByCategory = new Dictionary&lt;ErrorCategory, int&gt;
    ///             {
    ///                 [ErrorCategory.RateLimitRetryable] = 10
    ///             };
    ///         },
    ///         configureFunctionTimeout: TimeSpan.FromMinutes(2),
    ///         includeDetailedErrorsInChat: false)  // Secure by default
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithErrorHandling(
        this AgentBuilder builder,
        Action<CircuitBreakerMiddleware> configureCircuitBreaker,
        Action<ErrorTrackingMiddleware>? configureErrorTracking = null,
        Action<TotalErrorThresholdMiddleware>? configureTotalThreshold = null,
        Action<ErrorHandlingConfig>? configureFunctionRetry = null,
        TimeSpan? configureFunctionTimeout = null,
        bool? includeDetailedErrorsInChat = null)
    {
        // Iteration-level middleware
        if (configureCircuitBreaker != null)
            builder.WithCircuitBreaker(configureCircuitBreaker);
        else
            builder.WithCircuitBreaker(maxConsecutiveCalls: 5);

        if (configureErrorTracking != null)
            builder.WithErrorTracking(configureErrorTracking);
        else
            builder.WithErrorTracking(maxConsecutiveErrors: 3);

        if (configureTotalThreshold != null)
            builder.WithTotalErrorThreshold(configureTotalThreshold);
        else
            builder.WithTotalErrorThreshold(maxTotalErrors: 10);

        // Function-level middleware
        if (configureFunctionRetry != null)
            builder.WithFunctionRetry(configureFunctionRetry);
        else
            builder.WithFunctionRetry();

        if (configureFunctionTimeout.HasValue)
            builder.WithFunctionTimeout(configureFunctionTimeout.Value);
        else
            builder.WithFunctionTimeout();

        // Error formatting (innermost - security boundary)
        if (includeDetailedErrorsInChat.HasValue)
            builder.WithErrorFormatting(includeDetailedErrorsInChat.Value);
        else
            builder.WithErrorFormatting();

        return builder;
    }

    //      
    // PII PROTECTION
    //      

    /// <summary>
    /// Adds PII (Personally Identifiable Information) protection middleware
    /// with default settings. Detects and handles email, credit cards, SSN,
    /// phone numbers, and IP addresses.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// Default strategies:
    /// - Email: Redact → [EMAIL_REDACTED]
    /// - Credit Card: Block (throws PIIBlockedException)
    /// - SSN: Block (throws PIIBlockedException)
    /// - Phone: Mask → ***-***-1234
    /// - IP Address: Hash → &lt;ip_hash:a1b2c3d4&gt;
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithPIIProtection()
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithPIIProtection(this AgentBuilder builder)
    {
        var middleware = new PIIMiddleware();
        // Insert at the beginning so PII is sanitized before other middlewares see the messages
        builder.Middlewares.Insert(0, middleware);
        return builder;
    }

    /// <summary>
    /// Adds PII protection middleware with custom configuration.
    /// Allows per-type strategy configuration and custom detectors.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure the PII middleware</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithPIIProtection(config =>
    ///     {
    ///         // Configure strategies per PII type
    ///         config.EmailStrategy = PIIStrategy.Redact;
    ///         config.CreditCardStrategy = PIIStrategy.Block;
    ///         config.SSNStrategy = PIIStrategy.Block;
    ///         config.PhoneStrategy = PIIStrategy.Mask;
    ///         config.IPAddressStrategy = PIIStrategy.Hash;
    ///
    ///         // Also scan LLM output (in case it echoes PII)
    ///         config.ApplyToOutput = true;
    ///
    ///         // Add custom detector for employee IDs
    ///         config.AddCustomDetector(
    ///             name: "EmployeeId",
    ///             pattern: @"EMP-\d{6}",
    ///             strategy: PIIStrategy.Redact);
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithPIIProtection(this AgentBuilder builder, Action<PIIMiddleware> configure)
    {
        var middleware = new PIIMiddleware();
        configure(middleware);
        // Insert at the beginning so PII is sanitized before other middlewares see the messages
        builder.Middlewares.Insert(0, middleware);
        return builder;
    }

    //      
    // TOOL SCOPING
    //      

    /// <summary>
    /// Enables tool scoping middleware for plugin collapsing and skills architecture.
    /// When enabled, plugins and skills are hidden behind container functions,
    /// reducing the initial tool list and cognitive load on the LLM.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// Tool scoping allows you to organize functions hierarchically:
    /// - Plugin containers: Hide member functions until plugin is expanded
    /// - Skill containers: Hide skill-specific functions until skill is activated
    /// </para>
    /// <para>
    /// This can reduce initial tool list size by up to 87.5%, improving LLM performance
    /// and reducing token usage.
    /// </para>
    /// <para>
    /// <b>Phase 1 Note:</b> This middleware integrates with the existing scoping
    /// infrastructure in Agent.cs. Future phases will migrate more logic to middleware.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithPlugin&lt;FinancialPlugin&gt;()
    ///     .WithToolScoping()  // Enable tool scoping
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithToolScoping(this AgentBuilder builder)
    {
        // Enable scoping in config
        builder.Config.Scoping ??= new ScopingConfig();
        builder.Config.Scoping.Enabled = true;

        // NOTE: The ToolScopingMiddleware will be instantiated and added to the pipeline
        // during Build() after the Agent is constructed (since it needs the ToolVisibilityManager
        // which is created in the Agent constructor). See Build() for registration logic.

        return builder;
    }

    /// <summary>
    /// Enables tool scoping middleware with custom configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure scoping behavior</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithPlugin&lt;FinancialPlugin&gt;()
    ///     .WithToolScoping(config =>
    ///     {
    ///         config.ScopeFrontendTools = true;
    ///         config.MaxFunctionNamesInDescription = 5;
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithToolScoping(this AgentBuilder builder, Action<ScopingConfig> configure)
    {
        builder.Config.Scoping ??= new ScopingConfig();
        builder.Config.Scoping.Enabled = true;
        configure(builder.Config.Scoping);
        
        // NOTE: The ToolScopingMiddleware will be instantiated and added to the pipeline
        // during Build() after the Agent is constructed. See Build() for registration logic.
        
        return builder;
    }

    /// <summary>
    /// Disables tool scoping, making all tools visible to the LLM at all times.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// Use this when you want all functions to be immediately available without
    /// requiring container expansion. This may increase token usage but simplifies
    /// tool discovery.
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithPlugin&lt;FinancialPlugin&gt;()
    ///     .WithoutToolScoping()  // All tools always visible
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithoutToolScoping(this AgentBuilder builder)
    {
        builder.Config.Scoping ??= new ScopingConfig();
        builder.Config.Scoping.Enabled = false;
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
    /// AOT-Compatible: Uses generated PluginRegistry.All catalog (zero reflection in hot path).
    /// Automatically loads plugin registry from the assembly where T is defined if not already loaded.
    /// Auto-registers referenced plugins from skills via GetReferencedPlugins().
    /// WARNING: For Native AOT, requires PluginRegistry types in all referenced assemblies to be preserved.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if plugin is not found in any loaded registry.</exception>
    [RequiresUnreferencedCode("Plugin loading via WithPlugin requires PluginRegistry from assembly where T is defined to be preserved.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "RequiresUnreferencedCode declared on method")]
    public static AgentBuilder WithPlugin<T>(this AgentBuilder builder, IPluginMetadataContext? context = null) where T : class, new()
    {
        var pluginName = typeof(T).Name;
        var pluginAssembly = typeof(T).Assembly;

        // Try to find in already loaded plugins
        if (!builder._availablePlugins.TryGetValue(pluginName, out var factory))
        {
            // Not found - try loading the assembly where T is defined
            builder.LoadPluginRegistryFromAssembly(pluginAssembly);

            // Try again after loading
            if (!builder._availablePlugins.TryGetValue(pluginName, out factory))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginName}' not found in PluginRegistry.All. " +
                    $"Ensure the plugin class has [Function] or [Skill] attributes and the source generator ran successfully.");
            }
        }

        // AOT-compatible path: Use catalog
        builder._selectedPluginFactories.Add(factory);

        // Track as explicitly registered (for ToolVisibilityManager)
        builder._explicitlyRegisteredPlugins.Add(pluginName);

        // Store context
        builder.PluginContexts[pluginName] = context;

        // Auto-discover skill dependencies using catalog (zero reflection)
        AutoRegisterDependenciesFromFactory(builder, factory);

        return builder;
    }

    /// <summary>
    /// Registers a plugin using a pre-created instance with optional execution context.
    /// Used for DI-required plugins (e.g., AgentPlanPlugin, DynamicMemoryPlugin).
    /// The instance's generated Registration class is used for function creation (AOT-compatible).
    /// WARNING: For Native AOT, requires PluginRegistry from instance assembly to be preserved.
    /// </summary>
    [RequiresUnreferencedCode("Plugin instance registration requires PluginRegistry from instance's assembly to be preserved.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "RequiresUnreferencedCode declared on method")]
    public static AgentBuilder WithPlugin<T>(this AgentBuilder builder, T instance, IPluginMetadataContext? context = null) where T : class
    {
        var pluginName = typeof(T).Name;

        // Register as instance registration (will use generated Registration class for function creation)
        builder._instanceRegistrations.Add(new PluginInstanceRegistration(instance, pluginName));
        builder.PluginContexts[pluginName] = context;

        // Track this as explicitly registered
        builder._explicitlyRegisteredPlugins.Add(pluginName);

        // Auto-register dependencies if plugin is in catalog (for skill dependencies)
        // First try to load the assembly where the instance type is defined
        builder.LoadPluginRegistryFromAssembly(instance.GetType().Assembly);
        if (builder._availablePlugins.TryGetValue(pluginName, out var factory))
        {
            AutoRegisterDependenciesFromFactory(builder, factory);
        }

        return builder;
    }

    /// <summary>
    /// Registers a plugin by Type with optional execution context.
    /// AOT-Compatible: Uses generated PluginRegistry.All catalog (zero reflection in hot path).
    /// Automatically loads plugin registry from the assembly where pluginType is defined if not already loaded.
    /// Auto-registers referenced plugins from skills via GetReferencedPlugins().
    /// WARNING: For Native AOT, requires PluginRegistry from pluginType's assembly to be preserved.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if plugin is not found in any loaded registry.</exception>
    [RequiresUnreferencedCode("Plugin registration by Type requires PluginRegistry from plugin assembly to be preserved.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "RequiresUnreferencedCode declared on method")]
    public static AgentBuilder WithPlugin(this AgentBuilder builder, Type pluginType, IPluginMetadataContext? context = null)
    {
        var pluginName = pluginType.Name;
        var pluginAssembly = pluginType.Assembly;

        // Try to find in already loaded plugins
        if (!builder._availablePlugins.TryGetValue(pluginName, out var factory))
        {
            // Not found - try loading the assembly where pluginType is defined
            builder.LoadPluginRegistryFromAssembly(pluginAssembly);

            // Try again after loading
            if (!builder._availablePlugins.TryGetValue(pluginName, out factory))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginName}' not found in PluginRegistry.All. " +
                    $"Ensure the plugin class has [Function] or [Skill] attributes and the source generator ran successfully.");
            }
        }

        // AOT-compatible path: Use catalog
        builder._selectedPluginFactories.Add(factory);

        // Track as explicitly registered (for ToolVisibilityManager)
        builder._explicitlyRegisteredPlugins.Add(pluginName);

        // Store context
        builder.PluginContexts[pluginName] = context;

        // Auto-discover skill dependencies using catalog (zero reflection)
        AutoRegisterDependenciesFromFactory(builder, factory);

        return builder;
    }

    /// <summary>
    /// Auto-registers plugins referenced by skills using the plugin catalog (zero reflection).
    /// Phase 4.5: Also stores function filters for selective registration.
    /// </summary>
    private static void AutoRegisterDependenciesFromFactory(AgentBuilder builder, PluginFactory factory)
    {
        var dependencies = factory.GetReferencedPlugins();

        // Phase 4.5: Get function-specific references for selective registration
        var referencedFunctions = factory.GetReferencedFunctions();

        foreach (var depName in dependencies)
        {
            // Check if already selected
            if (builder._selectedPluginFactories.Any(f => f.Name.Equals(depName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Look up in catalog
            if (builder._availablePlugins!.TryGetValue(depName, out var depFactory))
            {
                builder._selectedPluginFactories.Add(depFactory);
                // Note: Dependencies are NOT added to _explicitlyRegisteredPlugins
                // This distinction matters for ToolVisibilityManager

                // Phase 4.5: Store function filter if specific functions are referenced
                if (referencedFunctions.TryGetValue(depName, out var functionNames) && functionNames.Length > 0)
                {
                    builder._pluginFunctionFilters[depName] = functionNames;
                }

                // Recurse for transitive dependencies
                AutoRegisterDependenciesFromFactory(builder, depFactory);
            }
        }
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
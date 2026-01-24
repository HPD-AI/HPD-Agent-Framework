using Microsoft.Extensions.Logging;

namespace HPD.Agent.Memory;

/// <summary>
/// Extension methods for configuring memory subsystems (Static Memory, Dynamic Memory, Plan Mode) on the AgentBuilder.
/// These extensions enable fluent configuration of different memory strategies and planning capabilities.
/// </summary>
public static class MemoryBuilderExtensions
{
    /// <summary>
    /// Configures Dynamic Memory for the agent.
    /// Dynamic Memory stores contextual facts that can be automatically evicted when approaching token limits.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configure">Configuration action for dynamic memory settings</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// var agent = await new AgentBuilder()
    ///     .WithDynamicMemory(opts => opts
    ///         .WithMaxTokens(5000)
    ///         .WithStorageDirectory("./memories"))
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithDynamicMemory(this AgentBuilder builder, Action<DynamicMemoryOptions>? configure = null)
    {
        var options = new DynamicMemoryOptions();
        configure?.Invoke(options);

        // Use custom store if provided, otherwise create JsonDynamicMemoryStore (default)
        var store = options.Store ?? new JsonDynamicMemoryStore(
            options.StorageDirectory,
            builder.Logger?.CreateLogger<JsonDynamicMemoryStore>());

        // Use MemoryId if provided, otherwise fall back to agent name
        var memoryId = options.MemoryId ?? builder.AgentName;
        var Toolkit = new DynamicMemoryToolkit(store, memoryId, builder.Logger?.CreateLogger<DynamicMemoryToolkit>());
        var middleware = new DynamicMemoryAgentMiddleware(store, options, builder.Logger?.CreateLogger<DynamicMemoryAgentMiddleware>());

        // Register Toolkit and Middleware directly without cross-extension dependencies
        RegisterDynamicMemoryToolkit(builder, Toolkit);
        builder.Middlewares.Add(middleware);

        return builder;
    }

    /// <summary>
    /// Registers the memory Toolkit directly with the builder's instance registrations.
    /// Uses AOT-compatible instance registration (generated Registration class for function creation).
    /// </summary>
    private static void RegisterDynamicMemoryToolkit(AgentBuilder builder, DynamicMemoryToolkit Toolkit)
    {
        var ToolkitName = typeof(DynamicMemoryToolkit).Name;
        builder._instanceRegistrations.Add(new ToolInstanceRegistration(Toolkit, ToolkitName));
        builder.ToolkitContexts[ToolkitName] = null; // No special context needed for memory Toolkit
    }

    /// <summary>
    /// Configures Static Memory for the agent.
    /// Static Memory provides the agent with a knowledge base of documents that can be injected or retrieved during reasoning.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configure">Configuration action for static memory settings</param>
    /// <returns>The builder for method chaining</returns>
    /// <example>
    /// <code>
    /// var agent = await new AgentBuilder()
    ///     .WithStaticMemory(opts => {
    ///         opts.Strategy = MemoryStrategy.FullTextInjection;
    ///         opts.AddDocument("./docs/guide.pdf");
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithStaticMemory(this AgentBuilder builder, Action<StaticMemoryOptions> configure)
    {
        var options = new StaticMemoryOptions();
        configure(options);

        var knowledgeId = options.KnowledgeId ?? options.AgentName ?? builder.AgentName;

        if (options.Store == null)
        {
            // Reuse shared text extractor instance if available, otherwise create new one
            builder._textExtractor ??= new HPD.Agent.TextExtraction.TextExtractionUtility();
            options.Store = new JsonStaticMemoryStore(
                options.StorageDirectory,
                builder._textExtractor,
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
            var middleware = new StaticMemoryAgentMiddleware(
                options.Store,
                knowledgeId,
                options.MaxTokens,
                builder.Logger?.CreateLogger<StaticMemoryAgentMiddleware>());
            builder.Middlewares.Add(middleware);
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
    /// Enables plan mode for the agent, allowing it to create and manage execution plans.
    /// Plan mode provides AIFunctions for creating plans, updating steps, and tracking progress.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configure">Configuration action for plan mode settings</param>
    /// <returns>The builder for method chaining</returns>
    /// <remarks>
    /// <para><b>Session Persistence (V2):</b></para>
    /// <para>
    /// Plans are now automatically persisted to the session via MiddlewareState (PlanModePersistentStateData).
    /// The old store-based persistence options (EnablePersistence, StorageDirectory, Store) are obsolete.
    /// Plans will automatically survive across agent runs within the same session.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = await new AgentBuilder()
    ///     .WithPlanMode()  // Plans auto-persist to session
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithPlanMode(this AgentBuilder builder, Action<PlanModeOptions>? configure = null)
    {
        var options = new PlanModeOptions();
        configure?.Invoke(options);

        if (!options.Enabled)
        {
            return builder;
        }

        // V2: Plans are now stored in MiddlewareState (PlanModePersistentStateData)
        // and automatically persisted to session. No separate store needed.

        // Create config for middleware
        var config = new PlanModeConfig
        {
            Enabled = options.Enabled,
            CustomInstructions = options.CustomInstructions
        };

        // Create toolkit (no longer needs store - uses MiddlewareState)
        var toolkit = new AgentPlanToolkit(builder.Logger?.CreateLogger<AgentPlanToolkit>());

        // Create middleware (no longer needs store - uses MiddlewareState)
        var middleware = new AgentPlanAgentMiddleware(
            config,
            builder.Logger?.CreateLogger<AgentPlanAgentMiddleware>());

        // Register toolkit directly (instance-based for DI toolkits)
        var toolkitName = typeof(AgentPlanToolkit).Name;
        builder._instanceRegistrations.Add(new ToolInstanceRegistration(toolkit, toolkitName));
        builder.ToolkitContexts[toolkitName] = null;

        // Register middleware directly
        builder.Middlewares.Add(middleware);

        return builder;
    }
}

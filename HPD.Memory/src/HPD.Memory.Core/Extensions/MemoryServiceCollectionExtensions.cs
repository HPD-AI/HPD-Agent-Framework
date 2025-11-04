// Copyright (c) Einstein Essibu. All rights reserved.
// Dependency injection extensions for HPD-Agent.Memory.

using HPDAgent.Memory.Abstractions.Pipeline;
using HPDAgent.Memory.Abstractions.Storage;
using HPDAgent.Memory.Core.Contexts;
using HPDAgent.Memory.Core.Orchestration;
using HPDAgent.Memory.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HPDAgent.Memory.Extensions;

/// <summary>
/// Extension methods for registering HPD-Agent.Memory services.
/// </summary>
public static partial class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Register core HPD-Agent.Memory services with in-memory storage.
    /// Good for testing and development.
    /// </summary>
    public static IServiceCollection AddHPDAgentMemory(this IServiceCollection services)
    {
        return services.AddHPDAgentMemoryCore()
            .AddInMemoryStorage();
    }

    /// <summary>
    /// Register core HPD-Agent.Memory services with local file storage.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="basePath">Base path for document storage</param>
    public static IServiceCollection AddHPDAgentMemory(
        this IServiceCollection services,
        string basePath)
    {
        return services.AddHPDAgentMemoryCore()
            .AddLocalFileStorage(basePath);
    }

    /// <summary>
    /// Register core orchestration services without storage.
    /// Use this when you want to provide custom storage implementations.
    /// </summary>
    public static IServiceCollection AddHPDAgentMemoryCore(this IServiceCollection services)
    {
        // Register orchestrators for common context types
        services.TryAddSingleton<IPipelineOrchestrator<DocumentIngestionContext>>(sp =>
            new InProcessOrchestrator<DocumentIngestionContext>(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InProcessOrchestrator<DocumentIngestionContext>>>()));

        services.TryAddSingleton<IPipelineOrchestrator<SemanticSearchContext>>(sp =>
            new InProcessOrchestrator<SemanticSearchContext>(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InProcessOrchestrator<SemanticSearchContext>>>()));

        return services;
    }

    /// <summary>
    /// Register in-memory storage implementations.
    /// Good for testing, development, and small deployments.
    /// </summary>
    public static IServiceCollection AddInMemoryStorage(this IServiceCollection services)
    {
        services.TryAddSingleton<IGraphStore, InMemoryGraphStore>();

        // In-memory document store uses temp directory
        services.TryAddSingleton<IDocumentStore>(sp =>
            new LocalFileDocumentStore(
                Path.Combine(Path.GetTempPath(), "hpd-agent-memory"),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalFileDocumentStore>>()));

        return services;
    }

    /// <summary>
    /// Register local file storage implementations.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="basePath">Base path for document storage</param>
    public static IServiceCollection AddLocalFileStorage(
        this IServiceCollection services,
        string basePath)
    {
        services.TryAddSingleton<IDocumentStore>(sp =>
            new LocalFileDocumentStore(
                basePath,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalFileDocumentStore>>()));

        // Graph store is always in-memory for now
        // TODO: Add Neo4j, Azure Cosmos DB Gremlin implementations
        services.TryAddSingleton<IGraphStore, InMemoryGraphStore>();

        return services;
    }

    /// <summary>
    /// Add a pipeline handler for a specific context type.
    /// </summary>
    public static IServiceCollection AddPipelineHandler<TContext, THandler>(
        this IServiceCollection services,
        string stepName)
        where TContext : IPipelineContext
        where THandler : class, IPipelineHandler<TContext>
    {
        // Register handler as singleton
        services.TryAddSingleton<THandler>();

        // Register handler with orchestrator
        services.AddSingleton(sp =>
        {
            var orchestrator = sp.GetRequiredService<IPipelineOrchestrator<TContext>>();
            var handler = sp.GetRequiredService<THandler>();

            // Fire and forget - orchestrator will handle async registration
            _ = orchestrator.AddHandlerAsync(handler);

            return handler;
        });

        return services;
    }

    // NOTE: The AddPipelineHandlersFromAssembly method has been REMOVED for Native AOT compatibility.
    // It used Assembly.GetTypes() which requires runtime reflection and is not Native AOT compatible.
    //
    // MIGRATION GUIDE:
    // Old (reflection-based, NOT Native AOT compatible):
    //   services.AddPipelineHandlersFromAssembly<DocumentIngestionContext>(typeof(MyHandler).Assembly);
    //
    // New Option 1 (recommended - automatic via source generation):
    //   Mark your handlers with [PipelineHandler] attribute and use generated extension methods:
    //   [PipelineHandler(StepName = "extract-text")]
    //   public class TextExtractionHandler : IPipelineHandler<DocumentIngestionContext> { }
    //
    //   Then register:
    //   services.AddGeneratedDocumentIngestionHandlers();  // Auto-discovers all [PipelineHandler] marked handlers
    //   // or
    //   services.AddAllGeneratedHandlers();  // Registers ALL handlers across all context types
    //
    // New Option 2 (manual, explicit registration):
    //   services.AddPipelineHandler<DocumentIngestionContext, TextExtractionHandler>("extract-text");
    //   services.AddPipelineHandler<DocumentIngestionContext, ChunkingHandler>("chunking");
    //
    // The source generator approach (Option 1) is STRONGLY RECOMMENDED as it provides:
    // - Full Native AOT compatibility (zero runtime reflection)
    // - Better performance (compile-time code generation)
    // - Type safety and IDE support
    // - Automatic discovery without manual registration

}

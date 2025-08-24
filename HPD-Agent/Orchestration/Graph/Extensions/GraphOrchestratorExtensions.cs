using System.Collections.Generic;

/// <summary>
/// Factory methods for creating common orchestrator configurations.
/// </summary>
public static class GraphOrchestratorExtensions
{
    /// <summary>
    /// Creates a GraphOrchestrator from a workflow definition and a registry of executable components.
    /// </summary>
    /// <typeparam name="TState">The workflow's state type.</typeparam>
    /// <param name="workflow">The declarative workflow definition.</param>
    /// <param name="registry">The registry containing the compiled StateNodes and ConditionFuncs.</param>
    /// <param name="checkpointStore">Optional persistence store for resilient workflows.</param>
    /// <returns>A fully configured GraphOrchestrator instance.</returns>
    public static GraphOrchestrator<TState> CreateOrchestrator<TState>(
        this WorkflowDefinition workflow,
        WorkflowRegistry<TState> registry,
        ICheckpointStore<TState>? checkpointStore = null) where TState : class, new()
    {
        return new GraphOrchestrator<TState>(workflow, registry, checkpointStore);
    }
}
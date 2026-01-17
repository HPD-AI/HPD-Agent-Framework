using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HPD.Events;
using HPDAgent.Graph.Abstractions.Caching;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Events;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Abstractions.Orchestration;
using HPDAgent.Graph.Core.Channels;
using HPDAgent.Graph.Core.State;
using HPDAgent.Graph.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace HPDAgent.Graph.Core.Orchestration;

/// <summary>
/// Core implementation of graph orchestrator.
/// Executes nodes in topological order with automatic parallelism.
/// Supports optional content-addressable caching for cost savings.
/// </summary>
public class GraphOrchestrator<TContext> : IGraphOrchestrator<TContext>
    where TContext : class, IGraphContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly INodeCacheStore? _cacheStore;
    private readonly INodeFingerprintCalculator? _fingerprintCalculator;
    private readonly Abstractions.Checkpointing.IGraphCheckpointStore? _checkpointStore;

    // Default suspension options for nodes that don't specify their own
    private readonly Abstractions.Execution.SuspensionOptions _defaultSuspensionOptions;

    // Track fingerprints for current execution (for incremental execution)
    // Thread-safe for parallel node execution
    private readonly ConcurrentDictionary<string, string> _currentFingerprints = new();

    // Track failed nodes for error propagation
    // Thread-safe for parallel node execution
    private readonly ConcurrentDictionary<string, Exception> _failedNodes = new();

    // Track output hashes for change-aware iteration 
    // Stores hash of each node's outputs to detect changes between iterations
    // Thread-safe for parallel node execution
    private readonly ConcurrentDictionary<string, string> _outputHashes = new();

    public GraphOrchestrator(
        IServiceProvider serviceProvider,
        INodeCacheStore? cacheStore = null,
        INodeFingerprintCalculator? fingerprintCalculator = null,
        Abstractions.Checkpointing.IGraphCheckpointStore? checkpointStore = null,
        Abstractions.Execution.SuspensionOptions? defaultSuspensionOptions = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _cacheStore = cacheStore;
        _fingerprintCalculator = fingerprintCalculator;
        _checkpointStore = checkpointStore;
        _defaultSuspensionOptions = defaultSuspensionOptions ?? Abstractions.Execution.SuspensionOptions.Default;
    }

    public async Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        context.Log("Orchestrator", $"Starting graph execution: {context.Graph.Name}", LogLevel.Information);

        var executionStartTime = DateTimeOffset.UtcNow;

        try
        {
            // Detect back-edges to determine execution mode
            var backEdges = context.Graph.GetBackEdges();

            if (backEdges.Count > 0)
            {
                context.Log("Orchestrator",
                    $"Graph has {backEdges.Count} back-edge(s), enabling iteration mode",
                    LogLevel.Information);

                return await ExecuteIterativeAsync(context, backEdges, executionStartTime, cancellationToken);
            }
            else
            {
                // Fast path: no back-edges, use original single-pass execution
                return await ExecuteAcyclicAsync(context, executionStartTime, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            HandleCancellation(context, executionStartTime);
            throw;
        }
        catch (Exception ex)
        {
            HandleException(context, ex, executionStartTime);
            throw;
        }
    }

    /// <summary>
    /// Original single-pass execution for acyclic graphs (DAGs).
    /// Zero overhead - identical to previous implementation.
    /// </summary>
    private async Task<TContext> ExecuteAcyclicAsync(
        TContext context,
        DateTimeOffset executionStartTime,
        CancellationToken cancellationToken)
    {
        // Get execution layers (topological sort)
        var layers = context.Graph.GetExecutionLayers();

        if (context is Context.GraphContext graphContext)
        {
            graphContext.SetTotalLayers(layers.Count);
        }

        context.Log("Orchestrator", $"Graph has {layers.Count} execution layers", LogLevel.Debug);

        // Emit graph started event
        EmitGraphStartedEvent(context, layers.Count);

        // Execute each layer
        for (int i = 0; i < layers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var layer = layers[i];
            context.CurrentLayerIndex = i;

            context.Log("Orchestrator", $"Executing layer {i} with {layer.NodeIds.Count} node(s)", LogLevel.Debug);

            await ExecuteLayerAsync(context, layer, cancellationToken);

            // Update managed context
            if (context.Managed is ManagedContext managed)
            {
                managed.IncrementStep();
            }

            // Save checkpoint after layer completion (fire-and-forget, non-blocking)
            if (_checkpointStore != null && context is Context.GraphContext ctxGraph)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SaveCheckpointAsync(ctxGraph, i, Abstractions.Checkpointing.CheckpointTrigger.LayerCompleted, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail execution
                        context.Log("Orchestrator", $"Failed to save checkpoint: {ex.Message}", LogLevel.Warning, exception: ex);
                    }
                });
            }
        }

        FinalizeExecution(context, executionStartTime, totalIterations: 1);
        return context;
    }

    /// <summary>
    /// Iterative execution for cyclic graphs.
    /// Evaluates back-edges after each pass and re-executes dirty nodes.
    /// Supports change-aware iteration with automatic convergence detection.
    /// </summary>
    private async Task<TContext> ExecuteIterativeAsync(
        TContext context,
        IReadOnlyList<BackEdge> backEdges,
        DateTimeOffset executionStartTime,
        CancellationToken cancellationToken)
    {
        // Use IterationOptions.MaxIterations if available, otherwise fall back to Graph.MaxIterations
        var options = context.Graph.IterationOptions;
        var maxIterations = options?.MaxIterations ?? context.Graph.MaxIterations;
        var iteration = 0;
        var converged = false;

        // Initial dirty set = all executable nodes
        var dirtyNodes = GetExecutableNodeIds(context.Graph);

        // Emit graph started event
        EmitGraphStartedEvent(context, layerCount: 0); // Updated per-iteration

        while (dirtyNodes.Count > 0 && iteration < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Execute one iteration
            var iterationResult = await ExecuteIterationAsync(
                context,
                dirtyNodes,
                backEdges,
                iteration,
                cancellationToken);

            dirtyNodes = iterationResult.NextDirtyNodes;

            // Check if graph converged (all outputs unchanged)
            if (iterationResult.Converged)
            {
                converged = true;
                break;
            }

            if (dirtyNodes.Count > 0)
            {
                iteration++;

                if (context is Context.GraphContext gc)
                {
                    gc.IncrementIteration();
                }
            }
        }

        // Handle max iterations reached (only if not converged)
        if (!converged && iteration >= maxIterations && dirtyNodes.Count > 0)
        {
            HandleMaxIterationsReached(context, maxIterations, dirtyNodes, backEdges);
        }

        FinalizeExecution(context, executionStartTime, iteration + 1);
        return context;
    }

    /// <summary>
    /// Executes a single iteration of the graph.
    /// Returns information about which nodes need re-execution.
    /// </summary>
    private async Task<IterationResult> ExecuteIterationAsync(
        TContext context,
        HashSet<string> dirtyNodes,
        IReadOnlyList<BackEdge> backEdges,
        int iteration,
        CancellationToken cancellationToken)
    {
        var iterationStart = DateTimeOffset.UtcNow;
        var options = context.Graph.IterationOptions;

        // Snapshot output hashes before execution (for convergence detection)
        var preIterationHashes = _outputHashes.ToDictionary(x => x.Key, x => x.Value);

        // Clear ephemeral channels between iterations (not on first)
        if (iteration > 0)
        {
            ClearEphemeralChannels(context);
        }

        // Invalidate output channels for dirty nodes
        InvalidateOutputChannels(context, dirtyNodes);

        // Compute layers from dirty nodes only
        var layers = ComputeLayersFromDirtyNodes(dirtyNodes, context.Graph, backEdges);

        if (context is Context.GraphContext gc)
        {
            gc.SetTotalLayers(layers.Count);
        }

        // Emit iteration started event
        context.EventCoordinator?.Emit(new IterationStartedEvent
        {
            IterationIndex = iteration,
            DirtyNodeCount = dirtyNodes.Count,
            DirtyNodeIds = dirtyNodes.ToList(),
            LayerCount = layers.Count,
            GraphContext = CreateGraphExecutionContext(context)
        });

        context.Log("Orchestrator",
            $"Iteration {iteration}: {layers.Count} layer(s), {dirtyNodes.Count} node(s)",
            LogLevel.Debug);

        // Execute all layers
        for (int i = 0; i < layers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.CurrentLayerIndex = i;
            await ExecuteLayerAsync(context, layers[i], cancellationToken);

            // Update managed context
            if (context.Managed is ManagedContext managed)
            {
                managed.IncrementStep();
            }
        }

        //  CRITICAL SYNCHRONIZATION POINT
        // Wait for all polling nodes to reach terminal state before evaluating back-edges
        // This prevents premature convergence while nodes are still polling
        await WaitForPollingNodesToResolveAsync(context, cancellationToken);

        var executedNodes = new HashSet<string>(dirtyNodes);

        // Check for convergence (when enabled) - do this BEFORE evaluating back-edges
        // Now safe - all nodes in terminal state after polling synchronization
        if (options?.UseChangeAwareIteration == true &&
            options?.EnableAutoConvergence == true &&
            HasConverged(preIterationHashes, executedNodes, context))
        {
            context.Log("Orchestrator",
                $"Graph converged at iteration {iteration}: no output changes detected",
                LogLevel.Information);

            context.EventCoordinator?.Emit(new GraphConvergedEvent
            {
                IterationIndex = iteration,
                TotalIterations = iteration + 1,
                ConvergenceReason = "all_outputs_unchanged",
                GraphContext = CreateGraphExecutionContext(context)
            });

            // Emit iteration completed event (with convergence)
            context.EventCoordinator?.Emit(new IterationCompletedEvent
            {
                IterationIndex = iteration,
                Duration = DateTimeOffset.UtcNow - iterationStart,
                ExecutedNodes = executedNodes.ToList(),
                BackEdgesTriggered = 0,
                NodesToReExecute = new List<string>(),
                GraphContext = CreateGraphExecutionContext(context)
            });

            return new IterationResult
            {
                ExecutedNodes = executedNodes,
                TriggeredBackEdges = new List<BackEdge>(),
                NextDirtyNodes = new HashSet<string>(),
                Converged = true
            };
        }

        // Evaluate back-edges to find triggered nodes
        // Pass pre-iteration hashes for change detection
        var (triggeredNodes, triggeredBackEdges) =
            EvaluateBackEdges(context, backEdges, iteration, preIterationHashes);

        // Compute next dirty set
        HashSet<string> nextDirtyNodes;
        if (triggeredNodes.Count > 0)
        {
            // Use change-aware or eager propagation based on options
            if (options?.UseChangeAwareIteration == true)
            {
                nextDirtyNodes = ComputeNextDirtyNodes(context, triggeredNodes, backEdges, iteration);

                context.Log("Orchestrator",
                    $"Back-edges triggered: [{string.Join(", ", triggeredNodes)}] → " +
                    $"re-executing {nextDirtyNodes.Count} node(s) (change-aware)",
                    LogLevel.Information);
            }
            else
            {
                // Legacy behavior: eager propagation
                nextDirtyNodes = GetAllForwardDependents(triggeredNodes, context.Graph, backEdges);

                context.Log("Orchestrator",
                    $"Back-edges triggered: [{string.Join(", ", triggeredNodes)}] → " +
                    $"re-executing {nextDirtyNodes.Count} node(s)",
                    LogLevel.Information);
            }

            // Un-mark dirty nodes as complete so they can re-execute
            context.UnmarkNodesComplete(nextDirtyNodes);
        }
        else
        {
            nextDirtyNodes = new HashSet<string>();
        }

        // Emit iteration completed event
        context.EventCoordinator?.Emit(new IterationCompletedEvent
        {
            IterationIndex = iteration,
            Duration = DateTimeOffset.UtcNow - iterationStart,
            ExecutedNodes = executedNodes.ToList(),
            BackEdgesTriggered = triggeredBackEdges.Count,
            NodesToReExecute = nextDirtyNodes.ToList(),
            GraphContext = CreateGraphExecutionContext(context)
        });

        // Save checkpoint after iteration (fire-and-forget)
        if (_checkpointStore != null && context is Context.GraphContext ctxGraph)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SaveIterationCheckpointAsync(ctxGraph, iteration, nextDirtyNodes, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    context.Log("Orchestrator", $"Failed to save iteration checkpoint: {ex.Message}", LogLevel.Warning, exception: ex);
                }
            });
        }

        return new IterationResult
        {
            ExecutedNodes = executedNodes,
            TriggeredBackEdges = triggeredBackEdges,
            NextDirtyNodes = nextDirtyNodes
        };
    }

    #region Back-Edge Evaluation

    /// <summary>
    /// Evaluates all back-edges and returns which target nodes should be re-executed.
    /// When change-aware iteration is enabled, skips back-edges whose source output hasn't changed.
    /// </summary>
    /// <param name="preIterationHashes">Hash snapshot from BEFORE the iteration executed (for change detection)</param>
    private (HashSet<string> TriggeredNodes, List<BackEdge> TriggeredEdges) EvaluateBackEdges(
        TContext context,
        IReadOnlyList<BackEdge> backEdges,
        int iteration,
        Dictionary<string, string>? preIterationHashes = null)
    {
        var triggeredNodes = new HashSet<string>();
        var triggeredEdges = new List<BackEdge>();
        var options = context.Graph.IterationOptions;
        var excludeFields = options?.IgnoreFieldsForChangeDetection;
        var algorithm = options?.HashAlgorithm ?? Abstractions.Graph.OutputHashAlgorithm.XxHash64;

        foreach (var backEdge in backEdges)
        {
            var channelName = $"node_output:{backEdge.SourceNodeId}";

            if (!context.Channels.Contains(channelName))
                continue;

            var outputs = context.Channels[channelName].Get<Dictionary<string, object>>();

            if (outputs == null)
                continue;

            // Check if source output actually changed (when change-aware iteration enabled)
            if (options?.UseChangeAwareIteration == true && preIterationHashes != null)
            {
                // Compare current output hash against PRE-iteration hash
                var currentHash = ComputeOutputHash(outputs, excludeFields, algorithm);
                preIterationHashes.TryGetValue(backEdge.SourceNodeId, out var previousHash);

                // If hash is same as before iteration, output didn't change
                if (currentHash == previousHash)
                {
                    context.Log("Orchestrator",
                        $"Back-edge {backEdge.SourceNodeId}→{backEdge.TargetNodeId} skipped: output unchanged",
                        LogLevel.Debug);

                    // Emit skip event for observability
                    context.EventCoordinator?.Emit(new BackEdgeSkippedEvent
                    {
                        SourceNodeId = backEdge.SourceNodeId,
                        TargetNodeId = backEdge.TargetNodeId,
                        Reason = "output_unchanged",
                        IterationIndex = iteration,
                        GraphContext = CreateGraphExecutionContext(context)
                    });

                    continue;
                }
            }

            // Evaluate condition (null condition = unconditional back-edge)
            var conditionMet = backEdge.Condition == null ||
                ConditionEvaluator.Evaluate(backEdge.Condition, outputs, context, backEdge.Edge);

            if (conditionMet)
            {
                triggeredNodes.Add(backEdge.TargetNodeId);
                triggeredEdges.Add(backEdge);

                // Emit back-edge triggered event
                context.EventCoordinator?.Emit(new BackEdgeTriggeredEvent
                {
                    SourceNodeId = backEdge.SourceNodeId,
                    TargetNodeId = backEdge.TargetNodeId,
                    ConditionDescription = backEdge.Condition?.GetDescription(),
                    TriggerValue = GetTriggerValue(backEdge.Condition, outputs),
                    IterationIndex = iteration,
                    GraphContext = CreateGraphExecutionContext(context)
                });

                context.Log("Orchestrator",
                    $"Back-edge triggered: {backEdge.SourceNodeId} → {backEdge.TargetNodeId}",
                    LogLevel.Debug);
            }
        }

        return (triggeredNodes, triggeredEdges);
    }

    private static object? GetTriggerValue(EdgeCondition? condition, Dictionary<string, object> outputs)
    {
        if (condition?.Field == null)
            return null;

        outputs.TryGetValue(condition.Field, out var value);
        return value;
    }

    #endregion

    #region Dependency Propagation

    /// <summary>
    /// Gets all nodes that depend on the triggered nodes (forward edges only).
    /// Back-edges are excluded to prevent infinite propagation.
    /// </summary>
    private static HashSet<string> GetAllForwardDependents(
        HashSet<string> triggeredNodes,
        Abstractions.Graph.Graph graph,
        IReadOnlyList<BackEdge> backEdges)
    {
        var backEdgeSet = backEdges
            .Select(b => (b.SourceNodeId, b.TargetNodeId))
            .ToHashSet();

        var allDirty = new HashSet<string>(triggeredNodes);
        var queue = new Queue<string>(triggeredNodes);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();

            foreach (var edge in graph.GetOutgoingEdges(nodeId))
            {
                // Skip back-edges (they point backwards, not forward)
                if (backEdgeSet.Contains((edge.From, edge.To)))
                    continue;

                if (allDirty.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return allDirty;
    }

    #endregion

    #region Layer Computation for Iteration

    /// <summary>
    /// Computes execution layers from dirty nodes only.
    /// Respects dependencies between dirty nodes, excludes back-edges.
    /// </summary>
    private static IReadOnlyList<ExecutionLayer> ComputeLayersFromDirtyNodes(
        HashSet<string> dirtyNodes,
        Abstractions.Graph.Graph graph,
        IReadOnlyList<BackEdge> backEdges)
    {
        if (dirtyNodes.Count == 0)
            return Array.Empty<ExecutionLayer>();

        var backEdgeSet = backEdges
            .Select(b => (b.SourceNodeId, b.TargetNodeId))
            .ToHashSet();

        // Initialize in-degree for dirty nodes only
        var inDegree = new Dictionary<string, int>();
        var outgoing = new Dictionary<string, List<string>>();

        foreach (var nodeId in dirtyNodes)
        {
            inDegree[nodeId] = 0;
            outgoing[nodeId] = new List<string>();
        }

        // Count in-degrees (forward edges only, between dirty nodes only)
        foreach (var edge in graph.Edges)
        {
            // Skip back-edges
            if (backEdgeSet.Contains((edge.From, edge.To)))
                continue;

            // Only consider edges between dirty nodes
            if (!dirtyNodes.Contains(edge.From) || !dirtyNodes.Contains(edge.To))
                continue;

            inDegree[edge.To]++;
            outgoing[edge.From].Add(edge.To);
        }

        // Kahn's algorithm
        var layers = new List<ExecutionLayer>();
        var level = 0;

        while (inDegree.Count > 0)
        {
            var ready = inDegree
                .Where(kvp => kvp.Value == 0)
                .Select(kvp => kvp.Key)
                .ToList();

            if (ready.Count == 0)
            {
                // Remaining nodes form a cycle among themselves
                // (shouldn't happen if back-edges correctly identified)
                break;
            }

            layers.Add(new ExecutionLayer
            {
                Level = level++,
                NodeIds = ready
            });

            foreach (var nodeId in ready)
            {
                inDegree.Remove(nodeId);

                foreach (var dep in outgoing[nodeId])
                {
                    if (inDegree.ContainsKey(dep))
                    {
                        inDegree[dep]--;
                    }
                }
            }
        }

        return layers;
    }

    #endregion

    #region Channel Invalidation

    /// <summary>
    /// Clears output channels for dirty nodes before re-execution.
    /// Ensures downstream nodes receive fresh outputs.
    /// </summary>
    private static void InvalidateOutputChannels(TContext context, HashSet<string> dirtyNodes)
    {
        foreach (var nodeId in dirtyNodes)
        {
            var channelName = $"node_output:{nodeId}";

            if (context.Channels.Contains(channelName))
            {
                context.Channels[channelName].Clear();
            }
        }
    }

    #endregion

    #region Output Hash Tracking (Change-Aware Iteration)

    /// <summary>
    /// Compute hash of node outputs for change detection.
    /// Uses fast hashing (XxHash64) for within-iteration comparison.
    /// </summary>
    /// <param name="outputs">Node outputs to hash</param>
    /// <param name="excludeFields">Fields to exclude (for non-deterministic values)</param>
    /// <param name="algorithm">Hashing algorithm to use</param>
    private static string ComputeOutputHash(
        Dictionary<string, object>? outputs,
        HashSet<string>? excludeFields = null,
        Abstractions.Graph.OutputHashAlgorithm algorithm = Abstractions.Graph.OutputHashAlgorithm.XxHash64)
    {
        if (outputs == null || outputs.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        foreach (var (key, value) in outputs.OrderBy(kv => kv.Key))
        {
            // Skip excluded fields (timestamps, request IDs, etc.)
            if (excludeFields?.Contains(key) == true)
                continue;

            builder.Append(key).Append('=');
            builder.Append(HashValue(value)).Append(';');
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(builder.ToString());

        return algorithm switch
        {
            Abstractions.Graph.OutputHashAlgorithm.SHA256 =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            _ => // XxHash64 (default)
                System.IO.Hashing.XxHash64.HashToUInt64(bytes).ToString("x16")
        };
    }

    /// <summary>
    /// Hash a single value. Handles: primitives, collections, complex objects (via JSON).
    /// </summary>
    private static string HashValue(object? value)
    {
        if (value == null)
            return "null";

        // Primitive types - direct conversion
        if (value is string str)
            return str;

        if (value is int || value is long || value is double ||
            value is float || value is decimal || value is bool)
            return value.ToString() ?? "null";

        // Collections - hash each element recursively
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            foreach (var item in enumerable)
            {
                if (!first) sb.Append(',');
                sb.Append(HashValue(item));
                first = false;
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Complex objects - JSON serialize using source-generated context
        // Note: May fail on circular references, falls back to ToString
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                value,
                HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.Object
            );
            return json;
        }
        catch
        {
            return value.ToString() ?? "object";
        }
    }

    /// <summary>
    /// Check if node's output changed since last check.
    /// Updates stored hash and returns change status.
    /// </summary>
    private bool NodeOutputChanged(
        string nodeId,
        Dictionary<string, object>? outputs,
        HashSet<string>? excludeFields = null,
        Abstractions.Graph.OutputHashAlgorithm algorithm = Abstractions.Graph.OutputHashAlgorithm.XxHash64)
    {
        var currentHash = ComputeOutputHash(outputs, excludeFields, algorithm);
        var changed = !_outputHashes.TryGetValue(nodeId, out var previousHash)
                      || previousHash != currentHash;

        _outputHashes[nodeId] = currentHash;
        return changed;
    }

    /// <summary>
    /// Check if any input to this node changed since last iteration.
    /// Compares current upstream output hashes with stored hashes.
    /// </summary>
    private bool HasChangedInputs(
        TContext context,
        string nodeId,
        HashSet<string>? excludeFields = null,
        Abstractions.Graph.OutputHashAlgorithm algorithm = Abstractions.Graph.OutputHashAlgorithm.XxHash64)
    {
        foreach (var edge in context.Graph.GetIncomingEdges(nodeId))
        {
            var sourceNodeId = edge.From;

            // Skip START node
            var sourceNode = context.Graph.GetNode(sourceNodeId);
            if (sourceNode?.Type == NodeType.Start)
                continue;

            // Get the hash we stored when this upstream node last executed
            if (!_outputHashes.TryGetValue(sourceNodeId, out var storedHash))
                continue; // No previous execution, can't compare

            // Get current output from channel
            var channelName = $"node_output:{sourceNodeId}";
            if (!context.Channels.Contains(channelName))
                continue;

            var outputs = context.Channels[channelName].Get<Dictionary<string, object>>();
            var currentHash = ComputeOutputHash(outputs, excludeFields, algorithm);

            // Compare: if different, this node's input changed
            if (currentHash != storedHash)
                return true;
        }

        return false; // All inputs unchanged
    }

    /// <summary>
    /// Check if all node outputs are unchanged from previous iteration (convergence).
    /// </summary>
    private bool HasConverged(
        Dictionary<string, string> preIterationHashes,
        HashSet<string> executedNodes,
        TContext context)
    {
        var options = context.Graph.IterationOptions;
        var excludeFields = options?.IgnoreFieldsForChangeDetection;
        var algorithm = options?.HashAlgorithm ?? Abstractions.Graph.OutputHashAlgorithm.XxHash64;

        // Check all executed nodes in this iteration
        foreach (var nodeId in executedNodes)
        {
            var channelName = $"node_output:{nodeId}";
            if (!context.Channels.Contains(channelName))
                continue;

            var outputs = context.Channels[channelName].Get<Dictionary<string, object>>();
            var currentHash = ComputeOutputHash(outputs, excludeFields, algorithm);

            if (!preIterationHashes.TryGetValue(nodeId, out var previousHash)
                || previousHash != currentHash)
            {
                return false; // At least one output changed
            }
        }

        return true; // All outputs unchanged = converged
    }

    /// <summary>
    /// Compute next dirty set by checking which nodes have changed inputs.
    /// Only nodes whose upstream outputs changed are marked dirty.
    /// </summary>
    private HashSet<string> ComputeNextDirtyNodes(
        TContext context,
        HashSet<string> triggeredNodes,
        IReadOnlyList<BackEdge> backEdges,
        int iteration)
    {
        if (triggeredNodes.Count == 0)
            return new HashSet<string>();

        var options = context.Graph.IterationOptions;
        var excludeFields = options?.IgnoreFieldsForChangeDetection;
        var algorithm = options?.HashAlgorithm ?? Abstractions.Graph.OutputHashAlgorithm.XxHash64;
        var alwaysDirty = options?.AlwaysDirtyNodes ?? new HashSet<string>();

        var backEdgeSet = backEdges
            .Select(b => (b.SourceNodeId, b.TargetNodeId))
            .ToHashSet();

        var dirtyNodes = new HashSet<string>();
        var visited = new HashSet<string>();
        var queue = new Queue<string>(triggeredNodes);

        // BFS through potential candidates
        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (!visited.Add(nodeId))
                continue;

            // Check if this node should be dirty
            bool shouldBeDirty =
                triggeredNodes.Contains(nodeId) ||      // Directly triggered
                alwaysDirty.Contains(nodeId) ||         // Forced dirty (escape hatch)
                HasChangedInputs(context, nodeId, excludeFields, algorithm);  // Inputs changed

            if (shouldBeDirty)
            {
                dirtyNodes.Add(nodeId);

                // Add downstream nodes as candidates (but don't mark dirty yet)
                foreach (var edge in context.Graph.GetOutgoingEdges(nodeId))
                {
                    // Skip back-edges
                    if (backEdgeSet.Contains((edge.From, edge.To)))
                        continue;

                    queue.Enqueue(edge.To);
                }
            }
            else
            {
                // Emit skip event
                context.EventCoordinator?.Emit(new NodeSkippedUnchangedEvent
                {
                    NodeId = nodeId,
                    IterationIndex = iteration,
                    Reason = "inputs_unchanged",
                    GraphContext = CreateGraphExecutionContext(context)
                });
            }
            // If not dirty, don't propagate - downstream can't be affected
        }

        return dirtyNodes;
    }

    #endregion

    #region Helper Methods

    private static HashSet<string> GetExecutableNodeIds(Abstractions.Graph.Graph graph)
    {
        return graph.Nodes
            .Where(n => n.Type is NodeType.Handler
                               or NodeType.Router
                               or NodeType.SubGraph
                               or NodeType.Map)
            .Select(n => n.Id)
            .ToHashSet();
    }

    private void EmitGraphStartedEvent(TContext context, int layerCount)
    {
        context.EventCoordinator?.Emit(new GraphExecutionStartedEvent
        {
            NodeCount = context.Graph.Nodes.Count,
            LayerCount = layerCount,
            GraphContext = CreateGraphExecutionContext(context),
            Kind = EventKind.Lifecycle,
            Priority = EventPriority.Normal
        });
    }

    private void HandleMaxIterationsReached(
        TContext context,
        int maxIterations,
        HashSet<string> dirtyNodes,
        IReadOnlyList<BackEdge> backEdges)
    {
        var activeBackEdges = backEdges
            .Where(b => dirtyNodes.Contains(b.TargetNodeId))
            .Select(b => $"{b.SourceNodeId}->{b.TargetNodeId}")
            .ToList();

        context.Log("Orchestrator",
            $"Max iterations ({maxIterations}) reached. " +
            $"{dirtyNodes.Count} nodes still dirty: [{string.Join(", ", dirtyNodes)}]",
            LogLevel.Warning);

        context.EventCoordinator?.Emit(new MaxIterationsReachedEvent
        {
            MaxIterations = maxIterations,
            RemainingDirtyNodes = dirtyNodes.ToList(),
            ActiveBackEdges = activeBackEdges,
            GraphContext = CreateGraphExecutionContext(context)
        });
    }

    private void FinalizeExecution(
        TContext context,
        DateTimeOffset startTime,
        int totalIterations)
    {
        if (context is Context.GraphContext gc)
        {
            gc.MarkComplete();
        }

        context.Log("Orchestrator",
            $"Graph completed in {totalIterations} iteration(s)",
            LogLevel.Information);

        context.EventCoordinator?.Emit(new GraphExecutionCompletedEvent
        {
            Duration = DateTimeOffset.UtcNow - startTime,
            SuccessfulNodes = context.CompletedNodes.Count,
            FailedNodes = 0,
            SkippedNodes = 0,
            GraphContext = CreateGraphExecutionContext(context),
            Kind = EventKind.Lifecycle,
            Priority = EventPriority.Normal
        });
    }

    private void HandleCancellation(TContext context, DateTimeOffset executionStartTime)
    {
        if (context is Context.GraphContext gc)
        {
            gc.MarkCancelled();
        }
        context.Log("Orchestrator", "Graph execution cancelled", LogLevel.Warning);

        context.EventCoordinator?.Emit(new GraphExecutionCompletedEvent
        {
            Duration = DateTimeOffset.UtcNow - executionStartTime,
            SuccessfulNodes = context.CompletedNodes.Count,
            FailedNodes = 0,
            SkippedNodes = 0,
            GraphContext = CreateGraphExecutionContext(context),
            Kind = EventKind.Lifecycle,
            Priority = EventPriority.Normal
        });
    }

    private void HandleException(TContext context, Exception ex, DateTimeOffset executionStartTime)
    {
        context.Log("Orchestrator", $"Graph execution failed: {ex.Message}", LogLevel.Error, exception: ex);

        context.EventCoordinator?.Emit(new GraphExecutionCompletedEvent
        {
            Duration = DateTimeOffset.UtcNow - executionStartTime,
            SuccessfulNodes = context.CompletedNodes.Count,
            FailedNodes = 1,
            SkippedNodes = 0,
            GraphContext = CreateGraphExecutionContext(context),
            Kind = EventKind.Lifecycle,
            Priority = EventPriority.Normal
        });
    }

    private static GraphExecutionContext CreateGraphExecutionContext(TContext context)
    {
        return new GraphExecutionContext
        {
            GraphId = context.Graph.Id,
            TotalNodes = context.Graph.Nodes.Count,
            CompletedNodes = context.CompletedNodes.Count,
            CurrentLayer = context.CurrentLayerIndex
        };
    }

    /// <summary>
    /// Saves checkpoint with iteration state information.
    /// </summary>
    private async Task SaveIterationCheckpointAsync(
        Context.GraphContext context,
        int iteration,
        HashSet<string> pendingDirtyNodes,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore == null)
            return;

        // Extract all node outputs from channels for checkpoint restoration
        var nodeOutputs = new Dictionary<string, object>();
        var nodeStateMetadata = new Dictionary<string, Abstractions.Checkpointing.NodeStateMetadata>();

        foreach (var channelName in context.Channels.ChannelNames)
        {
            if (channelName.StartsWith("node_output:"))
            {
                var nodeId = channelName.Substring("node_output:".Length);

                // Only include outputs from completed nodes
                if (context.CompletedNodes.Contains(nodeId))
                {
                    try
                    {
                        var output = context.Channels[channelName].Get<object>();
                        if (output != null)
                        {
                            nodeOutputs[channelName] = output;

                            // Capture node version for compatibility checking
                            var node = context.Graph.GetNode(nodeId);
                            if (node != null)
                            {
                                nodeStateMetadata[nodeId] = new Abstractions.Checkpointing.NodeStateMetadata
                                {
                                    NodeId = nodeId,
                                    Version = node.Version,
                                    StateJson = System.Text.Json.JsonSerializer.Serialize(
                                        output,
                                        HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.DictionaryStringObject
                                    ),
                                    CapturedAt = DateTimeOffset.UtcNow
                                };
                            }
                        }
                    }
                    catch
                    {
                        // Skip channels that can't be retrieved as object
                    }
                }
            }
        }

        var checkpoint = new Abstractions.Checkpointing.GraphCheckpoint
        {
            CheckpointId = Guid.NewGuid().ToString("N"),
            ExecutionId = context.ExecutionId,
            GraphId = context.Graph.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedNodes = context.CompletedNodes,
            NodeOutputs = nodeOutputs,
            NodeStateMetadata = nodeStateMetadata,
            CurrentIteration = iteration,
            PendingDirtyNodes = pendingDirtyNodes,
            ContextJson = System.Text.Json.JsonSerializer.Serialize(
                new HPDAgent.Graph.Abstractions.Serialization.ContextMetadata
                {
                    ExecutionId = context.ExecutionId,
                    CompletedNodes = context.CompletedNodes.ToList(),
                    CurrentLayerIndex = context.CurrentLayerIndex,
                    CurrentIteration = iteration,
                    PendingDirtyNodes = pendingDirtyNodes.ToList()
                },
                HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.ContextMetadata
            ),
            Metadata = new Abstractions.Checkpointing.CheckpointMetadata
            {
                Trigger = Abstractions.Checkpointing.CheckpointTrigger.IterationCompleted,
                CompletedLayer = context.CurrentLayerIndex,
                IterationIndex = iteration
            }
        };

        await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);

        context.Log("Orchestrator", $"Iteration checkpoint saved after iteration {iteration}", LogLevel.Debug);
    }

    #endregion

    #region Supporting Types

    private sealed record IterationResult
    {
        public required HashSet<string> ExecutedNodes { get; init; }
        public required List<BackEdge> TriggeredBackEdges { get; init; }
        public required HashSet<string> NextDirtyNodes { get; init; }

        /// <summary>
        /// True if the graph converged (all outputs unchanged) during this iteration.
        /// Only set when change-aware iteration with auto-convergence is enabled.
        /// </summary>
        public bool Converged { get; init; } = false;
    }

    #endregion

    public async Task<TContext> ResumeAsync(TContext context, CancellationToken cancellationToken = default)
    {
        context.Log("Orchestrator", $"Resuming graph execution: {context.Graph.Name}", LogLevel.Information);

        // Load latest checkpoint if available
        if (_checkpointStore != null && context is Context.GraphContext graphContext)
        {
            var checkpoint = await _checkpointStore.LoadLatestCheckpointAsync(graphContext.ExecutionId, cancellationToken);

            if (checkpoint != null)
            {
                context.Log("Orchestrator", $"Loaded checkpoint from {checkpoint.CreatedAt} with {checkpoint.CompletedNodes.Count} completed nodes", LogLevel.Information);

                // Validate node versions and restore state
                var restoredNodes = 0;
                var discardedNodes = 0;

                foreach (var completedNodeId in checkpoint.CompletedNodes)
                {
                    var currentNode = context.Graph.GetNode(completedNodeId);
                    if (currentNode == null)
                    {
                        context.Log("Orchestrator",
                            $"Node '{completedNodeId}' from checkpoint not found in current graph, skipping",
                            LogLevel.Warning);
                        continue;
                    }

                    // Version validation
                    if (checkpoint.NodeStateMetadata.TryGetValue(completedNodeId, out var savedState))
                    {
                        if (savedState.Version != currentNode.Version)
                        {
                            context.Log("Orchestrator",
                                $"Version mismatch for node '{completedNodeId}': saved={savedState.Version}, current={currentNode.Version}. " +
                                $"State will be discarded. Node will re-execute with current version.",
                                LogLevel.Warning);
                            discardedNodes++;
                            continue; // Don't mark as complete, force re-execution
                        }
                    }

                    // Version matches or no version info (old checkpoint) - mark complete
                    if (!graphContext.IsNodeComplete(completedNodeId))
                    {
                        graphContext.MarkNodeComplete(completedNodeId);
                    }
                    restoredNodes++;
                }

                // Restore node outputs from checkpoint (only for version-compatible nodes)
                foreach (var kvp in checkpoint.NodeOutputs)
                {
                    if (kvp.Key.StartsWith("node_output:"))
                    {
                        var nodeId = kvp.Key.Substring("node_output:".Length);

                        // Only restore if node is marked as complete (passed version check)
                        if (!graphContext.IsNodeComplete(nodeId))
                        {
                            continue; // Skip outputs from version-incompatible nodes
                        }
                    }

                    try
                    {
                        graphContext.Channels[kvp.Key].Set(kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        context.Log("Orchestrator", $"Failed to restore channel '{kvp.Key}': {ex.Message}", LogLevel.Warning);
                    }
                }

                context.Log("Orchestrator",
                    $"Restored {restoredNodes} node(s) from checkpoint. Discarded {discardedNodes} node(s) due to version mismatch.",
                    LogLevel.Information);
            }
        }

        context.Log("Orchestrator", $"Already completed: {context.CompletedNodes.Count} node(s)", LogLevel.Debug);

        // ResumeAsync uses the same logic as ExecuteAsync
        // Nodes that are already complete will be skipped
        return await ExecuteAsync(context, cancellationToken);
    }

    private async Task ExecuteLayerAsync(TContext context, ExecutionLayer layer, CancellationToken cancellationToken)
    {
        var layerStartTime = DateTimeOffset.UtcNow;

        // Get nodes to execute (filter out already-completed nodes and nodes with failed dependencies)
        var nodesToExecute = layer.NodeIds
            .Where(nodeId => !context.IsNodeComplete(nodeId))
            .Select(nodeId => context.Graph.GetNode(nodeId))
            .Where(node => node != null && (node.Type == NodeType.Handler || node.Type == NodeType.Router || node.Type == NodeType.SubGraph || node.Type == NodeType.Map))
            .Where(node => !ShouldSkipDueToFailedDependency(context, node!))
            .ToList();

        if (nodesToExecute.Count == 0)
        {
            context.Log("Orchestrator", $"Layer {layer.Level}: All nodes already complete or skipped, skipping", LogLevel.Debug);
            return;
        }

        // Emit layer started event
        context.EventCoordinator?.Emit(new Abstractions.Events.LayerExecutionStartedEvent
        {
            LayerIndex = layer.Level,
            NodeCount = nodesToExecute.Count,
            GraphContext = new Abstractions.Events.GraphExecutionContext
            {
                GraphId = context.Graph.Id,
                TotalNodes = context.Graph.Nodes.Count,
                CompletedNodes = context.CompletedNodes.Count,
                CurrentLayer = layer.Level
            },
            Kind = HPD.Events.EventKind.Lifecycle,
            Priority = HPD.Events.EventPriority.Normal
        });

        if (nodesToExecute.Count == 1)
        {
            // Single node - execute sequentially
            await ExecuteNodeAsync(context, nodesToExecute[0]!, cancellationToken);
        }
        else
        {
            // Multiple nodes - execute in parallel
            context.Log("Orchestrator", $"Layer {layer.Level}: Executing {nodesToExecute.Count} nodes in parallel", LogLevel.Debug);
            await ExecuteNodesInParallelAsync(context, nodesToExecute!, cancellationToken);
        }

        // Emit layer completed event
        context.EventCoordinator?.Emit(new Abstractions.Events.LayerExecutionCompletedEvent
        {
            LayerIndex = layer.Level,
            Duration = DateTimeOffset.UtcNow - layerStartTime,
            SuccessfulNodes = nodesToExecute.Count(n => context.IsNodeComplete(n!.Id)),
            GraphContext = new Abstractions.Events.GraphExecutionContext
            {
                GraphId = context.Graph.Id,
                TotalNodes = context.Graph.Nodes.Count,
                CompletedNodes = context.CompletedNodes.Count,
                CurrentLayer = layer.Level
            },
            Kind = HPD.Events.EventKind.Lifecycle,
            Priority = HPD.Events.EventPriority.Normal
        });

        // Clear ephemeral channels after layer execution (prevents state leakage in loops)
        ClearEphemeralChannels(context);
    }

    private async Task ExecuteNodesInParallelAsync(TContext context, List<Node> nodes, CancellationToken cancellationToken)
    {
        // Check if any nodes have MaxParallelExecutions set (parallelism limiting)
        var nodeParallelismLimits = new Dictionary<string, SemaphoreSlim>();
        foreach (var node in nodes)
        {
            if (node.MaxParallelExecutions.HasValue)
            {
                // Create semaphore with initial count = max parallel executions
                nodeParallelismLimits[node.Id] = new SemaphoreSlim(node.MaxParallelExecutions.Value, node.MaxParallelExecutions.Value);
            }
        }

        try
        {
            // Create isolated contexts for each node
            var tasks = nodes.Select(async node =>
            {
                // Apply parallelism limit if this node has one
                SemaphoreSlim? parallelismSemaphore = null;
                if (nodeParallelismLimits.TryGetValue(node.Id, out parallelismSemaphore))
                {
                    // Wait for available execution slot (throttling)
                    await parallelismSemaphore.WaitAsync(cancellationToken);
                }

                try
                {
                    var isolatedContext = (TContext)context.CreateIsolatedCopy();
                    await ExecuteNodeAsync(isolatedContext, node, cancellationToken);
                    return isolatedContext;
                }
                finally
                {
                    // Release execution slot after completion
                    parallelismSemaphore?.Release();
                }
            });

            // Execute all in parallel
            var isolatedContexts = await Task.WhenAll(tasks);

            // Merge results back into parent context
            foreach (var isolatedContext in isolatedContexts)
            {
                context.MergeFrom(isolatedContext);
            }
        }
        finally
        {
            // Dispose semaphores
            foreach (var semaphore in nodeParallelismLimits.Values)
            {
                semaphore.Dispose();
            }
        }
    }

    private async Task ExecuteNodeAsync(TContext context, Node node, CancellationToken cancellationToken)
    {
        // Check if already completed (idempotency)
        if (context.IsNodeComplete(node.Id))
        {
            context.Log("Orchestrator", $"Node {node.Id} already complete, skipping", LogLevel.Debug, nodeId: node.Id);
            return;
        }

        // Check max executions (loop/cycle protection)
        // Use per-node MaxExecutions if set, otherwise fall back to graph-level MaxIterations
        var maxExecutions = node.MaxExecutions ?? context.Graph.MaxIterations;
        var executionCount = context.GetNodeExecutionCount(node.Id);
        if (executionCount >= maxExecutions)
        {
            context.Log("Orchestrator",
                $"Node {node.Id} exceeded max executions ({maxExecutions})",
                LogLevel.Warning, nodeId: node.Id);
            return;
        }

        // NEW: Pre-execution condition check - skip if no incoming edge conditions are met
        var (shouldSkip, skipDetails) = EvaluateSkipConditions(context, node);
        if (shouldSkip)
        {
            context.Log("Orchestrator",
                $"Skipping {node.Id}: conditions not met. Evaluated edges: {skipDetails}",
                LogLevel.Information,
                nodeId: node.Id);

            HandleSkipped(context, node, new NodeExecutionResult.Skipped(
                Reason: SkipReason.ConditionNotMet,
                Message: $"All incoming edge conditions evaluated to false. {skipDetails}"
            ));

            // Record skip reason for checkpoint/resume
            context.Channels[$"skip_reason:{node.Id}"].Set(SkipReason.ConditionNotMet);
            return;
        }

        context.SetCurrentNode(node.Id);
        context.IncrementNodeExecutionCount(node.Id);

        // Mark as Running
        context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Running.ToString());

        context.Log("Orchestrator", $"Executing node: {node.Name} (ID: {node.Id}, Type: {node.Type})", LogLevel.Information, nodeId: node.Id);

        var nodeStartTime = DateTimeOffset.UtcNow;

        // Emit node started event
        context.EventCoordinator?.Emit(new Abstractions.Events.NodeExecutionStartedEvent
        {
            NodeId = node.Id,
            HandlerName = node.HandlerName ?? node.Type.ToString(),
            LayerIndex = context.CurrentLayerIndex,
            GraphContext = new Abstractions.Events.GraphExecutionContext
            {
                GraphId = context.Graph.Id,
                TotalNodes = context.Graph.Nodes.Count,
                CompletedNodes = context.CompletedNodes.Count,
                CurrentLayer = context.CurrentLayerIndex
            },
            Kind = HPD.Events.EventKind.Lifecycle,
            Priority = HPD.Events.EventPriority.Normal
        });

        try
        {
            // Handle Map nodes by iterating over collection
            if (node.Type == NodeType.Map)
            {
                await ExecuteMapAsync(context, node, cancellationToken);
                return;
            }

            // Handle SubGraph nodes by recursively executing the sub-graph
            if (node.Type == NodeType.SubGraph)
            {
                await ExecuteSubGraphAsync(context, node, cancellationToken);
                return;
            }

            // Get handler for Handler/Router nodes
            var handler = ResolveHandler(node);
            if (handler == null)
            {
                throw new InvalidOperationException($"No handler found for node '{node.Id}' with handler name '{node.HandlerName}'");
            }

            // Prepare inputs from upstream nodes
            var inputs = PrepareInputs(context, node);

            // Check cache if caching is enabled
            string? fingerprint = null;
            if (_cacheStore != null && _fingerprintCalculator != null)
            {
                // Compute fingerprint for this node
                var upstreamHashes = GetUpstreamFingerprints(context, node);
                var globalHash = context.Graph.Id + context.Graph.Version; // Simple global hash
                fingerprint = _fingerprintCalculator.Compute(node.Id, inputs, upstreamHashes, globalHash);

                // Store for downstream nodes
                _currentFingerprints[node.Id] = fingerprint;

                // Try to get from cache
                var cached = await _cacheStore.GetAsync(fingerprint, cancellationToken);
                if (cached != null)
                {
                    context.Log("Orchestrator", $"Cache HIT for node {node.Id} (fingerprint: {fingerprint[..8]}...)",
                        LogLevel.Debug, nodeId: node.Id);

                    // Create success result from cache
                    var cachedResult = new NodeExecutionResult.Success(
                        Outputs: cached.Outputs,
                        Duration: cached.Duration,
                        Metadata: new NodeExecutionMetadata
                        {
                            AttemptNumber = 1,
                            CustomMetrics = new Dictionary<string, object>
                            {
                                ["CacheHit"] = true,
                                ["Fingerprint"] = fingerprint
                            }
                        }
                    );

                    HandleSuccess(context, node, cachedResult);
                    return; // Skip execution entirely
                }

                context.Log("Orchestrator", $"Cache MISS for node {node.Id} (fingerprint: {fingerprint[..8]}...)",
                    LogLevel.Debug, nodeId: node.Id);
            }

            // Execute with timeout if specified
            NodeExecutionResult result;
            if (node.Timeout.HasValue)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(node.Timeout.Value);
                result = await handler.ExecuteAsync(context, inputs, cts.Token);
            }
            else
            {
                result = await handler.ExecuteAsync(context, inputs, cancellationToken);
            }

            // Handle result using pattern matching
            switch (result)
            {
                case NodeExecutionResult.Success success:
                    HandleSuccess(context, node, success);
                    break;

                case NodeExecutionResult.Failure failure:
                    await HandleFailureAsync(context, node, failure, cancellationToken);
                    break;

                case NodeExecutionResult.Skipped skipped:
                    HandleSkipped(context, node, skipped);
                    break;

                case NodeExecutionResult.Suspended suspended:
                    // New: async handling with potential continuation
                    var shouldContinue = await HandleSuspendedAsync(context, node, suspended, cancellationToken);
                    if (!shouldContinue)
                    {
                        // Halt execution - throw exception so caller knows graph is suspended
                        throw new GraphSuspendedException(node.Id, suspended.SuspendToken, suspended.Message);
                    }
                    // If shouldContinue is true, node is marked complete and execution continues to next node
                    break;

                case NodeExecutionResult.Cancelled cancelled:
                    HandleCancelled(context, node, cancelled);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown result type: {result.GetType().Name}");
            }
        }
        catch (OperationCanceledException)
        {
            context.Log("Orchestrator", $"Node {node.Id} cancelled", LogLevel.Warning, nodeId: node.Id);
            throw;
        }
        catch (Exception ex)
        {
            context.Log("Orchestrator", $"Node {node.Id} failed: {ex.Message}", LogLevel.Error, nodeId: node.Id, exception: ex);
            throw;
        }
        finally
        {
            context.SetCurrentNode(null);
        }
    }

    private IGraphNodeHandler<TContext>? ResolveHandler(Node node)
    {
        if (string.IsNullOrWhiteSpace(node.HandlerName))
        {
            return null;
        }

        // Get all registered handlers
        var handlers = _serviceProvider.GetServices<IGraphNodeHandler<TContext>>();
        return handlers.FirstOrDefault(h => h.HandlerName == node.HandlerName);
    }

    private Abstractions.Routing.IMapRouter? ResolveRouter(Node node)
    {
        if (string.IsNullOrWhiteSpace(node.MapRouterName))
        {
            return null;
        }

        // Get all registered routers from DI (same pattern as handlers)
        var routers = _serviceProvider.GetServices<Abstractions.Routing.IMapRouter>();
        return routers.FirstOrDefault(r => r.RouterName == node.MapRouterName);
    }

    private void ValidateMapNode(Node node)
    {
        // Validate mutually exclusive properties
        if (node.MapProcessorGraph != null && node.MapProcessorGraphs != null)
        {
            throw new InvalidOperationException(
                $"Map node '{node.Id}' cannot have both MapProcessorGraph and MapProcessorGraphs. " +
                "Use MapProcessorGraph for homogeneous mapping or MapProcessorGraphs for heterogeneous mapping.");
        }

        // Validate MapProcessorGraphs requires MapRouterName
        if (node.MapProcessorGraphs != null && string.IsNullOrWhiteSpace(node.MapRouterName))
        {
            throw new InvalidOperationException(
                $"Map node '{node.Id}' has MapProcessorGraphs but no MapRouterName. " +
                "MapRouterName is required for heterogeneous mapping.");
        }

        // Validate at least one processor is specified
        if (node.MapProcessorGraph == null &&
            node.MapProcessorGraphRef == null &&
            node.MapProcessorGraphs == null)
        {
            throw new InvalidOperationException(
                $"Map node '{node.Id}' must have MapProcessorGraph, MapProcessorGraphRef, or MapProcessorGraphs.");
        }
    }

    private Abstractions.Graph.Graph SelectProcessorGraph(Node node, object item, TContext context)
    {
        // Homogeneous map - same graph for all items
        if (node.MapProcessorGraph != null)
        {
            return node.MapProcessorGraph;
        }

        // Heterogeneous map - route using DI-resolved router (same as handlers!)
        if (node.MapProcessorGraphs != null && node.MapRouterName != null)
        {
            // Resolve router from DI (identical to handler resolution)
            var router = ResolveRouter(node);
            if (router == null)
            {
                throw new InvalidOperationException(
                    $"Map router '{node.MapRouterName}' not found. " +
                    "Ensure router is registered in DI (use services.AddGeneratedMapRouters()).");
            }

            var routingValue = router.Route(item);

            if (node.MapProcessorGraphs.TryGetValue(routingValue, out var routedGraph))
            {
                context.Log("Orchestrator",
                    $"Map node '{node.Id}' routed item to processor '{routingValue}'",
                    LogLevel.Debug, nodeId: node.Id);
                return routedGraph;
            }

            // No match - use default if available
            if (node.MapDefaultGraph != null)
            {
                context.Log("Orchestrator",
                    $"Map routing value '{routingValue}' not found, using default graph",
                    LogLevel.Debug, nodeId: node.Id);
                return node.MapDefaultGraph;
            }

            // No match and no default - throw error
            throw new InvalidOperationException(
                $"Map node '{node.Id}' routing value '{routingValue}' not found in MapProcessorGraphs and no MapDefaultGraph specified. " +
                $"Available keys: {string.Join(", ", node.MapProcessorGraphs.Keys)}");
        }

        throw new InvalidOperationException(
            $"Map node '{node.Id}' has no valid processor graph configuration.");
    }

    private HandlerInputs PrepareInputs(TContext context, Node node)
    {
        var inputs = new HandlerInputs();

        // Get incoming edges
        var incomingEdges = context.Graph.GetIncomingEdges(node.Id);

        // Group edges by source node for default edge handling
        var edgesBySource = incomingEdges.GroupBy(e => e.From);

        // Track which sources contributed to each non-prefixed key (for ambiguity warnings)
        var keySourceMap = new Dictionary<string, List<string>>();

        foreach (var sourceGroup in edgesBySource)
        {
            var sourceNodeId = sourceGroup.Key;
            var sourceNode = context.Graph.GetNode(sourceNodeId);

            if (sourceNode != null && context.IsNodeComplete(sourceNodeId))
            {
                // Get outputs from the channel
                var channelName = $"node_output:{sourceNodeId}";
                if (context.Channels.Contains(channelName))
                {
                    var outputs = context.Channels[channelName].Get<Dictionary<string, object>>();
                    if (outputs != null)
                    {
                        // Two-pass evaluation for default edges
                        var regularEdges = sourceGroup.Where(e => e.Condition?.Type != ConditionType.Default).ToList();
                        var defaultEdge = sourceGroup.FirstOrDefault(e => e.Condition?.Type == ConditionType.Default);
                        bool anyRegularEdgeMatched = false;

                        // Pass 1: Evaluate regular conditions
                        foreach (var edge in regularEdges)
                        {
                            if (ConditionEvaluator.Evaluate(edge.Condition, outputs, context, edge))
                            {
                                // Condition met - include these outputs with namespace
                                AddOutputsWithNamespace(inputs, sourceNodeId, outputs, keySourceMap);
                                anyRegularEdgeMatched = true;
                                break; // Only use outputs from first matching edge per source
                            }
                            else
                            {
                                // Condition not met - skip this edge
                                context.Log("Orchestrator",
                                    $"Edge condition not met: {edge.From} -> {edge.To} ({edge.Condition?.GetDescription() ?? "N/A"})",
                                    LogLevel.Debug, nodeId: node.Id);
                            }
                        }

                        // Pass 2: If no regular edges matched, try default edge
                        if (!anyRegularEdgeMatched && defaultEdge != null)
                        {
                            context.Log("Orchestrator",
                                $"Using default edge: {defaultEdge.From} -> {defaultEdge.To}",
                                LogLevel.Debug, nodeId: node.Id);

                            // Include outputs from default edge with namespace
                            AddOutputsWithNamespace(inputs, sourceNodeId, outputs, keySourceMap);
                        }
                    }
                }
            }
        }

        // Log warnings for ambiguous non-prefixed keys (multiple sources for same key)
        foreach (var kvp in keySourceMap.Where(k => k.Value.Count > 1))
        {
            context.Log("Orchestrator",
                $"Warning: Key '{kvp.Key}' has multiple sources: [{string.Join(", ", kvp.Value)}]. " +
                $"Using value from '{kvp.Value.First()}'. Consider using namespaced access: '{kvp.Value.First()}.{kvp.Key}'",
                LogLevel.Warning,
                nodeId: node.Id);
        }

        // Include SharedData if available (prefixed with "shared.")
        if (context.SharedData != null)
        {
            foreach (var kvp in context.SharedData)
            {
                // Add with "shared." prefix for clear namespacing
                inputs.Add($"shared.{kvp.Key}", kvp.Value);

                // Also add without prefix if not already present (convenience)
                if (!inputs.Contains(kvp.Key))
                {
                    inputs.Add(kvp.Key, kvp.Value);
                }
            }
        }

        return inputs;
    }

    /// <summary>
    /// Adds outputs to inputs with namespace prefix for source attribution.
    /// Also adds non-prefixed keys for backward compatibility (first source wins).
    /// </summary>
    private void AddOutputsWithNamespace(
        HandlerInputs inputs,
        string sourceNodeId,
        Dictionary<string, object> outputs,
        Dictionary<string, List<string>> keySourceMap)
    {
        foreach (var kvp in outputs)
        {
            // Always add with namespace prefix (e.g., "solver1.answer")
            inputs.Add($"{sourceNodeId}.{kvp.Key}", kvp.Value);

            // Track sources for non-prefixed key (for ambiguity detection)
            if (!keySourceMap.TryGetValue(kvp.Key, out var sources))
            {
                sources = new List<string>();
                keySourceMap[kvp.Key] = sources;
            }
            sources.Add(sourceNodeId);

            // Add non-prefixed for backward compatibility (first source wins)
            if (!inputs.Contains(kvp.Key))
            {
                inputs.Add(kvp.Key, kvp.Value);
            }
        }
    }

    private void HandleSuccess(TContext context, Node node, NodeExecutionResult.Success success)
    {
        var isCacheHit = success.Metadata?.CustomMetrics?.ContainsKey("CacheHit") == true;

        context.Log("Orchestrator",
            $"Node {node.Id} completed successfully in {success.Duration.TotalMilliseconds:F2}ms{(isCacheHit ? " (from cache)" : "")}",
            LogLevel.Information, nodeId: node.Id);

        // Store outputs in channel for downstream nodes
        var channelName = $"node_output:{node.Id}";
        context.Channels[channelName].Set(success.Outputs);

        // Store result for upstream condition evaluation
        context.Channels[$"node_result:{node.Id}"].Set(success);

        // Track output hash for change-aware iteration (always track, even if not enabled)
        // This ensures hashes are available if the feature is enabled mid-execution
        var options = context.Graph.IterationOptions;
        var excludeFields = options?.IgnoreFieldsForChangeDetection;
        var algorithm = options?.HashAlgorithm ?? Abstractions.Graph.OutputHashAlgorithm.XxHash64;
        var outputHash = ComputeOutputHash(success.Outputs, excludeFields, algorithm);
        _outputHashes[node.Id] = outputHash;

        // Cache the result if caching is enabled and this wasn't a cache hit
        if (!isCacheHit && _cacheStore != null && _currentFingerprints.TryGetValue(node.Id, out var fingerprint))
        {
            var cachedResult = new CachedNodeResult
            {
                Outputs = success.Outputs,
                CachedAt = DateTimeOffset.UtcNow,
                Duration = success.Duration,
                Metadata = success.Metadata?.CustomMetrics
            };

            // Fire and forget - don't await (caching shouldn't slow down execution)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _cacheStore.SetAsync(fingerprint, cachedResult);
                }
                catch (Exception ex)
                {
                    context.Log("Orchestrator", $"Failed to cache result for node {node.Id}: {ex.Message}",
                        LogLevel.Warning, nodeId: node.Id);
                }
            });
        }

        // Mark node as complete
        context.MarkNodeComplete(node.Id);

        // Update state to Succeeded
        context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Succeeded.ToString());

        // Emit node completed event
        context.EventCoordinator?.Emit(new Abstractions.Events.NodeExecutionCompletedEvent
        {
            NodeId = node.Id,
            HandlerName = node.HandlerName ?? node.Type.ToString(),
            LayerIndex = context.CurrentLayerIndex,
            Progress = (float)context.CompletedNodes.Count / context.Graph.Nodes.Count,
            Outputs = success.Outputs, // Include outputs for downstream consumers
            Duration = success.Duration,
            Result = success,
            GraphContext = new Abstractions.Events.GraphExecutionContext
            {
                GraphId = context.Graph.Id,
                TotalNodes = context.Graph.Nodes.Count,
                CompletedNodes = context.CompletedNodes.Count,
                CurrentLayer = context.CurrentLayerIndex
            },
            Kind = HPD.Events.EventKind.Lifecycle,
            Priority = HPD.Events.EventPriority.Normal
        });
    }

    private async Task HandleFailureAsync(TContext context, Node node, NodeExecutionResult.Failure failure, CancellationToken cancellationToken)
    {
        context.Log("Orchestrator",
            $"Node {node.Id} failed: {failure.Exception.Message} (Severity: {failure.Severity})",
            LogLevel.Error, nodeId: node.Id, exception: failure.Exception);

        // Check if we should retry
        if (node.RetryPolicy != null && failure.IsTransient && node.RetryPolicy.ShouldRetry(failure.Exception))
        {
            var executionCount = context.GetNodeExecutionCount(node.Id);
            if (executionCount < node.RetryPolicy.MaxAttempts)
            {
                var delay = node.RetryPolicy.GetDelay(executionCount);
                context.Log("Orchestrator",
                    $"Retrying node {node.Id} after {delay.TotalMilliseconds}ms (attempt {executionCount + 1}/{node.RetryPolicy.MaxAttempts})",
                    LogLevel.Warning, nodeId: node.Id);

                await Task.Delay(delay, cancellationToken);
                await ExecuteNodeAsync(context, node, cancellationToken);
                return;
            }
        }

        // Apply error propagation policy
        var policy = node.ErrorPolicy ?? ErrorPropagationPolicy.StopGraph(); // Default: StopGraph

        // Check if error should propagate based on custom predicate
        if (policy.ShouldPropagate != null && !policy.ShouldPropagate(failure.Exception))
        {
            context.Log("Orchestrator",
                $"Error in node {node.Id} filtered by ShouldPropagate predicate - not propagating",
                LogLevel.Warning, nodeId: node.Id);
            return; // Don't propagate this error
        }

        switch (policy.Mode)
        {
            case PropagationMode.StopGraph:
                // Immediately stop entire graph execution
                context.Log("Orchestrator",
                    $"Stopping graph execution due to failure in node {node.Id} (Mode: StopGraph)",
                    LogLevel.Error, nodeId: node.Id);
                throw new GraphExecutionException($"Node {node.Id} failed: {failure.Exception.Message}", failure.Exception);

            case PropagationMode.SkipDependents:
                // Mark this node as failed, downstream dependents will be skipped
                _failedNodes.TryAdd(node.Id, failure.Exception);
                context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Failed.ToString());
                context.Log("Orchestrator",
                    $"Node {node.Id} failed - downstream dependents will be skipped (Mode: SkipDependents)",
                    LogLevel.Warning, nodeId: node.Id);
                break;

            case PropagationMode.ExecuteFallback:
                // Execute fallback node
                if (string.IsNullOrEmpty(policy.FallbackNodeId))
                {
                    throw new InvalidOperationException($"Node {node.Id} has ExecuteFallback mode but no FallbackNodeId specified");
                }

                var fallbackNode = context.Graph.GetNode(policy.FallbackNodeId);
                if (fallbackNode == null)
                {
                    throw new InvalidOperationException($"Fallback node {policy.FallbackNodeId} not found for node {node.Id}");
                }

                context.Log("Orchestrator",
                    $"Node {node.Id} failed - executing fallback node {fallbackNode.Id} (Mode: ExecuteFallback)",
                    LogLevel.Warning, nodeId: node.Id);

                // Store error context for fallback node
                context.Channels[$"error_context:{node.Id}"].Set(new Dictionary<string, object>
                {
                    ["original_node_id"] = node.Id,
                    ["exception"] = failure.Exception,
                    ["severity"] = failure.Severity.ToString()
                });

                await ExecuteNodeAsync(context, fallbackNode, cancellationToken);
                break;

            case PropagationMode.Isolate:
                // Isolate error - don't affect downstream nodes
                // Note: Still mark as Failed for observability, just don't propagate
                _failedNodes.TryAdd(node.Id, failure.Exception);
                context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Failed.ToString());
                context.Log("Orchestrator",
                    $"Node {node.Id} failed but error is isolated - downstream nodes will continue (Mode: Isolate)",
                    LogLevel.Warning, nodeId: node.Id);
                // Store result for upstream condition evaluation
                context.Channels[$"node_result:{node.Id}"].Set(failure);
                // Mark as complete so downstream nodes can execute
                context.MarkNodeComplete(node.Id);
                break;

            case PropagationMode.DelegateToHandler:
                // Execute error handler node
                if (string.IsNullOrEmpty(policy.ErrorHandlerNodeId))
                {
                    throw new InvalidOperationException($"Node {node.Id} has DelegateToHandler mode but no ErrorHandlerNodeId specified");
                }

                var handlerNode = context.Graph.GetNode(policy.ErrorHandlerNodeId);
                if (handlerNode == null)
                {
                    throw new InvalidOperationException($"Error handler node {policy.ErrorHandlerNodeId} not found for node {node.Id}");
                }

                context.Log("Orchestrator",
                    $"Node {node.Id} failed - delegating to error handler {handlerNode.Id} (Mode: DelegateToHandler)",
                    LogLevel.Warning, nodeId: node.Id);

                // Store error details for handler
                context.Channels[$"error_details:{node.Id}"].Set(new Dictionary<string, object>
                {
                    ["original_node_id"] = node.Id,
                    ["exception"] = failure.Exception,
                    ["exception_type"] = failure.Exception.GetType().Name,
                    ["exception_message"] = failure.Exception.Message,
                    ["severity"] = failure.Severity.ToString(),
                    ["is_transient"] = failure.IsTransient
                });

                await ExecuteNodeAsync(context, handlerNode, cancellationToken);

                // Handler result determines what happens next
                // If handler returns Success, continue; if Failure, stop; if Suspended, pause
                // Handler already executed, result is in context
                break;

            default:
                throw new NotSupportedException($"Propagation mode {policy.Mode} is not supported");
        }
    }

    private void HandleSkipped(TContext context, Node node, NodeExecutionResult.Skipped skipped)
    {
        // Update state to Skipped
        context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Skipped.ToString());

        context.Log("Orchestrator",
            $"Node {node.Id} skipped: {skipped.Reason} - {skipped.Message}",
            LogLevel.Information, nodeId: node.Id);

        // Store result for upstream condition evaluation
        context.Channels[$"node_result:{node.Id}"].Set(skipped);

        // Mark node as complete so it doesn't block downstream execution
        context.MarkNodeComplete(node.Id);

        // Emit node skipped event
        context.EventCoordinator?.Emit(new Abstractions.Events.NodeExecutionCompletedEvent
        {
            NodeId = node.Id,
            HandlerName = node.HandlerName ?? node.Type.ToString(),
            LayerIndex = context.CurrentLayerIndex,
            Progress = (float)context.CompletedNodes.Count / context.Graph.Nodes.Count,
            Duration = TimeSpan.Zero,
            Result = skipped,
            GraphContext = new Abstractions.Events.GraphExecutionContext
            {
                GraphId = context.Graph.Id,
                TotalNodes = context.Graph.Nodes.Count,
                CompletedNodes = context.CompletedNodes.Count,
                CurrentLayer = context.CurrentLayerIndex
            },
            Kind = HPD.Events.EventKind.Lifecycle,
            Priority = HPD.Events.EventPriority.Normal
        });
    }

    /// <summary>
    /// Handles node suspension with layered suspension pattern:
    /// 1. Durability: Save checkpoint (if store available)
    /// 2. Reactivity: Emit bidirectional event (if coordinator available)
    /// 3. Waiting: Await response with timeout
    /// 4. Fallback: On timeout/denial, return false to halt cleanly
    /// </summary>
    /// <returns>True if approved and should continue, false if should halt</returns>
    private async Task<bool> HandleSuspendedAsync(
        TContext context,
        Node node,
        NodeExecutionResult.Suspended suspended,
        CancellationToken ct)
    {
        // Check if sensor polling pattern
        if (suspended.Reason == Abstractions.Execution.SuspendReason.PollingCondition ||
            suspended.Reason == Abstractions.Execution.SuspendReason.ResourceWait)
        {
            // Update state to Polling
            context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Polling.ToString());
            return await HandleSensorPollingAsync(context, node, suspended, ct);
        }

        // HITL suspension or external task wait
        context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Suspended.ToString());

        var options = node.SuspensionOptions ?? _defaultSuspensionOptions;

        context.Log("Orchestrator",
            $"Node {node.Id} suspended for external input: {suspended.Message}",
            LogLevel.Information, nodeId: node.Id);

        // Store suspension info in context (always, for resume capability)
        context.AddTag("suspended_nodes", node.Id);
        context.AddTag($"suspend_token:{node.Id}", suspended.SuspendToken);
        context.AddTag($"suspend_outcome:{node.Id}", Abstractions.Execution.SuspensionOutcome.Pending.ToString());

        if (suspended.ResumeValue != null)
        {
            context.Channels[$"suspend_resume:{node.Id}"].Set(suspended.ResumeValue);
        }

        // LAYER 1: Durability - Save checkpoint first (if store available)
        if (options.SaveCheckpointFirst && _checkpointStore != null)
        {
            try
            {
                await SaveSuspensionCheckpointAsync(context, node, suspended, ct);
                context.Log("Orchestrator",
                    $"Checkpoint saved for suspended node {node.Id}",
                    LogLevel.Information, nodeId: node.Id);
            }
            catch (Exception ex)
            {
                // Log but don't fail - suspension should still work
                context.Log("Orchestrator",
                    $"Failed to save checkpoint for suspended node {node.Id}: {ex.Message}",
                    LogLevel.Warning, nodeId: node.Id, exception: ex);
            }
        }
        else if (options.SaveCheckpointFirst && _checkpointStore == null)
        {
            context.Log("Orchestrator",
                $"No checkpoint store configured; skipping checkpoint for suspended node {node.Id}",
                LogLevel.Debug, nodeId: node.Id);
        }

        // LAYER 2 & 3: Reactivity and Waiting (if coordinator available)
        if (options.EmitEvents && context.EventCoordinator != null)
        {
            // REUSE EXISTING EVENT TYPE - map SuspendToken to RequestId
            var requestEvent = new Abstractions.Events.NodeApprovalRequestEvent
            {
                RequestId = suspended.SuspendToken,
                SourceName = $"GraphOrchestrator:{context.ExecutionId}",
                NodeId = node.Id,
                Message = suspended.Message ?? $"Node {node.Id} requires approval",
                Description = $"Suspended at {DateTimeOffset.UtcNow:O}",
                Metadata = suspended.ResumeValue != null
                    ? new Dictionary<string, object?> { ["ResumeValue"] = suspended.ResumeValue }
                    : null
            };

            if (options.ActiveWaitTimeout > TimeSpan.Zero)
            {
                // CRITICAL: Register waiter BEFORE emitting to avoid race condition
                // WaitForResponseAsync registers immediately when called, not when awaited
                var waitTask = context.EventCoordinator.WaitForResponseAsync<Abstractions.Events.NodeApprovalResponseEvent>(
                    suspended.SuspendToken,
                    options.ActiveWaitTimeout,
                    ct
                );

                // Now emit the event (waiter is already registered)
                context.EventCoordinator.Emit(requestEvent);

                try
                {
                    var response = await waitTask;

                    if (response.Approved)
                    {
                        context.Log("Orchestrator",
                            $"Node {node.Id} suspension approved",
                            LogLevel.Information, nodeId: node.Id);

                        // Update outcome
                        context.AddTag($"suspend_outcome:{node.Id}", Abstractions.Execution.SuspensionOutcome.Approved.ToString());

                        // Store resume data if provided
                        if (response.ResumeData != null)
                        {
                            context.Channels[$"suspend_response:{node.Id}"].Set(response.ResumeData);
                        }

                        // Clear suspension token (use empty string since RemoveTag may not exist)
                        context.AddTag($"suspend_token:{node.Id}", "");

                        // Mark node complete so execution continues to NEXT node
                        context.MarkNodeComplete(node.Id);

                        return true; // Continue to next node
                    }
                    else
                    {
                        context.Log("Orchestrator",
                            $"Node {node.Id} suspension denied: {response.Reason}",
                            LogLevel.Warning, nodeId: node.Id);

                        // Update outcome
                        context.AddTag($"suspend_outcome:{node.Id}", Abstractions.Execution.SuspensionOutcome.Denied.ToString());

                        return false; // Halt execution
                    }
                }
                catch (TimeoutException)
                {
                    context.Log("Orchestrator",
                        $"Node {node.Id} suspension timed out after {options.ActiveWaitTimeout}",
                        LogLevel.Information, nodeId: node.Id);

                    // Update outcome
                    context.AddTag($"suspend_outcome:{node.Id}", Abstractions.Execution.SuspensionOutcome.TimedOut.ToString());

                    // Emit timeout event for observability
                    context.EventCoordinator.Emit(new Abstractions.Events.NodeApprovalTimeoutEvent
                    {
                        RequestId = suspended.SuspendToken,
                        SourceName = $"GraphOrchestrator:{context.ExecutionId}",
                        NodeId = node.Id,
                        WaitedFor = options.ActiveWaitTimeout
                    });

                    // LAYER 4: Fallback - checkpoint already saved, halt cleanly
                    return false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // External cancellation - different from timeout
                    context.Log("Orchestrator",
                        $"Node {node.Id} suspension wait cancelled",
                        LogLevel.Information, nodeId: node.Id);

                    // Keep as Pending - can be resumed
                    throw; // Re-throw cancellation
                }
            }
            else
            {
                // No active wait - just emit and halt immediately
                context.EventCoordinator.Emit(requestEvent);
            }
        }
        else if (options.EmitEvents && context.EventCoordinator == null)
        {
            context.Log("Orchestrator",
                $"No EventCoordinator available; cannot emit suspension event for node {node.Id}",
                LogLevel.Warning, nodeId: node.Id);
        }

        // Halt execution
        return false;
    }

    /// <summary>
    /// Handles sensor polling with iterative active waiting (stack-safe, no recursion).
    /// Retries in-place until condition met or timeout.
    /// </summary>
    private async Task<bool> HandleSensorPollingAsync(
        TContext context,
        Node node,
        NodeExecutionResult.Suspended suspended,
        CancellationToken ct)
    {
        // Try to restore polling state from previous checkpoint
        var pollingState = TryRestorePollingState(context, node.Id);

        var startTime = pollingState?.StartTime ?? DateTimeOffset.UtcNow;
        var currentAttempt = pollingState?.AttemptNumber ?? 0;
        var maxWaitTime = suspended.MaxWaitTime ?? TimeSpan.FromHours(1);
        var maxRetries = suspended.MaxRetries ?? int.MaxValue;

        context.Log("Orchestrator",
            $"Node {node.Id} polling (attempt {currentAttempt}, retry after {suspended.RetryAfter})",
            LogLevel.Debug, nodeId: node.Id);

        // Emit initial polling event
        context.EventCoordinator?.Emit(new Abstractions.Events.NodePollingEvent
        {
            NodeId = node.Id,
            ExecutionId = context.ExecutionId,
            SuspendToken = suspended.SuspendToken,
            AttemptNumber = currentAttempt,
            RetryAfter = suspended.RetryAfter!.Value,
            MaxWaitTime = maxWaitTime
        });

        // ITERATIVE POLLING LOOP (stack-safe)
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

            // Update polling state in context tags (for checkpoint resume)
            var currentPollingState = new HPDAgent.Graph.Abstractions.Serialization.PollingState
            {
                StartTime = startTime,
                AttemptNumber = currentAttempt,
                SuspendToken = suspended.SuspendToken,
                RetryAfter = suspended.RetryAfter!.Value,
                MaxWaitTime = maxWaitTime
            };
            context.AddTag($"polling_info:{node.Id}", System.Text.Json.JsonSerializer.Serialize(
                currentPollingState,
                HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.PollingState
            ));

            // CHECKPOINT FIRST (durability)
            if (_checkpointStore != null && context is Context.GraphContext ctxGraph)
            {
                try
                {
                    await SaveCheckpointAsync(ctxGraph, context.CurrentLayerIndex,
                        Abstractions.Checkpointing.CheckpointTrigger.Suspension, ct);
                }
                catch (Exception ex)
                {
                    context.Log("Orchestrator",
                        $"Failed to save checkpoint during polling for node {node.Id}: {ex.Message}",
                        LogLevel.Warning, nodeId: node.Id, exception: ex);
                }
            }

            // Check timeout (important after resume or after delays)
            var elapsed = DateTimeOffset.UtcNow - startTime;
            if (elapsed >= maxWaitTime)
            {
                context.Log("Orchestrator",
                    $"Node {node.Id} polling timed out after {elapsed}",
                    LogLevel.Warning, nodeId: node.Id);

                // Clear polling state
                context.RemoveTag($"polling_info:{node.Id}");

                // Create failure result and handle it
                var timeoutFailure = new NodeExecutionResult.Failure(
                    Exception: new TimeoutException($"Sensor polling timeout after {elapsed}"),
                    Severity: Abstractions.Execution.ErrorSeverity.Fatal,
                    IsTransient: false,
                    Duration: elapsed
                );

                // Emit timeout event
                context.EventCoordinator?.Emit(new Abstractions.Events.NodePollingTimeoutEvent
                {
                    NodeId = node.Id,
                    ExecutionId = context.ExecutionId,
                    SuspendToken = suspended.SuspendToken,
                    Elapsed = elapsed
                });

                // Clear polling state
                context.RemoveTag($"polling_info:{node.Id}");

                // Store result BEFORE calling HandleFailureAsync (in case it throws)
                context.Channels[$"node_result:{node.Id}"].Set(timeoutFailure);

                // Handle failure through normal error propagation system
                // This applies the node's error policy (Isolate, StopGraph, etc.)
                await HandleFailureAsync(context, node, timeoutFailure, ct);

                return true; // Continue execution if error policy allows it
            }

            // Wait before retry (always wait in polling loop since we already executed once to get here)
            await Task.Delay(suspended.RetryAfter!.Value, ct);

            // Increment attempt count BEFORE executing (so we can check if we've exceeded maxRetries)
            currentAttempt++;

            // Check max retries BEFORE executing the next attempt
            // currentAttempt now represents the NEXT execution number (including the initial one)
            // So if maxRetries=3, we allow attempts 0, 1, 2 (3 total calls)
            // currentAttempt=3 means we're about to do the 4th call, which exceeds maxRetries
            if (currentAttempt >= maxRetries)
            {
                context.Log("Orchestrator",
                    $"Node {node.Id} exceeded max retries ({maxRetries})",
                    LogLevel.Warning, nodeId: node.Id);

                // Clear polling state
                context.RemoveTag($"polling_info:{node.Id}");

                // Create failure result and handle it
                var maxRetriesFailure = new NodeExecutionResult.Failure(
                    Exception: new InvalidOperationException($"Max polling retries exceeded ({currentAttempt})"),
                    Severity: Abstractions.Execution.ErrorSeverity.Fatal,
                    IsTransient: false,
                    Duration: TimeSpan.Zero
                );

                // Emit max retries event
                context.EventCoordinator?.Emit(new Abstractions.Events.NodePollingMaxRetriesEvent
                {
                    NodeId = node.Id,
                    ExecutionId = context.ExecutionId,
                    SuspendToken = suspended.SuspendToken,
                    Attempts = currentAttempt
                });

                // Store result BEFORE calling HandleFailureAsync (in case it throws)
                context.Channels[$"node_result:{node.Id}"].Set(maxRetriesFailure);

                // Handle failure through normal error propagation system
                // This applies the node's error policy (Isolate, StopGraph, etc.)
                await HandleFailureAsync(context, node, maxRetriesFailure, ct);

                return true; // Continue execution if error policy allows it
            }

            // Re-execute node (polling check)
            context.Log("Orchestrator",
                $"Node {node.Id} retrying polling check (attempt {currentAttempt})",
                LogLevel.Debug, nodeId: node.Id);

            // State back to Running for retry
            context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Running.ToString());

            // Re-execute handler directly
            var handler = ResolveHandler(node);
            if (handler == null)
            {
                throw new InvalidOperationException($"No handler found for node '{node.Id}' with handler name '{node.HandlerName}'");
            }

            var inputs = PrepareInputs(context, node);
            var retryResult = await handler.ExecuteAsync(context, inputs, ct);

            // Check result - loop continues if still suspended, exits otherwise
            if (retryResult is NodeExecutionResult.Suspended retrySuspended &&
                (retrySuspended.Reason == Abstractions.Execution.SuspendReason.PollingCondition ||
                 retrySuspended.Reason == Abstractions.Execution.SuspendReason.ResourceWait))
            {
                // Still suspended - continue loop
                continue;
            }

            // Not suspended anymore (success, failure, or different suspension type)
            // Clear polling state BEFORE handling result (ensures cleanup happens before any checkpoint)
            context.RemoveTag($"polling_info:{node.Id}");

            // Handle the final result
            return await HandleNodeResultAsync(context, node, retryResult, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean up polling state on cancellation
            context.RemoveTag($"polling_info:{node.Id}");

            // Create cancellation result
            var cancelResult = new NodeExecutionResult.Cancelled(
                Reason: Abstractions.Execution.CancellationReason.UserRequested,
                Message: "Polling cancelled by user"
            );

            // Handle cancellation
            HandleCancelled(context, node, cancelResult);

            // Re-throw to propagate cancellation
            throw;
        }
    }

    /// <summary>
    /// Try to restore polling state from checkpoint tags.
    /// Returns null if no polling state found.
    /// </summary>
    private Abstractions.Serialization.PollingState? TryRestorePollingState(TContext context, string nodeId)
    {
        var tagKey = $"polling_info:{nodeId}";
        if (context.Tags.TryGetValue(tagKey, out var values) && values.Count > 0)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize(
                    values.First(),
                    HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.PollingState);
            }
            catch (System.Text.Json.JsonException ex)
            {
                context.Log("Orchestrator",
                    $"Failed to deserialize polling state for node {nodeId}: {ex.Message}",
                    LogLevel.Warning, nodeId: nodeId, exception: ex);
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Wait for all polling nodes to reach terminal state before iteration can complete.
    /// This prevents premature back-edge evaluation while nodes are still polling.
    /// Uses intelligent delay to avoid busy-waiting.
    /// </summary>
    private async Task WaitForPollingNodesToResolveAsync(TContext context, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Get all nodes currently in polling state
            var pollingNodes = context.GetNodesInState(NodeState.Polling);

            if (pollingNodes.Count == 0)
            {
                // No nodes polling - iteration can complete
                return;
            }

            context.Log("Orchestrator",
                $"Iteration waiting for {pollingNodes.Count} polling node(s) to resolve: {string.Join(", ", pollingNodes)}",
                LogLevel.Debug);

            // Intelligent delay: Calculate next retry time across all polling nodes
            var nextRetryTime = GetEarliestPollingNodeRetryTime(context, pollingNodes);
            var delay = nextRetryTime - DateTimeOffset.UtcNow;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
            else
            {
                // Small delay to avoid tight loop
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }

            // NOTE: Polling nodes will transition to terminal states via HandleSensorPollingAsync
            // (running in parallel). This loop just waits for those transitions.
        }
    }

    /// <summary>
    /// Get earliest retry time across all polling nodes (intelligent delay optimization).
    /// </summary>
    private DateTimeOffset GetEarliestPollingNodeRetryTime(TContext context, IReadOnlyList<string> pollingNodeIds)
    {
        var earliestRetryTime = DateTimeOffset.MaxValue;

        foreach (var nodeId in pollingNodeIds)
        {
            // Try to get polling info from tags
            var tagKey = $"polling_info:{nodeId}";
            if (context.Tags.TryGetValue(tagKey, out var values) && values.Count > 0)
            {
                try
                {
                    var pollingState = System.Text.Json.JsonSerializer.Deserialize(
                        values.First(),
                        HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.PollingState);
                    if (pollingState != null)
                    {
                        // Calculate next retry time for this node
                        // Note: We don't have LastAttempt in PollingState, so use conservative estimate
                        var nextRetry = DateTimeOffset.UtcNow + pollingState.RetryAfter;
                        if (nextRetry < earliestRetryTime)
                        {
                            earliestRetryTime = nextRetry;
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Failed to deserialize - ignore this node
                }
            }
        }

        // If no valid polling states found, return current time (will trigger small delay)
        return earliestRetryTime == DateTimeOffset.MaxValue
            ? DateTimeOffset.UtcNow
            : earliestRetryTime;
    }

    /// <summary>
    /// Handle node result after polling completes or fails.
    /// </summary>
    private async Task<bool> HandleNodeResultAsync(
        TContext context,
        Node node,
        NodeExecutionResult result,
        CancellationToken ct)
    {
        switch (result)
        {
            case NodeExecutionResult.Success success:
                HandleSuccess(context, node, success);
                return true; // Continue to next node

            case NodeExecutionResult.Failure failure:
                await HandleFailureAsync(context, node, failure, ct);
                return false; // Halt execution

            case NodeExecutionResult.Skipped skipped:
                HandleSkipped(context, node, skipped);
                return false; // Halt execution

            case NodeExecutionResult.Suspended s:
                // Different type of suspension (e.g., HITL)
                return await HandleSuspendedAsync(context, node, s, ct);

            case NodeExecutionResult.Cancelled cancelled:
                HandleCancelled(context, node, cancelled);
                return false; // Halt execution

            default:
                context.MarkNodeComplete(node.Id);
                return true; // Continue for other result types
        }
    }

    /// <summary>
    /// Polling state stored in context tags for checkpoint resume.
    /// </summary>

    /// <summary>
    /// Saves checkpoint specifically for suspension with suspension-related metadata.
    /// </summary>
    private async Task SaveSuspensionCheckpointAsync(
        TContext context,
        Node node,
        NodeExecutionResult.Suspended suspended,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore == null || context is not Context.GraphContext graphContext)
            return;

        // Extract all node outputs from channels for checkpoint restoration
        var nodeOutputs = new Dictionary<string, object>();
        var nodeStateMetadata = new Dictionary<string, Abstractions.Checkpointing.NodeStateMetadata>();

        foreach (var channelName in context.Channels.ChannelNames)
        {
            if (channelName.StartsWith("node_output:"))
            {
                var nodeId = channelName.Substring("node_output:".Length);

                // Only include outputs from completed nodes
                if (context.CompletedNodes.Contains(nodeId))
                {
                    try
                    {
                        var output = context.Channels[channelName].Get<object>();
                        if (output != null)
                        {
                            nodeOutputs[channelName] = output;

                            // Capture node version for compatibility checking
                            var completedNode = context.Graph.GetNode(nodeId);
                            if (completedNode != null)
                            {
                                nodeStateMetadata[nodeId] = new Abstractions.Checkpointing.NodeStateMetadata
                                {
                                    NodeId = nodeId,
                                    Version = completedNode.Version,
                                    StateJson = System.Text.Json.JsonSerializer.Serialize(
                                        output,
                                        HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.DictionaryStringObject
                                    ),
                                    CapturedAt = DateTimeOffset.UtcNow
                                };
                            }
                        }
                    }
                    catch
                    {
                        // Skip channels that can't be retrieved as object
                    }
                }
            }
        }

        var checkpoint = new Abstractions.Checkpointing.GraphCheckpoint
        {
            CheckpointId = Guid.NewGuid().ToString("N"),
            ExecutionId = context.ExecutionId,
            GraphId = context.Graph.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedNodes = context.CompletedNodes,
            NodeOutputs = nodeOutputs,
            NodeStateMetadata = nodeStateMetadata,
            ContextJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ExecutionId = context.ExecutionId,
                CompletedNodes = context.CompletedNodes,
                CurrentLayerIndex = graphContext.CurrentLayerIndex,
                SuspendedNodeId = node.Id,
                SuspendToken = suspended.SuspendToken
            }),
            Metadata = new Abstractions.Checkpointing.CheckpointMetadata
            {
                Trigger = Abstractions.Checkpointing.CheckpointTrigger.Suspension,
                CompletedLayer = graphContext.CurrentLayerIndex,
                SuspendedNodeId = node.Id,
                SuspendToken = suspended.SuspendToken,
                SuspensionOutcome = Abstractions.Execution.SuspensionOutcome.Pending
            }
        };

        await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);
    }

    private void HandleCancelled(TContext context, Node node, NodeExecutionResult.Cancelled cancelled)
    {
        // Update state to Cancelled
        context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Cancelled.ToString());

        context.Log("Orchestrator",
            $"Node {node.Id} cancelled: {cancelled.Reason} - {cancelled.Message}",
            LogLevel.Warning, nodeId: node.Id);

        throw new OperationCanceledException($"Node {node.Id} was cancelled: {cancelled.Reason}");
    }

    /// <summary>
    /// Checks if a node should be skipped due to a failed dependency.
    /// Used for SkipDependents error propagation mode.
    /// </summary>
    private bool ShouldSkipDueToFailedDependency(TContext context, Node node)
    {
        // Get all incoming edges for this node
        var incomingEdges = context.Graph.GetIncomingEdges(node.Id);

        foreach (var edge in incomingEdges)
        {
            // Check if the source node failed
            if (_failedNodes.ContainsKey(edge.From))
            {
                var failedNode = context.Graph.GetNode(edge.From);
                if (failedNode == null) continue;

                // Check the error policy of the failed node
                var policy = failedNode.ErrorPolicy;
                if (policy == null || policy.Mode != PropagationMode.SkipDependents)
                    continue;

                // Check if this node is in the affected list (or if no list specified, all dependents affected)
                if (policy.AffectedNodes != null && policy.AffectedNodes.Count > 0)
                {
                    if (!policy.AffectedNodes.Contains(node.Id))
                        continue; // This node is not in the affected list
                }

                // This node should be skipped
                context.Log("Orchestrator",
                    $"Skipping node {node.Id} due to failed dependency {edge.From}",
                    LogLevel.Warning, nodeId: node.Id);

                // Mark as skipped in context
                context.Channels[$"skip_reason:{node.Id}"].Set(SkipReason.DependencyFailed.ToString());
                context.MarkNodeComplete(node.Id); // Mark as "complete" so it doesn't block further processing

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates whether a node should be skipped based on incoming edge conditions.
    /// Returns (shouldSkip, detailsForLogging).
    /// </summary>
    /// <remarks>
    /// A node should be skipped if ALL of the following are true:
    /// - It has incoming edges (not a START node)
    /// - All source nodes have completed
    /// - No incoming edge conditions evaluate to true (including unconditional and default edges)
    /// </remarks>
    private (bool ShouldSkip, string Details) EvaluateSkipConditions(TContext context, Node node)
    {
        // Get all incoming edges
        var incomingEdges = context.Graph.GetIncomingEdges(node.Id);

        // Emit diagnostic for skip condition evaluation
        context.EmitDebug("EvaluateSkipConditions",
            $"Evaluating {node.Id}: CompletedNodes=[{string.Join(",", context.CompletedNodes)}], Channels=[{string.Join(",", context.Channels.ChannelNames)}]",
            nodeId: node.Id);

        // No incoming edges = always execute (START node or explicit entry point)
        if (incomingEdges.Count == 0)
            return (false, "No incoming edges");

        var evaluatedEdges = new List<string>();
        var hasAnyCompletedSource = false;
        var failedEdgeSources = new List<string>();

        foreach (var edge in incomingEdges)
        {
            // Skip edges from incomplete nodes (they haven't run yet - topology will handle scheduling)
            if (!context.IsNodeComplete(edge.From))
            {
                evaluatedEdges.Add($"{edge.From}→{edge.To}: source incomplete");
                context.EmitTrace("EvaluateSkipConditions", $"Edge {edge.From}→{edge.To}: source NOT complete", nodeId: node.Id);
                continue;
            }

            hasAnyCompletedSource = true;

            // Get source node outputs
            var channelName = $"node_output:{edge.From}";
            if (!context.Channels.Contains(channelName))
            {
                evaluatedEdges.Add($"{edge.From}→{edge.To}: no outputs");
                failedEdgeSources.Add(edge.From);
                context.EmitDebug("EvaluateSkipConditions", $"Edge {edge.From}→{edge.To}: channel {channelName} NOT found", nodeId: node.Id);
                continue;
            }

            var outputs = context.Channels[channelName].Get<Dictionary<string, object>>();
            context.EmitDebug("EvaluateSkipConditions",
                $"Edge {edge.From}→{edge.To}: outputs={outputs?.Count ?? -1} keys=[{(outputs != null ? string.Join(",", outputs.Keys) : "null")}]",
                nodeId: node.Id);

            // Unconditional edge (no condition) = active path exists
            if (edge.Condition == null)
            {
                evaluatedEdges.Add($"{edge.From}→{edge.To}: unconditional (ACTIVE)");

                // Emit edge traversed event
                context.EventCoordinator?.Emit(new Abstractions.Events.EdgeTraversedEvent
                {
                    FromNodeId = edge.From,
                    ToNodeId = edge.To,
                    HasCondition = false,
                    GraphContext = CreateGraphExecutionContext(context)
                });

                return (false, string.Join("; ", evaluatedEdges));
            }

            // Default edge = active (fallback path)
            if (edge.Condition.Type == ConditionType.Default)
            {
                evaluatedEdges.Add($"{edge.From}→{edge.To}: default edge (ACTIVE)");

                // Emit edge traversed event
                context.EventCoordinator?.Emit(new Abstractions.Events.EdgeTraversedEvent
                {
                    FromNodeId = edge.From,
                    ToNodeId = edge.To,
                    HasCondition = true,
                    ConditionDescription = "default",
                    GraphContext = CreateGraphExecutionContext(context)
                });

                return (false, string.Join("; ", evaluatedEdges));
            }

            // Evaluate the condition (use new overload that supports upstream conditions)
            var conditionMet = ConditionEvaluator.Evaluate(edge.Condition, outputs, context, edge);
            var conditionDesc = edge.Condition.GetDescription() ?? edge.Condition.Type.ToString();

            // Emit condition evaluation diagnostic
            var actualVal = outputs != null && edge.Condition?.Field != null && outputs.TryGetValue(edge.Condition.Field, out var fv) ? fv?.ToString() : "(not found)";
            context.EmitDebug("EvaluateSkipConditions",
                $"Condition {conditionDesc}: field={edge.Condition?.Field}, actual='{actualVal}', expected='{edge.Condition?.Value}', result={conditionMet}",
                nodeId: node.Id);

            if (conditionMet)
            {
                evaluatedEdges.Add($"{edge.From}→{edge.To}: {conditionDesc} (ACTIVE)");

                // Emit edge traversed event
                context.EventCoordinator?.Emit(new Abstractions.Events.EdgeTraversedEvent
                {
                    FromNodeId = edge.From,
                    ToNodeId = edge.To,
                    HasCondition = true,
                    ConditionDescription = conditionDesc,
                    GraphContext = CreateGraphExecutionContext(context)
                });

                return (false, string.Join("; ", evaluatedEdges));
            }
            else
            {
                evaluatedEdges.Add($"{edge.From}→{edge.To}: {conditionDesc} (not met)");
                failedEdgeSources.Add(edge.From);

                // Get actual value for debugging
                string? actualValue = null;
                if (edge.Condition.Field != null && outputs != null &&
                    outputs.TryGetValue(edge.Condition.Field, out var fieldValue))
                {
                    actualValue = fieldValue?.ToString();
                }

                // Emit edge condition failed event
                context.EventCoordinator?.Emit(new Abstractions.Events.EdgeConditionFailedEvent
                {
                    FromNodeId = edge.From,
                    ToNodeId = edge.To,
                    ConditionDescription = conditionDesc,
                    ActualValue = actualValue,
                    ExpectedValue = edge.Condition.Value?.ToString(),
                    GraphContext = CreateGraphExecutionContext(context)
                });
            }
        }

        // If no source nodes have completed yet, don't skip - wait for topology
        if (!hasAnyCompletedSource)
        {
            return (false, "No completed source nodes yet");
        }

        // No active paths found from any completed source → skip this node
        // Emit node skipped event
        context.EventCoordinator?.Emit(new Abstractions.Events.NodeSkippedEvent
        {
            NodeId = node.Id,
            Reason = "All incoming edge conditions evaluated to false",
            PotentialSourceNodes = failedEdgeSources.Distinct().ToList(),
            GraphContext = CreateGraphExecutionContext(context)
        });

        return (true, string.Join("; ", evaluatedEdges));
    }

    /// <summary>
    /// Execute a sub-graph node by recursively running the nested graph.
    /// </summary>
    private async Task ExecuteSubGraphAsync(TContext context, Node node, CancellationToken cancellationToken)
    {
        if (node.SubGraph == null)
        {
            throw new InvalidOperationException($"SubGraph node '{node.Id}' has no SubGraph definition");
        }

        context.Log("Orchestrator", $"Entering sub-graph: {node.SubGraph.Name}", LogLevel.Information, nodeId: node.Id);

        // Prepare inputs for sub-graph
        var inputs = PrepareInputs(context, node);

        // Create isolated context for sub-graph execution, preserving TContext type
        var subGraphContext = (TContext)context.CreateIsolatedCopy();

        // For GraphContext, create new instance with replaced graph while preserving TContext type
        // This works because CreateIsolatedCopy() returns the same type as the source
        if (subGraphContext is Context.GraphContext baseContext)
        {
            // Create a new GraphContext-based instance with the sub-graph
            // If TContext is a derived type, this will preserve the derived type through CreateIsolatedCopy
            var subGraphContextWithGraph = new Context.GraphContext(
                executionId: context.ExecutionId + $":{node.Id}",
                graph: node.SubGraph,
                services: context.Services,
                channels: baseContext.Channels,
                managed: baseContext.Managed
            );

            // Pass inputs to sub-graph via channels
            foreach (var input in inputs.GetAll())
            {
                subGraphContextWithGraph.Channels[$"input:{input.Key}"].Set(input.Value);
            }

            // If TContext is exactly GraphContext, use it directly
            // If TContext is a derived type, we need to preserve custom properties
            // For now, we execute with GraphContext - derived types should override ExecuteSubGraphAsync if needed
            var orchestrator = new GraphOrchestrator<Context.GraphContext>(_serviceProvider, _cacheStore, _fingerprintCalculator, _checkpointStore);
            await orchestrator.ExecuteAsync(subGraphContextWithGraph, cancellationToken);

            // Collect outputs from sub-graph
            var outputs = new Dictionary<string, object>();
            foreach (var chName in subGraphContextWithGraph.Channels.ChannelNames)
            {
                if (chName.StartsWith("output:"))
                {
                    var outputKey = chName.Substring("output:".Length);
                    var value = subGraphContextWithGraph.Channels[chName].Get<object>();
                    if (value != null)
                    {
                        outputs[outputKey] = value;
                    }
                }
            }

            // Store sub-graph outputs in parent context
            var outputChannelName = $"node_output:{node.Id}";
            context.Channels[outputChannelName].Set(outputs);

            // Create and store Success result (for upstream condition evaluation)
            var duration = DateTimeOffset.UtcNow - (context.Tags.TryGetValue($"node_start_time:{node.Id}", out var startTimes) && startTimes.Count > 0
                ? DateTimeOffset.Parse(startTimes.First())
                : DateTimeOffset.UtcNow);
            var successResult = new NodeExecutionResult.Success(
                Outputs: outputs,
                Duration: duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1)
            );
            context.Channels[$"node_result:{node.Id}"].Set(successResult);

            // Update state to Succeeded
            context.AddTag($"node_state:{node.Id}", Abstractions.Execution.NodeState.Succeeded.ToString());

            // Mark node as complete
            context.MarkNodeComplete(node.Id);

            context.Log("Orchestrator", $"Exiting sub-graph: {node.SubGraph.Name} with {outputs.Count} outputs",
                LogLevel.Information, nodeId: node.Id);
        }
        else
        {
            throw new InvalidOperationException($"SubGraph execution requires TContext to be or derive from GraphContext. Actual type: {typeof(TContext).Name}");
        }
    }

    /// <summary>
    /// Execute a map node by iterating over input collection and running processor graph for each item.
    /// </summary>
    private async Task ExecuteMapAsync(TContext context, Node node, CancellationToken cancellationToken)
    {
        // VALIDATION
        ValidateMapNode(node);

        context.Log("Orchestrator", $"Entering map: {node.Name}", LogLevel.Information, nodeId: node.Id);

        // Determine input channel
        var inputChannelName = node.MapInputChannel;
        if (string.IsNullOrEmpty(inputChannelName))
        {
            // Default: read from previous node's output
            var incomingEdges = context.Graph.Edges.Where(e => e.To == node.Id).ToList();
            if (incomingEdges.Count > 0)
            {
                inputChannelName = $"node_output:{incomingEdges[0].From}";
            }
            else
            {
                throw new InvalidOperationException($"Map node '{node.Id}' has no MapInputChannel and no incoming edges");
            }
        }

        // Read input items
        var inputData = context.Channels[inputChannelName].Get<object>();
        if (inputData == null)
        {
            throw new InvalidOperationException($"Map node '{node.Id}' input channel '{inputChannelName}' is null");
        }

        // If the input is a Dictionary (handler output format), extract the "output" key
        object? actualInputData = inputData;
        if (inputData is Dictionary<string, object> handlerOutput)
        {
            if (handlerOutput.TryGetValue("output", out var outputValue))
            {
                actualInputData = outputValue;
            }
            else if (handlerOutput.Count == 1)
            {
                // If there's only one key, use its value
                actualInputData = handlerOutput.Values.First();
            }
            else
            {
                throw new InvalidOperationException($"Map node '{node.Id}' input channel '{inputChannelName}' contains a dictionary but no 'output' key. Available keys: {string.Join(", ", handlerOutput.Keys)}");
            }
        }

        if (actualInputData is not System.Collections.IEnumerable inputItems)
        {
            throw new InvalidOperationException($"Map node '{node.Id}' input channel '{inputChannelName}' must be IEnumerable, got {actualInputData?.GetType().FullName ?? "null"}");
        }

        var itemList = inputItems.Cast<object>().ToList();

        // Handle empty input
        if (itemList.Count == 0)
        {
            context.Log("Orchestrator", $"Map node '{node.Id}' has empty input, returning empty results", LogLevel.Information, nodeId: node.Id);
            var emptyOutputChannelName = node.MapOutputChannel ?? $"node_output:{node.Id}";
            context.Channels[emptyOutputChannelName].Set(new List<object>());
            context.MarkNodeComplete(node.Id);
            return;
        }

        // Note: Optional type validation removed for Native AOT compatibility
        // Type.GetType() requires runtime reflection which is not AOT-compatible
        // Type safety is enforced at handler level instead

        // Determine concurrency
        var maxConcurrency = node.MaxParallelMapTasks ?? 0;
        if (maxConcurrency == 0)
        {
            maxConcurrency = Environment.ProcessorCount;
        }

        context.Log("Orchestrator",
            $"Map node '{node.Id}' processing {itemList.Count} items with concurrency={maxConcurrency}",
            LogLevel.Information, nodeId: node.Id);

        // Error mode
        var errorMode = node.MapErrorMode ?? Abstractions.Graph.MapErrorMode.FailFast;

        // Initialize item states for polling support
        var itemStates = new System.Collections.Concurrent.ConcurrentDictionary<int, MapItemState>();
        var startTime = DateTimeOffset.UtcNow;

        for (int i = 0; i < itemList.Count; i++)
        {
            itemStates[i] = new MapItemState
            {
                Status = MapItemStatus.Pending,
                StartTime = DateTimeOffset.UtcNow
            };
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? firstException = null; // Track first exception for FailFast mode

        try
        {
            // OUTER POLLING LOOP: Continue until all items reach terminal state
            while (itemStates.Values.Any(s => !s.IsTerminal()))
            {
                cts.Token.ThrowIfCancellationRequested();

                // Get items ready to execute (pending or ready to retry)
                var itemsToExecute = itemStates
                    .Where(kvp => ShouldExecuteMapItem(kvp.Value))
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (itemsToExecute.Count == 0)
                {
                    // No items ready - calculate next wake-up time (intelligent delay)
                    var nextRetryTime = GetEarliestRetryTime(itemStates);
                    var delay = nextRetryTime - DateTimeOffset.UtcNow;

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cts.Token);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token); // Avoid tight loop
                    }
                    continue;
                }

                // Execute ready items in parallel (with dynamic parallelism)
                var effectiveParallelism = Math.Min(itemsToExecute.Count, maxConcurrency);

                await Parallel.ForEachAsync(itemsToExecute,
                    new ParallelOptions { MaxDegreeOfParallelism = effectiveParallelism, CancellationToken = cts.Token },
                    async (index, ct) =>
                    {
                        var item = itemList[index];
                        var itemState = itemStates[index];

                        // Update state to Running
                        context.AddTag($"node_state:map:{node.Id}[{index}]", NodeState.Running.ToString());
                        itemState.LastAttempt = DateTimeOffset.UtcNow;

                        try
                        {
                            // Create isolated context for this item
                            var mapContext = (TContext)context.CreateIsolatedCopy();

                            if (mapContext is Context.GraphContext baseContext)
                            {
                                // Select processor graph based on item
                                Abstractions.Graph.Graph processorGraph = SelectProcessorGraph(node, item, context);

                                // Create context with processor graph
                                var mapContextWithGraph = new Context.GraphContext(
                                    executionId: context.ExecutionId + $":{node.Id}[{index}]",
                                    graph: processorGraph,
                                    services: context.Services,
                                    channels: baseContext.Channels,
                                    managed: baseContext.Managed
                                );

                                // Write item to input channel
                                mapContextWithGraph.Channels["input:item"].Set(item);
                                mapContextWithGraph.Channels["input:index"].Set(index);

                                // Execute processor graph
                                var orchestrator = new GraphOrchestrator<Context.GraphContext>(_serviceProvider, _cacheStore, _fingerprintCalculator, _checkpointStore);
                                await orchestrator.ExecuteAsync(mapContextWithGraph, ct);

                                // Check if any nodes failed in the processor graph
                                // A graph with failures will have at least one handler node with a Failure result
                                var hasFailures = false;
                                NodeExecutionResult.Failure? failureResult = null;

                                // Check if the processor graph execution was successful
                                // We consider it successful if the graph completed without errors
                                // The nested orchestrator handles polling internally, so when ExecuteAsync completes,
                                // all polling has resolved and we have final results

                                // Check graph completion status
                                if (!mapContextWithGraph.IsComplete)
                                {
                                    // Graph didn't complete - this is an error
                                    hasFailures = true;
                                    failureResult = new NodeExecutionResult.Failure(
                                        Exception: new InvalidOperationException($"Processor graph for item {index} did not complete"),
                                        Severity: ErrorSeverity.Fatal,
                                        IsTransient: false,
                                        Duration: DateTimeOffset.UtcNow - itemState.StartTime
                                    );
                                }
                                else
                                {
                                    // Graph completed - check if any handler nodes failed
                                    var handlerNodes = processorGraph.Nodes.Where(n => n.Type == Abstractions.Graph.NodeType.Handler).ToList();

                                    foreach (var graphNode in handlerNodes)
                                    {
                                        var resultChannelName = $"node_result:{graphNode.Id}";

                                        // Try to get the result
                                        try
                                        {
                                            if (mapContextWithGraph.Channels.Contains(resultChannelName))
                                            {
                                                var result = mapContextWithGraph.Channels[resultChannelName].Get<NodeExecutionResult>();

                                                // If we find a Failure, the graph failed
                                                if (result is NodeExecutionResult.Failure failure)
                                                {
                                                    hasFailures = true;
                                                    failureResult = failure;
                                                    break;
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            // Channel doesn't exist or can't be read - skip this node
                                            continue;
                                        }
                                    }
                                }

                                if (hasFailures && failureResult != null)
                                {
                                    // Graph completed but with failures - mark item as failed
                                    itemState.Status = MapItemStatus.Failed;
                                    itemState.Result = failureResult;
                                    context.AddTag($"node_state:map:{node.Id}[{index}]", NodeState.Failed.ToString());

                                    context.Log("Orchestrator", $"Map node '{node.Id}' item {index + 1}/{itemList.Count} failed: {failureResult.Exception.Message}",
                                        LogLevel.Warning, nodeId: node.Id);
                                }
                                else
                                {
                                    // Graph succeeded - read final result
                                    // Priority: 1) output: channels (explicit output), 2) node_output: from last handler
                                    object? result = null;

                                    // First, try to find explicit output: channels
                                    if (mapContextWithGraph.Channels.ChannelNames.Any(c => c.StartsWith("output:")))
                                    {
                                        var outputChannel = mapContextWithGraph.Channels.ChannelNames.First(c => c.StartsWith("output:"));
                                        result = mapContextWithGraph.Channels[outputChannel].Get<object>();
                                    }
                                    else
                                    {
                                        // No explicit output channel - try to read from handler nodes
                                        // Find the last handler node in execution order
                                        var handlerNodes = processorGraph.Nodes
                                            .Where(n => n.Type == Abstractions.Graph.NodeType.Handler)
                                            .ToList();

                                        if (handlerNodes.Count > 0)
                                        {
                                            // For simple linear graphs, use the only/last handler
                                            var lastHandler = handlerNodes.Last();
                                            var outputChannelName = $"node_output:{lastHandler.Id}";

                                            if (mapContextWithGraph.Channels.Contains(outputChannelName))
                                            {
                                                var handlerOutput = mapContextWithGraph.Channels[outputChannelName].Get<Dictionary<string, object>>();
                                                // Handler outputs are stored as Dictionary<string, object>
                                                // Extract the actual result value
                                                if (handlerOutput != null)
                                                {
                                                    result = handlerOutput;
                                                }
                                            }
                                        }
                                    }

                                    // Store result as Success
                                    itemState.Status = MapItemStatus.Succeeded;
                                    var outputs = new Dictionary<string, object>();
                                    if (result != null)
                                    {
                                        outputs["output"] = result;
                                    }
                                    itemState.Result = new NodeExecutionResult.Success(
                                        Outputs: outputs,
                                        Duration: DateTimeOffset.UtcNow - itemState.StartTime
                                    );
                                    itemState.Context = mapContext;
                                    context.AddTag($"node_state:map:{node.Id}[{index}]", NodeState.Succeeded.ToString());

                                    context.Log("Orchestrator", $"Map node '{node.Id}' completed item {index + 1}/{itemList.Count}",
                                        LogLevel.Debug, nodeId: node.Id);
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException($"Map execution requires TContext to be or derive from GraphContext. Actual type: {typeof(TContext).Name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            // Item execution failed
                            itemState.Status = MapItemStatus.Failed;
                            itemState.Result = new NodeExecutionResult.Failure(
                                Exception: ex,
                                Severity: ErrorSeverity.Fatal,
                                IsTransient: false,
                                Duration: DateTimeOffset.UtcNow - itemState.StartTime
                            );
                            context.AddTag($"node_state:map:{node.Id}[{index}]", NodeState.Failed.ToString());

                            context.Log("Orchestrator",
                                $"Map node '{node.Id}' failed processing item {index}. Mode: {errorMode}. Error: {ex.Message}",
                                LogLevel.Error, nodeId: node.Id, exception: ex);

                            // In FailFast mode, capture first exception and cancel remaining tasks
                            if (errorMode == Abstractions.Graph.MapErrorMode.FailFast)
                            {
                                // Capture first exception atomically
                                System.Threading.Interlocked.CompareExchange(ref firstException, ex, null);
                                cts.Cancel();
                            }
                        }
                    });
            }
        }
        catch (OperationCanceledException) when (firstException != null)
        {
            // In FailFast mode, if we cancelled due to an exception, rethrow the original exception
            // instead of the TaskCanceledException
            throw firstException;
        }
        finally
        {
            cts.Dispose();
        }

        var duration = DateTimeOffset.UtcNow - startTime;

        // All items resolved - aggregate results based on error mode
        var finalResults = new List<object?>();
        var successCount = 0;
        var failureCount = 0;

        for (int i = 0; i < itemList.Count; i++)
        {
            var itemState = itemStates[i];

            // Merge context back to parent
            if (itemState.Context != null)
            {
                context.MergeFrom(itemState.Context);
            }

            if (itemState.Status == MapItemStatus.Succeeded)
            {
                // Extract result from Success output
                if (itemState.Result is NodeExecutionResult.Success success &&
                    success.Outputs.TryGetValue("output", out var output))
                {
                    finalResults.Add(output);
                    successCount++;
                }
                else
                {
                    finalResults.Add(null);
                    successCount++;
                }
            }
            else if (itemState.Status == MapItemStatus.Failed)
            {
                failureCount++;

                switch (errorMode)
                {
                    case Abstractions.Graph.MapErrorMode.ContinueWithNulls:
                        finalResults.Add(null);
                        break;

                    case Abstractions.Graph.MapErrorMode.ContinueOmitFailures:
                        // Skip failed item
                        break;

                    case Abstractions.Graph.MapErrorMode.FailFast:
                        // Throw first failure
                        if (itemState.Result is NodeExecutionResult.Failure failure)
                        {
                            throw failure.Exception;
                        }
                        break;
                }
            }
        }

        // Write results to output channel
        var outputChannelName = node.MapOutputChannel ?? $"node_output:{node.Id}";
        context.Channels[outputChannelName].Set(finalResults);

        // Mark node as complete
        context.MarkNodeComplete(node.Id);

        // Log statistics
        context.Log("Orchestrator",
            $"Map node '{node.Id}' processed {itemList.Count} items: {successCount} succeeded, {failureCount} failed, " +
            $"{duration.TotalMilliseconds}ms (avg: {duration.TotalMilliseconds / (double)itemList.Count:F2}ms/item, concurrency: {maxConcurrency})",
            LogLevel.Information, nodeId: node.Id);

        context.Log("Orchestrator", $"Exiting map: {node.Name} with {finalResults.Count} results",
            LogLevel.Information, nodeId: node.Id);
    }

    /// <summary>
    /// Check if map item should execute (pending or ready to retry).
    /// </summary>
    private bool ShouldExecuteMapItem(MapItemState itemState)
    {
        if (itemState.Status == MapItemStatus.Pending)
            return true;

        if (itemState.Status == MapItemStatus.Polling)
        {
            var elapsed = DateTimeOffset.UtcNow - itemState.LastAttempt;
            return elapsed >= itemState.RetryAfter;
        }

        return false;
    }

    /// <summary>
    /// Get earliest retry time across all polling items (intelligent delay).
    /// </summary>
    private DateTimeOffset GetEarliestRetryTime(System.Collections.Concurrent.ConcurrentDictionary<int, MapItemState> itemStates)
    {
        var pollingItems = itemStates.Values
            .Where(s => s.Status == MapItemStatus.Polling)
            .ToList();

        if (pollingItems.Count == 0)
            return DateTimeOffset.UtcNow;

        return pollingItems.Min(s => s.LastAttempt + s.RetryAfter);
    }

    /// <summary>
    /// Get fingerprints from all upstream nodes (for hierarchical hashing).
    /// </summary>
    private Dictionary<string, string> GetUpstreamFingerprints(TContext context, Node node)
    {
        var upstreamHashes = new Dictionary<string, string>();

        foreach (var edge in context.Graph.GetIncomingEdges(node.Id))
        {
            if (_currentFingerprints.TryGetValue(edge.From, out var upstreamHash))
            {
                upstreamHashes[edge.From] = upstreamHash;
            }
        }

        return upstreamHashes;
    }

    /// <summary>
    /// Clear all ephemeral channels to prevent state leakage across loop iterations.
    /// Ephemeral channels are designed for temporary routing decisions and should be
    /// cleared after each execution step to avoid stale data affecting future iterations.
    /// </summary>
    private void ClearEphemeralChannels(TContext context)
    {
        foreach (var channelName in context.Channels.ChannelNames.ToList())
        {
            var channel = context.Channels[channelName];
            if (channel is EphemeralChannel ephemeral)
            {
                ephemeral.Clear();
            }
        }
    }

    public async IAsyncEnumerable<Event> ExecuteStreamingAsync(
        TContext context,
        StreamingOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new StreamingOptions();

        context.Log("Orchestrator", $"Starting streaming graph execution: {context.Graph.Name}", LogLevel.Information);

        Exception? errorToYield = null;

        // Get execution layers (topological sort)
        IReadOnlyList<ExecutionLayer> layers;
        try
        {
            layers = context.Graph.GetExecutionLayers();

            if (context is Context.GraphContext graphContext)
            {
                graphContext.SetTotalLayers(layers.Count);
            }

            context.Log("Orchestrator", $"Graph has {layers.Count} execution layers", LogLevel.Debug);

        }
        catch (Exception ex)
        {
            errorToYield = ex;
            yield break;
        }

        // Calculate total nodes for progress tracking
        var totalNodes = layers.SelectMany(l => l.NodeIds).Count();
        var completedNodes = 0;

        // Execute each layer
        for (int i = 0; i < layers.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var layer = layers[i];
            context.CurrentLayerIndex = i;

            context.Log("Orchestrator", $"Executing layer {i} with {layer.NodeIds.Count} node(s)", LogLevel.Debug);

            // Get nodes to execute in this layer
            var nodesToExecute = layer.NodeIds
                .Where(nodeId => !context.IsNodeComplete(nodeId))
                .Select(nodeId => context.Graph.GetNode(nodeId))
                .Where(node => node != null && (node.Type == NodeType.Handler || node.Type == NodeType.Router || node.Type == NodeType.SubGraph))
                .ToList();

            if (nodesToExecute.Count == 0)
            {
                continue;
            }

            // Execute nodes and yield results
            if (options.EmissionMode == PartialResultEmissionMode.EveryNode)
            {
                // Emit result after each node
                foreach (var node in nodesToExecute)
                {
                    var startTime = DateTimeOffset.UtcNow;

                    try
                    {
                        await ExecuteNodeAsync(context, node!, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        errorToYield = ex;
                        break;
                    }

                    var duration = DateTimeOffset.UtcNow - startTime;
                    completedNodes++;
                    var progress = (float)completedNodes / totalNodes;

                    yield return new NodeExecutionCompletedEvent
                    {
                        NodeId = node!.Id,
                        HandlerName = node.HandlerName ?? node.Type.ToString(),
                        LayerIndex = i,
                        Progress = progress,
                        Outputs = options.IncludeOutputs
                            ? context.Channels[$"node_output:{node!.Id}"].Get<Dictionary<string, object>>()
                            : null,
                        Duration = duration,
                        Result = new NodeExecutionResult.Success(
                            Outputs: new Dictionary<string, object>(),
                            Duration: duration
                        )
                    };
                }

                if (errorToYield != null)
                    break;
            }
            else
            {
                // Execute layer and emit result at end
                var layerStartTime = DateTimeOffset.UtcNow;

                try
                {
                    if (nodesToExecute.Count == 1)
                    {
                        await ExecuteNodeAsync(context, nodesToExecute[0]!, cancellationToken);
                    }
                    else
                    {
                        await ExecuteNodesInParallelAsync(context, nodesToExecute!, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    errorToYield = ex;
                    break;
                }

                var layerDuration = DateTimeOffset.UtcNow - layerStartTime;
                completedNodes += nodesToExecute.Count;

                // Emit layer completion event
                yield return new LayerExecutionCompletedEvent
                {
                    LayerIndex = i,
                    Duration = layerDuration,
                    SuccessfulNodes = nodesToExecute.Count
                };
            }

            // Clear ephemeral channels after layer execution
            ClearEphemeralChannels(context);

            // Update managed context
            if (context.Managed is ManagedContext managed)
            {
                managed.IncrementStep();
            }
        }

        if (errorToYield == null && context is Context.GraphContext gc)
        {
            gc.MarkComplete();
            context.Log("Orchestrator", "Streaming graph execution completed successfully", LogLevel.Information);
        }
        else if (errorToYield is OperationCanceledException)
        {
            if (context is Context.GraphContext gc2)
            {
                gc2.MarkCancelled();
            }
            context.Log("Orchestrator", "Streaming graph execution cancelled", LogLevel.Warning);
        }
        else if (errorToYield != null)
        {
            context.Log("Orchestrator", $"Streaming graph execution failed: {errorToYield.Message}", LogLevel.Error, exception: errorToYield);
        }

        // Throw error after iterator completes
        if (errorToYield != null)
        {
            throw errorToYield;
        }
    }

    /// <summary>
    /// Saves checkpoint for the current execution state.
    /// Called after each layer completion (fire-and-forget pattern).
    /// </summary>
    private async Task SaveCheckpointAsync(Context.GraphContext context, int completedLayerIndex, Abstractions.Checkpointing.CheckpointTrigger trigger, CancellationToken cancellationToken)
    {
        if (_checkpointStore == null)
            return;

        // Extract all node outputs from channels for checkpoint restoration
        var nodeOutputs = new Dictionary<string, object>();
        var nodeStateMetadata = new Dictionary<string, Abstractions.Checkpointing.NodeStateMetadata>();

        foreach (var channelName in context.Channels.ChannelNames)
        {
            if (channelName.StartsWith("node_output:"))
            {
                var nodeId = channelName.Substring("node_output:".Length);

                // Only include outputs from completed nodes
                if (context.CompletedNodes.Contains(nodeId))
                {
                    try
                    {
                        var output = context.Channels[channelName].Get<object>();
                        if (output != null)
                        {
                            nodeOutputs[channelName] = output;

                            // Capture node version for compatibility checking
                            var node = context.Graph.GetNode(nodeId);
                            if (node != null)
                            {
                                nodeStateMetadata[nodeId] = new Abstractions.Checkpointing.NodeStateMetadata
                                {
                                    NodeId = nodeId,
                                    Version = node.Version,
                                    StateJson = System.Text.Json.JsonSerializer.Serialize(
                                        output,
                                        HPDAgent.Graph.Abstractions.Serialization.GraphJsonSerializerContext.Default.DictionaryStringObject
                                    ),
                                    CapturedAt = DateTimeOffset.UtcNow
                                };
                            }
                        }
                    }
                    catch
                    {
                        // Skip channels that can't be retrieved as object
                        // (may have specific type requirements)
                    }
                }
            }
        }

        var checkpoint = new Abstractions.Checkpointing.GraphCheckpoint
        {
            CheckpointId = Guid.NewGuid().ToString("N"),
            ExecutionId = context.ExecutionId,
            GraphId = context.Graph.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedNodes = context.CompletedNodes,
            NodeOutputs = nodeOutputs,
            NodeStateMetadata = nodeStateMetadata,
            ContextJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ExecutionId = context.ExecutionId,
                CompletedNodes = context.CompletedNodes,
                CurrentLayerIndex = completedLayerIndex,
            }),
            Metadata = new Abstractions.Checkpointing.CheckpointMetadata
            {
                Trigger = trigger,
                CompletedLayer = completedLayerIndex
            }
        };

        await _checkpointStore.SaveCheckpointAsync(checkpoint, cancellationToken);

        context.Log("Orchestrator", $"Checkpoint saved after layer {completedLayerIndex} with {nodeOutputs.Count} node outputs", LogLevel.Debug);
    }
}

/// <summary>
/// Exception thrown when graph execution fails.
/// </summary>
public class GraphExecutionException : Exception
{
    public GraphExecutionException(string message) : base(message) { }
    public GraphExecutionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when a node suspends execution (human-in-the-loop).
/// Caller should save checkpoint and wait for external input.
/// </summary>
public class GraphSuspendedException : Exception
{
    public string NodeId { get; }
    public string SuspendToken { get; }

    public GraphSuspendedException(string nodeId, string suspendToken, string? message = null)
        : base(message ?? $"Graph suspended at node {nodeId}")
    {
        NodeId = nodeId;
        SuspendToken = suspendToken;
    }
}

/// <summary>
/// Map item execution state for polling support.
/// </summary>
internal enum MapItemStatus
{
    Pending,
    Polling,
    Succeeded,
    Failed
}

/// <summary>
/// Per-item state tracking for Map node polling.
/// Tracks the execution state, timing, and results for each mapped item.
/// </summary>
internal class MapItemState
{
    public MapItemStatus Status { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset LastAttempt { get; set; }
    public TimeSpan RetryAfter { get; set; }
    public int AttemptNumber { get; set; }
    public NodeExecutionResult? Result { get; set; }
    public Abstractions.Context.IGraphContext? Context { get; set; }

    /// <summary>
    /// Check if this item has reached a terminal state (succeeded or failed).
    /// </summary>
    public bool IsTerminal() => Status == MapItemStatus.Succeeded || Status == MapItemStatus.Failed;
}

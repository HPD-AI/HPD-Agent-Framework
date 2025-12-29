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

    // Track fingerprints for current execution (for incremental execution)
    // Thread-safe for parallel node execution
    private readonly ConcurrentDictionary<string, string> _currentFingerprints = new();

    // Track failed nodes for error propagation
    // Thread-safe for parallel node execution
    private readonly ConcurrentDictionary<string, Exception> _failedNodes = new();

    public GraphOrchestrator(
        IServiceProvider serviceProvider,
        INodeCacheStore? cacheStore = null,
        INodeFingerprintCalculator? fingerprintCalculator = null,
        Abstractions.Checkpointing.IGraphCheckpointStore? checkpointStore = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _cacheStore = cacheStore;
        _fingerprintCalculator = fingerprintCalculator;
        _checkpointStore = checkpointStore;
    }

    public async Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        context.Log("Orchestrator", $"Starting graph execution: {context.Graph.Name}", LogLevel.Information);

        var executionStartTime = DateTimeOffset.UtcNow;

        try
        {
            // Get execution layers (topological sort)
            var layers = context.Graph.GetExecutionLayers();

            if (context is Context.GraphContext graphContext)
            {
                graphContext.SetTotalLayers(layers.Count);
            }

            context.Log("Orchestrator", $"Graph has {layers.Count} execution layers", LogLevel.Debug);

            // Emit graph started event
            context.EventCoordinator?.Emit(new Abstractions.Events.GraphExecutionStartedEvent
            {
                NodeCount = context.Graph.Nodes.Count,
                LayerCount = layers.Count,
                GraphContext = new Abstractions.Events.GraphExecutionContext
                {
                    GraphId = context.Graph.Id,
                    TotalNodes = context.Graph.Nodes.Count,
                    CompletedNodes = 0,
                    CurrentLayer = null
                },
                Kind = HPD.Events.EventKind.Lifecycle,
                Priority = HPD.Events.EventPriority.Normal
            });

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

            if (context is Context.GraphContext gc)
            {
                gc.MarkComplete();
            }

            context.Log("Orchestrator", "Graph execution completed successfully", LogLevel.Information);

            // Emit graph completed event (success)
            context.EventCoordinator?.Emit(new Abstractions.Events.GraphExecutionCompletedEvent
            {
                Duration = DateTimeOffset.UtcNow - executionStartTime,
                SuccessfulNodes = context.CompletedNodes.Count,
                FailedNodes = 0,
                SkippedNodes = 0,
                GraphContext = new Abstractions.Events.GraphExecutionContext
                {
                    GraphId = context.Graph.Id,
                    TotalNodes = context.Graph.Nodes.Count,
                    CompletedNodes = context.CompletedNodes.Count,
                    CurrentLayer = null
                },
                Kind = HPD.Events.EventKind.Lifecycle,
                Priority = HPD.Events.EventPriority.Normal
            });
        }
        catch (OperationCanceledException)
        {
            if (context is Context.GraphContext gc)
            {
                gc.MarkCancelled();
            }
            context.Log("Orchestrator", "Graph execution cancelled", LogLevel.Warning);

            // Emit graph completed event (cancelled)
            context.EventCoordinator?.Emit(new Abstractions.Events.GraphExecutionCompletedEvent
            {
                Duration = DateTimeOffset.UtcNow - executionStartTime,
                SuccessfulNodes = context.CompletedNodes.Count,
                FailedNodes = 0,
                SkippedNodes = 0,
                GraphContext = new Abstractions.Events.GraphExecutionContext
                {
                    GraphId = context.Graph.Id,
                    TotalNodes = context.Graph.Nodes.Count,
                    CompletedNodes = context.CompletedNodes.Count,
                    CurrentLayer = null
                },
                Kind = HPD.Events.EventKind.Lifecycle,
                Priority = HPD.Events.EventPriority.Normal
            });

            throw;
        }
        catch (Exception ex)
        {
            context.Log("Orchestrator", $"Graph execution failed: {ex.Message}", LogLevel.Error, exception: ex);

            // Emit graph completed event (failure)
            context.EventCoordinator?.Emit(new Abstractions.Events.GraphExecutionCompletedEvent
            {
                Duration = DateTimeOffset.UtcNow - executionStartTime,
                SuccessfulNodes = context.CompletedNodes.Count,
                FailedNodes = 1,
                SkippedNodes = 0,
                GraphContext = new Abstractions.Events.GraphExecutionContext
                {
                    GraphId = context.Graph.Id,
                    TotalNodes = context.Graph.Nodes.Count,
                    CompletedNodes = context.CompletedNodes.Count,
                    CurrentLayer = null
                },
                Kind = HPD.Events.EventKind.Lifecycle,
                Priority = HPD.Events.EventPriority.Normal
            });

            throw;
        }

        return context;
    }

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

        // Check max executions (loop protection)
        if (node.MaxExecutions.HasValue)
        {
            var executionCount = context.GetNodeExecutionCount(node.Id);
            if (executionCount >= node.MaxExecutions.Value)
            {
                context.Log("Orchestrator",
                    $"Node {node.Id} exceeded max executions ({node.MaxExecutions.Value})",
                    LogLevel.Warning, nodeId: node.Id);
                return;
            }
        }

        context.SetCurrentNode(node.Id);
        context.IncrementNodeExecutionCount(node.Id);

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
                    HandleSuspended(context, node, suspended);
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
                            if (ConditionEvaluator.Evaluate(edge.Condition, outputs))
                            {
                                // Condition met - include these outputs
                                foreach (var kvp in outputs)
                                {
                                    inputs.Add(kvp.Key, kvp.Value);
                                }
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

                            // Include outputs from default edge
                            foreach (var kvp in outputs)
                            {
                                inputs.Add(kvp.Key, kvp.Value);
                            }
                        }
                    }
                }
            }
        }

        return inputs;
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

        // Emit node completed event
        context.EventCoordinator?.Emit(new Abstractions.Events.NodeExecutionCompletedEvent
        {
            NodeId = node.Id,
            HandlerName = node.HandlerName ?? node.Type.ToString(),
            LayerIndex = context.CurrentLayerIndex,
            Progress = (float)context.CompletedNodes.Count / context.Graph.Nodes.Count,
            Outputs = null, // Don't include outputs by default for performance
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
                context.Log("Orchestrator",
                    $"Node {node.Id} failed but error is isolated - downstream nodes will continue (Mode: Isolate)",
                    LogLevel.Warning, nodeId: node.Id);
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
        context.Log("Orchestrator",
            $"Node {node.Id} skipped: {skipped.Reason} - {skipped.Message}",
            LogLevel.Information, nodeId: node.Id);
    }

    private void HandleSuspended(TContext context, Node node, NodeExecutionResult.Suspended suspended)
    {
        context.Log("Orchestrator",
            $"Node {node.Id} suspended for external input: {suspended.Message}",
            LogLevel.Information, nodeId: node.Id);

        // Store suspension info in context for later resume
        context.AddTag("suspended_nodes", node.Id);
        context.AddTag($"suspend_token:{node.Id}", suspended.SuspendToken);

        if (suspended.ResumeValue != null)
        {
            // Store the resume value for when execution resumes
            var channelName = $"suspend_resume:{node.Id}";
            context.Channels[channelName].Set(suspended.ResumeValue);
        }

        // Log suspension details
        context.Log("Orchestrator",
            $"Suspend token for {node.Id}: {suspended.SuspendToken}",
            LogLevel.Debug, nodeId: node.Id);

        // Throw exception to halt execution (caller should save checkpoint)
        throw new GraphSuspendedException(node.Id, suspended.SuspendToken, suspended.Message);
    }

    private void HandleCancelled(TContext context, Node node, NodeExecutionResult.Cancelled cancelled)
    {
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

        // Optional type validation
        if (!string.IsNullOrEmpty(node.MapItemType))
        {
            var expectedType = Type.GetType(node.MapItemType);
            if (expectedType != null)
            {
                var actualType = itemList[0]?.GetType();
                if (actualType != null && !expectedType.IsAssignableFrom(actualType))
                {
                    context.Log("Orchestrator",
                        $"Map node '{node.Id}' type mismatch: expected {node.MapItemType}, got {actualType.FullName}",
                        LogLevel.Warning, nodeId: node.Id);
                }
            }
        }

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

        // Execute map with parallelism
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, (object? Result, Exception? Error, TContext? Context)>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var tasks = itemList.Select(async (item, index) =>
            {
                await semaphore.WaitAsync(cts.Token);

                try
                {
                    // Create isolated context for this item
                    var mapContext = (TContext)context.CreateIsolatedCopy();

                    if (mapContext is Context.GraphContext baseContext)
                    {
                        // ===== NEW: SELECT PROCESSOR GRAPH BASED ON ITEM =====
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
                        await orchestrator.ExecuteAsync(mapContextWithGraph, cts.Token);

                        // Read result from output channel
                        object? result = null;
                        if (mapContextWithGraph.Channels.ChannelNames.Any(c => c.StartsWith("output:")))
                        {
                            var outputChannel = mapContextWithGraph.Channels.ChannelNames.First(c => c.StartsWith("output:"));
                            result = mapContextWithGraph.Channels[outputChannel].Get<object>();
                        }

                        results[index] = (result, null, mapContext);

                        context.Log("Orchestrator", $"Map node '{node.Id}' completed item {index + 1}/{itemList.Count}",
                            LogLevel.Debug, nodeId: node.Id);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Map execution requires TContext to be or derive from GraphContext. Actual type: {typeof(TContext).Name}");
                    }
                }
                catch (Exception ex)
                {
                    results[index] = (null, ex, null);

                    context.Log("Orchestrator",
                        $"Map node '{node.Id}' failed processing item {index}. Mode: {errorMode}. Error: {ex.Message}",
                        LogLevel.Error, nodeId: node.Id, exception: ex);

                    // In FailFast mode, cancel remaining tasks
                    if (errorMode == Abstractions.Graph.MapErrorMode.FailFast)
                    {
                        cts.Cancel();
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            // Wait for all tasks
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Expected in FailFast mode
            }
        }
        finally
        {
            semaphore.Dispose();
            cts.Dispose();
        }

        stopwatch.Stop();

        // Collect errors
        var errors = results.Values
            .Where(r => r.Error != null)
            .Select(r => r.Error!)
            .ToList();

        // Handle errors based on mode
        if (errors.Any() && errorMode == Abstractions.Graph.MapErrorMode.FailFast)
        {
            var exception = errors.Count == 1
                ? errors[0]
                : new AggregateException($"Map node '{node.Id}' failed with {errors.Count} errors", errors);

            context.Log("Orchestrator",
                $"Map node '{node.Id}' failed in FailFast mode with {errors.Count} error(s)",
                LogLevel.Error, nodeId: node.Id, exception: exception);

            throw exception;
        }

        // Merge contexts back to parent
        foreach (var kvp in results.OrderBy(r => r.Key))
        {
            if (kvp.Value.Context != null)
            {
                context.MergeFrom(kvp.Value.Context);
            }
        }

        // Aggregate results
        var finalResults = new List<object?>();
        var successCount = 0;
        var failureCount = errors.Count;

        for (int i = 0; i < itemList.Count; i++)
        {
            if (!results.TryGetValue(i, out var resultTuple))
            {
                // Task was cancelled before completion
                continue;
            }

            if (resultTuple.Error != null)
            {
                switch (errorMode)
                {
                    case Abstractions.Graph.MapErrorMode.ContinueWithNulls:
                        finalResults.Add(null);
                        break;

                    case Abstractions.Graph.MapErrorMode.ContinueOmitFailures:
                        // Skip failed item
                        break;

                    case Abstractions.Graph.MapErrorMode.FailFast:
                        // Already handled above
                        break;
                }
            }
            else
            {
                finalResults.Add(resultTuple.Result);
                successCount++;
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
            $"{stopwatch.ElapsedMilliseconds}ms (avg: {stopwatch.ElapsedMilliseconds / (double)itemList.Count:F2}ms/item, concurrency: {maxConcurrency})",
            LogLevel.Information, nodeId: node.Id);

        context.Log("Orchestrator", $"Exiting map: {node.Name} with {finalResults.Count} results",
            LogLevel.Information, nodeId: node.Id);
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
                                    StateJson = System.Text.Json.JsonSerializer.Serialize(output),
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

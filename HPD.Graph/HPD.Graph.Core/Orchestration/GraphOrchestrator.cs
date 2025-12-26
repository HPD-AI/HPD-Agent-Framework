using HPDAgent.Graph.Abstractions.Caching;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Abstractions.Orchestration;
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

    // Track fingerprints for current execution (for incremental execution)
    private readonly Dictionary<string, string> _currentFingerprints = new();

    public GraphOrchestrator(
        IServiceProvider serviceProvider,
        INodeCacheStore? cacheStore = null,
        INodeFingerprintCalculator? fingerprintCalculator = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _cacheStore = cacheStore;
        _fingerprintCalculator = fingerprintCalculator;
    }

    public async Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        context.Log("Orchestrator", $"Starting graph execution: {context.Graph.Name}", LogLevel.Information);

        try
        {
            // Get execution layers (topological sort)
            var layers = context.Graph.GetExecutionLayers();

            if (context is Context.GraphContext graphContext)
            {
                graphContext.SetTotalLayers(layers.Count);
            }

            context.Log("Orchestrator", $"Graph has {layers.Count} execution layers", LogLevel.Debug);

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
            }

            if (context is Context.GraphContext gc)
            {
                gc.MarkComplete();
            }

            context.Log("Orchestrator", "Graph execution completed successfully", LogLevel.Information);
        }
        catch (OperationCanceledException)
        {
            if (context is Context.GraphContext gc)
            {
                gc.MarkCancelled();
            }
            context.Log("Orchestrator", "Graph execution cancelled", LogLevel.Warning);
            throw;
        }
        catch (Exception ex)
        {
            context.Log("Orchestrator", $"Graph execution failed: {ex.Message}", LogLevel.Error, exception: ex);
            throw;
        }

        return context;
    }

    public async Task<TContext> ResumeAsync(TContext context, CancellationToken cancellationToken = default)
    {
        context.Log("Orchestrator", $"Resuming graph execution: {context.Graph.Name}", LogLevel.Information);
        context.Log("Orchestrator", $"Already completed: {context.CompletedNodes.Count} node(s)", LogLevel.Debug);

        // ResumeAsync uses the same logic as ExecuteAsync
        // Nodes that are already complete will be skipped
        return await ExecuteAsync(context, cancellationToken);
    }

    private async Task ExecuteLayerAsync(TContext context, ExecutionLayer layer, CancellationToken cancellationToken)
    {
        // Get nodes to execute (filter out already-completed nodes)
        var nodesToExecute = layer.NodeIds
            .Where(nodeId => !context.IsNodeComplete(nodeId))
            .Select(nodeId => context.Graph.GetNode(nodeId))
            .Where(node => node != null && (node.Type == NodeType.Handler || node.Type == NodeType.Router || node.Type == NodeType.SubGraph))
            .ToList();

        if (nodesToExecute.Count == 0)
        {
            context.Log("Orchestrator", $"Layer {layer.Level}: All nodes already complete, skipping", LogLevel.Debug);
            return;
        }

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
    }

    private async Task ExecuteNodesInParallelAsync(TContext context, List<Node> nodes, CancellationToken cancellationToken)
    {
        // Create isolated contexts for each node
        var tasks = nodes.Select(async node =>
        {
            var isolatedContext = (TContext)context.CreateIsolatedCopy();
            await ExecuteNodeAsync(isolatedContext, node, cancellationToken);
            return isolatedContext;
        });

        // Execute all in parallel
        var isolatedContexts = await Task.WhenAll(tasks);

        // Merge results back into parent context
        foreach (var isolatedContext in isolatedContexts)
        {
            context.MergeFrom(isolatedContext);
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

        try
        {
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

    private HandlerInputs PrepareInputs(TContext context, Node node)
    {
        var inputs = new HandlerInputs();

        // Get incoming edges
        var incomingEdges = context.Graph.GetIncomingEdges(node.Id);

        foreach (var edge in incomingEdges)
        {
            var sourceNode = context.Graph.GetNode(edge.From);
            if (sourceNode != null && context.IsNodeComplete(sourceNode.Id))
            {
                // Get outputs from the channel
                var channelName = $"node_output:{edge.From}";
                if (context.Channels.Contains(channelName))
                {
                    var outputs = context.Channels[channelName].Get<Dictionary<string, object>>();
                    if (outputs != null)
                    {
                        // Evaluate edge condition
                        if (ConditionEvaluator.Evaluate(edge.Condition, outputs))
                        {
                            // Condition met - include these outputs
                            foreach (var kvp in outputs)
                            {
                                inputs.Add(kvp.Key, kvp.Value);
                            }
                        }
                        else
                        {
                            // Condition not met - skip this edge
                            context.Log("Orchestrator",
                                $"Edge condition not met: {edge.From} -> {edge.To} ({edge.Condition?.GetDescription() ?? "N/A"})",
                                LogLevel.Debug, nodeId: node.Id);
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

        // No retry or max attempts exceeded - propagate failure
        throw new GraphExecutionException($"Node {node.Id} failed: {failure.Exception.Message}", failure.Exception);
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

        // Create isolated context for sub-graph execution
        var subGraphContext = (TContext)context.CreateIsolatedCopy();

        // Replace graph with sub-graph
        var subGraphContextWithGraph = new Context.GraphContext(
            executionId: context.ExecutionId + $":{node.Id}",
            graph: node.SubGraph,
            services: context.Services,
            channels: subGraphContext.Channels,
            managed: subGraphContext.Managed
        );

        // Pass inputs to sub-graph via channels
        foreach (var input in inputs.GetAll())
        {
            subGraphContextWithGraph.Channels[$"input:{input.Key}"].Set(input.Value);
        }

        // Execute sub-graph recursively
        var orchestrator = new GraphOrchestrator<Context.GraphContext>(_serviceProvider, _cacheStore, _fingerprintCalculator);
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

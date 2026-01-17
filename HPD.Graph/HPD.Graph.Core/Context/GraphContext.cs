using System.Collections.Concurrent;
using HPDAgent.Graph.Abstractions.Channels;
using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.State;
using HPDAgent.Graph.Core.Channels;
using HPDAgent.Graph.Core.State;
using GraphDefinition = HPDAgent.Graph.Abstractions.Graph.Graph;

namespace HPDAgent.Graph.Core.Context;

/// <summary>
/// Base implementation of IGraphContext.
/// Can be extended for domain-specific contexts.
/// Thread-safe for parallel execution and merging.
/// </summary>
public class GraphContext : IGraphContext
{
    // Thread-safe collections for parallel execution
    private readonly ConcurrentDictionary<string, byte> _completedNodes = new();
    private readonly ConcurrentDictionary<string, int> _nodeExecutionCounts = new();
    private readonly ConcurrentBag<GraphLogEntry> _logEntries = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _tags = new();
    private readonly ConcurrentDictionary<string, object>? _sharedData;

    // Iteration tracking for cyclic graphs
    private int _currentIteration;

    public string ExecutionId { get; init; }
    public GraphDefinition Graph { get; init; }
    public string? CurrentNodeId { get; private set; }
    public IReadOnlySet<string> CompletedNodes => _completedNodes.Keys.ToHashSet();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; private set; }
    public bool IsComplete { get; private set; }
    public bool IsCancelled { get; private set; }
    public IGraphChannelSet Channels { get; init; }
    public IManagedContext Managed { get; init; }
    public IReadOnlyList<GraphLogEntry> LogEntries => _logEntries.ToList();
    public IServiceProvider Services { get; init; }
    public HPD.Events.IEventCoordinator? EventCoordinator { get; init; }
    public IDictionary<string, List<string>> Tags => _tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Distinct().ToList());
    public IDictionary<string, object>? SharedData => _sharedData;
    public int CurrentLayerIndex { get; set; }
    public int TotalLayers { get; private set; }
    public int CurrentIteration => _currentIteration;

    public float Progress
    {
        get
        {
            if (TotalLayers == 0) return 0f;
            return (float)CurrentLayerIndex / TotalLayers;
        }
    }

    public GraphContext(
        string executionId,
        GraphDefinition graph,
        IServiceProvider services,
        IGraphChannelSet? channels = null,
        IManagedContext? managed = null,
        bool enableSharedData = false)
    {
        ExecutionId = executionId ?? throw new ArgumentNullException(nameof(executionId));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Channels = channels ?? new GraphChannelSet();
        Managed = managed ?? new ManagedContext();
        _sharedData = enableSharedData ? new ConcurrentDictionary<string, object>() : null;
        StartedAt = DateTimeOffset.UtcNow;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public IGraphStateScope GetScope(string? scopeName = null)
    {
        return new GraphStateScope(Channels, scopeName);
    }

    public void SetCurrentNode(string? nodeId)
    {
        CurrentNodeId = nodeId;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkNodeComplete(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("Node ID cannot be null or whitespace.", nameof(nodeId));
        }

        _completedNodes.TryAdd(nodeId, 0); // Thread-safe add
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsNodeComplete(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        return _completedNodes.ContainsKey(nodeId);
    }

    public void UnmarkNodeComplete(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("Node ID cannot be null or whitespace.", nameof(nodeId));
        }

        _completedNodes.TryRemove(nodeId, out _);
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void UnmarkNodesComplete(IEnumerable<string> nodeIds)
    {
        if (nodeIds == null)
        {
            throw new ArgumentNullException(nameof(nodeIds));
        }

        foreach (var nodeId in nodeIds)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                _completedNodes.TryRemove(nodeId, out _);
            }
        }
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public int GetNodeExecutionCount(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return 0;
        }

        return _nodeExecutionCounts.TryGetValue(nodeId, out var count) ? count : 0;
    }

    public void IncrementNodeExecutionCount(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("Node ID cannot be null or whitespace.", nameof(nodeId));
        }

        _nodeExecutionCounts.AddOrUpdate(nodeId, 1, (_, count) => count + 1);
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Log(string source, string message, LogLevel level = LogLevel.Information,
                    string? nodeId = null, Exception? exception = null)
    {
        _logEntries.Add(new GraphLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = source,
            Level = level,
            Message = message,
            NodeId = nodeId,
            Exception = exception
        });

        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    public void AddTag(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be null or whitespace.", nameof(key));
        }

        var bag = _tags.GetOrAdd(key, _ => new ConcurrentBag<string>());

        // ConcurrentBag doesn't have Contains, so we accept duplicates
        // This is acceptable for tags (duplicates will be filtered when reading)
        bag.Add(value);
    }

    public void RemoveTag(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be null or whitespace.", nameof(key));
        }

        _tags.TryRemove(key, out _);
    }

    public virtual IGraphContext CreateIsolatedCopy()
    {
        var copy = new GraphContext(
            ExecutionId, Graph, Services, CloneChannels(), Managed,
            enableSharedData: _sharedData != null)
        {
            StartedAt = StartedAt,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            CurrentLayerIndex = CurrentLayerIndex,
            TotalLayers = TotalLayers,
            _currentIteration = _currentIteration
        };

        // Copy completed nodes so isolated context knows which upstream nodes have completed
        // This is critical for PrepareInputs() to correctly evaluate edge conditions
        foreach (var nodeId in CompletedNodes)
        {
            copy.MarkNodeComplete(nodeId);
        }

        // Copy shared data (read-only reference is fine - SharedData is for global context)
        if (_sharedData != null && copy._sharedData != null)
        {
            foreach (var kvp in _sharedData)
            {
                copy._sharedData[kvp.Key] = kvp.Value;
            }
        }

        return copy;
    }

    public virtual void MergeFrom(IGraphContext isolatedContext)
    {
        if (isolatedContext is not GraphContext isolated)
        {
            throw new ArgumentException("Context must be GraphContext", nameof(isolatedContext));
        }

        // Merge channels using channel semantics
        foreach (var channelName in isolated.Channels.ChannelNames)
        {
            var isolatedChannel = isolated.Channels[channelName];
            var parentChannel = Channels[channelName];

            switch (isolatedChannel.UpdateSemantics)
            {
                case ChannelUpdateSemantics.Append:
                    var values = isolatedChannel.Get<List<object>>();
                    if (values != null && values.Count > 0)
                    {
                        parentChannel.Update(values);
                    }
                    break;

                case ChannelUpdateSemantics.Reducer:
                case ChannelUpdateSemantics.LastValue:
                default:
                    var value = isolatedChannel.Get<object>();
                    if (value != null)
                    {
                        parentChannel.Set(value);
                    }
                    break;
            }
        }

        // Merge completed nodes (thread-safe)
        foreach (var nodeId in isolated.CompletedNodes)
        {
            _completedNodes.TryAdd(nodeId, 0);
        }

        // Merge execution counts (additive, thread-safe)
        // Each isolated execution increments by 1, so we add to accumulate total executions
        foreach (var kvp in isolated._nodeExecutionCounts)
        {
            _nodeExecutionCounts.AddOrUpdate(kvp.Key, kvp.Value, (_, existing) => existing + kvp.Value);
        }

        // Merge logs (chronological, thread-safe via ConcurrentBag)
        foreach (var log in isolated.LogEntries.OrderBy(l => l.Timestamp))
        {
            _logEntries.Add(log);
        }

        // Merge tags (thread-safe)
        foreach (var kvp in isolated.Tags)
        {
            var bag = _tags.GetOrAdd(kvp.Key, _ => new ConcurrentBag<string>());
            foreach (var tag in kvp.Value)
            {
                bag.Add(tag); // Duplicates will be filtered when reading Tags property
            }
        }

        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    internal void SetTotalLayers(int totalLayers)
    {
        TotalLayers = totalLayers;
    }

    internal void MarkComplete()
    {
        IsComplete = true;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    internal void MarkCancelled()
    {
        IsCancelled = true;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    internal void IncrementIteration()
    {
        Interlocked.Increment(ref _currentIteration);
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    internal void SetCurrentIteration(int iteration)
    {
        _currentIteration = iteration;
        LastUpdatedAt = DateTimeOffset.UtcNow;
    }

    private IGraphChannelSet CloneChannels()
    {
        // Create a new channel set for the isolated context
        var clonedChannels = new GraphChannelSet();

        // Copy node_output channels - these are needed by PrepareInputs() to evaluate edge conditions
        // and pass outputs from completed upstream nodes to downstream nodes
        foreach (var channelName in Channels.ChannelNames)
        {
            if (channelName.StartsWith("node_output:"))
            {
                // Copy the node output dictionary to the isolated context
                var outputs = Channels[channelName].Get<Dictionary<string, object>>();
                if (outputs != null)
                {
                    clonedChannels[channelName].Set(outputs);
                }
            }
        }

        // Note: Other channels (user-defined) are NOT copied to avoid type erasure issues
        // with generic channels like AppendChannel<T>. These will be merged back via MergeFrom()
        // after parallel execution completes.

        return clonedChannels;
    }
}

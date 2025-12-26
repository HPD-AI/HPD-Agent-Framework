using FluentAssertions;
using HPD.Graph.Tests.Helpers;
using HPDAgent.Graph.Core.Channels;
using HPDAgent.Graph.Core.Context;
using HPDAgent.Graph.Core.Orchestration;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace HPD.Graph.Tests.Performance;

/// <summary>
/// Performance and scalability tests for graph execution.
/// These tests validate that the system can handle large-scale workloads.
/// </summary>
public class PerformanceTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Scalability Tests

    [Fact]
    public async Task LargeSequentialGraph_100Nodes_ExecutesSuccessfully()
    {
        // Arrange - 100 node sequential chain
        var builder = new TestGraphBuilder().AddStartNode();

        for (int i = 1; i <= 100; i++)
        {
            builder.AddHandlerNode($"node{i}", "SuccessHandler");
        }

        builder.AddEndNode();
        builder.AddEdge("start", "node1");

        for (int i = 1; i < 100; i++)
        {
            builder.AddEdge($"node{i}", $"node{i + 1}");
        }

        builder.AddEdge("node100", "end");
        var graph = builder.Build();

        var services = TestServiceProvider.Create();
        var context = new GraphContext("perf-test", graph, services);
        var orchestrator = new GraphOrchestrator<GraphContext>(services);

        // Act
        var sw = Stopwatch.StartNew();
        await orchestrator.ExecuteAsync(context);
        sw.Stop();

        // Assert
        for (int i = 1; i <= 100; i++)
        {
            context.ShouldHaveCompletedNode($"node{i}");
        }

        _output.WriteLine($"100 sequential nodes completed in {sw.ElapsedMilliseconds}ms");

        // Should complete in reasonable time (< 5 seconds for simple success handlers)
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LargeParallelGraph_100Nodes_ExecutesSuccessfully()
    {
        // Arrange - 100 parallel nodes converging to merge
        var builder = new TestGraphBuilder()
            .AddStartNode()
            .AddHandlerNode("merge", "SuccessHandler")
            .AddEndNode();

        for (int i = 1; i <= 100; i++)
        {
            var nodeId = $"parallel{i}";
            builder.AddHandlerNode(nodeId, "SuccessHandler");
            builder.AddEdge("start", nodeId);
            builder.AddEdge(nodeId, "merge");
        }

        builder.AddEdge("merge", "end");
        var graph = builder.Build();

        var services = TestServiceProvider.Create();
        var context = new GraphContext("perf-test", graph, services);
        var orchestrator = new GraphOrchestrator<GraphContext>(services);

        // Act
        var sw = Stopwatch.StartNew();
        await orchestrator.ExecuteAsync(context);
        sw.Stop();

        // Assert - All nodes should complete
        for (int i = 1; i <= 100; i++)
        {
            context.ShouldHaveCompletedNode($"parallel{i}");
        }
        context.ShouldHaveCompletedNode("merge");

        _output.WriteLine($"100 parallel nodes completed in {sw.ElapsedMilliseconds}ms");

        // Parallel should be faster than sequential (< 2 seconds)
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task VeryLargeGraph_1000Nodes_HandlesMemoryEfficiently()
    {
        // Arrange - 1000 node graph (10 layers of 100 parallel nodes each)
        var builder = new TestGraphBuilder().AddStartNode();

        for (int layer = 0; layer < 10; layer++)
        {
            // Add 100 nodes per layer
            for (int i = 0; i < 100; i++)
            {
                var nodeId = $"L{layer}N{i}";
                builder.AddHandlerNode(nodeId, "SuccessHandler");

                // Connect to previous layer or start
                if (layer == 0)
                {
                    builder.AddEdge("start", nodeId);
                }
                else
                {
                    // Connect to one node from previous layer
                    builder.AddEdge($"L{layer - 1}N{i}", nodeId);
                }
            }
        }

        // Connect last layer to end
        builder.AddEndNode();
        for (int i = 0; i < 100; i++)
        {
            builder.AddEdge($"L9N{i}", "end");
        }

        var graph = builder.Build();

        var services = TestServiceProvider.Create();
        var context = new GraphContext("perf-test", graph, services);
        var orchestrator = new GraphOrchestrator<GraphContext>(services);

        // Capture memory before
        var memoryBefore = GC.GetTotalMemory(true);

        // Act
        var sw = Stopwatch.StartNew();
        await orchestrator.ExecuteAsync(context);
        sw.Stop();

        var memoryAfter = GC.GetTotalMemory(false);
        var memoryUsed = (memoryAfter - memoryBefore) / (1024 * 1024); // Convert to MB

        // Assert - Should complete without excessive memory usage
        _output.WriteLine($"1000 nodes completed in {sw.ElapsedMilliseconds}ms, memory used: ~{memoryUsed}MB");

        // Should handle 1000 nodes efficiently
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        // Memory usage should be reasonable (< 100MB for simple handlers)
        memoryUsed.Should().BeLessThan(100);
    }

    [Fact]
    public async Task ChannelOperations_10000Writes_PerformantlyHandled()
    {
        // Arrange
        var appendChannel = new AppendChannel<int>("perf-test");

        // Act
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            appendChannel.Set(i);
        }
        sw.Stop();

        var values = appendChannel.Get<List<int>>();

        // Assert
        values.Should().HaveCount(10000);
        _output.WriteLine($"10,000 channel writes completed in {sw.ElapsedMilliseconds}ms");

        // Should complete quickly (< 100ms)
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    #endregion

    #region Concurrency Stress Tests

    [Fact]
    public async Task ConcurrentExecution_10ParallelGraphs_NoThreadSafetyIssues()
    {
        // Arrange - 10 identical graphs to execute in parallel
        var graphs = Enumerable.Range(0, 10).Select(i =>
        {
            var builder = new TestGraphBuilder()
                .AddStartNode()
                .AddHandlerNode("node1", "SuccessHandler")
                .AddHandlerNode("node2", "SuccessHandler")
                .AddHandlerNode("node3", "SuccessHandler")
                .AddEndNode()
                .AddEdge("start", "node1")
                .AddEdge("node1", "node2")
                .AddEdge("node2", "node3")
                .AddEdge("node3", "end");
            return builder.Build();
        }).ToArray();

        var services = TestServiceProvider.Create();

        // Act
        var sw = Stopwatch.StartNew();
        var tasks = graphs.Select((graph, index) =>
        {
            var context = new GraphContext($"concurrent-{index}", graph, services);
            var orchestrator = new GraphOrchestrator<GraphContext>(services);
            return orchestrator.ExecuteAsync(context);
        }).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert - All should complete successfully
        tasks.Should().AllSatisfy(t => t.IsCompletedSuccessfully.Should().BeTrue());
        _output.WriteLine($"10 parallel graph executions completed in {sw.ElapsedMilliseconds}ms");

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task WideParallelism_100ParallelNodes_StressTest()
    {
        // Arrange - Single graph with 100 parallel nodes
        var builder = new TestGraphBuilder()
            .AddStartNode()
            .AddHandlerNode("merge", "SuccessHandler")
            .AddEndNode();

        for (int i = 0; i < 100; i++)
        {
            var nodeId = $"stress{i}";
            builder.AddHandlerNode(nodeId, "SuccessHandler");
            builder.AddEdge("start", nodeId);
            builder.AddEdge(nodeId, "merge");
        }

        builder.AddEdge("merge", "end");
        var graph = builder.Build();

        var services = TestServiceProvider.Create();
        var context = new GraphContext("stress-test", graph, services);
        var orchestrator = new GraphOrchestrator<GraphContext>(services);

        // Act
        var sw = Stopwatch.StartNew();
        await orchestrator.ExecuteAsync(context);
        sw.Stop();

        // Assert - All nodes complete, no thread safety issues
        for (int i = 0; i < 100; i++)
        {
            context.ShouldHaveCompletedNode($"stress{i}");
        }

        _output.WriteLine($"100 parallel nodes stress test completed in {sw.ElapsedMilliseconds}ms");

        // Should handle wide parallelism efficiently
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ContextMerge_100ParallelBranches_PerformantMerging()
    {
        // Arrange - 100 parallel branches that all merge
        var builder = new TestGraphBuilder()
            .AddStartNode()
            .AddHandlerNode("merge", "CounterHandler")
            .AddEndNode();

        for (int i = 0; i < 100; i++)
        {
            var nodeId = $"counter{i}";
            builder.AddHandlerNode(nodeId, "CounterHandler");
            builder.AddEdge("start", nodeId);
            builder.AddEdge(nodeId, "merge");
        }

        builder.AddEdge("merge", "end");
        var graph = builder.Build();

        var services = TestServiceProvider.Create();
        var context = new GraphContext("merge-test", graph, services);
        var orchestrator = new GraphOrchestrator<GraphContext>(services);

        // Act
        var sw = Stopwatch.StartNew();
        await orchestrator.ExecuteAsync(context);
        sw.Stop();

        // Assert - All contexts merged successfully
        context.ShouldHaveCompletedNode("merge");

        _output.WriteLine($"100 branch context merge completed in {sw.ElapsedMilliseconds}ms");

        // Context merging should be efficient
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Throughput Tests

    [Fact]
    public async Task Throughput_MultipleSmallGraphs_HighThroughput()
    {
        // Arrange - Execute 50 small graphs sequentially
        var graph = new TestGraphBuilder()
            .AddStartNode()
            .AddHandlerNode("node1", "SuccessHandler")
            .AddHandlerNode("node2", "SuccessHandler")
            .AddEndNode()
            .AddEdge("start", "node1")
            .AddEdge("node1", "node2")
            .AddEdge("node2", "end")
            .Build();

        var services = TestServiceProvider.Create();
        var orchestrator = new GraphOrchestrator<GraphContext>(services);

        // Act
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
        {
            var context = new GraphContext($"throughput-{i}", graph, services);
            await orchestrator.ExecuteAsync(context);
        }
        sw.Stop();

        // Assert
        var throughput = 50.0 / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"Throughput: {throughput:F2} graphs/second ({sw.ElapsedMilliseconds}ms total)");

        // Should achieve decent throughput
        throughput.Should().BeGreaterThan(10); // At least 10 graphs/sec
    }

    #endregion
}

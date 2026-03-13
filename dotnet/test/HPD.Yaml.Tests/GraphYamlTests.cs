using HPDAgent.Graph.Abstractions.Artifacts;
using HPDAgent.Graph.Abstractions.Graph;
using HPDAgent.Graph.Abstractions.Serialization;

namespace HPD.Yaml.Tests;

public class GraphYamlTests
{
    [Fact]
    public void Deserialize_MinimalGraph_ParsesCorrectly()
    {
        var yaml = @"
id: test-graph
name: Test Graph
version: '1.0.0'
entryNodeId: START
exitNodeId: END
nodes:
  - id: START
    name: Start
    type: Start
  - id: worker
    name: Worker Node
    type: Handler
    handlerName: WorkerHandler
  - id: END
    name: End
    type: End
edges:
  - from: START
    to: worker
  - from: worker
    to: END
";

        var graph = GraphYaml.FromYaml(yaml);

        Assert.Equal("test-graph", graph.Id);
        Assert.Equal("Test Graph", graph.Name);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal(NodeType.Handler, graph.Nodes[1].Type);
        Assert.Equal("WorkerHandler", graph.Nodes[1].HandlerName);
    }

    [Fact]
    public void Deserialize_NodeWithTimeoutAndRetry_ParsesCorrectly()
    {
        var yaml = @"
id: node-test
name: Node Test
entryNodeId: START
exitNodeId: END
nodes:
  - id: START
    name: Start
    type: Start
  - id: api-call
    name: API Call
    type: Handler
    handlerName: ApiHandler
    timeout: PT30S
    retryPolicy:
      maxAttempts: 3
      initialDelay: PT2S
      strategy: JitteredExponential
      maxDelay: PT30S
    maxParallelExecutions: 5
  - id: END
    name: End
    type: End
edges:
  - from: START
    to: api-call
  - from: api-call
    to: END
";

        var graph = GraphYaml.FromYaml(yaml);

        var apiNode = graph.Nodes[1];
        Assert.Equal(TimeSpan.FromSeconds(30), apiNode.Timeout);
        Assert.NotNull(apiNode.RetryPolicy);
        Assert.Equal(3, apiNode.RetryPolicy!.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), apiNode.RetryPolicy.InitialDelay);
        Assert.Equal(BackoffStrategy.JitteredExponential, apiNode.RetryPolicy.Strategy);
        Assert.Equal(5, apiNode.MaxParallelExecutions);
    }

    [Fact]
    public void Deserialize_ConditionalEdges_ParsesCorrectly()
    {
        var yaml = @"
id: conditional-graph
name: Conditional
entryNodeId: START
exitNodeId: END
nodes:
  - id: START
    name: Start
    type: Start
  - id: router
    name: Router
    type: Router
    handlerName: RouterHandler
  - id: path_a
    name: Path A
    type: Handler
    handlerName: PathAHandler
  - id: path_b
    name: Path B
    type: Handler
    handlerName: PathBHandler
  - id: END
    name: End
    type: End
edges:
  - from: START
    to: router
  - from: router
    to: path_a
    condition:
      type: FieldEquals
      field: route
      value: a
  - from: router
    to: path_b
    condition:
      type: Default
  - from: path_a
    to: END
  - from: path_b
    to: END
";

        var graph = GraphYaml.FromYaml(yaml);

        Assert.Equal(5, graph.Edges.Count);
        var condEdge = graph.Edges[1];
        Assert.NotNull(condEdge.Condition);
        Assert.Equal(ConditionType.FieldEquals, condEdge.Condition!.Type);
        Assert.Equal("route", condEdge.Condition.Field);
    }

    [Fact]
    public void Deserialize_StaticPartitionDefinition_ParsesCorrectly()
    {
        var converter = new PartitionDefinitionYamlConverter();
        var deserializer = HPD.Yaml.Core.YamlDefaults.CreateDeserializerBuilder()
            .WithTypeConverter(converter)
            .Build();

        var yaml = @"
type: static
keys:
  - us-east
  - us-west
  - eu-central
";

        var partition = deserializer.Deserialize<PartitionDefinition>(yaml);

        Assert.IsType<StaticPartitionDefinition>(partition);
        var staticDef = (StaticPartitionDefinition)partition;
        Assert.Equal(3, staticDef.Keys.Count);
        Assert.Contains("us-east", staticDef.Keys);
        Assert.Contains("eu-central", staticDef.Keys);
    }

    [Fact]
    public void Deserialize_TimePartitionDefinition_ParsesCorrectly()
    {
        var converter = new PartitionDefinitionYamlConverter();
        var deserializer = HPD.Yaml.Core.YamlDefaults.CreateDeserializerBuilder()
            .WithTypeConverter(converter)
            .Build();

        var yaml = @"
type: time
interval: Daily
start: '2025-01-01T00:00:00+00:00'
end: '2025-01-31T00:00:00+00:00'
timezone: UTC
";

        var partition = deserializer.Deserialize<PartitionDefinition>(yaml);

        Assert.IsType<TimePartitionDefinition>(partition);
        var timeDef = (TimePartitionDefinition)partition;
        Assert.Equal(TimePartitionDefinition.Granularity.Daily, timeDef.Interval);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), timeDef.Start);
        Assert.Equal(new DateTimeOffset(2025, 1, 31, 0, 0, 0, TimeSpan.Zero), timeDef.End);
    }

    [Fact]
    public void RoundTrip_Graph_PreservesStructure()
    {
        var original = new Graph
        {
            Id = "rt-graph",
            Name = "Round-Trip Graph",
            Version = "1.0.0",
            EntryNodeId = "START",
            ExitNodeId = "END",
            Nodes = new List<Node>
            {
                new Node { Id = "START", Name = "Start", Type = NodeType.Start },
                new Node { Id = "worker", Name = "Worker", Type = NodeType.Handler, HandlerName = "WorkerHandler" },
                new Node { Id = "END", Name = "End", Type = NodeType.End }
            },
            Edges = new List<Edge>
            {
                new Edge { From = "START", To = "worker" },
                new Edge { From = "worker", To = "END" }
            }
        };

        var yaml = GraphYaml.ToYaml(original);
        var deserialized = GraphYaml.FromYaml(yaml);

        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Nodes.Count, deserialized.Nodes.Count);
        Assert.Equal(original.Edges.Count, deserialized.Edges.Count);
    }
}

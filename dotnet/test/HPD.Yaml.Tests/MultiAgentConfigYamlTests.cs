using HPD.MultiAgent.Config;
using HPDAgent.Graph.Abstractions.Graph;

namespace HPD.Yaml.Tests;

public class MultiAgentConfigYamlTests
{
    [Fact]
    public void Deserialize_MinimalWorkflow_ParsesCorrectly()
    {
        var yaml = @"
name: simple-workflow
version: '1.0.0'
agents:
  planner:
    agent:
      name: planner-agent
      systemInstructions: You plan tasks.
  executor:
    agent:
      name: executor-agent
      systemInstructions: You execute tasks.
edges:
  - from: START
    to: planner
  - from: planner
    to: executor
  - from: executor
    to: END
";

        var config = MultiAgentConfigYaml.FromYaml(yaml);

        Assert.Equal("simple-workflow", config.Name);
        Assert.Equal(2, config.Agents.Count);
        Assert.True(config.Agents.ContainsKey("planner"));
        Assert.True(config.Agents.ContainsKey("executor"));
        Assert.Equal(3, config.Edges.Count);
        Assert.Equal("planner-agent", config.Agents["planner"].Agent.Name);
    }

    [Fact]
    public void Deserialize_ConditionalEdges_ParsesCorrectly()
    {
        var yaml = @"
name: conditional-workflow
agents:
  classifier:
    agent:
      name: classifier
      systemInstructions: Classify input
    outputMode: Structured
  handler_a:
    agent:
      name: handler-a
      systemInstructions: Handle type A
  handler_b:
    agent:
      name: handler-b
      systemInstructions: Handle type B
edges:
  - from: START
    to: classifier
  - from: classifier
    to: handler_a
    when:
      type: FieldEquals
      field: category
      value: A
  - from: classifier
    to: handler_b
    when:
      type: Default
  - from: handler_a
    to: END
  - from: handler_b
    to: END
";

        var config = MultiAgentConfigYaml.FromYaml(yaml);

        Assert.Equal(5, config.Edges.Count);
        var conditionalEdge = config.Edges[1];
        Assert.NotNull(conditionalEdge.When);
        Assert.Equal(ConditionType.FieldEquals, conditionalEdge.When!.Type);
        Assert.Equal("category", conditionalEdge.When.Field);
    }

    [Fact]
    public void Deserialize_WithRetryAndTimeout_ParsesCorrectly()
    {
        var yaml = @"
name: resilient-workflow
agents:
  worker:
    agent:
      name: worker
      systemInstructions: Do work
    timeout: PT30S
    retry:
      maxAttempts: 3
      initialDelay: PT1S
      strategy: Exponential
      maxDelay: PT10S
edges:
  - from: START
    to: worker
  - from: worker
    to: END
settings:
  defaultTimeout: PT60S
  maxIterations: 50
  enableCheckpointing: true
  streamingMode: PerNode
";

        var config = MultiAgentConfigYaml.FromYaml(yaml);

        var worker = config.Agents["worker"];
        Assert.Equal(TimeSpan.FromSeconds(30), worker.Timeout);
        Assert.NotNull(worker.Retry);
        Assert.Equal(3, worker.Retry!.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), worker.Retry.InitialDelay);
        Assert.Equal(BackoffStrategy.Exponential, worker.Retry.Strategy);
        Assert.Equal(TimeSpan.FromSeconds(60), config.Settings.DefaultTimeout);
        Assert.Equal(50, config.Settings.MaxIterations);
        Assert.True(config.Settings.EnableCheckpointing);
    }

    [Fact]
    public void RoundTrip_MultiAgentWorkflowConfig_PreservesValues()
    {
        var original = new MultiAgentWorkflowConfig
        {
            Name = "round-trip-workflow",
            Description = "Test workflow",
            Version = "2.0.0",
            Agents = new Dictionary<string, AgentNodeConfig>
            {
                ["agent1"] = new AgentNodeConfig
                {
                    Agent = new HPD.Agent.AgentConfig
                    {
                        Name = "agent-1",
                        SystemInstructions = "First agent"
                    }
                }
            },
            Edges = new List<EdgeConfig>
            {
                new EdgeConfig { From = "START", To = "agent1" },
                new EdgeConfig { From = "agent1", To = "END" }
            }
        };

        var yaml = MultiAgentConfigYaml.ToYaml(original);
        var deserialized = MultiAgentConfigYaml.FromYaml(yaml);

        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Description, deserialized.Description);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Single(deserialized.Agents);
        Assert.Equal("agent-1", deserialized.Agents["agent1"].Agent.Name);
        Assert.Equal(2, deserialized.Edges.Count);
    }
}

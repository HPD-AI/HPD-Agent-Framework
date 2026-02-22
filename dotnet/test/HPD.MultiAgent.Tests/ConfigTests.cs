using System.Text.Json;
using HPD.Agent;
using HPD.MultiAgent;
using HPD.MultiAgent.Config;
using HPDAgent.Graph.Abstractions;
using HPDAgent.Graph.Abstractions.Graph;

namespace HPD.MultiAgent.Tests;

public class ConfigTests
{
    [Fact]
    public void MultiAgentWorkflowConfig_Serializes_To_Json()
    {
        var config = new MultiAgentWorkflowConfig
        {
            Name = "TestWorkflow",
            Description = "A test workflow",
            Version = "1.0.0",
            Agents = new Dictionary<string, AgentNodeConfig>
            {
                ["classifier"] = new AgentNodeConfig
                {
                    Agent = new AgentConfig
                    {
                        Name = "Classifier",
                        SystemInstructions = "Classify queries"
                    },
                    OutputMode = AgentOutputMode.Union,
                    Timeout = TimeSpan.FromSeconds(30)
                }
            },
            Edges = new List<EdgeConfig>
            {
                new EdgeConfig { From = "START", To = "classifier" }
            },
            Settings = new WorkflowSettingsConfig
            {
                MaxIterations = 10,
                EnableMetrics = true
            }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("TestWorkflow");
        json.Should().Contain("classifier");
    }

    [Fact]
    public void MultiAgentWorkflowConfig_Deserializes_From_Json()
    {
        var json = """
        {
            "Name": "TestWorkflow",
            "Description": "A test workflow",
            "Version": "1.0.0",
            "Agents": {
                "classifier": {
                    "Agent": {
                        "Name": "Classifier",
                        "SystemInstructions": "Classify queries"
                    },
                    "OutputMode": "Union"
                }
            },
            "Edges": [
                { "From": "START", "To": "classifier" }
            ],
            "Settings": {
                "MaxIterations": 10
            }
        }
        """;

        var config = JsonSerializer.Deserialize<MultiAgentWorkflowConfig>(json);

        config.Should().NotBeNull();
        config!.Name.Should().Be("TestWorkflow");
        config.Agents.Should().ContainKey("classifier");
        config.Agents["classifier"].OutputMode.Should().Be(AgentOutputMode.Union);
        config.Edges.Should().HaveCount(1);
        config.Settings.MaxIterations.Should().Be(10);
    }

    [Fact]
    public void AgentNodeConfig_With_Retry_Serializes()
    {
        var config = new AgentNodeConfig
        {
            Agent = new AgentConfig { Name = "Test" },
            Retry = new RetryConfig
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(1),
                Strategy = HPDAgent.Graph.Abstractions.Graph.BackoffStrategy.Exponential,
                OnlyTransient = true
            }
        };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("MaxAttempts");
        json.Should().Contain("Exponential");
    }

    [Fact]
    public void AgentNodeConfig_With_Error_Config_Serializes()
    {
        var config = new AgentNodeConfig
        {
            Agent = new AgentConfig { Name = "Test" },
            OnError = new ErrorConfig
            {
                Mode = ErrorMode.Fallback,
                FallbackAgent = "fallbackAgent"
            }
        };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("Fallback");
        json.Should().Contain("fallbackAgent");
    }

    [Fact]
    public void EdgeConfig_With_Condition_Serializes()
    {
        var config = new EdgeConfig
        {
            From = "classifier",
            To = "solver",
            When = new ConditionConfig
            {
                Type = ConditionType.FieldEquals,
                Field = "category",
                Value = "math"
            }
        };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("FieldEquals");
        json.Should().Contain("category");
        json.Should().Contain("math");
    }

    [Fact]
    public void WorkflowSettingsConfig_Defaults_Are_Correct()
    {
        var settings = new WorkflowSettingsConfig();

        settings.EnableCheckpointing.Should().BeFalse();
        settings.EnableMetrics.Should().BeTrue();
        settings.StreamingMode.Should().Be(StreamingMode.PerNode);
        settings.MaxIterations.Should().Be(25);
    }

    [Fact]
    public void IterationOptionsConfig_Serializes()
    {
        var config = new IterationOptionsConfig
        {
            MaxIterations = 50,
            UseChangeAwareIteration = true,
            EnableAutoConvergence = true,
            IgnoreFieldsForChangeDetection = new List<string> { "timestamp", "requestId" },
            AlwaysDirtyNodes = new List<string> { "validator" }
        };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("UseChangeAwareIteration");
        json.Should().Contain("timestamp");
        json.Should().Contain("validator");
    }

    [Fact]
    public void ErrorMode_All_Values_Serialize_Correctly()
    {
        var modes = Enum.GetValues<ErrorMode>();

        foreach (var mode in modes)
        {
            var config = new ErrorConfig { Mode = mode };
            var json = JsonSerializer.Serialize(config);
            json.Should().Contain(mode.ToString());
        }
    }

    [Fact]
    public void StreamingMode_All_Values_Serialize_Correctly()
    {
        var modes = Enum.GetValues<StreamingMode>();

        foreach (var mode in modes)
        {
            var config = new WorkflowSettingsConfig { StreamingMode = mode };
            var json = JsonSerializer.Serialize(config);
            json.Should().Contain(mode.ToString());
        }
    }

    [Fact]
    public void AgentOutputMode_All_Values_Serialize_Correctly()
    {
        var modes = Enum.GetValues<AgentOutputMode>();

        foreach (var mode in modes)
        {
            var config = new AgentNodeConfig
            {
                Agent = new AgentConfig { Name = "Test" },
                OutputMode = mode
            };
            var json = JsonSerializer.Serialize(config);
            json.Should().Contain(mode.ToString());
        }
    }
}

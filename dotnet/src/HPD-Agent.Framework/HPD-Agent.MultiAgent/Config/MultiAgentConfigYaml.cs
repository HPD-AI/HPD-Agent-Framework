using HPD.Agent;
using HPD.MultiAgent;
using HPD.Yaml.Core;
using HPD.Yaml.Core.Converters;
using HPDAgent.Graph.Abstractions.Graph;
using YamlDotNet.Serialization;

namespace HPD.MultiAgent.Config;

/// <summary>
/// YAML serialization support for <see cref="MultiAgentWorkflowConfig"/>.
/// Re-registers agent-level enum converters since MultiAgent embeds AgentConfig.
/// </summary>
public static class MultiAgentConfigYaml
{
    private static readonly IDeserializer s_deserializer = YamlDefaults.CreateDeserializerBuilder()
        // Agent enums (re-registered because MultiAgent embeds AgentConfig)
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningEffort>())
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningOutput>())
        // MultiAgent enums
        .WithTypeConverter(new StringEnumYamlConverter<AgentOutputMode>())
        .WithTypeConverter(new StringEnumYamlConverter<BackoffStrategy>())
        .WithTypeConverter(new StringEnumYamlConverter<ErrorMode>())
        .WithTypeConverter(new StringEnumYamlConverter<StreamingMode>())
        .WithTypeConverter(new StringEnumYamlConverter<ConditionType>())
        .Build();

    private static readonly ISerializer s_serializer = YamlDefaults.CreateSerializerBuilder()
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningEffort>())
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningOutput>())
        .WithTypeConverter(new StringEnumYamlConverter<AgentOutputMode>())
        .WithTypeConverter(new StringEnumYamlConverter<BackoffStrategy>())
        .WithTypeConverter(new StringEnumYamlConverter<ErrorMode>())
        .WithTypeConverter(new StringEnumYamlConverter<StreamingMode>())
        .WithTypeConverter(new StringEnumYamlConverter<ConditionType>())
        .Build();

    /// <summary>
    /// Deserializes a <see cref="MultiAgentWorkflowConfig"/> from a YAML string.
    /// </summary>
    public static MultiAgentWorkflowConfig FromYaml(string yaml)
    {
        return s_deserializer.Deserialize<MultiAgentWorkflowConfig>(yaml)
            ?? throw new InvalidOperationException("Failed to deserialize MultiAgentWorkflowConfig from YAML.");
    }

    /// <summary>
    /// Deserializes a <see cref="MultiAgentWorkflowConfig"/> from a YAML file.
    /// </summary>
    public static MultiAgentWorkflowConfig FromYamlFile(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return FromYaml(yaml);
    }

    /// <summary>
    /// Serializes a <see cref="MultiAgentWorkflowConfig"/> to a YAML string.
    /// </summary>
    public static string ToYaml(MultiAgentWorkflowConfig config)
    {
        return s_serializer.Serialize(config);
    }
}

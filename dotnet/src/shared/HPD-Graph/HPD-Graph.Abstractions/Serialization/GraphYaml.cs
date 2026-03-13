using HPD.Yaml.Core;
using HPD.Yaml.Core.Converters;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Graph;
using YamlDotNet.Serialization;

namespace HPDAgent.Graph.Abstractions.Serialization;

/// <summary>
/// YAML serialization support for HPD-Graph types (GraphDefinition, Node, Edge, etc.).
/// Includes polymorphic PartitionDefinition converter.
///
/// Limitations:
/// - Node.Config is constrained to Dictionary&lt;string, string&gt; in YAML
///   (Dictionary&lt;string, object&gt; loses type info). Cast at read time.
/// - RetryPolicy.RetryableExceptions (IReadOnlyList&lt;Type&gt;) is excluded from YAML.
/// - Node.SubGraph/MapProcessorGraph require separate file references in YAML.
/// </summary>
public static class GraphYaml
{
    private static readonly IDeserializer s_deserializer = YamlDefaults.CreateDeserializerBuilder()
        .WithTypeConverter(new PartitionDefinitionYamlConverter())
        .WithTypeConverter(new StringEnumYamlConverter<NodeType>())
        .WithTypeConverter(new StringEnumYamlConverter<ConditionType>())
        .WithTypeConverter(new StringEnumYamlConverter<BackoffStrategy>())
        .WithTypeConverter(new StringEnumYamlConverter<CloningPolicy>())
        .WithTypeConverter(new StringEnumYamlConverter<MapErrorMode>())
        .WithTypeConverter(new StringEnumYamlConverter<ErrorSeverity>())
        .Build();

    private static readonly ISerializer s_serializer = YamlDefaults.CreateSerializerBuilder()
        .WithTypeConverter(new PartitionDefinitionYamlConverter())
        .WithTypeConverter(new StringEnumYamlConverter<NodeType>())
        .WithTypeConverter(new StringEnumYamlConverter<ConditionType>())
        .WithTypeConverter(new StringEnumYamlConverter<BackoffStrategy>())
        .WithTypeConverter(new StringEnumYamlConverter<CloningPolicy>())
        .WithTypeConverter(new StringEnumYamlConverter<MapErrorMode>())
        .WithTypeConverter(new StringEnumYamlConverter<ErrorSeverity>())
        .Build();

    /// <summary>
    /// Deserializes a <see cref="Graph.Graph"/> definition from a YAML string.
    /// </summary>
    public static Graph.Graph FromYaml(string yaml)
    {
        return s_deserializer.Deserialize<Graph.Graph>(yaml)
            ?? throw new InvalidOperationException("Failed to deserialize Graph from YAML.");
    }

    /// <summary>
    /// Deserializes a <see cref="Graph.Graph"/> definition from a YAML file.
    /// </summary>
    public static Graph.Graph FromYamlFile(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return FromYaml(yaml);
    }

    /// <summary>
    /// Serializes a <see cref="Graph.Graph"/> to a YAML string.
    /// </summary>
    public static string ToYaml(Graph.Graph graph)
    {
        return s_serializer.Serialize(graph);
    }

    /// <summary>
    /// Deserializes a <see cref="Node"/> from a YAML string.
    /// </summary>
    public static Node NodeFromYaml(string yaml)
    {
        return s_deserializer.Deserialize<Node>(yaml)
            ?? throw new InvalidOperationException("Failed to deserialize Node from YAML.");
    }
}

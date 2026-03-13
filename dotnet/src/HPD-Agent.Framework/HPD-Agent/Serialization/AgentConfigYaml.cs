using HPD.Yaml.Core;
using HPD.Yaml.Core.Converters;
using YamlDotNet.Serialization;

namespace HPD.Agent.Serialization;

/// <summary>
/// YAML serialization support for <see cref="AgentConfig"/>.
/// Provides FromYaml/ToYaml convenience methods with all required type converters registered.
///
/// Properties excluded from YAML (code-only, marked [JsonIgnore] on AgentConfig):
/// - SessionStore, AgentStore, SessionStoreOptions
/// - ServerConfiguredTools, ConfigureOptions, ChatClientMiddleware
/// - explicitlyRegisteredToolkits
/// - ProviderConfig.DefaultChatOptions
/// - ErrorHandlingConfig.CustomRetryStrategy
/// </summary>
public static class AgentConfigYaml
{
    private static readonly IDeserializer s_deserializer = YamlDefaults.CreateDeserializerBuilder()
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningEffort>())
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningOutput>())
        .Build();

    private static readonly ISerializer s_serializer = YamlDefaults.CreateSerializerBuilder()
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningEffort>())
        .WithTypeConverter(new StringEnumYamlConverter<ReasoningOutput>())
        .Build();

    /// <summary>
    /// Deserializes an <see cref="AgentConfig"/> from a YAML string.
    /// </summary>
    public static AgentConfig FromYaml(string yaml)
    {
        return s_deserializer.Deserialize<AgentConfig>(yaml)
            ?? throw new InvalidOperationException("Failed to deserialize AgentConfig from YAML.");
    }

    /// <summary>
    /// Deserializes an <see cref="AgentConfig"/> from a YAML file.
    /// </summary>
    public static AgentConfig FromYamlFile(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return FromYaml(yaml);
    }

    /// <summary>
    /// Serializes an <see cref="AgentConfig"/> to a YAML string.
    /// </summary>
    public static string ToYaml(AgentConfig config)
    {
        return s_serializer.Serialize(config);
    }
}

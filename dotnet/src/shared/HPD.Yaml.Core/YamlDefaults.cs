using HPD.Yaml.Core.Converters;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HPD.Yaml.Core;

/// <summary>
/// Provides pre-configured YamlDotNet serializer/deserializer builders with
/// shared HPD converters (TimeSpan, JsonElement, Uri) already registered.
/// Domain-specific projects add their own type converters on top.
/// </summary>
public static class YamlDefaults
{
    /// <summary>
    /// Creates a <see cref="DeserializerBuilder"/> pre-configured with HPD shared converters
    /// and camelCase naming convention (matching STJ defaults).
    /// </summary>
    public static DeserializerBuilder CreateDeserializerBuilder()
    {
        return new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new TimeSpanYamlConverter())
            .WithTypeConverter(new JsonElementYamlConverter())
            .WithTypeConverter(new UriYamlConverter())
            .IgnoreUnmatchedProperties();
    }

    /// <summary>
    /// Creates a <see cref="SerializerBuilder"/> pre-configured with HPD shared converters
    /// and camelCase naming convention (matching STJ defaults).
    /// </summary>
    public static SerializerBuilder CreateSerializerBuilder()
    {
        return new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new TimeSpanYamlConverter())
            .WithTypeConverter(new JsonElementYamlConverter())
            .WithTypeConverter(new UriYamlConverter())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull);
    }
}

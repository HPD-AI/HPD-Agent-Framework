using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HPD.OpenApi.Core;

/// <summary>
/// Converts YAML strings/streams to JSON for OpenAPI spec processing.
/// Used internally to enable YAML spec support without changing the existing
/// JSON-based parsing pipeline.
/// </summary>
internal static class YamlToJsonConverter
{
    private static readonly IDeserializer s_yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance) // Preserve original casing
        .Build();

    /// <summary>
    /// Converts a YAML string to a JSON string.
    /// </summary>
    public static string ConvertToJson(string yaml)
    {
        var yamlObject = s_yamlDeserializer.Deserialize<object>(yaml);
        return JsonSerializer.Serialize(yamlObject, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Converts a YAML stream to a JSON MemoryStream.
    /// </summary>
    public static MemoryStream ConvertToJsonStream(Stream yamlStream)
    {
        using var reader = new StreamReader(yamlStream);
        var yaml = reader.ReadToEnd();
        var json = ConvertToJson(yaml);
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Determines if a file path points to a YAML file based on extension.
    /// </summary>
    public static bool IsYamlFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }
}

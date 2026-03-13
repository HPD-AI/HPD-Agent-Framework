using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace HPD.Yaml.Core.Converters;

/// <summary>
/// YAML converter for <see cref="Uri"/>.
/// Reads URI strings and writes them back as plain strings.
/// </summary>
public sealed class UriYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Uri);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();

        if (string.IsNullOrWhiteSpace(scalar.Value))
            return null;

        if (Uri.TryCreate(scalar.Value, UriKind.RelativeOrAbsolute, out var uri))
            return uri;

        throw new YamlException(scalar.Start, scalar.End,
            $"Cannot parse '{scalar.Value}' as Uri.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is Uri uri)
        {
            emitter.Emit(new Scalar(uri.ToString()));
        }
        else
        {
            emitter.Emit(new Scalar("null"));
        }
    }
}

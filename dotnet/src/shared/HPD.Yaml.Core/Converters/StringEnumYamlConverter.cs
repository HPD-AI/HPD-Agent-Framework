using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace HPD.Yaml.Core.Converters;

/// <summary>
/// Generic YAML converter for enum types.
/// Reads case-insensitive enum names and writes PascalCase names.
/// </summary>
public sealed class StringEnumYamlConverter<TEnum> : IYamlTypeConverter where TEnum : struct, Enum
{
    public bool Accepts(Type type) => type == typeof(TEnum) || type == typeof(TEnum?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            if (type == typeof(TEnum?))
                return null;
            return default(TEnum);
        }

        if (Enum.TryParse<TEnum>(scalar.Value, ignoreCase: true, out var result))
            return result;

        throw new YamlException(scalar.Start, scalar.End,
            $"Cannot parse '{scalar.Value}' as {typeof(TEnum).Name}. " +
            $"Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is TEnum e)
        {
            emitter.Emit(new Scalar(e.ToString()));
        }
        else
        {
            emitter.Emit(new Scalar("null"));
        }
    }
}

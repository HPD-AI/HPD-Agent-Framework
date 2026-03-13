using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace HPD.Yaml.Core.Converters;

/// <summary>
/// YAML converter for <see cref="JsonElement"/> and <see cref="JsonElement?"/>.
/// Reads YAML scalars/sequences/mappings and converts them to JsonElement.
/// Writes JsonElement back to YAML scalars.
/// </summary>
public sealed class JsonElementYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(JsonElement) || type == typeof(JsonElement?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (string.IsNullOrEmpty(scalar.Value) && type == typeof(JsonElement?))
                return null;

            return ParseScalar(scalar);
        }

        // For sequences and mappings, deserialize as object then convert
        var obj = rootDeserializer(typeof(object));
        if (obj == null)
        {
            if (type == typeof(JsonElement?))
                return null;
            return JsonDocument.Parse("null").RootElement;
        }

        var json = JsonSerializer.Serialize(obj);
        return JsonDocument.Parse(json).RootElement;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is JsonElement element)
        {
            EmitJsonElement(emitter, element);
        }
        else
        {
            emitter.Emit(new Scalar("null"));
        }
    }

    private static JsonElement ParseScalar(Scalar scalar)
    {
        var value = scalar.Value;

        // Boolean
        if (bool.TryParse(value, out var b))
            return JsonDocument.Parse(b ? "true" : "false").RootElement;

        // Integer
        if (long.TryParse(value, out var l))
            return JsonDocument.Parse(l.ToString()).RootElement;

        // Floating point
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonDocument.Parse(d.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement;

        // Null
        if (value is "null" or "~" or "")
            return JsonDocument.Parse("null").RootElement;

        // String
        return JsonDocument.Parse($"\"{JsonEncodedText.Encode(value)}\"").RootElement;
    }

    private static void EmitJsonElement(IEmitter emitter, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                emitter.Emit(new Scalar(element.GetString() ?? ""));
                break;
            case JsonValueKind.Number:
                emitter.Emit(new Scalar(element.GetRawText()));
                break;
            case JsonValueKind.True:
                emitter.Emit(new Scalar("true"));
                break;
            case JsonValueKind.False:
                emitter.Emit(new Scalar("false"));
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                emitter.Emit(new Scalar("null"));
                break;
            default:
                // For arrays/objects, emit as raw JSON string
                emitter.Emit(new Scalar(element.GetRawText()));
                break;
        }
    }
}

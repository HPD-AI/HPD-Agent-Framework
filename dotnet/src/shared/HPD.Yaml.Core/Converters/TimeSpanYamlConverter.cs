using System.Globalization;
using System.Xml;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace HPD.Yaml.Core.Converters;

/// <summary>
/// YAML converter for <see cref="TimeSpan"/>.
/// Accepts ISO 8601 duration (e.g. "PT30S", "PT5M", "P1DT2H") or
/// .NET TimeSpan format (e.g. "00:00:30", "1.02:00:00").
/// Serializes to ISO 8601 duration format.
/// </summary>
public sealed class TimeSpanYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TimeSpan) || type == typeof(TimeSpan?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            if (type == typeof(TimeSpan?))
                return null;
            throw new YamlException(scalar.Start, scalar.End, "TimeSpan value cannot be empty.");
        }

        // Try ISO 8601 duration first (PT30S, PT5M, P1DT2H30M, etc.)
        if (scalar.Value.StartsWith("P", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return XmlConvert.ToTimeSpan(scalar.Value);
            }
            catch (FormatException)
            {
                // Fall through to .NET format
            }
        }

        // Try .NET TimeSpan format (00:00:30, 1.02:00:00, etc.)
        if (TimeSpan.TryParse(scalar.Value, CultureInfo.InvariantCulture, out var ts))
            return ts;

        // Try simple suffixed format (30s, 5m, 2h, 1d)
        if (TryParseSuffixed(scalar.Value, out ts))
            return ts;

        throw new YamlException(scalar.Start, scalar.End,
            $"Cannot parse '{scalar.Value}' as TimeSpan. " +
            "Use ISO 8601 (PT30S), .NET format (00:00:30), or suffixed (30s, 5m, 2h, 1d).");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is TimeSpan ts)
        {
            emitter.Emit(new Scalar(XmlConvert.ToString(ts)));
        }
        else
        {
            emitter.Emit(new Scalar("null"));
        }
    }

    private static bool TryParseSuffixed(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (value.Length < 2)
            return false;

        var suffix = value[^1];
        if (!double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;

        result = suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(number),
            'm' or 'M' => TimeSpan.FromMinutes(number),
            'h' or 'H' => TimeSpan.FromHours(number),
            'd' or 'D' => TimeSpan.FromDays(number),
            _ => TimeSpan.Zero
        };

        return suffix is 's' or 'S' or 'm' or 'M' or 'h' or 'H' or 'd' or 'D';
    }
}

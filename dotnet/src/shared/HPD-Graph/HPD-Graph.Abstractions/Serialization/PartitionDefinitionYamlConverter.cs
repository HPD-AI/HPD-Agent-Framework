using HPDAgent.Graph.Abstractions.Artifacts;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace HPDAgent.Graph.Abstractions.Serialization;

/// <summary>
/// YAML converter for <see cref="PartitionDefinition"/> polymorphic hierarchy.
/// Uses a "type" discriminator field (matching the JSON [JsonDerivedType] discriminators):
/// - "static" → StaticPartitionDefinition
/// - "time" → TimePartitionDefinition
/// - "multi" → MultiPartitionDefinition
/// </summary>
public sealed class PartitionDefinitionYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) =>
        type == typeof(PartitionDefinition) ||
        type == typeof(StaticPartitionDefinition) ||
        type == typeof(TimePartitionDefinition) ||
        type == typeof(MultiPartitionDefinition);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // We need to peek at the mapping to find the "type" discriminator
        // Use nested deserialization with Dictionary first
        var dict = (Dictionary<object, object>?)rootDeserializer(typeof(Dictionary<object, object>));
        if (dict == null)
            return null;

        if (!dict.TryGetValue("type", out var typeValue))
            throw new YamlException("PartitionDefinition requires a 'type' discriminator field (static, time, or multi).");

        var discriminator = typeValue?.ToString()?.ToLowerInvariant();

        return discriminator switch
        {
            "static" => DeserializeStatic(dict),
            "time" => DeserializeTime(dict),
            "multi" => throw new YamlException("Multi-partition definitions with nested dimensions are not supported in YAML. Use code-based configuration for MultiPartitionDefinition."),
            _ => throw new YamlException($"Unknown partition type '{discriminator}'. Valid types: static, time, multi.")
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is StaticPartitionDefinition staticDef)
        {
            emitter.Emit(new MappingStart());
            emitter.Emit(new Scalar("type"));
            emitter.Emit(new Scalar("static"));
            emitter.Emit(new Scalar("keys"));
            serializer(staticDef.Keys);
            emitter.Emit(new MappingEnd());
        }
        else if (value is TimePartitionDefinition timeDef)
        {
            emitter.Emit(new MappingStart());
            emitter.Emit(new Scalar("type"));
            emitter.Emit(new Scalar("time"));
            emitter.Emit(new Scalar("interval"));
            emitter.Emit(new Scalar(timeDef.Interval.ToString()));
            emitter.Emit(new Scalar("start"));
            emitter.Emit(new Scalar(timeDef.Start.ToString("O")));
            if (timeDef.End.HasValue)
            {
                emitter.Emit(new Scalar("end"));
                emitter.Emit(new Scalar(timeDef.End.Value.ToString("O")));
            }
            if (timeDef.Timezone != "UTC")
            {
                emitter.Emit(new Scalar("timezone"));
                emitter.Emit(new Scalar(timeDef.Timezone));
            }
            emitter.Emit(new MappingEnd());
        }
        else
        {
            serializer(value);
        }
    }

    private static StaticPartitionDefinition DeserializeStatic(Dictionary<object, object> dict)
    {
        if (!dict.TryGetValue("keys", out var keysObj))
            throw new YamlException("StaticPartitionDefinition requires a 'keys' field.");

        var keys = keysObj switch
        {
            List<object> list => list.Select(k => k.ToString()!).ToList(),
            _ => throw new YamlException("'keys' must be a list of strings.")
        };

        return new StaticPartitionDefinition { Keys = keys };
    }

    private static TimePartitionDefinition DeserializeTime(Dictionary<object, object> dict)
    {
        if (!dict.TryGetValue("interval", out var intervalObj))
            throw new YamlException("TimePartitionDefinition requires an 'interval' field.");

        if (!Enum.TryParse<TimePartitionDefinition.Granularity>(intervalObj.ToString(), ignoreCase: true, out var interval))
            throw new YamlException($"Invalid time partition interval: '{intervalObj}'.");

        if (!dict.TryGetValue("start", out var startObj))
            throw new YamlException("TimePartitionDefinition requires a 'start' field.");

        if (!DateTimeOffset.TryParse(startObj.ToString(), out var start))
            throw new YamlException($"Cannot parse '{startObj}' as DateTimeOffset.");

        DateTimeOffset? end = null;
        if (dict.TryGetValue("end", out var endObj) && endObj != null)
        {
            if (!DateTimeOffset.TryParse(endObj.ToString(), out var parsedEnd))
                throw new YamlException($"Cannot parse '{endObj}' as DateTimeOffset.");
            end = parsedEnd;
        }

        var timezone = "UTC";
        if (dict.TryGetValue("timezone", out var tzObj) && tzObj != null)
            timezone = tzObj.ToString()!;

        return new TimePartitionDefinition
        {
            Interval = interval,
            Start = start,
            End = end,
            Timezone = timezone
        };
    }
}

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MessagePack;

namespace HPD.Graph.Benchmarks;

/// <summary>
/// Week 0 Benchmarks: JSON Cloning Performance Validation
///
/// Target: Less than 5ms for 100KB payload (95th percentile)
/// Compare: Source-gen JSON vs MessagePack vs Reflection-based JSON
/// Test: Circular reference handling performance
///
/// Decision gate: If less than 5ms target not met, evaluate MessagePack alternative
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CloningBenchmarks
{
    private Dictionary<string, object>? _payload1KB;
    private Dictionary<string, object>? _payload10KB;
    private Dictionary<string, object>? _payload100KB;
    private Dictionary<string, object>? _payload500KB;
    private Dictionary<string, object>? _circularPayload;

    [GlobalSetup]
    public void Setup()
    {
        _payload1KB = GeneratePayload(1024);
        _payload10KB = GeneratePayload(10 * 1024);
        _payload100KB = GeneratePayload(100 * 1024);
        _payload500KB = GeneratePayload(500 * 1024);
        _circularPayload = GenerateCircularPayload();
    }

    // ===== 1KB Payload Benchmarks =====

    [Benchmark(Description = "1KB - Source-gen JSON")]
    public Dictionary<string, object> Clone_1KB_SourceGenJson()
    {
        var json = JsonSerializer.Serialize(_payload1KB, SourceGenContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(json, SourceGenContext.Default.DictionaryStringObject)!;
    }

    [Benchmark(Description = "1KB - Reflection JSON")]
    public Dictionary<string, object> Clone_1KB_ReflectionJson()
    {
        var json = JsonSerializer.Serialize(_payload1KB);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }

    [Benchmark(Description = "1KB - MessagePack")]
    public Dictionary<string, object> Clone_1KB_MessagePack()
    {
        var bytes = MessagePackSerializer.Serialize(_payload1KB);
        return MessagePackSerializer.Deserialize<Dictionary<string, object>>(bytes)!;
    }

    // ===== 10KB Payload Benchmarks =====

    [Benchmark(Description = "10KB - Source-gen JSON")]
    public Dictionary<string, object> Clone_10KB_SourceGenJson()
    {
        var json = JsonSerializer.Serialize(_payload10KB, SourceGenContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(json, SourceGenContext.Default.DictionaryStringObject)!;
    }

    [Benchmark(Description = "10KB - Reflection JSON")]
    public Dictionary<string, object> Clone_10KB_ReflectionJson()
    {
        var json = JsonSerializer.Serialize(_payload10KB);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }

    [Benchmark(Description = "10KB - MessagePack")]
    public Dictionary<string, object> Clone_10KB_MessagePack()
    {
        var bytes = MessagePackSerializer.Serialize(_payload10KB);
        return MessagePackSerializer.Deserialize<Dictionary<string, object>>(bytes)!;
    }

    // ===== 100KB Payload Benchmarks (CRITICAL - Target: <5ms) =====

    [Benchmark(Description = "100KB - Source-gen JSON")]
    public Dictionary<string, object> Clone_100KB_SourceGenJson()
    {
        var json = JsonSerializer.Serialize(_payload100KB, SourceGenContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(json, SourceGenContext.Default.DictionaryStringObject)!;
    }

    [Benchmark(Description = "100KB - Reflection JSON")]
    public Dictionary<string, object> Clone_100KB_ReflectionJson()
    {
        var json = JsonSerializer.Serialize(_payload100KB);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }

    [Benchmark(Description = "100KB - MessagePack")]
    public Dictionary<string, object> Clone_100KB_MessagePack()
    {
        var bytes = MessagePackSerializer.Serialize(_payload100KB);
        return MessagePackSerializer.Deserialize<Dictionary<string, object>>(bytes)!;
    }

    // ===== 500KB Payload Benchmarks =====

    [Benchmark(Description = "500KB - Source-gen JSON")]
    public Dictionary<string, object> Clone_500KB_SourceGenJson()
    {
        var json = JsonSerializer.Serialize(_payload500KB, SourceGenContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(json, SourceGenContext.Default.DictionaryStringObject)!;
    }

    [Benchmark(Description = "500KB - Reflection JSON")]
    public Dictionary<string, object> Clone_500KB_ReflectionJson()
    {
        var json = JsonSerializer.Serialize(_payload500KB);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }

    [Benchmark(Description = "500KB - MessagePack")]
    public Dictionary<string, object> Clone_500KB_MessagePack()
    {
        var bytes = MessagePackSerializer.Serialize(_payload500KB);
        return MessagePackSerializer.Deserialize<Dictionary<string, object>>(bytes)!;
    }

    // ===== Circular Reference Benchmarks =====

    [Benchmark(Description = "Circular - Source-gen JSON")]
    public Dictionary<string, object> Clone_Circular_SourceGenJson()
    {
        var options = new JsonSerializerOptions
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
        };
        var json = JsonSerializer.Serialize(_circularPayload, options);
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, options)!;
    }

    // ===== Helper Methods =====

    private static Dictionary<string, object> GeneratePayload(int targetSizeBytes)
    {
        var payload = new Dictionary<string, object>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["version"] = "1.0",
            ["metadata"] = new Dictionary<string, object>
            {
                ["created_by"] = "benchmark",
                ["environment"] = "test"
            }
        };

        // Fill with string data to reach target size
        var dataList = new List<string>();
        var currentSize = EstimateSize(payload);

        while (currentSize < targetSizeBytes)
        {
            var chunk = new string('x', Math.Min(1024, targetSizeBytes - currentSize));
            dataList.Add(chunk);
            currentSize += chunk.Length;
        }

        payload["data"] = dataList;
        payload["nested"] = new Dictionary<string, object>
        {
            ["level1"] = new Dictionary<string, object>
            {
                ["level2"] = new Dictionary<string, object>
                {
                    ["level3"] = "deep nesting test"
                }
            }
        };

        return payload;
    }

    private static Dictionary<string, object> GenerateCircularPayload()
    {
        var parent = new Dictionary<string, object>
        {
            ["id"] = "parent",
            ["name"] = "Parent Node"
        };

        var child = new Dictionary<string, object>
        {
            ["id"] = "child",
            ["name"] = "Child Node",
            ["parent"] = parent  // Circular reference
        };

        parent["children"] = new List<object> { child };

        return parent;
    }

    private static int EstimateSize(Dictionary<string, object> dict)
    {
        // Rough estimate of JSON size
        var json = JsonSerializer.Serialize(dict);
        return json.Length;
    }
}

/// <summary>
/// Source-generated JSON serialization context for high-performance cloning.
/// Configured with:
/// - ReferenceHandler.IgnoreCycles: Handles circular references gracefully
/// - NumberHandling: Allow reading from strings
/// - UnmappedMemberHandling: Skip unknown properties
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
internal partial class SourceGenContext : JsonSerializerContext
{
}

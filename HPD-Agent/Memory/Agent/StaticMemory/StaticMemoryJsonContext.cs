using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Source-generated JSON serialization context for agent knowledge documents.
/// Provides AOT and trimming compatibility.
/// </summary>
[JsonSerializable(typeof(StaticMemoryDocument))]
[JsonSerializable(typeof(List<StaticMemoryDocument>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class StaticMemoryJsonContext : JsonSerializerContext
{
}

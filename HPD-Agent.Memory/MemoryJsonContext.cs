using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HPD.Agent.Memory;

/// <summary>
/// Consolidated source-generated JSON serialization context for all Memory module types.
/// Provides AOT and trimming compatibility for:
/// - Dynamic Memory: DynamicMemoryConfig
/// - Plan Mode: PlanModeConfig (state types are in HPD-Agent core)
/// - Static Memory: StaticMemoryConfig, StaticMemoryDocument
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
// Dynamic Memory types
[JsonSerializable(typeof(DynamicMemoryConfig))]
// Plan Mode types (state types like AgentPlanData are in HPD-Agent core)
[JsonSerializable(typeof(PlanModeConfig))]
// Static Memory types
[JsonSerializable(typeof(StaticMemoryConfig))]
[JsonSerializable(typeof(StaticMemoryDocument))]
[JsonSerializable(typeof(List<StaticMemoryDocument>))]
public partial class MemoryJsonContext : JsonSerializerContext
{
}

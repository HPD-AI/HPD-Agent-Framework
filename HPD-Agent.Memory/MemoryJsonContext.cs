using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HPD.Agent.Memory;

/// <summary>
/// Consolidated source-generated JSON serialization context for all Memory module types.
/// Provides AOT and trimming compatibility for:
/// - Dynamic Memory: DynamicMemoryConfig
/// - Plan Mode: PlanModeConfig, AgentPlan, PlanStep, PlanStepStatus
/// - Static Memory: StaticMemoryConfig, StaticMemoryDocument
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
// Dynamic Memory types
[JsonSerializable(typeof(DynamicMemoryConfig))]
// Plan Mode types
[JsonSerializable(typeof(PlanModeConfig))]
[JsonSerializable(typeof(AgentPlan))]
[JsonSerializable(typeof(PlanStep))]
[JsonSerializable(typeof(PlanStepStatus))]
[JsonSerializable(typeof(List<PlanStep>))]
// Static Memory types
[JsonSerializable(typeof(StaticMemoryConfig))]
[JsonSerializable(typeof(StaticMemoryDocument))]
[JsonSerializable(typeof(List<StaticMemoryDocument>))]
public partial class MemoryJsonContext : JsonSerializerContext
{
}

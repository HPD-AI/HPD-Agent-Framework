using System.Text.Json;
using System.Text.Json.Serialization;
using AgentConsoleTest;

/// <summary>
/// JSON serialization context for AgentConsoleTest-specific types (AOT-compatible).
/// Only includes types defined in this test project - core HPD types are in HPDJsonContext.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]

// --- UI State Management Types (UICore.cs) ---
[JsonSerializable(typeof(UIState))]
[JsonSerializable(typeof(StreamingState))]
[JsonSerializable(typeof(ToolCallStatus))]
[JsonSerializable(typeof(ToolCallState))]
[JsonSerializable(typeof(HistoryItem))]
[JsonSerializable(typeof(HistoryItemType))]
[JsonSerializable(typeof(SessionStats))]
[JsonSerializable(typeof(List<HistoryItem>))]
[JsonSerializable(typeof(List<ToolCallState>))]
[JsonSerializable(typeof(Dictionary<string, ToolCallState>))]

// --- Command System Types (UI/Commands/) ---
[JsonSerializable(typeof(SlashCommand))]
[JsonSerializable(typeof(CommandContext))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(CommandSuggestion))]
[JsonSerializable(typeof(List<SlashCommand>))]
[JsonSerializable(typeof(List<CommandSuggestion>))]

// --- UI Components Types (Components.cs) ---
[JsonSerializable(typeof(ResultType))]

// --- Middleware Types (Middleware/) ---
[JsonSerializable(typeof(EnvironmentContext))]
[JsonSerializable(typeof(IReadOnlyList<string>))]

// --- Workflow Handler Types (SmartQuantWorkflow.cs) ---
[JsonSerializable(typeof(ClassifierHandler))]
[JsonSerializable(typeof(GeneralAgentHandler))]
[JsonSerializable(typeof(QuickSolver1Handler))]
[JsonSerializable(typeof(QuickSolver2Handler))]
[JsonSerializable(typeof(QuickSolver3Handler))]
[JsonSerializable(typeof(QuickVerifierHandler))]
[JsonSerializable(typeof(QuickReturnHandler))]

// --- Graph Workflow Handler Types (GraphQuantWorkflow.cs) ---
[JsonSerializable(typeof(Solver1Handler))]
[JsonSerializable(typeof(Solver2Handler))]
[JsonSerializable(typeof(Solver3Handler))]
[JsonSerializable(typeof(VerifierHandler))]
[JsonSerializable(typeof(RouterHandler))]
[JsonSerializable(typeof(ReturnAnswerHandler))]
[JsonSerializable(typeof(FeedbackHandler))]

// --- Common Collection Types ---
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(List<string>))]

public partial class AgentConsoleTestJsonContext : JsonSerializerContext
{
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD.Agent.Serialization;

/// <summary>
/// Source generator context for Native AOT compatible event serialization.
/// All framework event types must be registered here for proper serialization.
/// </summary>
/// <remarks>
/// <para>
/// This context uses System.Text.Json source generation for:
/// - Zero reflection overhead
/// - Faster startup time
/// - Smaller binary size
/// - Native AOT compatibility
/// </para>
/// <para>
/// Custom events defined by users are auto-registered via the CustomEventSourceGenerator.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
// Base types
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(AgentExecutionContext))]

// Message Turn Events
[JsonSerializable(typeof(MessageTurnStartedEvent))]
[JsonSerializable(typeof(MessageTurnFinishedEvent))]
[JsonSerializable(typeof(MessageTurnErrorEvent))]

// Agent Turn Events
[JsonSerializable(typeof(AgentTurnStartedEvent))]
[JsonSerializable(typeof(AgentTurnFinishedEvent))]
[JsonSerializable(typeof(StateSnapshotEvent))]

// Content Events
[JsonSerializable(typeof(TextMessageStartEvent))]
[JsonSerializable(typeof(TextDeltaEvent))]
[JsonSerializable(typeof(TextMessageEndEvent))]

// Reasoning Events
[JsonSerializable(typeof(Reasoning))]
[JsonSerializable(typeof(ReasoningPhase))]

// Tool Events
[JsonSerializable(typeof(ToolCallStartEvent))]
[JsonSerializable(typeof(ToolCallArgsEvent))]
[JsonSerializable(typeof(ToolCallEndEvent))]
[JsonSerializable(typeof(ToolCallResultEvent))]

// Permission Events
[JsonSerializable(typeof(PermissionRequestEvent))]
[JsonSerializable(typeof(PermissionResponseEvent))]
[JsonSerializable(typeof(PermissionApprovedEvent))]
[JsonSerializable(typeof(PermissionDeniedEvent))]
[JsonSerializable(typeof(ContinuationRequestEvent))]
[JsonSerializable(typeof(ContinuationResponseEvent))]

// Clarification Events
[JsonSerializable(typeof(ClarificationRequestEvent))]
[JsonSerializable(typeof(ClarificationResponseEvent))]

// Middleware Events
[JsonSerializable(typeof(MiddlewareProgressEvent))]
[JsonSerializable(typeof(MiddlewareErrorEvent))]

// Frontend Tool Events
[JsonSerializable(typeof(FrontendTools.FrontendToolInvokeRequestEvent))]
[JsonSerializable(typeof(FrontendTools.FrontendToolInvokeResponseEvent))]
[JsonSerializable(typeof(FrontendTools.FrontendPluginsRegisteredEvent))]
[JsonSerializable(typeof(FrontendTools.IToolResultContent))]
[JsonSerializable(typeof(FrontendTools.TextContent))]
[JsonSerializable(typeof(FrontendTools.BinaryContent))]
[JsonSerializable(typeof(FrontendTools.JsonContent))]
[JsonSerializable(typeof(FrontendTools.FrontendToolAugmentation))]

// Branch events removed - branching is now an application-level concern
// Applications should define their own branch event types if needed

// Observability Events
[JsonSerializable(typeof(ScopedToolsVisibleEvent))]
[JsonSerializable(typeof(ContainerExpandedEvent))]
[JsonSerializable(typeof(ContainerType))]
[JsonSerializable(typeof(MiddlewarePipelineStartEvent))]
[JsonSerializable(typeof(MiddlewarePipelineEndEvent))]
[JsonSerializable(typeof(PermissionCheckEvent))]
[JsonSerializable(typeof(IterationStartEvent))]
[JsonSerializable(typeof(CircuitBreakerTriggeredEvent))]
[JsonSerializable(typeof(HistoryReductionCacheEvent))]
[JsonSerializable(typeof(CheckpointEvent))]
[JsonSerializable(typeof(CheckpointOperation))]
[JsonSerializable(typeof(InternalParallelToolExecutionEvent))]
[JsonSerializable(typeof(InternalRetryEvent))]
[JsonSerializable(typeof(RetryStatus))]
[JsonSerializable(typeof(FunctionRetryEvent))]
[JsonSerializable(typeof(DeltaSendingActivatedEvent))]
[JsonSerializable(typeof(PlanModeActivatedEvent))]
[JsonSerializable(typeof(NestedAgentInvokedEvent))]
[JsonSerializable(typeof(DocumentProcessedEvent))]
[JsonSerializable(typeof(InternalMessagePreparedEvent))]
[JsonSerializable(typeof(BidirectionalEventProcessedEvent))]
[JsonSerializable(typeof(AgentDecisionEvent))]
[JsonSerializable(typeof(AgentCompletionEvent))]
[JsonSerializable(typeof(IterationMessagesEvent))]
[JsonSerializable(typeof(SchemaChangedEvent))]
[JsonSerializable(typeof(ScopingStateEvent))]

// Common types for IDictionary<string, object?> serialization (e.g., PermissionRequestEvent.Arguments)
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(object))]

internal partial class AgentEventJsonContext : JsonSerializerContext { }

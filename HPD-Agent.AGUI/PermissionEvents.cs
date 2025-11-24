using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

/// <summary>
/// Interface for emitting permission events to an AGUI stream.
/// This is implemented by the application's streaming infrastructure.
/// </summary>
public interface IPermissionEventEmitter
{
    Task EmitAsync<T>(T eventData) where T : BaseEvent;
}

/// <summary>
/// AGUI event for function permission requests.
/// </summary>
public sealed record FunctionPermissionRequestEvent : BaseEvent
{
    [JsonPropertyName("permission_id")]
    public required string PermissionId { get; init; }

    [JsonPropertyName("function_name")]
    public required string FunctionName { get; init; }

    [JsonPropertyName("function_description")]
    public required string FunctionDescription { get; init; }

    [JsonPropertyName("arguments")]
    public required Dictionary<string, object?> Arguments { get; init; }

    [JsonPropertyName("options")]
    public string[] Options { get; init; } = ["Allow", "Deny", "Always Allow", "Always Deny"];
}

/// <summary>
/// AGUI event for continuation permission requests when function call limits are reached.
/// </summary>
public sealed record ContinuationPermissionRequestEvent : BaseEvent
{
    [JsonPropertyName("permission_id")]
    public required string PermissionId { get; init; }

    [JsonPropertyName("current_iteration")]
    public required int CurrentIteration { get; init; }

    [JsonPropertyName("max_iterations")]
    public required int MaxIterations { get; init; }

    [JsonPropertyName("completed_functions")]
    public required string[] CompletedFunctions { get; init; }

    [JsonPropertyName("elapsed_time")]
    public required string ElapsedTime { get; init; }

    [JsonPropertyName("options")]
    public string[] Options { get; init; } = ["Continue", "Stop", "Set New Limit"];
}

/// <summary>
/// A data model for permission responses sent from a web client.
/// This is typically deserialized from a custom AGUI event.
/// </summary>
public class PermissionResponsePayload
{
    public string PermissionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "function" or "continuation"
    public bool Approved { get; set; }
    public bool RememberChoice { get; set; }

    // For continuation responses
    public int? NewMaxIterations { get; set; } // When user chooses "Set New Limit"

    public object? AdditionalData { get; set; }
}

using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Codex;

/// <summary>
/// OpenAI Codex/Responses API input types.
/// These match the format expected by the ChatGPT backend API, not the standard OpenAI API.
/// </summary>

#region Input Types

/// <summary>
/// Base interface for all input items in the Codex API.
/// Note: Using explicit Type properties instead of JsonPolymorphic for better serialization control.
/// </summary>
public abstract record CodexInputItem;

/// <summary>
/// System message in the Codex API format.
/// </summary>
public record CodexSystemMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "system"; // Can be "system" or "developer"

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

/// <summary>
/// User message in the Codex API format.
/// </summary>
public record CodexUserMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    /// <summary>
    /// Content parts. Use List&lt;object&gt; to ensure derived types serialize correctly.
    /// </summary>
    [JsonPropertyName("content")]
    public required List<object> Content { get; init; }
}

/// <summary>
/// User message content part.
/// Note: Using explicit Type properties instead of JsonPolymorphic for better serialization control.
/// </summary>
public abstract record CodexUserContentPart;

public record CodexInputText : CodexUserContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public record CodexInputImage : CodexUserContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_image";

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; init; }
}

public record CodexInputFile : CodexUserContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_file";

    [JsonPropertyName("file_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileUrl { get; init; }

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; init; }

    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; init; }

    [JsonPropertyName("file_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileData { get; init; }
}

/// <summary>
/// Assistant message in the Codex API format.
/// </summary>
public record CodexAssistantMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "assistant";

    [JsonPropertyName("content")]
    public required List<CodexOutputText> Content { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
}

public record CodexOutputText
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "output_text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Function call in the Codex API format.
/// </summary>
public record CodexFunctionCall : CodexInputItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function_call";

    [JsonPropertyName("call_id")]
    public required string CallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
}

/// <summary>
/// Function call output/result in the Codex API format.
/// </summary>
public record CodexFunctionCallOutput : CodexInputItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function_call_output";

    [JsonPropertyName("call_id")]
    public required string CallId { get; init; }

    [JsonPropertyName("output")]
    public required string Output { get; init; }
}

/// <summary>
/// Reference to a previous item (used for reasoning, tool results, etc.)
/// </summary>
public record CodexItemReference : CodexInputItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "item_reference";

    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

/// <summary>
/// Reasoning content for input (when sending reasoning back to the API).
/// Used when store: false and we can't use item references.
/// </summary>
public record CodexReasoningInput : CodexInputItem
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "reasoning";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("encrypted_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedContent { get; init; }

    [JsonPropertyName("summary")]
    public List<CodexReasoningSummary> Summary { get; init; } = new();
}

#endregion

#region Tool Definitions

/// <summary>
/// Tool definition for the Codex API.
/// Note: Using explicit Type properties instead of JsonPolymorphic for better serialization control.
/// </summary>
public abstract record CodexTool;

public record CodexFunctionTool : CodexTool
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public required object Parameters { get; init; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; init; }
}

#endregion

#region Request

/// <summary>
/// Request body for the Codex /responses endpoint.
/// </summary>
public record CodexRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("input")]
    public required List<object> Input { get; init; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    /// <summary>
    /// Tools list. Use List&lt;object&gt; to ensure derived types serialize correctly.
    /// </summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ToolChoice { get; init; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; init; }

    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; init; }

    /// <summary>
    /// System instructions for the model. Required by the Codex API.
    /// </summary>
    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexReasoningOptions? Reasoning { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexTextOptions? Text { get; init; }
}

public record CodexReasoningOptions
{
    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
}

public record CodexTextOptions
{
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Format { get; init; }
}

#endregion

#region Response

/// <summary>
/// Response from the Codex /responses endpoint.
/// </summary>
public record CodexResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("output")]
    public required List<CodexOutputItem> Output { get; init; }

    [JsonPropertyName("usage")]
    public CodexUsage? Usage { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexError? Error { get; init; }

    [JsonPropertyName("incomplete_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexIncompleteDetails? IncompleteDetails { get; init; }
}

/// <summary>
/// Output item from the Codex API response.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CodexMessageOutput), "message")]
[JsonDerivedType(typeof(CodexFunctionCallOutput2), "function_call")]
[JsonDerivedType(typeof(CodexReasoningOutput), "reasoning")]
public abstract record CodexOutputItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

public record CodexMessageOutput : CodexOutputItem
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "assistant";

    [JsonPropertyName("content")]
    public required List<CodexMessageContentPart> Content { get; init; }
}

public record CodexMessageContentPart
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CodexAnnotation>? Annotations { get; init; }
}

public record CodexAnnotation
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
}

public record CodexFunctionCallOutput2 : CodexOutputItem
{
    [JsonPropertyName("call_id")]
    public required string CallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }
}

public record CodexReasoningOutput : CodexOutputItem
{
    [JsonPropertyName("encrypted_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedContent { get; init; }

    [JsonPropertyName("summary")]
    public List<CodexReasoningSummary>? Summary { get; init; }
}

public record CodexReasoningSummary
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "summary_text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public record CodexUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }

    [JsonPropertyName("input_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexTokenDetails? InputTokensDetails { get; init; }

    [JsonPropertyName("output_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodexTokenDetails? OutputTokensDetails { get; init; }
}

public record CodexTokenDetails
{
    [JsonPropertyName("cached_tokens")]
    public int? CachedTokens { get; init; }

    [JsonPropertyName("reasoning_tokens")]
    public int? ReasoningTokens { get; init; }
}

public record CodexError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public record CodexIncompleteDetails
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

#endregion

#region Streaming

/// <summary>
/// Base type for streaming events from the Codex API.
/// </summary>
public record CodexStreamEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public record CodexResponseCreatedEvent : CodexStreamEvent
{
    [JsonPropertyName("response")]
    public required CodexResponseCreatedData Response { get; init; }
}

public record CodexResponseCreatedData
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }
}

public record CodexTextDeltaEvent : CodexStreamEvent
{
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public record CodexOutputItemAddedEvent : CodexStreamEvent
{
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("item")]
    public required CodexOutputItem Item { get; init; }
}

public record CodexOutputItemDoneEvent : CodexStreamEvent
{
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("item")]
    public required CodexOutputItem Item { get; init; }
}

public record CodexResponseCompletedEvent : CodexStreamEvent
{
    [JsonPropertyName("response")]
    public required CodexResponse Response { get; init; }
}

public record CodexFunctionCallArgumentsDeltaEvent : CodexStreamEvent
{
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public record CodexErrorEvent : CodexStreamEvent
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Event emitted when a new reasoning summary part is added.
/// </summary>
public record CodexReasoningSummaryPartAddedEvent : CodexStreamEvent
{
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; init; }
}

/// <summary>
/// Event emitted for reasoning summary text deltas during streaming.
/// </summary>
public record CodexReasoningSummaryTextDeltaEvent : CodexStreamEvent
{
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; init; }

    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

#endregion

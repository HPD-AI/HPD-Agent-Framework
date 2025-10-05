using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.Providers.OpenRouter;

/// <summary>
/// OpenRouter-specific response models for handling reasoning content
/// </summary>
internal class OpenRouterChatResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenRouterChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public OpenRouterUsage? Usage { get; set; }
}

internal class OpenRouterChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public OpenRouterMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_details")]
    public List<OpenRouterReasoningDetail>? ReasoningDetails { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenRouterToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; set; }
}

internal class OpenRouterReasoningDetail
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } // "reasoning.summary" | "reasoning.encrypted" | "reasoning.text"

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; } // "anthropic-claude-v1" | "openai-responses-v1"

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; } // encrypted data

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

internal class OpenRouterToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public OpenRouterFunctionCall? Function { get; set; }
}

internal class OpenRouterFunctionCall
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal class OpenRouterUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

// Streaming response models
internal class OpenRouterStreamingResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenRouterStreamingChoice> Choices { get; set; } = new();
}

internal class OpenRouterStreamingChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public OpenRouterDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal class OpenRouterDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_details")]
    public List<OpenRouterReasoningDetail>? ReasoningDetails { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenRouterToolCallDelta>? ToolCalls { get; set; }
}

internal class OpenRouterToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public OpenRouterFunctionCallDelta? Function { get; set; }
}

internal class OpenRouterFunctionCallDelta
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

// Request models for sending messages to OpenRouter
internal class OpenRouterChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenRouterRequestMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenRouterRequestTool>? Tools { get; set; }

    [JsonPropertyName("reasoning")]
    public OpenRouterReasoningConfig? Reasoning { get; set; }
}

internal class OpenRouterReasoningConfig
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("effort")]
    public string? Effort { get; set; } // "high", "medium", "low"

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("exclude")]
    public bool? Exclude { get; set; }
}

internal class OpenRouterRequestMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenRouterRequestToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("reasoning_details")]
    public List<OpenRouterReasoningDetail>? ReasoningDetails { get; set; }
}

internal class OpenRouterRequestToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenRouterRequestFunction Function { get; set; } = new();
}

internal class OpenRouterRequestFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

internal class OpenRouterRequestTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenRouterRequestToolFunction Function { get; set; } = new();
}

internal class OpenRouterRequestToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

using System.Text.Json.Serialization;
using System.Text.Json;

/// <summary>
/// Request model for OpenRouter API
/// </summary>
public class OpenRouterRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenRouterMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenRouterTool>? Tools { get; set; }

    [JsonPropertyName("response_format")]
    public object? ResponseFormat { get; set; }

    [JsonPropertyName("reasoning")]
    public OpenRouterReasoning? Reasoning { get; set; }
}

/// <summary>
/// Response model for OpenRouter API
/// </summary>
public class OpenRouterResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenRouterChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenRouterUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public OpenRouterError? Error { get; set; }
}

/// <summary>
/// Message model for OpenRouter API
/// </summary>
public class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenRouterToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("reasoning")]
    public JsonElement? Reasoning { get; set; }
}

/// <summary>
/// Tool call model for OpenRouter API
/// </summary>
public class OpenRouterToolCall
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public OpenRouterFunction? Function { get; set; }
}

/// <summary>
/// Function model for OpenRouter API
/// </summary>
public class OpenRouterFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

/// <summary>
/// Tool model for OpenRouter API
/// </summary>
public class OpenRouterTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenRouterToolFunction? Function { get; set; }
}

/// <summary>
/// Tool function model for OpenRouter API
/// </summary>
public class OpenRouterToolFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public System.Text.Json.JsonElement Parameters { get; set; }
}

/// <summary>
/// Choice model for OpenRouter API
/// </summary>
public class OpenRouterChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public OpenRouterMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public OpenRouterMessage? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>
/// Usage model for OpenRouter API
/// </summary>
public class OpenRouterUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>
/// Error model for OpenRouter API
/// </summary>
public class OpenRouterError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary>
/// Reasoning configuration for the OpenRouter API.
/// </summary>
public class OpenRouterReasoning
{
    [JsonPropertyName("exclude")]
    public bool? Exclude { get; set; }

    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}


using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.Providers.OpenRouter;

/// <summary>
/// Source-generated JSON type information for OpenRouter client.
/// Enables AOT-safe JSON serialization/deserialization.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenRouterChatResponse))]
[JsonSerializable(typeof(OpenRouterStreamingResponse))]
[JsonSerializable(typeof(OpenRouterChoice))]
[JsonSerializable(typeof(OpenRouterMessage))]
[JsonSerializable(typeof(OpenRouterReasoningDetail))]
[JsonSerializable(typeof(OpenRouterToolCall))]
[JsonSerializable(typeof(OpenRouterFunctionCall))]
[JsonSerializable(typeof(OpenRouterUsage))]
[JsonSerializable(typeof(OpenRouterStreamingChoice))]
[JsonSerializable(typeof(OpenRouterDelta))]
[JsonSerializable(typeof(OpenRouterToolCallDelta))]
[JsonSerializable(typeof(OpenRouterFunctionCallDelta))]
[JsonSerializable(typeof(OpenRouterChatRequest))]
[JsonSerializable(typeof(OpenRouterRequestMessage))]
[JsonSerializable(typeof(OpenRouterRequestToolCall))]
[JsonSerializable(typeof(OpenRouterRequestFunction))]
[JsonSerializable(typeof(OpenRouterRequestTool))]
[JsonSerializable(typeof(OpenRouterRequestToolFunction))]
[JsonSerializable(typeof(OpenRouterReasoningConfig))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(object))]
internal sealed partial class OpenRouterJsonContext : JsonSerializerContext;

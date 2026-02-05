using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Anthropic;

/// <summary>
/// IChatClient implementation for the Anthropic Messages API using OAuth Bearer token authentication.
/// This client handles OAuth tokens with the anthropic-beta header for Claude Code features.
/// </summary>
public class AnthropicOAuthChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly string _baseUrl;
    private readonly AnthropicOAuthClientOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public AnthropicOAuthChatClient(
        HttpClient httpClient,
        string modelId,
        string baseUrl = "https://api.anthropic.com",
        AnthropicOAuthClientOptions? options = null)
    {
        _httpClient = httpClient;
        _modelId = modelId;
        _baseUrl = baseUrl.TrimEnd('/');
        _options = options ?? new AnthropicOAuthClientOptions();
    }

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new(
        providerName: "Anthropic-OAuth",
        providerUri: new Uri("https://anthropic.com"),
        defaultModelId: _modelId);

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options, stream: false);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages");
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new AnthropicApiException(
                $"Anthropic API request failed with status {httpResponse.StatusCode}: {responseBody}",
                (int)httpResponse.StatusCode,
                responseBody);
        }

        var anthropicResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseBody, JsonOptions)
            ?? throw new AnthropicApiException("Failed to parse Anthropic response", 0, responseBody);

        return ConvertToResponse(anthropicResponse);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options, stream: true);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages");
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var httpResponse = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new AnthropicApiException(
                $"Anthropic API streaming request failed with status {httpResponse.StatusCode}: {errorBody}",
                (int)httpResponse.StatusCode,
                errorBody);
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? responseId = null;
        string? modelId = null;
        var currentToolId = "";
        var currentToolName = "";
        var toolInputBuilder = new StringBuilder();

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrEmpty(line))
                continue;

            if (!line.StartsWith("data: "))
                continue;

            var data = line["data: ".Length..];

            if (data == "[DONE]")
                break;

            JsonDocument? eventDoc = null;
            try
            {
                eventDoc = JsonDocument.Parse(data);
            }
            catch
            {
                continue;
            }

            using (eventDoc)
            {
                if (!eventDoc.RootElement.TryGetProperty("type", out var typeElement))
                    continue;

                var eventType = typeElement.GetString();

                switch (eventType)
                {
                    case "message_start":
                        if (eventDoc.RootElement.TryGetProperty("message", out var msgEl))
                        {
                            responseId = msgEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            modelId = msgEl.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
                            yield return new ChatResponseUpdate
                            {
                                ResponseId = responseId,
                                ModelId = modelId
                            };
                        }
                        break;

                    case "content_block_start":
                        if (eventDoc.RootElement.TryGetProperty("content_block", out var blockEl))
                        {
                            var blockType = blockEl.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;
                            if (blockType == "tool_use")
                            {
                                currentToolId = blockEl.TryGetProperty("id", out var tidEl) ? tidEl.GetString() ?? "" : "";
                                currentToolName = blockEl.TryGetProperty("name", out var tnEl) ? tnEl.GetString() ?? "" : "";
                                toolInputBuilder.Clear();
                            }
                        }
                        break;

                    case "content_block_delta":
                        if (eventDoc.RootElement.TryGetProperty("delta", out var deltaEl))
                        {
                            var deltaType = deltaEl.TryGetProperty("type", out var dtEl) ? dtEl.GetString() : null;

                            if (deltaType == "text_delta")
                            {
                                var text = deltaEl.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                                if (!string.IsNullOrEmpty(text))
                                {
                                    yield return new ChatResponseUpdate
                                    {
                                        ResponseId = responseId,
                                        Contents = [new TextContent(text)]
                                    };
                                }
                            }
                            else if (deltaType == "input_json_delta")
                            {
                                var partialJson = deltaEl.TryGetProperty("partial_json", out var pjEl) ? pjEl.GetString() : null;
                                if (!string.IsNullOrEmpty(partialJson))
                                {
                                    toolInputBuilder.Append(partialJson);
                                }
                            }
                            else if (deltaType == "thinking_delta")
                            {
                                var thinking = deltaEl.TryGetProperty("thinking", out var thEl) ? thEl.GetString() : null;
                                if (!string.IsNullOrEmpty(thinking))
                                {
                                    yield return new ChatResponseUpdate
                                    {
                                        ResponseId = responseId,
                                        Contents = [new TextReasoningContent(thinking)]
                                    };
                                }
                            }
                        }
                        break;

                    case "content_block_stop":
                        if (!string.IsNullOrEmpty(currentToolId))
                        {
                            var args = ParseToolInput(toolInputBuilder.ToString());
                            yield return new ChatResponseUpdate
                            {
                                ResponseId = responseId,
                                Contents = [new FunctionCallContent(currentToolId, currentToolName, args)],
                                FinishReason = ChatFinishReason.ToolCalls
                            };
                            currentToolId = "";
                            currentToolName = "";
                            toolInputBuilder.Clear();
                        }
                        break;

                    case "message_delta":
                        if (eventDoc.RootElement.TryGetProperty("delta", out var msgDeltaEl))
                        {
                            var stopReason = msgDeltaEl.TryGetProperty("stop_reason", out var srEl) ? srEl.GetString() : null;
                            var finishReason = MapStopReason(stopReason);

                            UsageDetails? usage = null;
                            if (eventDoc.RootElement.TryGetProperty("usage", out var usageEl))
                            {
                                var outputTokens = usageEl.TryGetProperty("output_tokens", out var otEl) ? otEl.GetInt32() : 0;
                                usage = new UsageDetails { OutputTokenCount = outputTokens };
                            }

                            yield return new ChatResponseUpdate
                            {
                                ResponseId = responseId,
                                FinishReason = finishReason,
                                AdditionalProperties = usage != null
                                    ? new AdditionalPropertiesDictionary
                                    {
                                        ["OutputTokens"] = usage.OutputTokenCount
                                    }
                                    : null
                            };
                        }
                        break;

                    case "message_stop":
                        // Final message - nothing to emit
                        break;

                    case "error":
                        var errorType = eventDoc.RootElement.TryGetProperty("error", out var errEl)
                            ? (errEl.TryGetProperty("type", out var etEl) ? etEl.GetString() : "unknown")
                            : "unknown";
                        var errorMessage = eventDoc.RootElement.TryGetProperty("error", out var err2El)
                            ? (err2El.TryGetProperty("message", out var emEl) ? emEl.GetString() : "Unknown error")
                            : "Unknown error";
                        throw new AnthropicApiException($"Anthropic streaming error: {errorType} - {errorMessage}", 0, data);
                }
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? key = null)
    {
        if (serviceType == typeof(IChatClient))
            return this;
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // HttpClient is owned externally - don't dispose
    }

    private AnthropicRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var messagesList = messages.ToList();

        // Extract system message
        var systemMessages = messagesList
            .Where(m => m.Role == ChatRole.System)
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var system = systemMessages.Count > 0 ? string.Join("\n\n", systemMessages) : null;

        // Convert messages to Anthropic format
        var anthropicMessages = messagesList
            .Where(m => m.Role != ChatRole.System)
            .Select(ConvertMessage)
            .ToList();

        var request = new AnthropicRequest
        {
            Model = _modelId,
            Messages = anthropicMessages,
            System = system,
            MaxTokens = options?.MaxOutputTokens ?? _options.MaxTokens,
            Stream = stream ? true : null,
            Temperature = options?.Temperature,
            TopP = options?.TopP
        };

        // Add tools if provided
        if (options?.Tools?.Count > 0)
        {
            request.Tools = options.Tools
                .OfType<AIFunction>()
                .Select(f => new AnthropicTool
                {
                    Name = f.Name,
                    Description = f.Description,
                    InputSchema = ConvertSchema(f.JsonSchema)
                })
                .ToList();

            // Map tool choice
            if (options.ToolMode is AutoChatToolMode)
            {
                request.ToolChoice = new { type = "auto" };
            }
            else if (options.ToolMode is RequiredChatToolMode required)
            {
                if (required.RequiredFunctionName != null)
                {
                    request.ToolChoice = new { type = "tool", name = required.RequiredFunctionName };
                }
                else
                {
                    request.ToolChoice = new { type = "any" };
                }
            }
        }

        // Add thinking configuration if specified
        if (_options.ThinkingBudgetTokens.HasValue)
        {
            request.Thinking = new AnthropicThinking
            {
                Type = "enabled",
                BudgetTokens = _options.ThinkingBudgetTokens.Value
            };
        }

        return request;
    }

    private static AnthropicMessage ConvertMessage(ChatMessage message)
    {
        var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
        var content = new List<object>();

        foreach (var item in message.Contents)
        {
            switch (item)
            {
                case TextContent text:
                    content.Add(new { type = "text", text = text.Text });
                    break;

                case DataContent dataContent when dataContent.MediaType?.StartsWith("image/") == true:
                    content.Add(new
                    {
                        type = "image",
                        source = new
                        {
                            type = "base64",
                            media_type = dataContent.MediaType ?? "image/png",
                            data = Convert.ToBase64String(dataContent.Data.ToArray())
                        }
                    });
                    break;

                case FunctionCallContent functionCall:
                    content.Add(new
                    {
                        type = "tool_use",
                        id = functionCall.CallId,
                        name = functionCall.Name,
                        input = functionCall.Arguments ?? new Dictionary<string, object?>()
                    });
                    break;

                case FunctionResultContent functionResult:
                    // Tool results need to be in a user message with tool_result content type
                    content.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = functionResult.CallId,
                        content = functionResult.Result?.ToString() ?? ""
                    });
                    break;
            }
        }

        // If only text content, simplify to string
        if (content.Count == 1 && content[0] is { } obj && obj.GetType().GetProperty("type")?.GetValue(obj)?.ToString() == "text")
        {
            var textProp = obj.GetType().GetProperty("text");
            var textValue = textProp?.GetValue(obj)?.ToString();
            return new AnthropicMessage { Role = role, Content = textValue ?? "" };
        }

        return new AnthropicMessage { Role = role, Content = content };
    }

    private static JsonNode? ConvertSchema(JsonElement? schema)
    {
        if (schema == null) return null;
        return JsonNode.Parse(schema.Value.GetRawText());
    }

    private ChatResponse ConvertToResponse(AnthropicResponse response)
    {
        var contents = new List<AIContent>();
        ChatFinishReason? finishReason = null;

        foreach (var block in response.Content ?? [])
        {
            if (block.TryGetProperty("type", out var typeEl))
            {
                var blockType = typeEl.GetString();

                if (blockType == "text" && block.TryGetProperty("text", out var textEl))
                {
                    contents.Add(new TextContent(textEl.GetString() ?? ""));
                }
                else if (blockType == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inputEl)
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(inputEl.GetRawText())
                        : null;
                    contents.Add(new FunctionCallContent(id, name, input));
                    finishReason = ChatFinishReason.ToolCalls;
                }
                else if (blockType == "thinking" && block.TryGetProperty("thinking", out var thinkEl))
                {
                    contents.Add(new TextReasoningContent(thinkEl.GetString() ?? ""));
                }
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, contents);

        return new ChatResponse([message])
        {
            ResponseId = response.Id,
            ModelId = response.Model,
            FinishReason = finishReason ?? MapStopReason(response.StopReason),
            Usage = response.Usage != null
                ? new UsageDetails
                {
                    InputTokenCount = response.Usage.InputTokens,
                    OutputTokenCount = response.Usage.OutputTokens,
                    TotalTokenCount = response.Usage.InputTokens + response.Usage.OutputTokens
                }
                : null
        };
    }

    private static ChatFinishReason? MapStopReason(string? stopReason)
    {
        return stopReason switch
        {
            "end_turn" => ChatFinishReason.Stop,
            "stop_sequence" => ChatFinishReason.Stop,
            "tool_use" => ChatFinishReason.ToolCalls,
            "max_tokens" => ChatFinishReason.Length,
            _ => null
        };
    }

    private static IDictionary<string, object?>? ParseToolInput(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(input);
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = input };
        }
    }
}

#region Anthropic API Types

internal class AnthropicRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; set; }

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonPropertyName("thinking")]
    public AnthropicThinking? Thinking { get; set; }
}

internal class AnthropicMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required object Content { get; set; }
}

internal class AnthropicTool
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonNode? InputSchema { get; set; }
}

internal class AnthropicThinking
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("budget_tokens")]
    public long BudgetTokens { get; set; }
}

internal class AnthropicResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("content")]
    public List<JsonElement>? Content { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

#endregion

/// <summary>
/// Exception thrown when the Anthropic API returns an error.
/// </summary>
public class AnthropicApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public AnthropicApiException(string message, int statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

using Microsoft.Extensions.AI;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Codex;

/// <summary>
/// IChatClient implementation for the OpenAI Codex/Responses API.
/// This client handles the custom request/response format used by the ChatGPT backend API.
/// </summary>
public class CodexChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _modelId;
    private readonly string _baseUrl;
    private readonly CodexClientOptions _options;

    /// <summary>
    /// JSON serializer options for Codex API requests.
    /// Based on AIJsonUtilities.DefaultOptions but with snake_case naming for API compatibility.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // Start with a copy of the default AI options for proper type handling
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            // Codex API uses snake_case naming
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            // Don't write null values to reduce payload size
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Compact output for API requests
            WriteIndented = false
        };

        return options;
    }

    public CodexChatClient(
        string modelId,
        string accessToken,
        string baseUrl = "https://chatgpt.com/backend-api/codex",
        CodexClientOptions? options = null)
    {
        _modelId = modelId;
        _baseUrl = baseUrl.TrimEnd('/');
        _options = options ?? new CodexClientOptions();

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Add required Codex headers (based on  implementation)
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("originator", "");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"HPD-Agent-CLI/1.0 ({Environment.OSVersion.Platform} {Environment.OSVersion.Version}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        // Add custom headers
        if (_options.CustomHeaders != null)
        {
            foreach (var header in _options.CustomHeaders)
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new(
        providerName: "OpenAI-Codex",
        providerUri: new Uri("https://chatgpt.com"),
        defaultModelId: _modelId);

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options, stream: false);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses");
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);

        var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new CodexApiException(
                $"Codex API request failed with status {httpResponse.StatusCode}: {responseBody}",
                (int)httpResponse.StatusCode,
                responseBody);
        }

        var codexResponse = JsonSerializer.Deserialize<CodexResponse>(responseBody, JsonOptions)
            ?? throw new CodexApiException("Failed to parse Codex response", 0, responseBody);

        if (codexResponse.Error != null)
        {
            throw new CodexApiException(
                $"Codex API error: {codexResponse.Error.Code} - {codexResponse.Error.Message}",
                400,
                responseBody);
        }

        return ConvertToResponse(codexResponse);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options, stream: true);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/responses");
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var httpResponse = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new CodexApiException(
                $"Codex API streaming request failed with status {httpResponse.StatusCode}: {errorBody}",
                (int)httpResponse.StatusCode,
                errorBody);
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var currentToolCallId = "";
        var currentToolName = "";
        var toolArgumentsBuilder = new StringBuilder();
        string? responseId = null;
        int? inputTokens = null;
        int? outputTokens = null;

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

            // Parse the event outside of try-catch to allow yielding
            var parseResult = TryParseStreamEvent(data, ref currentToolCallId, ref currentToolName, ref toolArgumentsBuilder, responseId);

            if (parseResult.Error != null)
                continue;

            // Handle function call completions (need to yield outside try-catch)
            if (parseResult.FunctionCallToYield != null)
            {
                yield return parseResult.FunctionCallToYield;
                continue;
            }

            var streamEvent = parseResult.Event;
            if (streamEvent == null)
                continue;

            switch (streamEvent)
            {
                case CodexResponseCreatedEvent created:
                    responseId = created.Response.Id;
                    yield return new ChatResponseUpdate
                    {
                        ResponseId = responseId,
                        ModelId = created.Response.Model,
                        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(created.Response.CreatedAt)
                    };
                    break;

                case CodexTextDeltaEvent textDelta:
                    yield return new ChatResponseUpdate
                    {
                        ResponseId = responseId,
                        Contents = new List<AIContent> { new TextContent(textDelta.Delta) }
                    };
                    break;

                case CodexOutputItemAddedEvent itemAdded:
                    if (itemAdded.Item is CodexFunctionCallOutput2 fc)
                    {
                        currentToolCallId = fc.CallId;
                        currentToolName = fc.Name;
                        toolArgumentsBuilder.Clear();
                    }
                    break;

                case CodexFunctionCallArgumentsDeltaEvent argsDelta:
                    toolArgumentsBuilder.Append(argsDelta.Delta);
                    break;

                case CodexOutputItemDoneEvent itemDone:
                    if (itemDone.Item is CodexFunctionCallOutput2 completedFc)
                    {
                        var args = ParseArguments(completedFc.Arguments);
                        yield return new ChatResponseUpdate
                        {
                            ResponseId = responseId,
                            Contents = new List<AIContent>
                            {
                                new FunctionCallContent(completedFc.CallId, completedFc.Name, args)
                            },
                            FinishReason = ChatFinishReason.ToolCalls
                        };
                        currentToolCallId = "";
                        currentToolName = "";
                        toolArgumentsBuilder.Clear();
                    }
                    break;

                case CodexResponseCompletedEvent completed:
                    inputTokens = completed.Response.Usage?.InputTokens;
                    outputTokens = completed.Response.Usage?.OutputTokens;

                    var hasFunctionCalls = completed.Response.Output.Any(o => o is CodexFunctionCallOutput2);
                    var finishReason = CodexMessageConverter.MapFinishReason(completed.Response, hasFunctionCalls);

                    yield return new ChatResponseUpdate
                    {
                        ResponseId = responseId,
                        FinishReason = finishReason,
                        Contents = new List<AIContent>(),
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["InputTokens"] = inputTokens,
                            ["OutputTokens"] = outputTokens
                        }
                    };
                    break;

                case CodexReasoningSummaryTextDeltaEvent reasoningDelta:
                    // Emit reasoning content as TextReasoningContent during streaming
                    yield return new ChatResponseUpdate
                    {
                        ResponseId = responseId,
                        Contents = new List<AIContent>
                        {
                            new TextReasoningContent(reasoningDelta.Delta)
                            {
                                AdditionalProperties = new AdditionalPropertiesDictionary
                                {
                                    ["itemId"] = reasoningDelta.ItemId,
                                    ["summaryIndex"] = reasoningDelta.SummaryIndex
                                }
                            }
                        }
                    };
                    break;

                case CodexReasoningSummaryPartAddedEvent:
                    // This event signals a new reasoning part started - no content to emit yet
                    break;

                case CodexErrorEvent error:
                    throw new CodexApiException($"Codex streaming error: {error.Code} - {error.Message}", 0, data);
            }
        }
    }

    /// <summary>
    /// Helper method for unhandled event types - returns null to skip them.
    /// </summary>
    private static CodexStreamEvent? LogUnhandledEvent(string? _) => null;

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
        _httpClient.Dispose();
    }

    private CodexRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        // Extract system instructions from system messages
        // The Codex API requires instructions at the top level, not in the input array
        var messagesList = messages.ToList();
        var systemMessages = messagesList.Where(m => m.Role == ChatRole.System).ToList();
        var nonSystemMessages = messagesList.Where(m => m.Role != ChatRole.System).ToList();

        // Combine all system messages into instructions
        var instructions = string.Join("\n\n", systemMessages
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t)));

        // If no system messages, provide a minimal default instruction
        if (string.IsNullOrEmpty(instructions))
        {
            instructions = "You are a helpful assistant.";
        }

        // Convert non-system messages to Codex input format
        var input = CodexMessageConverter.ConvertToCodexInput(nonSystemMessages, _modelId);

        var request = new CodexRequest
        {
            Model = _modelId,
            Input = input,
            Instructions = instructions,
            Stream = stream ? true : null,
            MaxOutputTokens = options?.MaxOutputTokens,
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            Store = false // Codex API requires store to be explicitly false
        };

        // Add tools if provided
        if (options?.Tools?.Count > 0)
        {
            request = request with
            {
                Tools = CodexMessageConverter.ConvertTools(options.Tools)
            };

            // Map tool choice
            if (options.ToolMode is AutoChatToolMode)
            {
                request = request with { ToolChoice = "auto" };
            }
            else if (options.ToolMode is RequiredChatToolMode required)
            {
                if (required.RequiredFunctionName != null)
                {
                    request = request with
                    {
                        ToolChoice = new { type = "function", function = new { name = required.RequiredFunctionName } }
                    };
                }
                else
                {
                    request = request with { ToolChoice = "required" };
                }
            }
        }

        // Add reasoning options for reasoning models
        if (CodexMessageConverter.IsReasoningModel(_modelId))
        {
            var reasoningEffort = _options.ReasoningEffort ?? "medium";
            request = request with
            {
                Reasoning = new CodexReasoningOptions
                {
                    Effort = reasoningEffort,
                    Summary = "auto"
                }
            };
        }

        return request;
    }

    private ChatResponse ConvertToResponse(CodexResponse codexResponse)
    {
        var message = CodexMessageConverter.ConvertFromCodexResponse(codexResponse);
        var hasFunctionCalls = codexResponse.Output.Any(o => o is CodexFunctionCallOutput2);

        return new ChatResponse(new List<ChatMessage> { message })
        {
            ResponseId = codexResponse.Id,
            ModelId = codexResponse.Model,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(codexResponse.CreatedAt),
            FinishReason = CodexMessageConverter.MapFinishReason(codexResponse, hasFunctionCalls),
            Usage = codexResponse.Usage != null
                ? new UsageDetails
                {
                    InputTokenCount = codexResponse.Usage.InputTokens,
                    OutputTokenCount = codexResponse.Usage.OutputTokens,
                    TotalTokenCount = codexResponse.Usage.InputTokens + codexResponse.Usage.OutputTokens
                }
                : null
        };
    }

    private static IDictionary<string, object?>? ParseArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments);
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = arguments };
        }
    }

    /// <summary>
    /// Result of parsing a stream event.
    /// </summary>
    private readonly struct StreamEventParseResult
    {
        public CodexStreamEvent? Event { get; init; }
        public ChatResponseUpdate? FunctionCallToYield { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Tries to parse a stream event from JSON data.
    /// Returns the parsed event, or a function call update to yield, or an error message.
    /// </summary>
    private StreamEventParseResult TryParseStreamEvent(
        string data,
        ref string currentToolCallId,
        ref string currentToolName,
        ref StringBuilder toolArgumentsBuilder,
        string? responseId)
    {
        try
        {
            var eventDoc = JsonDocument.Parse(data);
            var eventType = eventDoc.RootElement.GetProperty("type").GetString();

            // Handle output_item events manually due to polymorphic deserialization issues
            if (eventType == "response.output_item.added" || eventType == "response.output_item.done")
            {
                return HandleOutputItemEvent(eventDoc, eventType, ref currentToolCallId, ref currentToolName, ref toolArgumentsBuilder, responseId);
            }

            var streamEvent = eventType switch
            {
                "response.created" => JsonSerializer.Deserialize<CodexResponseCreatedEvent>(data, JsonOptions),
                "response.output_text.delta" => JsonSerializer.Deserialize<CodexTextDeltaEvent>(data, JsonOptions),
                "response.function_call_arguments.delta" => JsonSerializer.Deserialize<CodexFunctionCallArgumentsDeltaEvent>(data, JsonOptions),
                "response.reasoning_summary_text.delta" => JsonSerializer.Deserialize<CodexReasoningSummaryTextDeltaEvent>(data, JsonOptions),
                "response.reasoning_summary_part.added" => JsonSerializer.Deserialize<CodexReasoningSummaryPartAddedEvent>(data, JsonOptions),
                "response.completed" or "response.incomplete" => ParseCompletedEvent(eventDoc),
                "error" => JsonSerializer.Deserialize<CodexErrorEvent>(data, JsonOptions),
                // Ignore these events - they're informational only
                "response.in_progress" or
                "response.reasoning_summary_text.done" or
                "response.reasoning_summary_part.done" or
                "response.function_call_arguments.done" => null,
                _ => LogUnhandledEvent(eventType)
            };

            return new StreamEventParseResult { Event = streamEvent };
        }
        catch (Exception ex)
        {
            return new StreamEventParseResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// Handles output_item.added and output_item.done events manually due to polymorphic deserialization issues.
    /// </summary>
    private StreamEventParseResult HandleOutputItemEvent(
        JsonDocument eventDoc,
        string? eventType,
        ref string currentToolCallId,
        ref string currentToolName,
        ref StringBuilder toolArgumentsBuilder,
        string? responseId)
    {
        if (!eventDoc.RootElement.TryGetProperty("item", out var itemElement) ||
            !itemElement.TryGetProperty("type", out var itemTypeElement))
        {
            return new StreamEventParseResult { Event = null };
        }

        var itemType = itemTypeElement.GetString();
        if (itemType != "function_call")
        {
            // For reasoning or message items, we skip
            return new StreamEventParseResult { Event = null };
        }

        // Extract function call details directly
        var callId = itemElement.TryGetProperty("call_id", out var cid) ? cid.GetString() : "";
        var name = itemElement.TryGetProperty("name", out var n) ? n.GetString() : "";
        var arguments = itemElement.TryGetProperty("arguments", out var args) ? args.GetString() : "{}";

        if (eventType == "response.output_item.added")
        {
            currentToolCallId = callId ?? "";
            currentToolName = name ?? "";
            toolArgumentsBuilder.Clear();
            return new StreamEventParseResult { Event = null };
        }
        else // response.output_item.done
        {
            // Return the function call to be yielded
            var parsedArgs = ParseArguments(arguments ?? "{}");
            var update = new ChatResponseUpdate
            {
                ResponseId = responseId,
                Contents = new List<AIContent>
                {
                    new FunctionCallContent(callId ?? "", name ?? "", parsedArgs)
                },
                FinishReason = ChatFinishReason.ToolCalls
            };
            currentToolCallId = "";
            currentToolName = "";
            toolArgumentsBuilder.Clear();
            return new StreamEventParseResult { FunctionCallToYield = update };
        }
    }

    /// <summary>
    /// Parses a completed/incomplete event, manually extracting usage to avoid polymorphic issues.
    /// </summary>
    private static CodexResponseCompletedEvent? ParseCompletedEvent(JsonDocument eventDoc)
    {
        try
        {
            if (!eventDoc.RootElement.TryGetProperty("response", out var responseElement))
                return null;

            var id = responseElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var model = responseElement.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? "" : "";
            var createdAt = responseElement.TryGetProperty("created_at", out var catEl) ? catEl.GetInt64() : 0;

            CodexUsage? usage = null;
            if (responseElement.TryGetProperty("usage", out var usageEl))
            {
                var inputTokens = usageEl.TryGetProperty("input_tokens", out var itEl) ? itEl.GetInt32() : 0;
                var outputTokens = usageEl.TryGetProperty("output_tokens", out var otEl) ? otEl.GetInt32() : 0;
                usage = new CodexUsage
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens
                };
            }

            CodexIncompleteDetails? incompleteDetails = null;
            if (responseElement.TryGetProperty("incomplete_details", out var idEl2) && idEl2.ValueKind == JsonValueKind.Object)
            {
                var reason = idEl2.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;
                incompleteDetails = new CodexIncompleteDetails { Reason = reason };
            }

            // Create a minimal response - we don't need the full output array for streaming
            var response = new CodexResponse
            {
                Id = id,
                Model = model,
                CreatedAt = createdAt,
                Output = new List<CodexOutputItem>(), // Empty - we handle outputs via other events
                Usage = usage,
                IncompleteDetails = incompleteDetails
            };

            return new CodexResponseCompletedEvent
            {
                Type = "response.completed",
                Response = response
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Options for configuring the Codex chat client.
/// </summary>
public class CodexClientOptions
{
    /// <summary>
    /// Custom headers to include in requests.
    /// </summary>
    public Dictionary<string, string>? CustomHeaders { get; set; }

    /// <summary>
    /// Whether to store the conversation (enables item references).
    /// Note: Codex API currently requires this to be false.
    /// Default: false
    /// </summary>
    public bool Store { get; set; } = false;

    /// <summary>
    /// Reasoning effort level for reasoning models.
    /// Options: "low", "medium", "high"
    /// </summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// Exception thrown when the Codex API returns an error.
/// </summary>
public class CodexApiException : Exception
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public CodexApiException(string message, int statusCode, string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

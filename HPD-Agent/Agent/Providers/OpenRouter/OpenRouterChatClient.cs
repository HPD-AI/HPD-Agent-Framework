using Microsoft.Extensions.AI;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.Providers.OpenRouter;

/// <summary>
/// OpenRouter-specific IChatClient that properly exposes reasoning content from reasoning_details field.
/// This implementation uses HttpClient directly to parse OpenRouter's extended response format.
/// </summary>
internal sealed class OpenRouterChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly ChatClientMetadata _metadata;
    private static readonly OpenRouterJsonContext _jsonContext = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    public OpenRouterChatClient(HttpClient httpClient, string modelName)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _metadata = new ChatClientMetadata(
            providerName: "openrouter",
            providerUri: new Uri("https://openrouter.ai"),
            defaultModelId: modelName
        );
    }

    public ChatClientMetadata Metadata => _metadata;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, options, stream: false);
        var requestJson = JsonSerializer.Serialize(requestBody, _jsonContext.OpenRouterChatRequest);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();

        var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var openRouterResponse = JsonSerializer.Deserialize(responseJson, _jsonContext.OpenRouterChatResponse);

        if (openRouterResponse == null)
            throw new InvalidOperationException("Failed to deserialize OpenRouter response");

        return ConvertToChatResponse(openRouterResponse);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, options, stream: true);
        var requestJson = JsonSerializer.Serialize(requestBody, _jsonContext.OpenRouterChatRequest);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();

        using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? responseId = null;
        string? modelId = null;
        DateTimeOffset? createdAt = null;
        ChatRole? role = null;
        ChatFinishReason? finishReason = null;
        var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();
        var reasoningAccumulators = new Dictionary<int, ReasoningAccumulator>();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;

            var streamingResponse = JsonSerializer.Deserialize(data, _jsonContext.OpenRouterStreamingResponse);
            if (streamingResponse?.Choices == null || streamingResponse.Choices.Count == 0)
                continue;

            responseId ??= streamingResponse.Id;
            modelId ??= streamingResponse.Model;
            if (streamingResponse.Created > 0 && createdAt == null)
            {
                createdAt = DateTimeOffset.FromUnixTimeSeconds(streamingResponse.Created);
            }

            var choice = streamingResponse.Choices[0];
            var delta = choice.Delta;

            if (delta?.Role != null && role == null)
            {
                role = delta.Role.ToLower() switch
                {
                    "assistant" => ChatRole.Assistant,
                    "user" => ChatRole.User,
                    "system" => ChatRole.System,
                    "tool" => ChatRole.Tool,
                    _ => new ChatRole(delta.Role)
                };
            }

            if (choice.FinishReason != null && finishReason == null)
            {
                finishReason = choice.FinishReason.ToLower() switch
                {
                    "stop" => ChatFinishReason.Stop,
                    "length" => ChatFinishReason.Length,
                    "tool_calls" => ChatFinishReason.ToolCalls,
                    "content_filter" => ChatFinishReason.ContentFilter,
                    _ => ChatFinishReason.Stop
                };
            }

            var update = new ChatResponseUpdate
            {
                ResponseId = responseId,
                ModelId = modelId,
                CreatedAt = createdAt,
                Role = role,
                FinishReason = finishReason,
                RawRepresentation = streamingResponse
            };

            // Handle content
            if (!string.IsNullOrEmpty(delta?.Content))
            {
                update.Contents.Add(new TextContent(delta.Content));
            }

            // Handle reasoning details
            if (delta?.ReasoningDetails != null)
            {
                foreach (var reasoningDetail in delta.ReasoningDetails)
                {
                    if (!reasoningAccumulators.TryGetValue(reasoningDetail.Index, out var accumulator))
                    {
                        accumulator = new ReasoningAccumulator { Type = reasoningDetail.Type };
                        reasoningAccumulators[reasoningDetail.Index] = accumulator;
                    }

                    // Accumulate reasoning text/summary based on type
                    if (reasoningDetail.Type == "reasoning.text" && !string.IsNullOrEmpty(reasoningDetail.Text))
                    {
                        accumulator.Text.Append(reasoningDetail.Text);
                        update.Contents.Add(new TextReasoningContent(reasoningDetail.Text)
                        {
                            RawRepresentation = reasoningDetail
                        });
                    }
                    else if (reasoningDetail.Type == "reasoning.summary" && !string.IsNullOrEmpty(reasoningDetail.Summary))
                    {
                        accumulator.Text.Append(reasoningDetail.Summary);
                        update.Contents.Add(new TextReasoningContent(reasoningDetail.Summary)
                        {
                            RawRepresentation = reasoningDetail
                        });
                    }
                    else if (reasoningDetail.Type == "reasoning.encrypted")
                    {
                        // Store encrypted data for later
                        accumulator.EncryptedData = reasoningDetail.Data;
                    }
                }
            }

            // Handle tool calls
            if (delta?.ToolCalls != null)
            {
                foreach (var toolCallDelta in delta.ToolCalls)
                {
                    if (!toolCallAccumulators.TryGetValue(toolCallDelta.Index, out var accumulator))
                    {
                        accumulator = new ToolCallAccumulator
                        {
                            Id = toolCallDelta.Id,
                            Type = toolCallDelta.Type
                        };
                        toolCallAccumulators[toolCallDelta.Index] = accumulator;
                    }

                    accumulator.Id ??= toolCallDelta.Id;
                    accumulator.Type ??= toolCallDelta.Type;

                    if (toolCallDelta.Function != null)
                    {
                        accumulator.FunctionName ??= toolCallDelta.Function.Name;
                        if (!string.IsNullOrEmpty(toolCallDelta.Function.Arguments))
                        {
                            accumulator.Arguments.Append(toolCallDelta.Function.Arguments);
                        }
                    }
                }
            }

            yield return update;
        }

        // Emit final tool calls if any
        if (toolCallAccumulators.Count > 0)
        {
            var finalUpdate = new ChatResponseUpdate
            {
                ResponseId = responseId,
                ModelId = modelId,
                CreatedAt = createdAt,
                Role = role,
                FinishReason = finishReason
            };

            foreach (var (_, accumulator) in toolCallAccumulators.OrderBy(kvp => kvp.Key))
            {
                if (accumulator.Id != null && accumulator.FunctionName != null)
                {
                    var callContent = FunctionCallContent.CreateFromParsedArguments(
                        accumulator.Arguments.ToString(),
                        accumulator.Id,
                        accumulator.FunctionName,
                        static json =>
                        {
                            var dict = JsonSerializer.Deserialize(json, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>))) as IDictionary<string, object?> ?? new Dictionary<string, object?>();
                            // Remove __raw_json__ field that OpenRouter sometimes adds
                            dict.Remove("__raw_json__");
                            return dict;
                        });

                    finalUpdate.Contents.Add(callContent);
                }
            }

            if (finalUpdate.Contents.Count > 0)
            {
                yield return finalUpdate;
            }
        }
    }

    private ChatResponse ConvertToChatResponse(OpenRouterChatResponse openRouterResponse)
    {
        var choice = openRouterResponse.Choices.FirstOrDefault();
        if (choice?.Message == null)
            throw new InvalidOperationException("No choices in OpenRouter response");

        var message = new ChatMessage
        {
            Role = choice.Message.Role?.ToLower() switch
            {
                "assistant" => ChatRole.Assistant,
                "user" => ChatRole.User,
                "system" => ChatRole.System,
                "tool" => ChatRole.Tool,
                _ => ChatRole.Assistant
            },
            RawRepresentation = choice.Message
        };

        // Add text content
        if (!string.IsNullOrEmpty(choice.Message.Content))
        {
            message.Contents.Add(new TextContent(choice.Message.Content));
        }

        // Extract and add reasoning content
        if (choice.Message.ReasoningDetails != null)
        {
            foreach (var reasoningDetail in choice.Message.ReasoningDetails)
            {
                if (reasoningDetail.Type == "reasoning.text" && !string.IsNullOrEmpty(reasoningDetail.Text))
                {
                    message.Contents.Add(new TextReasoningContent(reasoningDetail.Text)
                    {
                        RawRepresentation = reasoningDetail
                    });
                }
                else if (reasoningDetail.Type == "reasoning.summary" && !string.IsNullOrEmpty(reasoningDetail.Summary))
                {
                    message.Contents.Add(new TextReasoningContent(reasoningDetail.Summary)
                    {
                        RawRepresentation = reasoningDetail
                    });
                }
                else if (reasoningDetail.Type == "reasoning.encrypted" && !string.IsNullOrEmpty(reasoningDetail.Data))
                {
                    // For encrypted reasoning, store in AdditionalProperties since ProtectedData might not be available in all versions
                    var reasoningContent = new TextReasoningContent(string.Empty)
                    {
                        RawRepresentation = reasoningDetail
                    };
                    if (reasoningContent.AdditionalProperties == null)
                    {
                        reasoningContent.AdditionalProperties = new AdditionalPropertiesDictionary();
                    }
                    reasoningContent.AdditionalProperties["ProtectedData"] = reasoningDetail.Data;
                    message.Contents.Add(reasoningContent);
                }
            }
        }

        // Add tool calls
        if (choice.Message.ToolCalls != null)
        {
            foreach (var toolCall in choice.Message.ToolCalls)
            {
                if (toolCall.Function != null && toolCall.Id != null)
                {
                    var callContent = FunctionCallContent.CreateFromParsedArguments(
                        toolCall.Function.Arguments ?? string.Empty,
                        toolCall.Id,
                        toolCall.Function.Name ?? string.Empty,
                        static json =>
                        {
                            var dict = JsonSerializer.Deserialize(json, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>))) as IDictionary<string, object?> ?? new Dictionary<string, object?>();
                            // Remove __raw_json__ field that OpenRouter sometimes adds
                            dict.Remove("__raw_json__");
                            return dict;
                        });

                    message.Contents.Add(callContent);
                }
            }
        }

        // Add refusal as error content
        if (!string.IsNullOrEmpty(choice.Message.Refusal))
        {
            message.Contents.Add(new ErrorContent(choice.Message.Refusal)
            {
                ErrorCode = "Refusal"
            });
        }

        var response = new ChatResponse(message)
        {
            ResponseId = openRouterResponse.Id,
            ModelId = openRouterResponse.Model,
            CreatedAt = openRouterResponse.Created > 0
                ? DateTimeOffset.FromUnixTimeSeconds(openRouterResponse.Created)
                : null,
            FinishReason = choice.FinishReason?.ToLower() switch
            {
                "stop" => ChatFinishReason.Stop,
                "length" => ChatFinishReason.Length,
                "tool_calls" => ChatFinishReason.ToolCalls,
                "content_filter" => ChatFinishReason.ContentFilter,
                _ => null
            },
            RawRepresentation = openRouterResponse
        };

        if (openRouterResponse.Usage != null)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = openRouterResponse.Usage.PromptTokens,
                OutputTokenCount = openRouterResponse.Usage.CompletionTokens,
                TotalTokenCount = openRouterResponse.Usage.TotalTokens
            };
        }

        return response;
    }

    private OpenRouterChatRequest BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var requestMessages = messages.Select(m =>
        {
            var msg = new OpenRouterRequestMessage
            {
                Role = m.Role.Value.ToLowerInvariant()
            };

            // Handle tool role messages (function results)
            if (m.Role == ChatRole.Tool && m.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } frc)
            {
                msg.ToolCallId = frc.CallId;
                // Set content to the function result
                msg.Content = frc.Result?.ToString() ?? string.Empty;
            }
            else
            {
                // For other roles, use text content (exclude TextReasoningContent)
                var textContents = m.Contents
                    .Where(c => c is TextContent && c is not TextReasoningContent)
                    .Cast<TextContent>()
                    .Select(tc => tc.Text);
                msg.Content = m.Text ?? string.Join("\n", textContents);
            }

            // DO NOT send reasoning_details back to the API
            // Root cause: OpenRouter's proxy layer has a bug when forwarding to Anthropic's native API
            // - OpenRouter returns reasoning in "reasoning_details" field (normalized OpenRouter format)
            // - Anthropic's native API expects reasoning in "content" array as "thinking" blocks
            // - OpenRouter fails to translate between these formats when proxying requests to Anthropic
            //
            // Error: "messages.1.content.0.type: Expected `thinking` or `redacted_thinking`, but found `tool_use`"
            //
            // Solution: Don't send reasoning_details in subsequent requests. The model generates fresh
            // reasoning for each turn anyway. Users still see reasoning in the initial response.
            //
            // Note: TextReasoningContent already filtered from msg.Content above

            // Add tool calls if present (for assistant role)
            var toolCalls = m.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0)
            {
                msg.ToolCalls = toolCalls.Select(fc => new OpenRouterRequestToolCall
                {
                    Id = fc.CallId,
                    Type = "function",
                    Function = new OpenRouterRequestFunction
                    {
                        Name = fc.Name,
                        Arguments = JsonSerializer.Serialize(fc.Arguments, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>)))
                    }
                }).ToList();
            }

            return msg;
        }).ToList();

        var request = new OpenRouterChatRequest
        {
            Model = _modelName,
            Messages = requestMessages,
            Stream = stream,
            Reasoning = new OpenRouterReasoningConfig
            {
                Enabled = true,  // Enable reasoning for the model
                Exclude = false  // Include reasoning in responses so users can see thinking
            }
        };

        if (options != null)
        {
            if (options.Temperature.HasValue) request.Temperature = (float)options.Temperature.Value;
            if (options.MaxOutputTokens.HasValue) request.MaxTokens = options.MaxOutputTokens.Value;
            if (options.TopP.HasValue) request.TopP = (float)options.TopP.Value;
            if (options.FrequencyPenalty.HasValue) request.FrequencyPenalty = (float)options.FrequencyPenalty.Value;
            if (options.PresencePenalty.HasValue) request.PresencePenalty = (float)options.PresencePenalty.Value;
            if (options.StopSequences?.Count > 0) request.Stop = options.StopSequences.ToList();

            // Add tools if present
            if (options.Tools?.Count > 0)
            {
                var tools = options.Tools.OfType<AIFunctionDeclaration>().Select(f =>
                {
                    // Ensure parameters object has required properties for OpenAI/Azure compatibility
                    var parameters = f.JsonSchema;

                    // Fix: OpenAI requires "properties" field even if empty
                    if (parameters.ValueKind == JsonValueKind.Object &&
                        !parameters.TryGetProperty("properties", out _))
                    {
                        // Schema is missing "properties" - rebuild it with empty properties
                        using var stream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(stream))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "object");
                            writer.WriteStartObject("properties");
                            writer.WriteEndObject(); // Empty properties
                            writer.WriteEndObject();
                        }
                        parameters = JsonDocument.Parse(stream.ToArray()).RootElement;
                    }

                    return new OpenRouterRequestTool
                    {
                        Type = "function",
                        Function = new OpenRouterRequestToolFunction
                        {
                            Name = f.Name,
                            Description = f.Description,
                            Parameters = parameters
                        }
                    };
                }).ToList();

                if (tools.Count > 0)
                {
                    request.Tools = tools;
                }
            }
        }

        return request;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType switch
        {
            Type t when t == typeof(ChatClientMetadata) => _metadata,
            Type t when t == typeof(HttpClient) => _httpClient,
            Type t when t.IsInstanceOfType(this) => this,
            _ => null
        };
    }

    public void Dispose()
    {
        // HttpClient is typically managed by the factory, so we don't dispose it here
    }

    private class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? FunctionName { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private class ReasoningAccumulator
    {
        public string? Type { get; set; }
        public StringBuilder Text { get; } = new();
        public string? EncryptedData { get; set; }
    }
}

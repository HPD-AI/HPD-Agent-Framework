using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>Represents an <see cref="IChatClient"/> for OpenRouter.</summary>
public sealed class OpenRouterChatClient : IChatClient
{
    private static readonly AIJsonSchemaTransformCache _schemaTransformCache = new(new()
    {
        ConvertBooleanSchemas = true, // Or other options as needed
    });

    private readonly ChatClientMetadata _metadata;
    private readonly Uri _apiEndpoint;
    private readonly HttpClient _httpClient;
    private readonly OpenRouterConfig _config;
    private readonly OpenRouterJsonContext _jsonContext;

    /// <summary>Initializes a new instance of the <see cref="OpenRouterChatClient"/> class.</summary>
    /// <param name="config">The configuration for OpenRouter.</param>
    /// <param name="httpClient">An <see cref="HttpClient"/> instance to use for HTTP operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is <see langword="null"/>.</exception>
    public OpenRouterChatClient(OpenRouterConfig config, HttpClient? httpClient = null)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
            
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new ArgumentException("API key cannot be null or empty", nameof(config.ApiKey));
            
        if (string.IsNullOrWhiteSpace(config.ModelName))
            throw new ArgumentException("Model name cannot be null or empty", nameof(config.ModelName));
        
        _config = config;
        
        _apiEndpoint = string.IsNullOrWhiteSpace(config.Endpoint) 
            ? new Uri("https://openrouter.ai/api/v1/chat/completions")
            : new Uri(config.Endpoint);
            
        _httpClient = httpClient ?? new HttpClient();
        
        _metadata = new ChatClientMetadata("openrouter", _apiEndpoint, config.ModelName);
        
        _jsonContext = OpenRouterJsonContext.Default;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (messages == null)
            throw new ArgumentNullException(nameof(messages));

        var requestPayload = CreateRequestPayload(messages, options, stream: false);
        var content = new StringContent(JsonSerializer.Serialize(requestPayload, _jsonContext.OpenRouterRequest), Encoding.UTF8, "application/json");
        
        var request = new HttpRequestMessage(HttpMethod.Post, _apiEndpoint)
        {
            Content = content
        };
        
        AddOpenRouterHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"OpenRouter API error: {response.StatusCode}, {error}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize(json, _jsonContext.OpenRouterResponse);
        
        if (result == null)
        {
            throw new InvalidOperationException("Failed to deserialize response from OpenRouter API.");
        }

        if (result.Error != null)
        {
            throw new InvalidOperationException($"OpenRouter error: {result.Error.Message}");
        }

        if (result.Choices == null || result.Choices.Count == 0)
        {
            throw new InvalidOperationException("No choices returned from OpenRouter API.");
        }

        var responseId = result.Id ?? Guid.NewGuid().ToString("N");
        var chatMessage = FromOpenRouterMessage(result.Choices[0].Message, responseId);
        
        DateTimeOffset? createdAt = result.Created.HasValue 
            ? DateTimeOffset.FromUnixTimeSeconds(result.Created.Value) 
            : null;
        
        return new ChatResponse(chatMessage)
        {
            CreatedAt = createdAt,
            FinishReason = ToFinishReason(result.Choices[0].FinishReason),
            ModelId = result.Model ?? options?.ModelId ?? _metadata.DefaultModelId,
            ResponseId = responseId,
            Usage = result.Usage != null ? new UsageDetails
            {
                InputTokenCount = result.Usage.PromptTokens,
                OutputTokenCount = result.Usage.CompletionTokens,
                TotalTokenCount = result.Usage.TotalTokens
            } : null
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (messages == null)
            throw new ArgumentNullException(nameof(messages));

        var requestPayload = CreateRequestPayload(messages, options, stream: true);
        var content = new StringContent(JsonSerializer.Serialize(requestPayload, _jsonContext.OpenRouterRequest), Encoding.UTF8, "application/json");
        
        var request = new HttpRequestMessage(HttpMethod.Post, _apiEndpoint)
        {
            Content = content
        };
        
        AddOpenRouterHeaders(request);

        using var httpResponse = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        
        if (!httpResponse.IsSuccessStatusCode)
        {
            var error = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"OpenRouter API error: {httpResponse.StatusCode}, {error}");
        }

        // We'll need to generate a response ID to use for all chunks
        var responseId = Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");
        
        using var httpResponseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var streamReader = new StreamReader(httpResponseStream);
        
        string? role = null;

        while (await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrEmpty(line) || line == "data: [DONE]")
                continue;
                
            // Parse SSE format - "data: {...}"
            if (line.StartsWith("data: "))
                line = line.Substring(6);
            
            OpenRouterResponse? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize(line, _jsonContext.OpenRouterResponse);
            }
            catch (JsonException)
            {
                // Skip malformed chunks
                continue;
            }
            
            if (chunk == null || chunk.Choices == null || chunk.Choices.Count == 0)
                continue;
                
            var choice = chunk.Choices[0];
            var delta = choice.Delta;
            
            if (delta == null)
                continue;
                
            // Get role if present in this chunk
            if (delta.Role != null)
            {
                role = delta.Role;
            }
            
            // Create and yield the update
            ChatResponseUpdate update = new()
            {
                ResponseId = responseId,
                MessageId = messageId,
                Role = role != null ? new ChatRole(role) : null,
                FinishReason = ToFinishReason(choice.FinishReason),
                ModelId = chunk.Model ?? options?.ModelId ?? _metadata.DefaultModelId,
                CreatedAt = chunk.Created.HasValue ? DateTimeOffset.FromUnixTimeSeconds(chunk.Created.Value) : null
            };
            
            // Add content if present in this chunk
            if (delta.Content != null)
            {
                update.Contents.Add(new TextContent(delta.Content));
            }
            
            // Add reasoning content if present in this chunk
            if (delta.Reasoning.HasValue && delta.Reasoning.Value.ValueKind == JsonValueKind.String)
            {
                update.Contents.Add(new TextReasoningContent(delta.Reasoning.Value.GetString()));
            }
            
            // Handle tool calls if present
            if (delta.ToolCalls != null && delta.ToolCalls.Count > 0)
            {
                foreach (var toolCall in delta.ToolCalls)
                {
                    if (toolCall.Function != null && toolCall.Function.Name != null)
                    {
                        var arguments = toolCall.Function.Arguments ?? "{}";
                        // FIX: Use AOT-safe deserialization
                        var args = JsonSerializer.Deserialize(arguments, _jsonContext.DictionaryStringObject);
                        update.Contents.Add(new FunctionCallContent(
                            toolCall.Id ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                            toolCall.Function.Name,
                            args
                        ));
                    }
                }
            }
            
            yield return update;
        }
    }

    /// <inheritdoc />
    object? IChatClient.GetService(Type serviceType, object? serviceKey)
    {
        if (serviceType == null)
            throw new ArgumentNullException(nameof(serviceType));

        return
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _metadata :
            serviceType == typeof(OpenRouterChatClient) ? this :
            null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private void AddOpenRouterHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
        
        if (!string.IsNullOrEmpty(_config.HttpReferer))
        {
            request.Headers.Add("HTTP-Referer", _config.HttpReferer);
        }
        
        if (!string.IsNullOrEmpty(_config.AppName))
        {
            request.Headers.Add("X-Title", _config.AppName);
        }
    }

    private static ChatFinishReason? ToFinishReason(string? finishReason) =>
        finishReason switch
        {
            null => null,
            "length" => ChatFinishReason.Length,
            "stop" => ChatFinishReason.Stop,
            "content_filter" => ChatFinishReason.ContentFilter,
            "function_call" => ChatFinishReason.ToolCalls,
            "tool_calls" => ChatFinishReason.ToolCalls,
            _ => new ChatFinishReason(finishReason),
        };

    private static ChatMessage FromOpenRouterMessage(OpenRouterMessage? message, string responseId)
    {
        if (message == null)
        {
            return new ChatMessage(new ChatRole("assistant"), new[] { new TextContent(string.Empty) }) 
            { 
                MessageId = responseId 
            };
        }

        List<AIContent> contents = new();

        // Add any tool calls
        if (message.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            foreach (var toolCall in message.ToolCalls)
            {
                if (toolCall.Function != null && toolCall.Function.Name != null)
                {
                    var arguments = toolCall.Function.Arguments ?? "{}";
                    // FIX: Use AOT-safe deserialization
                    var args = JsonSerializer.Deserialize(arguments, OpenRouterJsonContext.Default.DictionaryStringObject);
                    contents.Add(new FunctionCallContent(
                        toolCall.Id ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                        toolCall.Function.Name,
                        args
                    ));
                }
            }
        }

        // Add reasoning content if present
        if (message.Reasoning.HasValue && message.Reasoning.Value.ValueKind == JsonValueKind.String)
        {
            contents.Add(new TextReasoningContent(message.Reasoning.Value.GetString()));
        }
        else if (message.Reasoning.HasValue)
        {
            // Handle cases where reasoning might be a complex object by serializing it
            contents.Add(new TextReasoningContent(JsonSerializer.Serialize(message.Reasoning.Value, OpenRouterJsonContext.Default.JsonElement)));
        }

        // Add text content if present or if no tool calls/reasoning
        if (!string.IsNullOrEmpty(message.Content) || contents.Count == 0)
        {
            contents.Insert(0, new TextContent(message.Content ?? string.Empty));
        }

        return new ChatMessage(new ChatRole(message.Role ?? "assistant"), contents) 
        { 
            MessageId = responseId 
        };
    }

    private OpenRouterRequest CreateRequestPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var request = new OpenRouterRequest
        {
            Model = options?.ModelId ?? _config.ModelName,
            Messages = messages.Select(m => ToOpenRouterMessage(m)).ToList(),
            Stream = stream,
            Temperature = options?.Temperature ?? _config.Temperature,
            MaxTokens = options?.MaxOutputTokens ?? _config.MaxTokens
        };

        if (options != null)
        {
            if (options.TopP.HasValue)
            {
                request.TopP = options.TopP.Value;
            }

            if (options.PresencePenalty.HasValue)
            {
                request.PresencePenalty = options.PresencePenalty.Value;
            }

            if (options.FrequencyPenalty.HasValue)
            {
                request.FrequencyPenalty = options.FrequencyPenalty.Value;
            }

            if (options.StopSequences != null && options.StopSequences.Count > 0)
            {
                request.Stop = options.StopSequences.ToList();
            }

            if (options.ToolMode is not NoneChatToolMode && options.Tools != null && options.Tools.Count > 0)
            {
                request.Tools = options.Tools.OfType<AIFunction>().Select(f => new OpenRouterTool
                {
                    Type = "function",
                    Function = new OpenRouterToolFunction
                    {
                        Name = f.Name,
                        Description = f.Description,
                        Parameters = JsonSerializer.Deserialize<JsonElement>(_schemaTransformCache.GetOrCreateTransformedSchema(f).GetRawText(), OpenRouterJsonContext.Default.JsonElement)
                    }
                }).ToList();
            }

            if (options.ResponseFormat is ChatResponseFormatJson)
            {
                request.ResponseFormat = new { type = "json_object" };
            }
        }

        // Check for reasoning in ExtensionOptions
        if (options?.AdditionalProperties?.TryGetValue("reasoning", out var reasoningValue) == true)
        {
            if (reasoningValue is OpenRouterReasoning reasoning)
            {
                request.Reasoning = reasoning;
            }
            else if (reasoningValue is JsonElement jsonElement)
            {
                try
                {
                    request.Reasoning = JsonSerializer.Deserialize(jsonElement.GetRawText(), _jsonContext.OpenRouterReasoning);
                }
                catch (JsonException)
                {
                    // Ignore invalid reasoning configuration
                }
            }
        }

        return request;
    }

    private OpenRouterMessage ToOpenRouterMessage(ChatMessage message)
    {
        var result = new OpenRouterMessage { Role = message.Role.Value };
        
        // Extract text content
        var textContent = message.Contents.OfType<TextContent>().FirstOrDefault();
        if (textContent != null)
        {
            result.Content = textContent.Text;
        }
        else if (message.Contents.Count == 0)
        {
            result.Content = string.Empty;
        }
        
        // Handle function calls if present
        var functionCalls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (functionCalls.Count > 0)
        {
            result.ToolCalls = functionCalls.Select(fc => new OpenRouterToolCall
            {
                Type = "function",
                Id = fc.CallId,
                Function = new OpenRouterFunction
                {
                    Name = fc.Name,
                    // FIX: Use AOT-safe serialization
                    Arguments = JsonSerializer.Serialize(fc.Arguments, _jsonContext.DictionaryStringObject)
                }
            }).ToList();
        }
        
        // Handle function results if present
        var functionResults = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (functionResults != null)
        {
            // For function results, we need to set the role to "tool"
            result.Role = "tool";
            // FIX: Use AOT-safe serialization for object
            result.Content = JsonSerializer.Serialize(functionResults.Result, _jsonContext.Object);
            result.ToolCallId = functionResults.CallId;
        }
        
        return result;
    }
}
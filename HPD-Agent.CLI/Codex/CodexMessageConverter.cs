using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace HPD_Agent.CLI.Codex;

/// <summary>
/// Schema transformation cache for Codex API requirements.
/// Codex requires strict schemas with additionalProperties: false and all properties required.
/// </summary>
internal static class CodexSchemaCache
{
    /// <summary>
    /// Transform options for Codex API schema requirements.
    /// </summary>
    public static readonly AIJsonSchemaTransformOptions TransformOptions = new()
    {
        DisallowAdditionalProperties = true,
        RequireAllProperties = true,
        ConvertBooleanSchemas = true
    };

    /// <summary>
    /// Cached schema transformer for AIFunction instances.
    /// </summary>
    public static readonly AIJsonSchemaTransformCache SchemaCache = new(TransformOptions);
}

/// <summary>
/// Converts Microsoft.Extensions.AI chat messages to/from Codex API format.
/// </summary>
public static class CodexMessageConverter
{
    /// <summary>
    /// Determines if a model is a reasoning model (o-series, gpt-5, codex).
    /// Reasoning models use "developer" role instead of "system".
    /// </summary>
    public static bool IsReasoningModel(string modelId)
    {
        return modelId.StartsWith("o", StringComparison.OrdinalIgnoreCase) ||
               modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
               modelId.StartsWith("codex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts a list of ChatMessage to Codex API input format.
    /// Note: System messages should be extracted separately for the 'instructions' field.
    /// This method skips system messages as they're handled at the request level.
    /// </summary>
    public static List<object> ConvertToCodexInput(IEnumerable<ChatMessage> messages, string modelId)
    {
        var input = new List<object>();

        foreach (var message in messages)
        {
            switch (message.Role.Value.ToLowerInvariant())
            {
                case "system":
                    // System messages are handled at the request level via 'instructions' field
                    // Skip them here to avoid duplication
                    break;

                case "user":
                    input.Add(ConvertUserMessage(message));
                    break;

                case "assistant":
                    // Handle reasoning content first - use item references when store is enabled
                    foreach (var reasoning in message.Contents.OfType<TextReasoningContent>())
                    {
                        // Check if we have a valid item ID for reference (must start with "rs_")
                        var itemId = reasoning.AdditionalProperties?.TryGetValue("itemId", out var id) == true
                            ? id?.ToString()
                            : null;

                        if (!string.IsNullOrEmpty(itemId) && itemId.StartsWith("rs_"))
                        {
                            // Use item reference for efficient round-tripping (when store: true)
                            input.Add(new CodexItemReference { Id = itemId });
                        }
                        // When store: false, we don't send reasoning content back at all.
                        // The API doesn't need it - reasoning is for display only.
                        // Sending it with an invalid ID causes errors.
                    }

                    // Add assistant text content
                    var textContent = GetTextContent(message);
                    if (!string.IsNullOrEmpty(textContent))
                    {
                        input.Add(new CodexAssistantMessage
                        {
                            Content = new List<CodexOutputText>
                            {
                                new() { Text = textContent }
                            }
                        });
                    }

                    // Add any tool calls
                    foreach (var toolCall in message.Contents.OfType<FunctionCallContent>())
                    {
                        var argsJson = toolCall.Arguments != null
                            ? JsonSerializer.Serialize(toolCall.Arguments)
                            : "{}";

                        input.Add(new CodexFunctionCall
                        {
                            CallId = toolCall.CallId ?? Guid.NewGuid().ToString(),
                            Name = toolCall.Name,
                            Arguments = argsJson
                        });
                    }
                    break;

                case "tool":
                    // Tool results
                    foreach (var result in message.Contents.OfType<FunctionResultContent>())
                    {
                        input.Add(new CodexFunctionCallOutput
                        {
                            CallId = result.CallId ?? "",
                            Output = result.Result is string resultStr
                                ? resultStr
                                : JsonSerializer.Serialize(result.Result)
                        });
                    }
                    break;
            }
        }

        return input;
    }

    /// <summary>
    /// Converts a user ChatMessage to Codex user message format.
    /// </summary>
    private static CodexUserMessage ConvertUserMessage(ChatMessage message)
    {
        // Use List<object> to ensure derived types serialize with all their properties
        var contentParts = new List<object>();

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    contentParts.Add(new CodexInputText { Text = text.Text });
                    break;

                case DataContent dataContent when dataContent.MediaType?.StartsWith("image/") == true:
                    // Handle image data content
                    var imgMediaType = dataContent.MediaType ?? "image/png";
                    var imgBase64 = Convert.ToBase64String(dataContent.Data.ToArray());
                    contentParts.Add(new CodexInputImage
                    {
                        ImageUrl = $"data:{imgMediaType};base64,{imgBase64}"
                    });
                    break;

                case DataContent data when data.MediaType == "application/pdf":
                    // Handle PDF file
                    var pdfBase64 = Convert.ToBase64String(data.Data.ToArray());
                    contentParts.Add(new CodexInputFile
                    {
                        FileData = $"data:application/pdf;base64,{pdfBase64}",
                        Filename = "document.pdf"
                    });
                    break;

                case DataContent otherData:
                    // Handle other data content types
                    var otherMediaType = otherData.MediaType ?? "application/octet-stream";
                    if (otherMediaType.StartsWith("image/"))
                    {
                        var otherImageBase64 = Convert.ToBase64String(otherData.Data.ToArray());
                        contentParts.Add(new CodexInputImage
                        {
                            ImageUrl = $"data:{otherMediaType};base64,{otherImageBase64}"
                        });
                    }
                    break;
            }
        }

        // If no content parts were created, add the text content
        if (contentParts.Count == 0)
        {
            var text = GetTextContent(message);
            if (!string.IsNullOrEmpty(text))
            {
                contentParts.Add(new CodexInputText { Text = text });
            }
        }

        return new CodexUserMessage { Content = contentParts };
    }

    /// <summary>
    /// Gets the text content from a ChatMessage, excluding reasoning content.
    /// TextReasoningContent is a separate type from TextContent, so we filter by type.
    /// </summary>
    private static string GetTextContent(ChatMessage message)
    {
        var textParts = message.Contents
            .Where(c => c is TextContent && c.GetType() != typeof(TextReasoningContent))
            .Cast<TextContent>()
            .Select(t => t.Text);

        return string.Join("", textParts);
    }

    /// <summary>
    /// Converts Codex API response to ChatMessage.
    /// </summary>
    public static ChatMessage ConvertFromCodexResponse(CodexResponse response)
    {
        var contents = new List<AIContent>();

        foreach (var output in response.Output)
        {
            switch (output)
            {
                case CodexMessageOutput messageOutput:
                    foreach (var part in messageOutput.Content)
                    {
                        if (part.Type == "output_text" && !string.IsNullOrEmpty(part.Text))
                        {
                            contents.Add(new TextContent(part.Text));
                        }
                    }
                    break;

                case CodexFunctionCallOutput2 functionCall:
                    contents.Add(new FunctionCallContent(
                        functionCall.CallId,
                        functionCall.Name,
                        ParseArguments(functionCall.Arguments)));
                    break;

                case CodexReasoningOutput reasoning:
                    // Convert reasoning output to TextReasoningContent
                    // This properly represents reasoning/thinking from the model
                    if (reasoning.Summary?.Count > 0)
                    {
                        foreach (var summary in reasoning.Summary)
                        {
                            if (!string.IsNullOrEmpty(summary.Text))
                            {
                                var reasoningContent = new TextReasoningContent(summary.Text)
                                {
                                    // Store encrypted content if available for round-tripping
                                    ProtectedData = reasoning.EncryptedContent,
                                    RawRepresentation = reasoning
                                };

                                // Store the item ID for potential item_reference usage
                                if (!string.IsNullOrEmpty(reasoning.Id))
                                {
                                    reasoningContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                                    reasoningContent.AdditionalProperties["itemId"] = reasoning.Id;
                                }

                                contents.Add(reasoningContent);
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(reasoning.EncryptedContent))
                    {
                        // Even if no summary text, preserve the encrypted content
                        // Some models may only provide encrypted reasoning
                        var reasoningContent = new TextReasoningContent(string.Empty)
                        {
                            ProtectedData = reasoning.EncryptedContent,
                            RawRepresentation = reasoning
                        };

                        if (!string.IsNullOrEmpty(reasoning.Id))
                        {
                            reasoningContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                            reasoningContent.AdditionalProperties["itemId"] = reasoning.Id;
                        }

                        contents.Add(reasoningContent);
                    }
                    break;
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, contents);

        // Add usage information as additional properties if available
        if (response.Usage != null)
        {
            message.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            message.AdditionalProperties["InputTokens"] = response.Usage.InputTokens;
            message.AdditionalProperties["OutputTokens"] = response.Usage.OutputTokens;
        }

        return message;
    }

    /// <summary>
    /// Parses function arguments from JSON string.
    /// </summary>
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
    /// Minimal valid schema for Codex API strict mode.
    /// Codex requires: type: "object", properties (even if empty), additionalProperties: false
    /// </summary>
    private static readonly JsonElement MinimalValidSchema = JsonDocument.Parse(
        """{"type":"object","properties":{},"required":[],"additionalProperties":false}"""
    ).RootElement.Clone();

    /// <summary>
    /// Converts AIFunction tools to Codex tool format.
    /// Uses AIJsonUtilities for proper schema transformation with Codex-specific requirements.
    /// Returns List&lt;object&gt; to ensure derived types serialize correctly.
    /// </summary>
    public static List<object> ConvertTools(IEnumerable<AITool> tools)
    {
        var codexTools = new List<object>();

        foreach (var tool in tools)
        {
            if (tool is AIFunction function)
            {
                JsonElement transformedSchema;

                if (function.JsonSchema.ValueKind == JsonValueKind.Object)
                {
                    // Check if schema is empty or just "{}"
                    var schemaJson = function.JsonSchema.GetRawText();
                    if (schemaJson == "{}" || !function.JsonSchema.TryGetProperty("type", out _))
                    {
                        // Use minimal valid schema for empty schemas
                        transformedSchema = MinimalValidSchema;
                    }
                    else
                    {
                        // Transform the existing schema using the Codex schema cache
                        // This handles: additionalProperties: false, required properties, boolean schema conversion
                        transformedSchema = CodexSchemaCache.SchemaCache.GetOrCreateTransformedSchema(function);
                    }
                }
                else
                {
                    // No schema or undefined - use minimal valid schema
                    transformedSchema = MinimalValidSchema;
                }

                codexTools.Add(new CodexFunctionTool
                {
                    Name = function.Name,
                    Description = function.Description,
                    Parameters = transformedSchema,
                    Strict = true // Enable strict mode for better validation
                });
            }
        }

        return codexTools;
    }

    /// <summary>
    /// Maps finish reason from Codex response to ChatFinishReason.
    /// </summary>
    public static ChatFinishReason MapFinishReason(CodexResponse response, bool hasFunctionCalls)
    {
        if (response.Error != null)
            return ChatFinishReason.Stop;

        if (response.IncompleteDetails?.Reason != null)
        {
            return response.IncompleteDetails.Reason switch
            {
                "max_output_tokens" => ChatFinishReason.Length,
                "content_filter" => ChatFinishReason.ContentFilter,
                _ => ChatFinishReason.Stop
            };
        }

        if (hasFunctionCalls)
            return ChatFinishReason.ToolCalls;

        return ChatFinishReason.Stop;
    }
}

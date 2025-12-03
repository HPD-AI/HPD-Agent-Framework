// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.FrontendTools;

/// <summary>
/// Emitted by agent when a frontend tool needs to be invoked.
/// The middleware detects frontend tools and emits this event.
/// Frontend must respond with <see cref="FrontendToolInvokeResponseEvent"/>.
/// </summary>
/// <param name="RequestId">Unique identifier for this request (used to correlate response)</param>
/// <param name="ToolName">Name of the tool to invoke</param>
/// <param name="CallId">The function call ID from the LLM</param>
/// <param name="Arguments">Arguments to pass to the tool</param>
/// <param name="Description">Optional description of the tool (for debugging)</param>
public record FrontendToolInvokeRequestEvent(
    string RequestId,
    string ToolName,
    string CallId,
    IReadOnlyDictionary<string, object?> Arguments,
    string? Description = null
) : AgentEvent;

/// <summary>
/// Response from frontend after executing a tool.
/// Supports rich content types: text, binary (images/files), JSON.
/// </summary>
/// <param name="RequestId">Must match the RequestId from the corresponding request</param>
/// <param name="Content">The tool result content (text, binary, or JSON)</param>
/// <param name="Success">Whether the tool execution succeeded</param>
/// <param name="ErrorMessage">Error message if Success is false</param>
/// <param name="Augmentation">Optional state changes to apply before next iteration</param>
public record FrontendToolInvokeResponseEvent(
    string RequestId,
    IReadOnlyList<IToolResultContent> Content,
    bool Success = true,
    string? ErrorMessage = null,
    FrontendToolAugmentation? Augmentation = null
) : AgentEvent
{
    /// <summary>
    /// Convenience constructor for simple text results.
    /// </summary>
    public FrontendToolInvokeResponseEvent(
        string requestId,
        string textResult,
        bool success = true,
        string? errorMessage = null,
        FrontendToolAugmentation? augmentation = null)
        : this(requestId, new IToolResultContent[] { new TextContent(textResult) }, success, errorMessage, augmentation)
    { }

    /// <summary>
    /// Convenience constructor for single content item.
    /// </summary>
    public FrontendToolInvokeResponseEvent(
        string requestId,
        IToolResultContent content,
        bool success = true,
        string? errorMessage = null,
        FrontendToolAugmentation? augmentation = null)
        : this(requestId, new[] { content }, success, errorMessage, augmentation)
    { }
}

/// <summary>
/// Emitted after frontend plugins are successfully registered.
/// Useful for debugging and observability.
/// </summary>
/// <param name="RegisteredPlugins">Names of all registered plugins</param>
/// <param name="TotalTools">Total number of tools across all plugins</param>
/// <param name="Timestamp">When registration completed</param>
public record FrontendPluginsRegisteredEvent(
    IReadOnlyList<string> RegisteredPlugins,
    int TotalTools,
    DateTimeOffset Timestamp
) : AgentEvent;

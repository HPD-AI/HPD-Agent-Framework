using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HPD_Agent.Providers.Anthropic;

/// <summary>
/// Anthropic-specific provider configuration.
/// Used for FFI/JSON configuration and C# builder extensions.
///
/// JSON Example:
/// <code>
/// {
///   "Provider": {
///     "ProviderKey": "anthropic",
///     "ModelName": "claude-sonnet-4-5-20250929",
///     "ApiKey": "sk-ant-...",
///     "ProviderOptionsJson": "{\"ThinkingBudgetTokens\":4096,\"EnablePromptCaching\":true,\"ClaudeSkills\":[\"pdf\",\"xlsx\"]}"
///   }
/// }
/// </code>
/// </summary>
public class AnthropicProviderConfig
{
    //     
    // SAMPLING PARAMETERS
    //     

    /// <summary>
    /// Maximum output tokens. Default: 4096.
    /// </summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature (0.0 to 1.0). Lower = more deterministic.
    /// </summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// Nucleus sampling parameter. Alternative to temperature.
    /// </summary>
    [JsonPropertyName("topP")]
    public float? TopP { get; set; }

    /// <summary>
    /// Top-K sampling parameter (Anthropic-specific). Only sample from top K tokens.
    /// </summary>
    [JsonPropertyName("topK")]
    public int? TopK { get; set; }

    /// <summary>
    /// Stop sequences that will halt generation.
    /// </summary>
    [JsonPropertyName("stopSequences")]
    public List<string>? StopSequences { get; set; }

    //     
    // EXTENDED THINKING
    //     

    /// <summary>
    /// Enable extended thinking mode by setting the token budget.
    /// Extended thinking allows Claude to "think" before responding, improving reasoning quality.
    /// Beneficial for complex reasoning, math problems, multi-step planning, and code analysis.
    /// Note: Thinking tokens count toward your token usage and billing.
    /// </summary>
    [JsonPropertyName("thinkingBudgetTokens")]
    public int? ThinkingBudgetTokens { get; set; }

    /// <summary>
    /// Use interleaved thinking mode (allows thinking tokens to exceed max_tokens).
    /// Only works with Claude 4+ models on direct API or supported platforms.
    /// Requires ThinkingBudgetTokens to be set.
    /// </summary>
    [JsonPropertyName("useInterleavedThinking")]
    public bool UseInterleavedThinking { get; set; }

    //     
    // PROMPT CACHING
    //     

    /// <summary>
    /// Enable prompt caching to reduce costs on repeated prompts (up to 90% savings).
    /// </summary>
    [JsonPropertyName("enablePromptCaching")]
    public bool EnablePromptCaching { get; set; }

    /// <summary>
    /// Type of prompt caching when EnablePromptCaching is true.
    /// Values: "AutomaticToolsAndSystem", "FineGrained"
    /// Default: "AutomaticToolsAndSystem" (caches system prompts and tool definitions).
    /// </summary>
    [JsonPropertyName("promptCacheType")]
    public string PromptCacheType { get; set; } = "AutomaticToolsAndSystem";

    //     
    // CLAUDE SKILLS (Anthropic's built-in document processing)
    //     

    /// <summary>
    /// Claude Skills to enable (Anthropic's built-in server-side document processing).
    /// Available skills: "pdf", "xlsx", "pptx", "docx".
    /// Note: This is different from HPD-Agent's skill system.
    /// </summary>
    [JsonPropertyName("claudeSkills")]
    public List<string>? ClaudeSkills { get; set; }

    /// <summary>
    /// Container ID to reuse an existing container from a previous request.
    /// Containers maintain state across requests.
    /// </summary>
    [JsonPropertyName("containerId")]
    public string? ContainerId { get; set; }

    //     
    // MCP SERVERS (Anthropic's native MCP support)
    //     

    /// <summary>
    /// MCP (Model Context Protocol) servers for Claude to use.
    /// Anthropic natively supports MCP servers via API integration.
    /// </summary>
    [JsonPropertyName("mcpServers")]
    public List<AnthropicMCPServerConfig>? MCPServers { get; set; }

    //     
    // SERVICE TIER
    //     

    /// <summary>
    /// Service tier for request prioritization.
    /// Values: "Standard", "Priority", "Batch"
    /// - Standard: Default tier, fair queuing
    /// - Priority: Higher priority, lower latency (may have additional cost)
    /// - Batch: For non-time-sensitive requests (lower cost)
    /// </summary>
    [JsonPropertyName("serviceTier")]
    public string? ServiceTier { get; set; }
}

/// <summary>
/// Configuration for an MCP server to connect to Claude (FFI-serializable).
/// </summary>
public class AnthropicMCPServerConfig
{
    /// <summary>
    /// URL of the MCP server. Required.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Name identifier for the server. Required.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional authorization token for the MCP server.
    /// </summary>
    [JsonPropertyName("authorizationToken")]
    public string? AuthorizationToken { get; set; }

    /// <summary>
    /// Optional list of allowed tool names. If null, all tools are allowed.
    /// </summary>
    [JsonPropertyName("allowedTools")]
    public List<string>? AllowedTools { get; set; }
}

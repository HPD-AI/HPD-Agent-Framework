using System;
using System.Collections.Generic;
using Anthropic.SDK.Constants;
using HPD.Agent;

namespace HPD.Agent.Providers.Anthropic;

/// <summary>
/// Extension methods for AgentBuilder to configure Anthropic (Claude) as the AI provider.
/// </summary>
public static class AgentBuilderExtensions
{
    /// <summary>
    /// Configures the agent to use Anthropic (Claude) as the AI provider.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="configure">Action to configure Anthropic-specific options</param>
    /// <returns>The builder for method chaining</returns>
    /// <remarks>
    /// <para>
    /// This method creates an <see cref="AnthropicProviderConfig"/> that is:
    /// - Stored in <c>ProviderConfig.ProviderOptionsJson</c> for FFI/JSON serialization
    /// - Applied during <c>AnthropicProvider.CreateChatClient()</c> via the registered deserializer
    /// </para>
    /// <para>
    /// For FFI/JSON configuration, you can use the same config structure directly:
    /// <code>
    /// {
    ///   "Provider": {
    ///     "ProviderKey": "anthropic",
    ///     "ModelName": "claude-sonnet-4-5-20250929",
    ///     "ApiKey": "sk-ant-...",
    ///     "ProviderOptionsJson": "{\"thinkingBudgetTokens\":4096,\"enablePromptCaching\":true}"
    ///   }
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithAnthropic(opts =>
    ///     {
    ///         opts.ApiKey = "sk-ant-...";
    ///         opts.Model = AnthropicModels.Claude45Sonnet;
    ///         opts.MaxTokens = 4096;
    ///         opts.Temperature = 0.7f;
    ///
    ///         // Extended thinking (for complex reasoning)
    ///         opts.ThinkingBudgetTokens = 4096;
    ///
    ///         // Prompt caching (cost optimization)
    ///         opts.EnablePromptCaching = true;
    ///
    ///         // Claude Skills (Anthropic's document processing)
    ///         opts.ClaudeSkills = ["pdf", "xlsx"];
    ///
    ///         // MCP servers (Anthropic's native MCP support)
    ///         opts.MCPServers.Add(new MCPServerConfig
    ///         {
    ///             Url = "https://mcp.example.com",
    ///             Name = "my-server"
    ///         });
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithAnthropic(this AgentBuilder builder, Action<AnthropicOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AnthropicOptions();
        configure(options);

        // Validate required fields
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("ApiKey is required for Anthropic provider.");

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required for Anthropic provider.");

        // Build provider config
        builder.Config.Provider = new ProviderConfig
        {
            ProviderKey = "anthropic",
            ApiKey = options.ApiKey,
            ModelName = options.Model
        };

        // Convert AnthropicOptions to AnthropicProviderConfig and store it
        var providerConfig = ConvertToProviderConfig(options);
        builder.Config.Provider.SetTypedProviderConfig(providerConfig);

        return builder;
    }

    /// <summary>
    /// Converts the builder-friendly AnthropicOptions to the FFI-serializable AnthropicProviderConfig.
    /// </summary>
    private static AnthropicProviderConfig ConvertToProviderConfig(AnthropicOptions options)
    {
        var config = new AnthropicProviderConfig
        {
            MaxTokens = options.MaxTokens,
            Temperature = options.Temperature,
            TopP = options.TopP,
            TopK = options.TopK,
            StopSequences = options.StopSequences != null ? new List<string>(options.StopSequences) : null,
            ThinkingBudgetTokens = options.ThinkingBudgetTokens,
            UseInterleavedThinking = options.UseInterleavedThinking,
            EnablePromptCaching = options.EnablePromptCaching,
            PromptCacheType = options.PromptCacheType.ToString(),
            ClaudeSkills = options.ClaudeSkills,
            ContainerId = options.ContainerId,
            ServiceTier = options.ServiceTier?.ToString()
        };

        // Convert MCP servers
        if (options.MCPServers is { Count: > 0 })
        {
            config.MCPServers = new List<AnthropicMCPServerConfig>();
            foreach (var server in options.MCPServers)
            {
                config.MCPServers.Add(new AnthropicMCPServerConfig
                {
                    Url = server.Url,
                    Name = server.Name,
                    AuthorizationToken = server.AuthorizationToken,
                    AllowedTools = server.AllowedTools
                });
            }
        }

        return config;
    }
}

/// <summary>
/// Builder-friendly configuration options for Anthropic (Claude) provider.
/// This is converted to <see cref="AnthropicProviderConfig"/> for storage and FFI serialization.
/// </summary>
public class AnthropicOptions
{
    /// <summary>
    /// Your Anthropic API key. Required.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The model to use. Required.
    /// Use constants from <see cref="AnthropicModels"/> (e.g., AnthropicModels.Claude45Sonnet).
    /// </summary>
    public string Model { get; set; } = AnthropicModels.Claude45Sonnet;

    /// <summary>
    /// Maximum output tokens. Default: 4096.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Sampling temperature (0.0 to 1.0). Lower = more deterministic.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Nucleus sampling parameter. Alternative to temperature.
    /// </summary>
    public float? TopP { get; set; }

    /// <summary>
    /// Top-K sampling parameter (Anthropic-specific). Only sample from top K tokens.
    /// </summary>
    public int? TopK { get; set; }

    /// <summary>
    /// Stop sequences that will halt generation.
    /// </summary>
    public IList<string>? StopSequences { get; set; }

    //     
    // EXTENDED THINKING
    //     

    /// <summary>
    /// Enable extended thinking mode by setting the token budget.
    /// Extended thinking allows Claude to "think" before responding, improving reasoning quality.
    /// Beneficial for complex reasoning, math problems, multi-step planning, and code analysis.
    /// Note: Thinking tokens count toward your token usage and billing.
    /// </summary>
    public int? ThinkingBudgetTokens { get; set; }

    /// <summary>
    /// Use interleaved thinking mode (allows thinking tokens to exceed max_tokens).
    /// Only works with Claude 4+ models on direct API or supported platforms.
    /// Requires <see cref="ThinkingBudgetTokens"/> to be set.
    /// </summary>
    public bool UseInterleavedThinking { get; set; }

    //     
    // PROMPT CACHING
    //     

    /// <summary>
    /// Enable prompt caching to reduce costs on repeated prompts (up to 90% savings).
    /// </summary>
    public bool EnablePromptCaching { get; set; }

    /// <summary>
    /// Type of prompt caching when <see cref="EnablePromptCaching"/> is true.
    /// Default: AutomaticToolsAndSystem (caches system prompts and tool definitions).
    /// </summary>
    public PromptCacheType PromptCacheType { get; set; } = PromptCacheType.AutomaticToolsAndSystem;

    //     
    // CLAUDE SKILLS (Anthropic's built-in document processing)
    //     

    /// <summary>
    /// Claude Skills to enable (Anthropic's built-in server-side document processing).
    /// Available skills: "pdf", "xlsx", "pptx", "docx".
    /// Note: This is different from HPD-Agent's skill system.
    /// </summary>
    public List<string>? ClaudeSkills { get; set; }

    /// <summary>
    /// Container ID to reuse an existing container from a previous request.
    /// Containers maintain state across requests.
    /// </summary>
    public string? ContainerId { get; set; }

    //     
    // MCP SERVERS (Anthropic's native MCP support)
    //     

    /// <summary>
    /// MCP (Model Context Protocol) servers for Claude to use.
    /// Anthropic natively supports MCP servers via API integration.
    /// </summary>
    public List<MCPServerConfig> MCPServers { get; set; } = new();

    //     
    // SERVICE TIER
    //     

    /// <summary>
    /// Service tier for request prioritization.
    /// - Standard: Default tier, fair queuing
    /// - Priority: Higher priority, lower latency (may have additional cost)
    /// - Batch: For non-time-sensitive requests (lower cost)
    /// </summary>
    public ServiceTier? ServiceTier { get; set; }
}

/// <summary>
/// Configuration for an MCP server to connect to Claude (builder-friendly).
/// </summary>
public class MCPServerConfig
{
    /// <summary>
    /// URL of the MCP server. Required.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Name identifier for the server. Required.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional authorization token for the MCP server.
    /// </summary>
    public string? AuthorizationToken { get; set; }

    /// <summary>
    /// Optional list of allowed tool names. If null, all tools are allowed.
    /// </summary>
    public List<string>? AllowedTools { get; set; }
}

/// <summary>
/// Prompt cache type for Anthropic's prompt caching feature.
/// </summary>
public enum PromptCacheType
{
    /// <summary>
    /// Automatically caches system prompts and tool definitions (recommended).
    /// </summary>
    AutomaticToolsAndSystem,

    /// <summary>
    /// Use cache-control instructions for fine-grained control.
    /// </summary>
    FineGrained
}

/// <summary>
/// Service tier for Anthropic request prioritization.
/// </summary>
public enum ServiceTier
{
    /// <summary>
    /// Default tier with fair queuing.
    /// </summary>
    Standard,

    /// <summary>
    /// Higher priority with lower latency (may have additional cost).
    /// </summary>
    Priority,

    /// <summary>
    /// For non-time-sensitive requests (lower cost).
    /// </summary>
    Batch
}

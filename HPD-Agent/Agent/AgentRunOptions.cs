// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using HPD.Agent.Middleware;

namespace HPD.Agent;

/// <summary>
/// Per-invocation options for agent runs.
/// Enables runtime customization without mutating agent configuration.
/// FFI-serializable (JSON primitives only for serializable properties).
/// </summary>
/// <remarks>
/// <para>
/// <b>Design Philosophy:</b>
/// AgentRunOptions provides per-invocation customization that doesn't require rebuilding the agent.
/// This enables scenarios like:
/// - Runtime provider switching (OpenAI → Claude → local)
/// - Per-request temperature/token adjustments
/// - Multi-tenant SaaS with different contexts per user
/// - A/B testing with different configurations
/// </para>
/// <para>
/// <b>Priority Rules:</b>
/// - OverrideChatClient > ProviderKey/ModelId > Agent's default client
/// - SystemInstructions > Config.SystemInstructions (complete replacement)
/// - AdditionalSystemInstructions appends to resolved instructions
/// - ContextInstances > Builder-time contexts > Default context
/// </para>
/// </remarks>
public class AgentRunOptions
{
    /// <summary>
    /// Chat parameters (temperature, tokens, etc.)
    /// JSON-serializable, no Microsoft.Extensions.AI dependency.
    /// </summary>
    public ChatRunOptions? Chat { get; set; }

    /// <summary>
    /// Provider key to switch to (e.g., "openai", "anthropic", "ollama").
    /// Works with ModelId to create the client via provider registry.
    /// Useful for simple provider switching without manual client creation.
    /// </summary>
    public string? ProviderKey { get; set; }

    /// <summary>
    /// Model ID to use for the switched provider (e.g., "gpt-4", "claude-opus").
    /// Ignored if ProviderKey is not set.
    /// If null with ProviderKey, uses default model from config.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Override the chat client for this specific run.
    /// Highest priority - used if provided, overriding ProviderKey/ModelId.
    /// Enables dynamic provider switching without rebuilding.
    /// Not JSON-serializable (for direct C# usage).
    /// </summary>
    [JsonIgnore]
    public IChatClient? OverrideChatClient { get; set; }

    /// <summary>
    /// System instructions to use for this run (completely replaces configured instructions).
    /// Useful for completely different personas or behaviors.
    /// Example: "You are a strict code reviewer" vs "You are a brainstorming partner"
    /// If both this and AdditionalSystemInstructions are set, both are used.
    /// </summary>
    public string? SystemInstructions { get; set; }

    /// <summary>
    /// Additional system instructions to append to the base instructions.
    /// Useful for one-off adjustments without replacing base instructions.
    /// Example: Base="helpful assistant" + Additional="For this request, prioritize security"
    /// If SystemInstructions is set, this appends to that instead of base config.
    /// </summary>
    public string? AdditionalSystemInstructions { get; set; }

    /// <summary>
    /// Context values to inject or override for this run.
    /// Available to middleware via AgentMiddlewareContext.Properties.
    /// Useful for request-specific data: user ID, tenant ID, request metadata, etc.
    /// </summary>
    public Dictionary<string, object>? ContextOverrides { get; set; }

    /// <summary>
    /// Timeout for the entire run (overrides config).
    /// Useful for varying timeout based on message complexity or user tier.
    /// Null = use config default.
    /// </summary>
    public TimeSpan? RunTimeout { get; set; }

    /// <summary>
    /// Whether to use cached responses for this run.
    /// Null = use config default, true = always cache, false = skip cache.
    /// Useful for dry-runs or when freshness is critical.
    /// </summary>
    public bool? UseCache { get; set; }

    /// <summary>
    /// Skip tool/function execution for this run (dry-run mode).
    /// Useful for testing agent planning without side effects.
    /// Agent will plan and call functions, but they won't execute.
    /// </summary>
    public bool SkipTools { get; set; } = false;

    /// <summary>
    /// Runtime middleware to inject only for this run.
    /// Applied AFTER agent's configured middleware.
    /// Not JSON-serializable (for direct C# usage).
    /// Useful for temporary observability, monitoring, or custom logic.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<IAgentMiddleware>? RuntimeMiddleware { get; set; }

    /// <summary>
    /// Permission overrides for this specific run.
    /// Key = permission name, Value = approved (true) or denied (false).
    /// Overrides agent's configured permission policies temporarily.
    /// Example: { "delete_files": false, "external_api_calls": true }
    /// </summary>
    public Dictionary<string, bool>? PermissionOverrides { get; set; }

    /// <summary>
    /// Client tool configuration for this run.
    /// Allows dynamic plugin/tool registration without rebuilding agent.
    /// </summary>
    public ClientTools.AgentRunInput? ClientToolInput { get; set; }

    /// <summary>
    /// Conversation ID override (for multi-tenant scenarios or branching).
    /// Null = use thread's conversation ID.
    /// </summary>
    public string? ConversationIdOverride { get; set; }

    /// <summary>
    /// Custom streaming callback (for native bindings).
    /// Not JSON-serializable.
    /// Allows native code to handle streaming updates differently.
    /// </summary>
    [JsonIgnore]
    public Func<AgentEvent, Task>? CustomStreamCallback { get; set; }

    /// <summary>
    /// Runtime context instances for tools (Runtime Context Injection).
    /// Maps tool name -> context instance (e.g., "SearchTools" -> ProviderContext instance).
    /// Enables dynamic, per-invocation context injection WITHOUT rebuilding the agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Priority (for context resolution):</b>
    /// 1. Runtime context from ContextInstances (highest - per-invocation override)
    /// 2. Builder-time context from .WithTools&lt;T&gt;(context)
    /// 3. Default context from .WithDefaultMetadata(context)
    /// 4. Null (no context - templates unresolved, conditions skipped)
    /// </para>
    /// <para>
    /// <b>How It Works:</b>
    /// The source generator creates CreateTools() methods that accept context.
    /// Each tool's CreateFunctions(instance, context) is invoked with the selected context.
    /// This means descriptions are resolved, conditions evaluated, parameters filtered - all at runtime!
    /// </para>
    /// <para>
    /// <b>Use Cases:</b>
    /// - Multi-tenant SaaS: Different contexts per user/tenant
    /// - A/B Testing: Run variants with different contexts
    /// - Dynamic feature flags: Context can control function visibility
    /// - Request metadata: Inject tracing, user info, etc. per-call
    /// </para>
    /// <para>
    /// <b>Example:</b>
    /// <code>
    /// var options = new AgentRunOptions
    /// {
    ///     ContextInstances = new()
    ///     {
    ///         ["SearchTools"] = new ProviderContext { ProviderName = "OpenAI" },
    ///         ["DatabaseTools"] = new DbContext { TenantId = user.TenantId }
    ///     }
    /// };
    /// await agent.RunAsync(messages, options);
    /// </code>
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public Dictionary<string, IToolMetadata>? ContextInstances { get; set; }

    #region Background Responses

    /// <summary>
    /// Allow the provider to run the operation in background mode.
    /// When true, operation may return immediately with a ContinuationToken.
    /// When false, operation blocks until complete (traditional behavior).
    /// When null, uses default from AgentConfig.BackgroundResponses.DefaultAllow.
    /// Provider-dependent: Unsupporting providers will ignore this and behave synchronously.
    /// </summary>
    public bool? AllowBackgroundResponses { get; set; }

    /// <summary>
    /// Continuation token for polling/resuming a background operation.
    /// For polling: Set this to the token from a previous response to poll for completion.
    /// For streaming resumption: Set this to resume streaming from where it was interrupted.
    /// Uses Microsoft.Extensions.AI.ResponseContinuationToken type directly.
    /// </summary>
    [JsonIgnore]
    public ResponseContinuationToken? ContinuationToken { get; set; }

    /// <summary>
    /// Override polling interval for this specific run.
    /// Null = use config default (BackgroundResponsesConfig.DefaultPollingInterval).
    /// </summary>
    public TimeSpan? BackgroundPollingInterval { get; set; }

    /// <summary>
    /// Override timeout for this specific run.
    /// Null = use config default (BackgroundResponsesConfig.DefaultTimeout).
    /// </summary>
    public TimeSpan? BackgroundTimeout { get; set; }

    #endregion
}

/// <summary>
/// Chat-specific run options (JSON-serializable).
/// Subset of Microsoft.Extensions.AI.ChatOptions with only JSON primitives.
/// FFI-friendly - no complex types, no dependencies.
/// </summary>
public class ChatRunOptions
{
    /// <summary>
    /// Creates a new instance of ChatRunOptions.
    /// </summary>
    public ChatRunOptions() { }

    /// <summary>
    /// Creates a new instance of ChatRunOptions from Microsoft.Extensions.AI.ChatOptions.
    /// </summary>
    /// <param name="options">The ChatOptions to convert from</param>
    public ChatRunOptions(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        
        Temperature = options.Temperature.HasValue ? (double)options.Temperature.Value : null;
        TopP = options.TopP.HasValue ? (double)options.TopP.Value : null;
        MaxOutputTokens = options.MaxOutputTokens;
        FrequencyPenalty = options.FrequencyPenalty.HasValue ? (double)options.FrequencyPenalty.Value : null;
        PresencePenalty = options.PresencePenalty.HasValue ? (double)options.PresencePenalty.Value : null;
        ModelId = options.ModelId;
        StopSequences = options.StopSequences as IReadOnlyList<string>;
        
        if (options.AdditionalProperties?.Count > 0)
        {
            AdditionalProperties = new Dictionary<string, object>();
            foreach (var kvp in options.AdditionalProperties)
            {
                AdditionalProperties[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Temperature (0.0-2.0). Higher = more creative, lower = more deterministic.
    /// Null = use config default.
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Top-P (nucleus) sampling (0.0-1.0). Controls diversity of output.
    /// Null = use config default.
    /// </summary>
    [JsonPropertyName("topP")]
    public double? TopP { get; set; }

    /// <summary>
    /// Maximum output tokens for this run.
    /// Null = use config default.
    /// </summary>
    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Frequency penalty (-2.0 to 2.0). Reduces repetition.
    /// Null = use config default.
    /// </summary>
    [JsonPropertyName("frequencyPenalty")]
    public double? FrequencyPenalty { get; set; }

    /// <summary>
    /// Presence penalty (-2.0 to 2.0). Encourages new topics.
    /// Null = use config default.
    /// </summary>
    [JsonPropertyName("presencePenalty")]
    public double? PresencePenalty { get; set; }

    /// <summary>
    /// Model ID to use (e.g., "gpt-4-turbo").
    /// Note: Prefer using ProviderKey/ModelId in AgentRunOptions for provider switching.
    /// This is for fine-tuning within a provider.
    /// Null = use config default.
    /// </summary>
    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    /// <summary>
    /// Stop sequences that signal end of generation.
    /// </summary>
    [JsonPropertyName("stopSequences")]
    public IReadOnlyList<string>? StopSequences { get; set; }

    /// <summary>
    /// Provider-specific additional properties (for advanced use).
    /// </summary>
    [JsonPropertyName("additionalProperties")]
    public Dictionary<string, object>? AdditionalProperties { get; set; }

    /// <summary>
    /// Converts to Microsoft.Extensions.AI.ChatOptions for internal use.
    /// Returns null if no overrides are specified.
    /// </summary>
    public ChatOptions? ToMicrosoftChatOptions()
    {
        if (Temperature == null && TopP == null && MaxOutputTokens == null &&
            FrequencyPenalty == null && PresencePenalty == null &&
            string.IsNullOrEmpty(ModelId) && StopSequences == null &&
            (AdditionalProperties == null || AdditionalProperties.Count == 0))
        {
            return null;  // No overrides
        }

        var options = new ChatOptions();

        if (Temperature.HasValue)
            options.Temperature = (float)Temperature.Value;
        if (TopP.HasValue)
            options.TopP = (float)TopP.Value;
        if (MaxOutputTokens.HasValue)
            options.MaxOutputTokens = MaxOutputTokens.Value;
        if (FrequencyPenalty.HasValue)
            options.FrequencyPenalty = (float)FrequencyPenalty.Value;
        if (PresencePenalty.HasValue)
            options.PresencePenalty = (float)PresencePenalty.Value;
        if (!string.IsNullOrEmpty(ModelId))
            options.ModelId = ModelId;

        if (StopSequences?.Count > 0)
        {
            options.StopSequences = StopSequences.ToList();
        }

        if (AdditionalProperties?.Count > 0)
        {
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            foreach (var kvp in AdditionalProperties)
            {
                options.AdditionalProperties[kvp.Key] = kvp.Value;
            }
        }

        return options;
    }

    /// <summary>
    /// Merges these options with existing ChatOptions.
    /// This options instance takes precedence over base options.
    /// </summary>
    /// <param name="baseOptions">Base options to merge with (can be null)</param>
    /// <returns>Merged ChatOptions, or null if both are empty</returns>
    public ChatOptions? MergeWith(ChatOptions? baseOptions)
    {
        var thisOptions = ToMicrosoftChatOptions();

        if (thisOptions == null && baseOptions == null)
            return null;

        if (thisOptions == null)
            return baseOptions;

        if (baseOptions == null)
            return thisOptions;

        // Merge: this options take precedence
        var merged = new ChatOptions
        {
            Temperature = thisOptions.Temperature ?? baseOptions.Temperature,
            TopP = thisOptions.TopP ?? baseOptions.TopP,
            MaxOutputTokens = thisOptions.MaxOutputTokens ?? baseOptions.MaxOutputTokens,
            FrequencyPenalty = thisOptions.FrequencyPenalty ?? baseOptions.FrequencyPenalty,
            PresencePenalty = thisOptions.PresencePenalty ?? baseOptions.PresencePenalty,
            ModelId = thisOptions.ModelId ?? baseOptions.ModelId,
            StopSequences = thisOptions.StopSequences ?? baseOptions.StopSequences,
            Tools = baseOptions.Tools,  // Always from base (tools are agent-level)
            ToolMode = baseOptions.ToolMode,
            ResponseFormat = baseOptions.ResponseFormat,
            Seed = baseOptions.Seed
        };

        // Merge additional properties
        if (baseOptions.AdditionalProperties?.Count > 0 || thisOptions.AdditionalProperties?.Count > 0)
        {
            merged.AdditionalProperties = new AdditionalPropertiesDictionary();

            // Base first
            if (baseOptions.AdditionalProperties != null)
            {
                foreach (var kvp in baseOptions.AdditionalProperties)
                {
                    merged.AdditionalProperties[kvp.Key] = kvp.Value;
                }
            }

            // Override with this
            if (thisOptions.AdditionalProperties != null)
            {
                foreach (var kvp in thisOptions.AdditionalProperties)
                {
                    merged.AdditionalProperties[kvp.Key] = kvp.Value;
                }
            }
        }

        return merged;
    }
}
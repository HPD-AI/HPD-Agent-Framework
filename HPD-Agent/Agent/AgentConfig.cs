using System;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

/// A data-centric class that holds all the serializable configuration
/// for creating a new agent.
/// </summary>
public class AgentConfig
{
    public string Name { get; set; } = "HPD-Agent";
    public string SystemInstructions { get; set; } = "You are a helpful assistant.";
    
    /// <summary>
    /// Maximum number of turns the agent can take to call functions before requiring continuation permission.
    /// Each turn allows the LLM to analyze previous results and decide whether to call more functions or provide a final response.
    /// </summary>
    public int MaxAgenticIterations { get; set; } = 10;
    
    /// <summary>
    /// How many additional turns to allow when user chooses to continue beyond the limit.
    /// This includes extra iterations for the LLM to complete its task and generate a final response.
    /// </summary>
    public int ContinuationExtensionAmount { get; set; } = 3;

    /// <summary>
    /// Configuration for the AI provider (e.g., OpenAI, Ollama).
    /// </summary>
    public ProviderConfig? Provider { get; set; }

    /// <summary>
    /// Configuration for the agent's Dynamic memory (Full Text Injection).
    /// </summary>
    public DynamicMemoryConfig? DynamicMemory { get; set; }

    /// <summary>
    /// Configuration for the agent's static knowledge base (read-only expertise).
    /// </summary>
    public StaticMemoryConfig? StaticMemory { get; set; }

    /// <summary>
    /// Configuration for the Model Context Protocol (MCP).
    /// </summary>
    public McpConfig? Mcp { get; set; }

    /// <summary>
    /// Configuration for web search capabilities.
    /// </summary>
    public WebSearchConfig? WebSearch { get; set; }

    /// <summary>
    /// Configuration for error handling behavior.
    /// </summary>
    public ErrorHandlingConfig? ErrorHandling { get; set; }

    /// <summary>
    /// Configuration for document handling behavior.
    /// </summary>
    public DocumentHandlingConfig? DocumentHandling { get; set; }

    /// <summary>
    /// Configuration for conversation history reduction to manage context window size.
    /// </summary>
    public HistoryReductionConfig? HistoryReduction { get; set; }

    /// <summary>
    /// Configuration for plan mode - enables agents to create and manage execution plans.
    /// </summary>
    public PlanModeConfig? PlanMode { get; set; }

    /// <summary>
    /// Configuration for agentic loop safety controls (timeouts, circuit breakers).
    /// </summary>
    public AgenticLoopConfig? AgenticLoop { get; set; }

    /// <summary>
    /// Configuration for agent system messages (termination messages, error messages, etc.).
    /// Allows customization for internationalization, branding, or context-specific needs.
    /// </summary>
    public AgentMessagesConfig Messages { get; set; } = new();

    /// <summary>
    /// Configuration for tool selection behavior (how the LLM chooses which tools to use).
    /// </summary>
    public ToolSelectionConfig? ToolSelection { get; set; }

    /// <summary>
    /// Configuration for plugin scoping - hierarchical organization of plugin functions to reduce token usage.
    /// When enabled, plugin functions are hidden behind container functions, reducing initial tool list by up to 87.5%.
    /// </summary>
    public PluginScopingConfig? PluginScoping { get; set; }

    /// <summary>
    /// Tools that the agent can invoke but are NOT sent to the LLM in each request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some AI services (e.g., OpenAI Assistants, Anthropic with pre-configured tools) allow you to
    /// configure functions server-side that persist across requests. When the LLM calls these functions,
    /// your agent needs to be able to execute them even though they weren't in <see cref="ChatOptions.Tools"/>.
    /// </para>
    /// <para>
    /// <b>Use Cases:</b>
    /// <list type="bullet">
    /// <item>OpenAI Assistants with pre-configured tools</item>
    /// <item>Azure AI Function Apps registered with the service</item>
    /// <item>Anthropic accounts with account-level tool configurations</item>
    /// <item>Testing scenarios where you want to hide tools from the LLM but still handle calls</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Priority:</b> If a function exists in both <see cref="ChatOptions.Tools"/> and ServerConfiguredTools,
    /// the one in <see cref="ChatOptions.Tools"/> takes precedence (allows per-request overrides).
    /// </para>
    /// <para>
    /// Inspired by Microsoft.Extensions.AI's <c>FunctionInvokingChatClient.AdditionalTools</c>.
    /// </para>
    /// <para>
    /// <b>Example:</b>
    /// <code>
    /// var agent = new Agent(new AgentConfig 
    /// {
    ///     ServerConfiguredTools = [get_weather_function, search_web_function]
    /// });
    /// 
    /// // Request doesn't include tools (they're server-configured)
    /// var response = await agent.GetResponseAsync(messages, new ChatOptions());
    /// 
    /// // LLM calls "get_weather" (server knows about it)
    /// // Agent finds it in ServerConfiguredTools and executes it
    /// </code>
    /// </para>
    /// </remarks>
    [JsonIgnore] // Don't serialize AIFunction instances
    public IList<AITool>? ServerConfiguredTools { get; set; }
}

#region Supporting Configuration Classes

/// <summary>
/// Configuration for the agent's dynamic, editable working memory.
/// Mirrors properties from AgentDynamicMemoryOptions.
/// </summary>
public class DynamicMemoryConfig
{
    /// <summary>
    /// The root directory where agent memories will be stored.
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-Dynamic-memory-storage";

    /// <summary>
    /// The maximum number of tokens to include from the Dynamic memory.
    /// </summary>
    public int MaxTokens { get; set; } = 4000;

    /// <summary>
    /// Automatically evict old memories when approaching token limit.
    /// </summary>
    public bool EnableAutoEviction { get; set; } = true;

    /// <summary>
    /// Token threshold for triggering auto-eviction (percentage).
    /// </summary>
    public int AutoEvictionThreshold { get; set; } = 85;
}

/// <summary>
/// Configuration for the agent's static knowledge base.
/// This is read-only domain expertise (e.g., Python docs, design patterns, API references).
/// Mirrors properties from AgentKnowledgeOptions.
/// </summary>
public class StaticMemoryConfig
{
    /// <summary>
    /// Strategy for handling agent knowledge (FullTextInjection or IndexedRetrieval).
    /// </summary>
    public MemoryStrategy Strategy { get; set; } = MemoryStrategy.FullTextInjection;

    /// <summary>
    /// Directory where knowledge documents are stored.
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-static-memory";

    /// <summary>
    /// Maximum tokens to inject when using FullTextInjection strategy.
    /// </summary>
    public int MaxTokens { get; set; } = 8000;

    /// <summary>
    /// Optional agent name for scoping knowledge storage.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// List of document paths or URLs to add at agent build time.
    /// </summary>
    public List<string> DocumentPaths { get; set; } = new();
}

/// <summary>
/// Configuration for the Model Context Protocol (MCP).
/// </summary>
public class McpConfig
{
    public string ManifestPath { get; set; } = string.Empty;
    public MCPOptions? Options { get; set; }
}

/// <summary>
/// Configuration for AI provider settings.
/// Based on existing patterns in AgentBuilder.
/// </summary>
public class ProviderConfig
{
    /// <summary>
    /// Provider identifier (lowercase, e.g., "openai", "anthropic", "ollama").
    /// This is the primary key for provider resolution.
    /// </summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>
    /// DEPRECATED: Use ProviderKey instead. Kept for backward compatibility.
    /// </summary>
    [Obsolete("Use ProviderKey instead for better extensibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ChatProvider Provider { get; set; }

    public string ModelName { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public ChatOptions? DefaultChatOptions { get; set; }
    
    /// <summary>
    /// Provider-specific configuration as key-value pairs.
    /// See provider documentation for available options.
    /// 
    /// Examples:
    /// - OpenAI: { "Organization": "org-123", "StrictJsonSchema": true }
    /// - Anthropic: { "PromptCachingType": "AutomaticToolsAndSystem" }
    /// - OpenRouter: { "HttpReferer": "https://myapp.com" }
    /// - Ollama: { "NumCtx": 8192, "KeepAlive": "5m" }
    /// </summary>
    public Dictionary<string, object>? AdditionalProperties { get; set; }
}

/// <summary>
/// Holds all web search related configurations.
/// </summary>
public class WebSearchConfig
{
    /// <summary>
    /// The name of the default search provider to use if multiple are configured.
    /// Should match one of the keys in the provider configs (e.g., "Tavily").
    /// </summary>
    public string? DefaultProvider { get; set; }

    /// <summary>
    /// Configuration for Tavily web search provider.
    /// </summary>
    public TavilyConfig? Tavily { get; set; }

    /// <summary>
    /// Configuration for Brave web search provider.
    /// </summary>
    public BraveConfig? Brave { get; set; }

    /// <summary>
    /// Configuration for Bing web search provider.
    /// </summary>
    public BingConfig? Bing { get; set; }
}

/// <summary>
/// Configuration for error handling behavior.
/// </summary>
public class ErrorHandlingConfig
{
    /// <summary>
    /// Whether to normalize provider-specific errors into standard formats
    /// </summary>
    public bool NormalizeErrors { get; set; } = true;

    /// <summary>
    /// Whether to include provider-specific details in error messages
    /// </summary>
    public bool IncludeProviderDetails { get; set; } = false;

    /// <summary>
    /// Whether to include detailed exception messages in function results sent to the LLM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Security Warning:</b> Setting this to <c>true</c> may expose sensitive information to the LLM and end users:
    /// - Database connection strings
    /// - File system paths
    /// - API keys or tokens
    /// - Internal implementation details
    /// </para>
    /// <para>
    /// When <c>false</c> (default), function errors are reported to the LLM as generic messages like
    /// "Error: Function 'X' failed." The full exception is still available to application code via
    /// <see cref="FunctionResultContent.Exception"/> for logging and debugging.
    /// </para>
    /// <para>
    /// When <c>true</c>, the full exception message is included in the function result, allowing the LLM
    /// to potentially self-correct (e.g., retry with different arguments). Use this only in trusted
    /// environments or with sanitized exceptions.
    /// </para>
    /// <para>
    /// Inspired by Microsoft.Extensions.AI's <c>FunctionInvokingChatClient.IncludeDetailedErrors</c>.
    /// </para>
    /// </remarks>
    public bool IncludeDetailedErrorsInChat { get; set; } = false;

    /// <summary>
    /// Maximum number of retries for transient errors
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Timeout for a single function execution (default: 30 seconds)
    /// </summary>
    public TimeSpan? SingleFunctionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay before retrying failed function (default: 1 second, exponentially increased per attempt)
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether to use provider-specific retry delays from Retry-After headers or error messages.
    /// Default is true (opinionated: respect provider guidance).
    /// </summary>
    public bool UseProviderRetryDelays { get; set; } = true;

    /// <summary>
    /// Whether to automatically attempt token refresh on 401 authentication errors.
    /// Default is true (opinionated: auto-recovery when possible).
    /// </summary>
    public bool AutoRefreshTokensOn401 { get; set; } = true;

    /// <summary>
    /// Maximum retry delay cap to prevent excessive waiting.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Exponential backoff multiplier for retry delays.
    /// Default is 2.0 (doubles the delay each attempt).
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Optional per-category retry limits. If null, uses MaxRetries for all categories.
    /// Example: { ErrorCategory.RateLimitRetryable: 5, ErrorCategory.ServerError: 3 }
    /// </summary>
    public Dictionary<HPD.Agent.ErrorHandling.ErrorCategory, int>? MaxRetriesByCategory { get; set; }

    /// <summary>
    /// Provider-specific error handler. If null, auto-detects based on ChatProvider.
    /// Set this to customize error parsing for specific providers or use custom handlers.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HPD.Agent.ErrorHandling.IProviderErrorHandler? ProviderHandler { get; set; }

    /// <summary>
    /// Custom retry strategy that overrides default behavior.
    /// Parameters: (exception, attemptNumber, cancellationToken)
    /// Returns: TimeSpan for retry delay, or null to stop retrying.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<Exception, int, CancellationToken, Task<TimeSpan?>>? CustomRetryStrategy { get; set; }
}

/// <summary>
/// Configuration for document handling behavior.
/// </summary>
public class DocumentHandlingConfig
{
    /// <summary>
    /// Strategy for how documents should be processed and included in prompts.
    /// Default is FullTextInjection.
    /// </summary>
    public ConversationDocumentHandling Strategy { get; set; } = ConversationDocumentHandling.FullTextInjection;

    /// <summary>
    /// Custom document tag format for message injection.
    /// Uses string.Format with {0} = filename, {1} = extracted text.
    /// If null, uses default format from ConversationDocumentHelper.
    /// </summary>
    public string? DocumentTagFormat { get; set; }

    /// <summary>
    /// Maximum file size in bytes to process (default: 10MB).
    /// Files larger than this will be rejected.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>
/// Configuration for conversation history reduction using Microsoft.Extensions.AI IChatReducer.
/// </summary>
public class HistoryReductionConfig
{
    /// <summary>
    /// Whether history reduction is enabled.
    /// Default is false to maintain backward compatibility.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Strategy for reducing conversation history.
    /// Default is MessageCounting (keeps last N messages).
    /// </summary>
    public HistoryReductionStrategy Strategy { get; set; } = HistoryReductionStrategy.MessageCounting;

    /// <summary>
    /// Target number of messages to retain after reduction.
    /// Default is 20 messages.
    /// Used when MaxTokenBudget is not set or as a fallback.
    /// </summary>
    public int TargetMessageCount { get; set; } = 20;

    /// <summary>
    /// Threshold count for SummarizingChatReducer.
    /// Number of messages allowed beyond TargetMessageCount before summarization is triggered.
    /// Only used when Strategy is Summarizing. Default is 5.
    /// </summary>
    public int? SummarizationThreshold { get; set; } = 5;

    /// <summary>
    /// Maximum token budget before triggering reduction (optional, FFI-friendly).
    ///
    /// ⚠️ CURRENTLY DISABLED - Token tracking is not implemented.
    /// See docs/TOKEN_TRACKING_README.md for why this is architecturally impossible.
    ///
    /// This setting exists for API compatibility but is IGNORED by the history reduction system.
    /// History reduction uses message-count based strategy only (TargetMessageCount).
    ///
    /// Why token tracking doesn't work:
    /// - Provider APIs report cumulative input tokens, not per-message breakdowns
    /// - Prompt caching makes costs non-deterministic (cache hit = 90% cheaper)
    /// - Ephemeral context (system prompts, RAG, memory) adds 50-200% overhead
    /// - Reasoning tokens (o1, Gemini Thinking) can be 50x larger than visible output
    ///
    /// Industry context: LangChain, Semantic Kernel, and AutoGen all use message-count
    /// reduction for the same reason. Even Gemini CLI (with privileged API access) uses
    /// character estimation with ±20% acknowledged error.
    ///
    /// If null (recommended), uses message-based reduction.
    /// </summary>
    public int? MaxTokenBudget { get; set; } = null;

    /// <summary>
    /// Target token count after reduction (default: 4000).
    ///
    /// ⚠️ CURRENTLY IGNORED - Token tracking is not implemented.
    /// Only used when MaxTokenBudget is set (which is also disabled).
    /// See MaxTokenBudget documentation for details.
    /// </summary>
    public int TargetTokenBudget { get; set; } = 4000;

    /// <summary>
    /// Token threshold for triggering reduction when using token budgets.
    /// Number of tokens allowed beyond TargetTokenBudget before reduction is triggered.
    ///
    /// ⚠️ CURRENTLY IGNORED - Token tracking is not implemented.
    /// Only used when MaxTokenBudget is set (which is also disabled).
    /// See MaxTokenBudget documentation for details.
    /// </summary>
    public int TokenBudgetThreshold { get; set; } = 1000;

    /// <summary>
    /// When set, uses percentage-based triggers instead of absolute token counts.
    /// Requires ContextWindowSize to be configured.
    /// Example: 0.7 = trigger reduction at 70% of context window.
    ///
    /// ⚠️ CURRENTLY IGNORED - Token tracking is not implemented.
    /// See MaxTokenBudget documentation for why token-based reduction doesn't work.
    /// Use TargetMessageCount instead for reliable history reduction.
    /// </summary>
    public double? TokenBudgetTriggerPercentage { get; set; } = null;

    /// <summary>
    /// Percentage of context window to preserve after reduction.
    /// Only used when TokenBudgetTriggerPercentage is set.
    /// Example: 0.3 = keep 30% of context window after compression.
    ///
    /// ⚠️ CURRENTLY IGNORED - Token tracking is not implemented.
    /// See MaxTokenBudget documentation for details.
    /// </summary>
    public double TokenBudgetPreservePercentage { get; set; } = 0.3;

    /// <summary>
    /// Context window size for percentage calculations.
    /// Required when using TokenBudgetTriggerPercentage.
    ///
    /// ⚠️ CURRENTLY IGNORED - Token tracking is not implemented.
    /// See MaxTokenBudget documentation for details.
    /// </summary>
    public int? ContextWindowSize { get; set; } = null;

    /// <summary>
    /// Custom summarization prompt for SummarizingChatReducer.
    /// If null, uses the default prompt from Microsoft.Extensions.AI.
    /// Only used when Strategy is Summarizing.
    /// </summary>
    public string? CustomSummarizationPrompt { get; set; }

    /// <summary>
    /// Optional separate provider configuration for the summarization LLM.
    /// If null, uses the agent's main provider (baseClient).
    /// Useful for cost optimization - e.g., use GPT-4o-mini for summaries while main agent uses GPT-4.
    /// Only used when Strategy is Summarizing.
    /// </summary>
    public ProviderConfig? SummarizerProvider { get; set; }

    /// <summary>
    /// Metadata key used to mark summary messages in chat history.
    /// Summary messages are identified by this key in their Metadata dictionary.
    /// </summary>
    public const string SummaryMetadataKey = "__summary__";

    /// <summary>
    /// Whether to use a single comprehensive summary (re-summarize everything including old summary)
    /// or maintain layered summaries (incremental summarization).
    /// Default is true (single summary for better quality, following Semantic Kernel pattern).
    /// </summary>
    public bool UseSingleSummary { get; set; } = true;
}

/// <summary>
/// Strategy for reducing conversation history size.
/// </summary>
public enum HistoryReductionStrategy
{
    /// <summary>
    /// Keep only the N most recent messages (plus first system message).
    /// Fast and simple, but loses older context completely.
    /// </summary>
    MessageCounting,

    /// <summary>
    /// Use LLM to summarize older messages when history exceeds threshold.
    /// Preserves context through summarization, but requires additional LLM calls.
    /// </summary>
    Summarizing
}

/// <summary>
/// Configuration for plan mode capabilities.
/// Enables agents to create and manage execution plans for complex multi-step tasks.
/// </summary>
public class PlanModeConfig
{
    /// <summary>
    /// Whether plan mode is enabled for this agent.
    /// Default is true when configured via WithPlanMode().
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Custom instructions to add to system prompt explaining plan mode usage.
    /// If null, uses default instructions.
    /// </summary>
    public string? CustomInstructions { get; set; }
}

/// <summary>
/// Configuration for agentic loop safety controls to prevent runaway execution.
/// </summary>
public class AgenticLoopConfig
{
    /// <summary>
    /// Maximum duration for a single turn before timeout (default: 5 minutes)
    /// </summary>
    public TimeSpan? MaxTurnDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Max times the same function can be called consecutively before circuit breaker triggers (default: 5)
    /// </summary>
    public int? MaxConsecutiveFunctionCalls { get; set; } = 5;

    /// <summary>
    /// Maximum number of functions to execute in parallel (default: null = unlimited).
    /// Useful for limiting resource consumption when functions are CPU-intensive,
    /// respecting external API rate limits, or matching database connection pool sizes.
    /// </summary>
    public int? MaxParallelFunctions { get; set; } = null;

    /// <summary>
    /// Controls behavior when the LLM requests a function that isn't available.
    /// When false (default): Creates a "function not found" error message and continues the agentic loop.
    /// When true: Terminates the agentic loop immediately and returns control to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting this to false (default) allows the LLM to recover from hallucinated functions by seeing
    /// the error and trying different approaches. This is useful for normal single-agent scenarios.
    /// </para>
    /// <para>
    /// Setting this to true is useful for multi-agent handoff scenarios where an unknown function request
    /// might indicate that the current agent should transfer control to another agent that has that function.
    /// When the loop terminates, the caller receives the function call request and can route it appropriately.
    /// </para>
    /// <para>
    /// Note: Functions that are known (via ChatOptions.Tools or AgentConfig.ServerConfiguredTools) but aren't
    /// AIFunction instances (e.g., AIFunctionDeclaration only) will also cause termination regardless of
    /// this setting, as they cannot be invoked by the agent.
    /// </para>
    /// </remarks>
    public bool TerminateOnUnknownCalls { get; set; } = false;
}

/// <summary>
/// Configuration for tool selection behavior.
/// FFI-friendly: Uses primitives (strings) instead of complex types for cross-language compatibility.
/// </summary>
public class ToolSelectionConfig
{
    /// <summary>
    /// Tool selection mode: "Auto" (LLM decides), "None" (no tools), "RequireAny" (must call at least one), or "RequireSpecific" (must call the named function).
    /// Default is "Auto".
    /// </summary>
    public string ToolMode { get; set; } = "Auto";

    /// <summary>
    /// Required function name when ToolMode = "RequireSpecific".
    /// Ignored for other modes.
    /// </summary>
    public string? RequiredFunctionName { get; set; }
}

/// <summary>
/// Mistral AI-specific settings
/// </summary>
public class MistralSettings
{
    /// <summary>
    /// API key for the Mistral AI platform.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Configuration for plugin scoping feature.
/// Controls hierarchical organization of plugin functions to reduce token usage.
/// </summary>
public class PluginScopingConfig
{
    /// <summary>
    /// Enable plugin scoping for C# plugins. When true, plugin functions are hidden behind container functions.
    /// Default: false (disabled - all functions visible immediately).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Enable scoping for MCP tools. When true, tools from each MCP server are grouped behind a container.
    /// Example: MCP_filesystem container contains ReadFile, WriteFile, etc.
    /// Default: false (MCP tools always visible).
    /// </summary>
    public bool ScopeMCPTools { get; set; } = false;

    /// <summary>
    /// Enable scoping for Frontend (AGUI) tools. When true, all frontend tools are grouped in a FrontendTools container.
    /// Frontend tools are human-in-the-loop tools executed by the UI.
    /// Default: false (frontend tools always visible).
    /// </summary>
    public bool ScopeFrontendTools { get; set; } = false;

    /// <summary>
    /// Maximum number of function names to include in auto-generated container descriptions.
    /// For template descriptions like "MCP Server 'filesystem'. Contains 15 functions: ReadFile, WriteFile, ..."
    /// Default: 10.
    /// </summary>
    public int MaxFunctionNamesInDescription { get; set; } = 10;

    /// <summary>
    /// Optional post-expansion instructions for specific MCP servers.
    /// Key = MCP server name (e.g., "filesystem", "github")
    /// Value = Instructions shown to the agent after that server's container is expanded.
    /// Example: { "filesystem", "IMPORTANT: Always use absolute paths. Check FileExists before operations." }
    /// </summary>
    public Dictionary<string, string>? MCPServerInstructions { get; set; }

    /// <summary>
    /// Optional post-expansion instructions for Frontend tools container.
    /// Shown to the agent after expanding the FrontendTools container.
    /// Example: "These tools interact with the user. Use ConfirmAction for destructive operations."
    /// </summary>
    public string? FrontendToolsInstructions { get; set; }

    /// <summary>
    /// When true, ONLY functions referenced by skills are visible to the agent.
    /// All plugin containers and unreferenced functions are hidden.
    /// Skills become the exclusive interface for accessing functions.
    /// Default: false (plugins and unreferenced functions remain visible).
    ///
    /// Use this for "pure skills mode" where:
    /// - Plugins must be registered (for validation), but won't be visible
    /// - Functions only accessible through skill expansion
    /// - Skills provide the ONLY interface to the agent
    /// </summary>
    public bool SkillsOnlyMode { get; set; } = false;
}

#endregion

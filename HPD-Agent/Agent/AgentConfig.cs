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
    /// Configuration for the agent's injected memory (Full Text Injection).
    /// </summary>
    public InjectedMemoryConfig? InjectedMemory { get; set; }

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
    /// Configuration for tool selection behavior (how the LLM chooses which tools to use).
    /// </summary>
    public ToolSelectionConfig? ToolSelection { get; set; }
}

#region Supporting Configuration Classes

/// <summary>
/// Configuration for the agent's dynamic, editable working memory.
/// Mirrors properties from AgentInjectedMemoryOptions.
/// </summary>
public class InjectedMemoryConfig
{
    /// <summary>
    /// The root directory where agent memories will be stored.
    /// </summary>
    public string StorageDirectory { get; set; } = "./agent-injected-memory-storage";

    /// <summary>
    /// The maximum number of tokens to include from the injected memory.
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
    public ChatProvider Provider { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public ChatOptions? DefaultChatOptions { get; set; }
    
    /// <summary>
    /// Provider-specific configuration options
    /// </summary>
    public ProviderSpecificConfig? ProviderSpecific { get; set; }
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
    /// When set, this takes precedence over TargetMessageCount.
    /// Uses actual token counts from provider API responses (BAML-inspired pattern).
    /// Falls back to character-based estimation for messages without usage data.
    /// If null, uses message-based reduction (backward compatible).
    /// </summary>
    public int? MaxTokenBudget { get; set; } = null;

    /// <summary>
    /// Target token count after reduction (default: 4000).
    /// Only used when MaxTokenBudget is set.
    /// The reducer will aim to keep conversation around this token count.
    /// </summary>
    public int TargetTokenBudget { get; set; } = 4000;

    /// <summary>
    /// Token threshold for triggering reduction when using token budgets.
    /// Number of tokens allowed beyond TargetTokenBudget before reduction is triggered.
    /// Only used when MaxTokenBudget is set. Default is 1000 tokens.
    /// </summary>
    public int TokenBudgetThreshold { get; set; } = 1000;

    /// <summary>
    /// When set, uses percentage-based triggers instead of absolute token counts.
    /// Requires ContextWindowSize to be configured.
    /// Example: 0.7 = trigger reduction at 70% of context window.
    /// Takes precedence over MaxTokenBudget when both are set.
    /// </summary>
    public double? TokenBudgetTriggerPercentage { get; set; } = null;

    /// <summary>
    /// Percentage of context window to preserve after reduction.
    /// Only used when TokenBudgetTriggerPercentage is set.
    /// Example: 0.3 = keep 30% of context window after compression.
    /// Default is 0.3 (30% preservation).
    /// </summary>
    public double TokenBudgetPreservePercentage { get; set; } = 0.3;

    /// <summary>
    /// Context window size for percentage calculations.
    /// Required when using TokenBudgetTriggerPercentage.
    /// User must specify based on their model (e.g., 128000 for GPT-4).
    /// No auto-detection to maintain library neutrality.
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
/// Provider-specific configuration extensions for enhanced provider support.
/// These can be accessed via ChatOptions.AdditionalProperties or RawRepresentationFactory.
/// </summary>
public class ProviderSpecificConfig
{
    /// <summary>
    /// OpenAI-specific configuration
    /// </summary>
    public OpenAISettings? OpenAI { get; set; }

    /// <summary>
    /// Azure OpenAI-specific configuration
    /// </summary>
    public AzureOpenAISettings? AzureOpenAI { get; set; }

    /// <summary>
    /// Anthropic-specific configuration
    /// </summary>
    public AnthropicSettings? Anthropic { get; set; }

    /// <summary>
    /// Ollama-specific configuration
    /// </summary>
    public OllamaSettings? Ollama { get; set; }

    /// <summary>
    /// OpenRouter-specific configuration
    /// </summary>
    public OpenRouterSettings? OpenRouter { get; set; }

    /// <summary>
    /// Google AI-specific configuration
    /// </summary>
    public GoogleAISettings? GoogleAI { get; set; }

    /// <summary>
    /// Vertex AI-specific configuration
    /// </summary>
    public VertexAISettings? VertexAI { get; set; }

    /// <summary>
    /// Hugging Face-specific configuration
    /// </summary>
    public HuggingFaceSettings? HuggingFace { get; set; }

    /// <summary>
    /// AWS Bedrock-specific configuration
    /// </summary>
    public BedrockSettings? Bedrock { get; set; }

    /// <summary>
    /// Azure AI Inference-specific configuration
    /// </summary>
    public AzureAIInferenceSettings? AzureAIInference { get; set; }

    /// <summary>
    /// ONNX Runtime-specific configuration
    /// </summary>
    public OnnxRuntimeSettings? OnnxRuntime { get; set; }

    /// <summary>
    /// Mistral AI-specific configuration
    /// </summary>
    public MistralSettings? Mistral { get; set; }
}

/// <summary>
/// OpenAI-specific settings that can be applied via AdditionalProperties
/// </summary>
public class OpenAISettings
{
    /// <summary>
    /// Organization ID for OpenAI API requests
    /// </summary>
    public string? Organization { get; set; }

    /// <summary>
    /// Whether to use strict JSON schema validation
    /// </summary>
    public bool? StrictJsonSchema { get; set; }

    /// <summary>
    /// Image detail level for vision models (low, high, auto)
    /// </summary>
    public string? ImageDetail { get; set; }

    /// <summary>
    /// Voice selection for audio models
    /// </summary>
    public string? AudioVoice { get; set; }

    /// <summary>
    /// Audio output format
    /// </summary>
    public string? AudioFormat { get; set; }

    /// <summary>
    /// Whether to enable reasoning tokens display
    /// </summary>
    public bool? IncludeReasoningTokens { get; set; }
}

/// <summary>
/// Azure OpenAI-specific settings
/// </summary>
public class AzureOpenAISettings
{
    /// <summary>
    /// Azure resource name
    /// </summary>
    public string? ResourceName { get; set; }

    /// <summary>
    /// Deployment name for the model
    /// </summary>
    public string? DeploymentName { get; set; }

    /// <summary>
    /// Azure OpenAI API version
    /// </summary>
    public string ApiVersion { get; set; } = "2024-08-01-preview";

    /// <summary>
    /// Whether to use Entra ID authentication instead of API key
    /// </summary>
    public bool UseEntraId { get; set; } = false;

    /// <summary>
    /// Azure region for data residency requirements
    /// </summary>
    public string? Region { get; set; }
}

/// <summary>
/// Anthropic-specific settings for Claude models
/// </summary>
public class AnthropicSettings
{
    /// <summary>
    /// Prompt caching configuration
    /// </summary>
    public AnthropicPromptCaching? PromptCaching { get; set; }

    /// <summary>
    /// Tool choice configuration for function calling
    /// </summary>
    public AnthropicToolChoice? ToolChoice { get; set; }

    /// <summary>
    /// Base URL for Anthropic API (useful for proxies or custom endpoints)
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Request timeout in seconds (default is 600 seconds / 10 minutes)
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Maximum number of retries for failed requests (default is 2)
    /// </summary>
    public int? MaxRetries { get; set; }

    /// <summary>
    /// Whether to include reasoning tokens in the response (for reasoning models)
    /// </summary>
    public bool? IncludeReasoningTokens { get; set; }

    /// <summary>
    /// Metadata for tracking requests
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Enable beta features
    /// </summary>
    public bool EnableBetaFeatures { get; set; } = false;
}

/// <summary>
/// Anthropic prompt caching configuration
/// </summary>
public class AnthropicPromptCaching
{
    /// <summary>
    /// Prompt caching type
    /// </summary>
    public AnthropicPromptCacheType Type { get; set; } = AnthropicPromptCacheType.None;

    /// <summary>
    /// Whether to automatically cache system messages and tools
    /// </summary>
    public bool AutoCacheSystemAndTools { get; set; } = true;
}

/// <summary>
/// Types of prompt caching available in Anthropic
/// </summary>
public enum AnthropicPromptCacheType
{
    /// <summary>
    /// No prompt caching
    /// </summary>
    None,
    
    /// <summary>
    /// Automatic caching of tools and system messages
    /// </summary>
    AutomaticToolsAndSystem,
    
    /// <summary>
    /// Fine-grained manual control over cache points
    /// </summary>
    FineGrained
}

/// <summary>
/// Anthropic tool choice configuration
/// </summary>
public class AnthropicToolChoice
{
    /// <summary>
    /// Tool choice type
    /// </summary>
    public AnthropicToolChoiceType Type { get; set; } = AnthropicToolChoiceType.Auto;

    /// <summary>
    /// Specific tool name to use (when Type is Tool)
    /// </summary>
    public string? ToolName { get; set; }
}

/// <summary>
/// Types of tool choice behavior for Anthropic
/// </summary>
public enum AnthropicToolChoiceType
{
    /// <summary>
    /// Let Claude decide whether to use tools
    /// </summary>
    Auto,
    
    /// <summary>
    /// Force Claude to use a specific tool
    /// </summary>
    Tool,
    
    /// <summary>
    /// Force Claude to use any available tool
    /// </summary>
    Any
}

/// <summary>
/// Ollama-specific settings that can be applied to requests
/// </summary>
public class OllamaSettings
{
    /// <summary>
    /// Context window size (num_ctx parameter)
    /// </summary>
    public int? NumCtx { get; set; }

    /// <summary>
    /// How long to keep the model loaded in memory
    /// </summary>
    public string? KeepAlive { get; set; }

    /// <summary>
    /// Use memory locking to prevent swapping
    /// </summary>
    public bool? UseMlock { get; set; }

    /// <summary>
    /// Number of threads to use for inference
    /// </summary>
    public int? NumThread { get; set; }

    /// <summary>
    /// Temperature override for Ollama models
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Top-p override for Ollama models
    /// </summary>
    public float? TopP { get; set; }
}

/// <summary>
/// OpenRouter-specific settings and features
/// </summary>
public class OpenRouterSettings
{
    /// <summary>
    /// HTTP Referer header for OpenRouter requests
    /// </summary>
    public string? HttpReferer { get; set; }

    /// <summary>
    /// Application name for OpenRouter analytics
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Reasoning configuration for models that support it
    /// </summary>
    public OpenRouterReasoningConfig? Reasoning { get; set; }

    /// <summary>
    /// Preferred provider for models available on multiple providers
    /// </summary>
    public string? PreferredProvider { get; set; }

    /// <summary>
    /// Whether to allow fallback to other providers if preferred is unavailable
    /// </summary>
    public bool AllowFallback { get; set; } = true;
}

/// <summary>
/// OpenRouter reasoning configuration for models like DeepSeek-R1
/// </summary>
public class OpenRouterReasoningConfig
{
    /// <summary>
    /// Whether to enable reasoning mode
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum reasoning tokens to generate
    /// </summary>
    public int? MaxReasoningTokens { get; set; }

    /// <summary>
    /// Whether to include reasoning in the response
    /// </summary>
    public bool IncludeReasoning { get; set; } = true;
}

/// <summary>
/// Google AI-specific settings for the simple Gemini API via API Key
/// </summary>
public class GoogleAISettings
{
    /// <summary>
    /// API key for the Google AI platform.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// Vertex AI-specific settings for the enterprise Google Cloud platform
/// </summary>
public class VertexAISettings
{
    /// <summary>
    /// The Google Cloud Project ID.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// The Google Cloud Region (e.g., "us-central1").
    /// </summary>
    public string? Region { get; set; }
    
    // Note: AccessToken is typically handled by Google's Application Default Credentials (ADC)
    // and does not need to be explicitly set by the user in most cases.
}

/// <summary>
/// Hugging Face-specific settings for the Serverless Inference API
/// </summary>
public class HuggingFaceSettings
{
    /// <summary>
    /// API key for the Hugging Face Inference API.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// AWS Bedrock-specific settings
/// </summary>
public class BedrockSettings
{
    /// <summary>
    /// The AWS Region where the Bedrock service is hosted (e.g., "us-east-1").
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Optional AWS Access Key ID. If not provided, the SDK will use the default credential chain.
    /// </summary>
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// Optional AWS Secret Access Key. If not provided, the SDK will use the default credential chain.
    /// </summary>
    public string? SecretAccessKey { get; set; }
}

/// <summary>
/// Azure AI Inference-specific settings
/// </summary>
public class AzureAIInferenceSettings
{
    /// <summary>
    /// The unified endpoint for the Azure AI resource (e.g., "https://<your-resource-name>.inference.ai.azure.com").
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API key for the Azure AI resource.
    /// Can be omitted if using managed identity (TokenCredential).
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// ONNX Runtime-specific settings for local model inference with DirectML acceleration support
/// </summary>
public class OnnxRuntimeSettings
{
    /// <summary>
    /// The file path to the ONNX model directory.
    /// The tokenizer is automatically loaded from the same directory.
    /// For DirectML acceleration, ensure the model is compatible with GPU execution.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Optional stop sequences for the model.
    /// These will be used in addition to any provided in ChatOptions.
    /// Common examples: ["<|end|>", "<|eot_id|>", "</s>"]
    /// </summary>
    public IList<string>? StopSequences { get; set; }

    /// <summary>
    /// Whether to enable conversation caching for better performance.
    /// Should only be set to true when the client is not shared across multiple conversations.
    /// Caching can significantly improve response times for follow-up messages.
    /// </summary>
    public bool EnableCaching { get; set; } = false;

    /// <summary>
    /// Custom prompt formatter function for advanced prompt engineering.
    /// If null, the default formatter will be used which formats messages as JSON array.
    /// Use this to implement model-specific prompt templates (e.g., ChatML, Alpaca, etc.).
    /// </summary>
    /// <example>
    /// For ChatML format:
    /// <code>
    /// PromptFormatter = (messages, options) => {
    ///     var sb = new StringBuilder();
    ///     foreach (var msg in messages) {
    ///         sb.Append($"&lt;|{msg.Role.Value}|&gt;\n{msg.Text}&lt;|end|&gt;\n");
    ///     }
    ///     sb.Append("&lt;|assistant|&gt;\n");
    ///     return sb.ToString();
    /// }
    /// </code>
    /// </example>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, string>? PromptFormatter { get; set; }

    /// <summary>
    /// DirectML-specific execution provider options.
    /// Set to true to prefer DirectML execution provider for GPU acceleration on Windows.
    /// Falls back to CPU if DirectML is not available.
    /// </summary>
    public bool PreferDirectML { get; set; } = true;

    /// <summary>
    /// Maximum context length for the model in tokens.
    /// If not specified, uses the model's default context window.
    /// DirectML models may have different optimal context lengths.
    /// </summary>
    public int? MaxContextLength { get; set; }

    /// <summary>
    /// GPU device ID to use for DirectML execution (0-based).
    /// If null, uses the default GPU. Only applicable when PreferDirectML is true.
    /// </summary>
    public int? DeviceId { get; set; }
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

#endregion

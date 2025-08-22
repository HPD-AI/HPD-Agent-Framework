using Microsoft.Extensions.AI;
/// <summary>
/// A data-centric class that holds all the serializable configuration
/// for creating a new agent.
/// </summary>
public class AgentConfig
{
    public string Name { get; set; } = "HPD-Agent";
    public string SystemInstructions { get; set; } = "You are a helpful assistant.";
    public int MaxFunctionCalls { get; set; } = 10;
    public int MaxConversationHistory { get; set; } = 20;

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
    /// Configuration for audio capabilities (TTS/STT).
    /// </summary>
    public AudioConfig? Audio { get; set; }
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
/// Holds all audio-related configurations.
/// </summary>
public class AudioConfig
{
    public ElevenLabsConfig? ElevenLabs { get; set; }
    public AzureSpeechConfig? AzureSpeech { get; set; }
    public AudioCapabilityOptions? Options { get; set; }
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
}

#endregion

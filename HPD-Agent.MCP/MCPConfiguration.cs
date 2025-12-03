using System.Text.Json.Serialization;

namespace HPD.Agent.MCP;

/// <summary>
/// Root configuration object for MCP manifest files
/// </summary>
public class MCPManifest
{
    [JsonPropertyName("servers")]
    public List<MCPServerConfig> Servers { get; set; } = new();
}

/// <summary>
/// Configuration for a single MCP server
/// </summary>
public class MCPServerConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    /// <summary>
    /// Optional description for the MCP server container.
    /// If not provided, will attempt to extract from server's ServerInfo metadata.
    /// If both are unavailable, will auto-generate from function names.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enable scoping for this MCP server's tools.
    /// When true, tools are grouped behind a container (e.g., MCP_filesystem).
    /// When false, tools are exposed directly (e.g., filesystem_read_file).
    /// If not specified, defaults to false (no scoping).
    /// </summary>
    [JsonPropertyName("enableScoping")]
    public bool? EnableScoping { get; set; }

    /// <summary>
    /// Whether tools from this MCP server require user permission before execution.
    /// When true, all tools from this server will trigger permission requests.
    /// When false, tools execute without permission prompts (use for read-only servers).
    /// If not specified, defaults to true (require permission for safety).
    /// </summary>
    [JsonPropertyName("requiresPermission")]
    public bool RequiresPermission { get; set; } = true;

    [JsonPropertyName("timeout")]
    public int TimeoutMs { get; set; } = 30000;
    
    [JsonPropertyName("retryAttempts")]
    public int RetryAttempts { get; set; } = 3;
    
    [JsonPropertyName("environment")]
    public Dictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// Validates the server configuration
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Server name is required", nameof(Name));
            
        if (string.IsNullOrWhiteSpace(Command))
            throw new ArgumentException("Server command is required", nameof(Command));
            
        if (TimeoutMs <= 0)
            throw new ArgumentException("Timeout must be positive", nameof(TimeoutMs));
            
        if (RetryAttempts < 0)
            throw new ArgumentException("Retry attempts cannot be negative", nameof(RetryAttempts));
    }
}

/// <summary>
/// Options for configuring MCP integration
/// </summary>
public class MCPOptions
{
    public string ManifestPath { get; set; } = string.Empty;
    public bool FailOnServerError { get; set; } = false;
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentServers { get; set; } = 10;
}

/// <summary>
/// JSON serialization context for AOT compilation
/// </summary>
[JsonSerializable(typeof(MCPManifest))]
[JsonSerializable(typeof(MCPServerConfig))]
[JsonSerializable(typeof(List<MCPServerConfig>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public partial class MCPJsonSerializerContext : JsonSerializerContext
{
}

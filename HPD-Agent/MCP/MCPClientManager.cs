using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using System.Text.Json;


/// <summary>
/// Manages lifecycle of MCP clients and tool loading
/// </summary>
public class MCPClientManager : IDisposable
{
    private readonly Dictionary<string, IMcpClient> _clients = new();
    private readonly ILogger<MCPClientManager> _logger;
    private readonly MCPOptions _options;
    private bool _disposed = false;

    public MCPClientManager(ILogger<MCPClientManager> logger, MCPOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new MCPOptions();
    }

    /// <summary>
    /// Loads MCP tools from the specified manifest file
    /// </summary>
    public async Task<List<AIFunction>> LoadToolsFromManifestAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading MCP tools from manifest: {ManifestPath}", manifestPath);
        
        var manifest = await LoadManifestAsync(manifestPath, cancellationToken);
        var allTools = new List<AIFunction>();
        
        var enabledServers = manifest.Servers.Where(s => s.Enabled).ToList();
        _logger.LogInformation("Found {Count} enabled servers in manifest", enabledServers.Count);
        
        foreach (var serverConfig in enabledServers)
        {
            try
            {
                var tools = await LoadServerToolsAsync(serverConfig, cancellationToken);
                allTools.AddRange(tools);
                _logger.LogInformation("Loaded {Count} tools from server '{ServerName}'", tools.Count, serverConfig.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load tools from server '{ServerName}': {Error}", 
                    serverConfig.Name, ex.Message);
                
                if (_options.FailOnServerError)
                {
                    throw new InvalidOperationException($"Failed to load server '{serverConfig.Name}'", ex);
                }
                // Continue with other servers if FailOnServerError is false
            }
        }
        
        _logger.LogInformation("Successfully loaded {TotalCount} MCP tools from {ServerCount} servers", 
            allTools.Count, _clients.Count);
        
        return allTools;
    }

    /// <summary>
    /// Loads MCP tools from manifest content
    /// </summary>
    public async Task<List<AIFunction>> LoadToolsFromManifestContentAsync(string manifestContent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading MCP tools from manifest content");
        
        var manifest = ParseManifest(manifestContent);
        var allTools = new List<AIFunction>();
        
        var enabledServers = manifest.Servers.Where(s => s.Enabled).ToList();
        _logger.LogInformation("Found {Count} enabled servers in manifest", enabledServers.Count);
        
        foreach (var serverConfig in enabledServers)
        {
            try
            {
                var tools = await LoadServerToolsAsync(serverConfig, cancellationToken);
                allTools.AddRange(tools);
                _logger.LogInformation("Loaded {Count} tools from server '{ServerName}'", tools.Count, serverConfig.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load tools from server '{ServerName}': {Error}", 
                    serverConfig.Name, ex.Message);
                
                if (_options.FailOnServerError)
                {
                    throw new InvalidOperationException($"Failed to load server '{serverConfig.Name}'", ex);
                }
                // Continue with other servers if FailOnServerError is false
            }
        }
        
        _logger.LogInformation("Successfully loaded {TotalCount} MCP tools from {ServerCount} servers", 
            allTools.Count, _clients.Count);
        
        return allTools;
    }

    /// <summary>
    /// Loads and validates manifest from file
    /// </summary>
    private static async Task<MCPManifest> LoadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(manifestPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException($"MCP manifest file not found: {manifestPath}");
            }

            using var stream = fileInfo.OpenRead();
            using var reader = new StreamReader(stream);
            var manifestJson = await reader.ReadToEndAsync(cancellationToken);
            
            return ParseManifest(manifestJson);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load manifest from {manifestPath}", ex);
        }
    }

    /// <summary>
    /// Parses manifest from JSON content
    /// </summary>
    private static MCPManifest ParseManifest(string manifestContent)
    {
        var manifest = JsonSerializer.Deserialize(manifestContent, MCPJsonSerializerContext.Default.MCPManifest);

        if (manifest == null)
        {
            throw new InvalidOperationException("Failed to parse MCP manifest");
        }

        // Validate all server configurations
        foreach (var server in manifest.Servers)
        {
            server.Validate();
        }

        return manifest;
    }

    /// <summary>
    /// Loads tools from a specific MCP server
    /// </summary>
    private async Task<List<AIFunction>> LoadServerToolsAsync(MCPServerConfig serverConfig, CancellationToken cancellationToken)
    {
        var client = await GetOrCreateClientAsync(serverConfig, cancellationToken);
        
        // ListToolsAsync returns McpClientTool[], which inherit from AIFunction
        var mcpTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        
        // Direct cast - no conversion needed!
        return mcpTools.Cast<AIFunction>().ToList();
    }

    /// <summary>
    /// Gets or creates an MCP client for the specified server
    /// </summary>
    private async Task<IMcpClient> GetOrCreateClientAsync(MCPServerConfig serverConfig, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(serverConfig.Name, out var existingClient))
        {
            return existingClient;
        }

        _logger.LogDebug("Creating new MCP client for server '{ServerName}'", serverConfig.Name);

        var transportOptions = new StdioClientTransportOptions
        {
            Name = serverConfig.Name,
            Command = serverConfig.Command,
            Arguments = [.. serverConfig.Arguments]
        };

        var transport = new StdioClientTransport(transportOptions);
        
        // Create client with timeout handling
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(serverConfig.TimeoutMs));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        var client = await McpClientFactory.CreateAsync(transport);
        _clients[serverConfig.Name] = client;
        
        _logger.LogDebug("Successfully created MCP client for server '{ServerName}'", serverConfig.Name);
        return client;
    }

    /// <summary>
    /// Performs health check on all connected servers
    /// </summary>
    public async Task<Dictionary<string, bool>> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, bool>();
        
        foreach (var (serverName, client) in _clients)
        {
            try
            {
                // Try to list tools as a basic health check
                await client.ListToolsAsync(cancellationToken: cancellationToken);
                results[serverName] = true;
                _logger.LogDebug("Health check passed for server '{ServerName}'", serverName);
            }
            catch (Exception ex)
            {
                results[serverName] = false;
                _logger.LogWarning(ex, "Health check failed for server '{ServerName}': {Error}", serverName, ex.Message);
            }
        }
        
        return results;
    }

    /// <summary>
    /// Gets information about all loaded servers
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetServerStatus()
    {
        return _clients.ToDictionary(kvp => kvp.Key, kvp => true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _logger.LogInformation("Disposing MCPClientManager and {Count} clients", _clients.Count);
            
            foreach (var (serverName, client) in _clients)
            {
                try
                {
                    if (client is IAsyncDisposable asyncDisposable)
                    {
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    else if (client is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    
                    _logger.LogDebug("Disposed MCP client for server '{ServerName}'", serverName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing MCP client for server '{ServerName}': {Error}", serverName, ex.Message);
                }
            }
            
            _clients.Clear();
        }
        
        _disposed = true;
    }
}

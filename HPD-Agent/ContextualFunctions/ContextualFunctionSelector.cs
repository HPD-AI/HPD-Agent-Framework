using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;


/// <summary>
/// Metadata associated with a stored function embedding
/// </summary>
public class FunctionEmbeddingMetadata
{
    public string FunctionName { get; }
    public string? PluginTypeName { get; }
    public string Description { get; }
    public AIFunction Function { get; }
    
    public FunctionEmbeddingMetadata(string functionName, string? pluginTypeName, string description, AIFunction function)
    {
        FunctionName = functionName ?? throw new ArgumentNullException(nameof(functionName));
        PluginTypeName = pluginTypeName;
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Function = function ?? throw new ArgumentNullException(nameof(function));
    }
}

/// <summary>
/// Core implementation of contextual function selection using vector similarity search
/// </summary>
public class ContextualFunctionSelector : IDisposable
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IVectorStore _vectorStore;
    private readonly ContextualFunctionConfig _config;
    private readonly Dictionary<string, FunctionEmbeddingMetadata> _functionMetadata = new();
    private readonly ILogger<ContextualFunctionSelector>? _logger;
    private bool _disposed;
    private bool _initialized;
    
    public ContextualFunctionSelector(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IVectorStore vectorStore,
        ContextualFunctionConfig config,
        ILogger<ContextualFunctionSelector>? logger = null)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }
    
    /// <summary>
    /// Initializes the selector by generating embeddings for all provided functions
    /// </summary>
    public async Task InitializeAsync(IEnumerable<AIFunction> functions, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (_initialized)
            throw new InvalidOperationException("ContextualFunctionSelector is already initialized");
        
        _logger?.LogInformation("Initializing contextual function selector with {FunctionCount} functions", functions.Count());
        
        var functionList = functions.ToList();
        var functionDescriptions = new List<string>();
        var functionIds = new List<string>();
        
        // Clear any existing data
        await _vectorStore.ClearAsync(cancellationToken);
        _functionMetadata.Clear();
        
        // Prepare function descriptions for batch embedding generation
        foreach (var function in functionList)
        {
            var description = BuildFunctionDescription(function);
            var functionId = function.Name;
            
            functionDescriptions.Add(description);
            functionIds.Add(functionId);
            
            // Store metadata for later retrieval
            var metadata = new FunctionEmbeddingMetadata(
                function.Name,
                null, // Plugin type name will be set later by AgentBuilder
                description,
                function);
            
            _functionMetadata[functionId] = metadata;
        }
        
        if (functionDescriptions.Count == 0)
        {
            _logger?.LogWarning("No functions provided for initialization");
            _initialized = true;
            return;
        }
        
        try
        {
            // Generate embeddings for all function descriptions
            _logger?.LogDebug("Generating embeddings for {Count} function descriptions", functionDescriptions.Count);
            var embeddings = await _embeddingGenerator.GenerateAsync(functionDescriptions, cancellationToken: cancellationToken);
            
            // Store embeddings in vector store
            for (int i = 0; i < embeddings.Count; i++)
            {
                var embedding = embeddings[i];
                var functionId = functionIds[i];
                var metadata = _functionMetadata[functionId];
                
                await _vectorStore.StoreAsync(functionId, embedding.Vector, metadata, cancellationToken);
            }
            
            _logger?.LogInformation("Successfully initialized contextual function selector with {Count} function embeddings", embeddings.Count);
            _initialized = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize contextual function selector");
            throw;
        }
    }
    
    /// <summary>
    /// Registers the plugin type name for a function (called by AgentBuilder)
    /// </summary>
    public void RegisterFunctionPlugin(string functionName, string pluginTypeName)
    {
        if (_functionMetadata.TryGetValue(functionName, out var metadata))
        {
            // Update the metadata with plugin information
            var updatedMetadata = new FunctionEmbeddingMetadata(
                metadata.FunctionName,
                pluginTypeName,
                metadata.Description,
                metadata.Function);
            
            _functionMetadata[functionName] = updatedMetadata;
        }
    }
    
    /// <summary>
    /// Selects the most relevant functions based on conversation context
    /// </summary>
    public async Task<IEnumerable<AIFunction>> SelectRelevantFunctionsAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        
        if (!_initialized)
            throw new InvalidOperationException("ContextualFunctionSelector must be initialized before use");
        
        try
        {
            // Build context from recent messages
            var context = BuildContext(messages);
            
            if (string.IsNullOrWhiteSpace(context))
            {
                _logger?.LogDebug("No meaningful context found, using fallback");
                return HandleFallback(_config.OnEmbeddingFailure);
            }
            
            _logger?.LogDebug("Built context for function selection: {Context}", context);
            
            // Generate embedding for context
            var contextEmbedding = await _embeddingGenerator.GenerateAsync(context, cancellationToken: cancellationToken);
            
            // Search for similar functions
            var searchResults = await _vectorStore.SearchAsync(
                contextEmbedding.Vector,
                _config.MaxRelevantFunctions,
                _config.SimilarityThreshold,
                cancellationToken);
            
            // Map results to AIFunction instances
            var relevantFunctions = searchResults
                .Where(r => _functionMetadata.ContainsKey(r.Id))
                .Select(r => _functionMetadata[r.Id].Function)
                .ToList();
            
            _logger?.LogInformation("Selected {Count} relevant functions from {Total} available (threshold: {Threshold})", 
                relevantFunctions.Count, _functionMetadata.Count, _config.SimilarityThreshold);
            
            return relevantFunctions;
        }
        catch (Exception ex) when (_config.OnEmbeddingFailure != FallbackMode.ThrowException)
        {
            _logger?.LogError(ex, "Error during contextual function selection, using fallback mode: {FallbackMode}", _config.OnEmbeddingFailure);
            return HandleFallback(_config.OnEmbeddingFailure);
        }
    }
    
    /// <summary>
    /// Builds context string from recent conversation messages
    /// </summary>
    private string BuildContext(IEnumerable<ChatMessage> messages)
    {
        if (_config.CustomContextBuilder != null)
        {
            return _config.CustomContextBuilder(messages);
        }
        
        var recentMessages = messages
            .TakeLast(_config.RecentMessageWindow)
            .Where(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Text))
            .ToList();
        
        if (recentMessages.Count == 0)
            return string.Empty;
        
        var contextBuilder = new StringBuilder();
        
        foreach (var message in recentMessages)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                contextBuilder.AppendLine(message.Text);
            }
        }
        
        var context = contextBuilder.ToString().Trim();
        
        // Truncate if necessary
        if (_config.MaxContextTokens > 0)
        {
            context = TruncateContext(context, _config.MaxContextTokens, _config.TruncationStrategy);
        }
        
        return context;
    }
    
    /// <summary>
    /// Builds a descriptive string for a function to be used for embedding generation
    /// </summary>
    private string BuildFunctionDescription(AIFunction function)
    {
        if (_config.CustomFunctionDescriptor != null)
        {
            return _config.CustomFunctionDescriptor(function);
        }
        
        var description = new StringBuilder();
        
        // Function name
        description.AppendLine($"Function: {function.Name}");
        
        // Function description
        if (!string.IsNullOrWhiteSpace(function.Description))
        {
            description.AppendLine($"Description: {function.Description}");
        }
        
        // Parameter information from JsonSchema
        if (function.JsonSchema.ValueKind == JsonValueKind.Object && 
            function.JsonSchema.TryGetProperty("properties", out var propertiesElement))
        {
            description.AppendLine("Parameters:");
            foreach (var param in propertiesElement.EnumerateObject())
            {
                var paramDescription = "No description";
                if (param.Value.TryGetProperty("description", out var descElement))
                {
                    paramDescription = descElement.GetString() ?? "No description";
                }
                description.AppendLine($"- {param.Name}: {paramDescription}");
            }
        }
        
        return description.ToString().Trim();
    }
    
    /// <summary>
    /// Truncates context based on the specified strategy
    /// </summary>
    private static string TruncateContext(string context, int maxTokens, ContextTruncationStrategy strategy)
    {
        // Simple character-based truncation for now
        // TODO: Implement proper token counting when tokenizer libraries are available
        var maxChars = maxTokens * 4; // Rough approximation: 1 token ≈ 4 characters
        
        if (context.Length <= maxChars)
            return context;
        
        return strategy switch
        {
            ContextTruncationStrategy.KeepRecent => context[^maxChars..],
            ContextTruncationStrategy.KeepRelevant => context[..maxChars], // TODO: Implement keyword-based relevance
            ContextTruncationStrategy.KeepImportant => context[..maxChars], // TODO: Implement importance-based truncation
            _ => context[..maxChars]
        };
    }
    
    /// <summary>
    /// Handles fallback behavior when contextual selection fails
    /// </summary>
    private IEnumerable<AIFunction> HandleFallback(FallbackMode fallbackMode)
    {
        return fallbackMode switch
        {
            FallbackMode.UseAllFunctions => _functionMetadata.Values.Select(m => m.Function),
            FallbackMode.UseNoFunctions => Enumerable.Empty<AIFunction>(),
            FallbackMode.ThrowException => throw new InvalidOperationException("Contextual function selection failed and ThrowException fallback mode is configured"),
            _ => _functionMetadata.Values.Select(m => m.Function)
        };
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ContextualFunctionSelector));
    }
    
    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _vectorStore?.Dispose();
            _embeddingGenerator?.Dispose();
            _disposed = true;
        }
    }
}

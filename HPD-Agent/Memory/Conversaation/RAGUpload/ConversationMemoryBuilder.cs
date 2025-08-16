using Microsoft.KernelMemory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;
using Microsoft.KernelMemory.Pipeline;
using System.Linq;
using HPD_Agent.MemoryRAG;



/// <summary>
/// Builder for creating conversation-scoped memory for pure RAG usage.
/// </summary>
public class ConversationMemoryBuilder
{
    private readonly IKernelMemoryBuilder _kernelBuilder;
    private readonly string _conversationId;
    private readonly ConversationMemoryConfig _config;
    private ConversationUploadStrategy _uploadStrategy = ConversationUploadStrategy.RAG; // Default to RAG

    // Custom RAG extension points
    private IMemoryDb? _customMemoryDb;
    private ISearchClient? _customSearchClient;
    // Custom pipeline extension points
    private readonly List<(Type type, string name, Func<IServiceProvider, IPipelineStepHandler>? factory)> _customHandlers = new();
    private string[]? _customPipelineSteps;
    // Remote memory connection
    private string? _remoteEndpoint;
    private string? _remoteApiKey;

    public ConversationMemoryBuilder(string conversationId)
    {
        _conversationId = conversationId;
        _kernelBuilder = new KernelMemoryBuilder();
        _config = new ConversationMemoryConfig(conversationId);
    }

    // Core configuration:
    public ConversationMemoryBuilder WithUploadStrategy(ConversationUploadStrategy strategy)
    {
        _uploadStrategy = strategy;
        return this;
    }

    /// <summary>
    /// Internal property to be read by the Conversation class
    /// </summary>
    internal ConversationUploadStrategy UploadStrategy => _uploadStrategy;

    public ConversationMemoryBuilder WithEmbeddingProvider(MemoryEmbeddingProvider provider, string? model = null)
    {
        _config.EmbeddingProvider = provider;
        _config.EmbeddingModel = model;
        return this;
    }

    public ConversationMemoryBuilder WithTextGenerationProvider(TextGenerationProvider provider, string? model = null)
    {
    _config.TextGenerationProvider = provider;
        _config.TextGenerationModel = model;
        return this;
    }

    public ConversationMemoryBuilder WithStorageOptimization(ConversationStorageType storageType)
    {
        _config.StorageType = storageType;
        return this;
    }

    public ConversationMemoryBuilder WithContextWindowOptimization()
    {
        _config.ContextWindowOptimization = true;
        return this;
    }

    public ConversationMemoryBuilder WithFastRetrieval()
    {
        _config.FastRetrieval = true;
        return this;
    }

    // Register a custom memory database implementation
    public ConversationMemoryBuilder WithCustomRetrieval(IMemoryDb customMemoryDb)
    {
        _customMemoryDb = customMemoryDb ?? throw new ArgumentNullException(nameof(customMemoryDb));
        return this;
    }

    // Register a custom search client implementation
    public ConversationMemoryBuilder WithCustomSearchClient(ISearchClient customSearchClient)
    {
        _customSearchClient = customSearchClient ?? throw new ArgumentNullException(nameof(customSearchClient));
        return this;
    }

    // Register both custom search client and memory database
    public ConversationMemoryBuilder WithCustomRAGStrategy(ISearchClient searchClient, IMemoryDb memoryDb)
    {
        return WithCustomSearchClient(searchClient).WithCustomRetrieval(memoryDb);
    }

    // Register a custom handler type
    public ConversationMemoryBuilder WithCustomHandler<THandler>(string stepName) 
        where THandler : class, IPipelineStepHandler
    {
        _customHandlers.Add((typeof(THandler), stepName, null));
        return this;
    }

    // Register a custom handler instance factory
    public ConversationMemoryBuilder WithCustomHandler<THandler>(string stepName, Func<IServiceProvider, THandler> factory) 
        where THandler : class, IPipelineStepHandler
    {
        _customHandlers.Add((typeof(THandler), stepName, provider => factory(provider)));
        return this;
    }

    // Register a custom handler instance directly (for simple handlers without dependencies)
    public ConversationMemoryBuilder WithCustomHandler(string stepName, IPipelineStepHandler handler)
    {
        _customHandlers.Add((handler.GetType(), stepName, _ => handler));
        return this;
    }

    // Define the custom pipeline sequence
    public ConversationMemoryBuilder WithCustomPipeline(params string[] steps)
    {
        _customPipelineSteps = steps;
        return this;
    }

    /// <summary>
    /// Configures the builder to connect to a remote Kernel Memory service.
    /// IMPORTANT: When using remote memory, custom handlers must be registered
    /// on the server side via appsettings.json or server code.
    /// All WithCustomHandler() calls are ignored for remote connections.
    /// </summary>
    /// <param name="endpoint">URL of the remote Kernel Memory service</param>
    /// <param name="apiKey">Optional API key for authentication</param>
    public ConversationMemoryBuilder WithRemoteMemory(string endpoint, string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Remote endpoint cannot be null or empty.", nameof(endpoint));
        }
        _remoteEndpoint = endpoint;
        _remoteApiKey = apiKey;
        return this;
    }

    /// <summary>
    /// Build the configured IKernelMemory for RAG usage.
    /// Returns null if the strategy is DirectInjection, as no memory instance is needed.
    /// If custom handlers are configured, returns a CustomPipelineMemoryWrapper for enhanced runtime pipeline support.
    /// </summary>
    public IKernelMemory? Build()
    {
        // If strategy is DirectInjection, no need to build the memory stack.
        if (_uploadStrategy == ConversationUploadStrategy.DirectInjection)
        {
            return null;
        }

        // --- CHECK FOR REMOTE CONFIGURATION FIRST ---
        if (!string.IsNullOrEmpty(_remoteEndpoint))
        {
            // If a remote endpoint is set, we ignore all other configurations
            // and return a client that connects to the remote service.
            // Note: The CustomPipelineMemoryWrapper is not needed for a remote client,
            // as the remote service manages its own pipelines.
            return new MemoryWebClient(_remoteEndpoint, _remoteApiKey);
        }

        // --- EXISTING LOGIC: If not remote, proceed with the local build ---
        var defaultIndex = GetConversationIndex();
        
        ValidateCustomImplementations();
        ConfigureProviders();
        ConfigureStorage();
        RegisterCustomImplementations();
        
        // Build the memory client
        var memory = _kernelBuilder.Build<MemoryServerless>();
        
        // Register custom handlers with the orchestrator after building
        RegisterCustomHandlers(memory);
        
        // Return enhanced wrapper if custom pipeline is configured
        if (_customPipelineSteps != null && _customPipelineSteps.Length > 0)
        {
            return new CustomPipelineMemoryWrapper(memory, _customPipelineSteps, defaultIndex);
        }
        
        return memory;
    }

    private string GetConversationIndex() => $"conversation-{_conversationId.ToLowerInvariant()}";

    // Register custom handlers with the orchestrator after building memory
    private void RegisterCustomHandlers(IKernelMemory memory)
    {
        // For now, we'll store the handlers to be registered and provide a method to access them
        // This is because the actual registration needs to happen with the orchestrator
        // which may not be directly accessible depending on the Kernel Memory version
        
        // In the future, when the memory supports custom handlers, we would do:
        // foreach (var (type, name, factory) in _customHandlers)
        // {
        //     if (factory != null)
        //     {
        //         var instance = factory(serviceProvider) as IPipelineStepHandler;
        //         await memory.Orchestrator.AddHandlerAsync(instance);
        //     }
        //     else
        //     {
        //         memory.Orchestrator.AddHandler<THandler>(name);
        //     }
        // }
    }

    /// <summary>
    /// Gets the custom handlers that were registered with this builder.
    /// This can be used to manually register handlers with the orchestrator if needed.
    /// </summary>
    public IReadOnlyList<(Type type, string name, Func<IServiceProvider, IPipelineStepHandler>? factory)> GetCustomHandlers()
    {
        return _customHandlers.AsReadOnly();
    }

    /// <summary>
    /// Gets the custom pipeline steps that were configured with this builder.
    /// </summary>
    public string[]? GetCustomPipelineSteps()
    {
        return _customPipelineSteps;
    }

    private void ConfigureProviders()
    {
        // Embedding providers
        if (_config.EmbeddingProvider.HasValue)
        {
            switch (_config.EmbeddingProvider.Value)
            {
                case MemoryEmbeddingProvider.OpenAI:
                    var openCfg = new Microsoft.KernelMemory.OpenAIConfig
                    {
                        APIKey = GetApiKey("OPENAI_API_KEY"),
                        EmbeddingModel = _config.EmbeddingModel ?? "text-embedding-ada-002"
                    };
                    _kernelBuilder.WithOpenAITextEmbeddingGeneration(openCfg);
                    break;
                case MemoryEmbeddingProvider.AzureOpenAI:
                    var azCfg = new Microsoft.KernelMemory.AzureOpenAIConfig
                    {
                        APIKey = GetApiKey("AZURE_OPENAI_API_KEY"),
                        Endpoint = GetConfigValue("AZURE_OPENAI_ENDPOINT"),
                        Deployment = _config.EmbeddingModel ?? "text-embedding-ada-002"
                    };
                    _kernelBuilder.WithAzureOpenAITextEmbeddingGeneration(azCfg);
                    break;
                case MemoryEmbeddingProvider.VoyageAI:
                    var voyageConfig = new VoyageAIConfig
                    {
                        ApiKey = GetApiKey("VOYAGEAI_API_KEY"),
                        ModelName = _config.EmbeddingModel ?? "voyage-large-2",
                        Endpoint = GetConfigValue("VOYAGEAI_ENDPOINT") ?? "https://api.voyageai.com/v1/embeddings"
                    };
                    var voyageGenerator = new VoyageAITextEmbeddingGenerator(voyageConfig, new HttpClient(),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<VoyageAITextEmbeddingGenerator>.Instance);
                    _kernelBuilder.WithCustomEmbeddingGenerator(voyageGenerator);
                    break;
            }
        }

        // Text generation providers
    if (_config.TextGenerationProvider.HasValue)
        {
            switch (_config.TextGenerationProvider.Value)
            {
                case TextGenerationProvider.OpenAI:
                    var oaCfg = new Microsoft.KernelMemory.OpenAIConfig
                    {
                        APIKey = GetApiKey("OPENAI_API_KEY"),
                        TextModel = _config.TextGenerationModel ?? "gpt-3.5-turbo"
                    };
                    _kernelBuilder.WithOpenAITextGeneration(oaCfg);
                    break;
                case TextGenerationProvider.OpenRouter:
                    var openRouterConfig = new OpenRouterConfig
                    {
                        ApiKey = GetApiKey("OPENROUTER_API_KEY"),
                        ModelName = _config.TextGenerationModel ?? "anthropic/claude-3.5-sonnet",
                        Endpoint = GetConfigValue("OPENROUTER_ENDPOINT") ?? "https://openrouter.ai/api/v1/chat/completions"
                    };
                    var openRouterGenerator = new OpenRouterTextGenerator(openRouterConfig, new HttpClient(),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenRouterTextGenerator>.Instance);
                    _kernelBuilder.WithCustomTextGenerator(openRouterGenerator);
                    break;
            }
        }
    }

    private void ConfigureStorage()
    {
        switch (_config.StorageType)
        {
            case ConversationStorageType.InMemory:
                _kernelBuilder.WithSimpleVectorDb();
                break;
            case ConversationStorageType.SimpleVectorDb:
                var path = Path.Combine("./conversation-memory", _config.ConversationId);
                _kernelBuilder.WithSimpleVectorDb(path)
                              .WithSimpleFileStorage(path);
                break;
            case ConversationStorageType.Hybrid:
                _kernelBuilder.WithSimpleVectorDb();
                break;
        }
    }

    private static string GetApiKey(string envVar)
    {
        return $"placeholder-{envVar}";
    }

    private static string GetConfigValue(string envVar)
    {
        return $"placeholder-{envVar}";
    }

    private void ValidateCustomImplementations()
    {
        if (_customSearchClient != null && _customMemoryDb == null)
        {
            throw new InvalidOperationException("Custom search client requires custom memory db. They must work together.");
        }
    }

    private void RegisterCustomImplementations()
    {
        if (_customMemoryDb != null)
        {
            _kernelBuilder.WithCustomMemoryDb(_customMemoryDb);
        }
        if (_customSearchClient != null)
        {
            _kernelBuilder.AddSingleton<ISearchClient>(_customSearchClient);
        }
    }
}


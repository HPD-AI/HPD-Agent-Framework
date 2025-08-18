using Microsoft.KernelMemory;
using System.Collections.Generic;
using System;
using System.IO;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;
using Microsoft.KernelMemory.Pipeline;
using System.Linq;
using HPD_Agent.MemoryRAG;
#pragma warning disable RS1035 // Suppress banned API warnings for file IO and environment variable access

    /// <summary>
    /// Builder for creating agent internal memory (build-time, read-only, optimized).
    /// </summary>
    public class AgentMemoryBuilder
    {
        private readonly IKernelMemoryBuilder _kernelBuilder;
        private readonly string _agentName;
        private readonly AgentMemoryConfig _config;
        // Custom RAG extension points
        private IMemoryDb? _customMemoryDb;
        private ISearchClient? _customSearchClient;
        // Custom pipeline extension points
        private readonly List<(Type type, string name, Func<IServiceProvider, IPipelineStepHandler>? factory)> _customHandlers = new();
        private string[]? _customPipelineSteps;
        // Remote memory connection
        private string? _remoteEndpoint;
        private string? _remoteApiKey;

        public AgentMemoryBuilder(string agentName)
        {
            _agentName = agentName;
            _kernelBuilder = new KernelMemoryBuilder();
            _config = new AgentMemoryConfig(agentName);
        }

        // Core configuration methods:
        public AgentMemoryBuilder WithEmbeddingProvider(MemoryEmbeddingProvider provider, string? model = null)
        {
            _config.EmbeddingProvider = provider;
            _config.EmbeddingModel = model;
            return this;
        }

    public AgentMemoryBuilder WithTextGenerationProvider(TextGenerationProvider provider, string? model = null)
        {
            _config.TextGenerationProvider = provider;
            _config.TextGenerationModel = model;
            return this;
        }

        public AgentMemoryBuilder WithStorageOptimization(AgentStorageType storageType)
        {
            _config.StorageType = storageType;
            return this;
        }

        public AgentMemoryBuilder WithReadOnlyOptimization()
        {
            _config.ReadOnlyOptimization = true;
            return this;
        }

        public AgentMemoryBuilder WithDomainContext(IEnumerable<string> domains)
        {
            _config.DomainContexts = domains is string[] arr ? arr : domains.ToArray();
            return this;
        }

        // Content ingestion (build-time):
        public AgentMemoryBuilder WithDocuments(string directoryPath)
        {
            _config.DocumentDirectories.Add(directoryPath);
            return this;
        }

        public AgentMemoryBuilder WithWebSources(IEnumerable<string> urls)
        {
            _config.WebSourceUrls.AddRange(urls);
            return this;
        }

        public AgentMemoryBuilder WithTextContent(Dictionary<string, string> textItems)
        {
            foreach (var kv in textItems)
            {
                _config.TextItems[kv.Key] = kv.Value;
            }
            return this;
        }
        
        // Register a custom memory database implementation
        public AgentMemoryBuilder WithCustomRetrieval(IMemoryDb customMemoryDb)
        {
            _customMemoryDb = customMemoryDb ?? throw new ArgumentNullException(nameof(customMemoryDb));
            return this;
        }

        // Register a custom search client implementation
        public AgentMemoryBuilder WithCustomSearchClient(ISearchClient customSearchClient)
        {
            _customSearchClient = customSearchClient ?? throw new ArgumentNullException(nameof(customSearchClient));
            return this;
        }

        // Register both custom search client and memory database
        public AgentMemoryBuilder WithCustomRAGStrategy(ISearchClient searchClient, IMemoryDb memoryDb)
        {
            return WithCustomSearchClient(searchClient).WithCustomRetrieval(memoryDb);
        }

        // Register a custom handler type
        public AgentMemoryBuilder WithCustomHandler<THandler>(string stepName) 
            where THandler : class, IPipelineStepHandler
        {
            _customHandlers.Add((typeof(THandler), stepName, null));
            return this;
        }

        // Register a custom handler instance factory
        public AgentMemoryBuilder WithCustomHandler<THandler>(string stepName, Func<IServiceProvider, THandler> factory) 
            where THandler : class, IPipelineStepHandler
        {
            _customHandlers.Add((typeof(THandler), stepName, provider => factory(provider)));
            return this;
        }

        // Register a custom handler instance directly (for simple handlers without dependencies)
        public AgentMemoryBuilder WithCustomHandler(string stepName, IPipelineStepHandler handler)
        {
            _customHandlers.Add((handler.GetType(), stepName, _ => handler));
            return this;
        }

        // Define the custom pipeline sequence
        public AgentMemoryBuilder WithCustomPipeline(params string[] steps)
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
        public AgentMemoryBuilder WithRemoteMemory(string endpoint, string? apiKey = null)
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
        /// Build and return the configured IKernelMemory instance, applying providers, storage, and ingesting content.
        /// If custom handlers are configured, returns a CustomPipelineMemoryWrapper for enhanced runtime pipeline support.
        /// </summary>
        public IKernelMemory Build()
        {
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
            // Determine default index name
            var defaultIndex = GetAgentIndex();

            // Validate custom RAG configuration
            ValidateCustomImplementations();
            // Apply provider and storage configurations
            ConfigureProviders();
            ConfigureStorage();
            // Register custom RAG components
            RegisterCustomImplementations();

            // Build the memory client first
            var memory = _kernelBuilder.Build<MemoryServerless>();

            // Register custom handlers with the orchestrator after building
            RegisterCustomHandlers(memory);

            // Determine steps for build-time ingestion
            var steps = _customPipelineSteps ?? new[] { "extract", "partition", "gen_embeddings", "save_records" };

            // Ingest build-time content
            foreach (var dir in _config.DocumentDirectories)
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                {
                    memory.ImportDocumentAsync(file, index: defaultIndex, steps: steps).GetAwaiter().GetResult();
                }
            }
            foreach (var url in _config.WebSourceUrls)
            {
                memory.ImportWebPageAsync(url, index: defaultIndex).GetAwaiter().GetResult();
            }
            foreach (var kv in _config.TextItems)
            {
                memory.ImportTextAsync(kv.Value, documentId: kv.Key, index: defaultIndex).GetAwaiter().GetResult();
            }

            // Return enhanced wrapper if custom pipeline is configured
            if (_customPipelineSteps != null && _customPipelineSteps.Length > 0)
            {
                return new CustomPipelineMemoryWrapper(memory, _customPipelineSteps, defaultIndex);
            }

            return memory;
        }

        private string GetAgentIndex() => $"{_config.IndexPrefix}-{_config.AgentName.ToLowerInvariant()}";

        // Ensure both custom components are provided together
        private void ValidateCustomImplementations()
        {
            if (_customSearchClient != null && _customMemoryDb == null)
            {
                throw new InvalidOperationException(
                    "Custom search client requires custom memory db. They must work together.");
            }
        }

        // Register custom memory DB and search client into the kernel builder
        private void RegisterCustomImplementations()
        {
            if (_customMemoryDb != null)
            {
                _kernelBuilder.WithCustomMemoryDb(_customMemoryDb);
            }
            if (_customSearchClient != null)
            {
                // Inject custom search client
                _kernelBuilder.AddSingleton<ISearchClient>(_customSearchClient);
            }
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
                    default:
                        throw new NotSupportedException($"Embedding provider {_config.EmbeddingProvider.Value} is not supported.");
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
                case AgentStorageType.InMemory:
                    _kernelBuilder.WithSimpleVectorDb();
                    break;
                case AgentStorageType.SimpleVectorDb:
                    var path = System.IO.Path.Combine("./agent-memory", _config.AgentName);
                    _kernelBuilder.WithSimpleVectorDb(path)
                                  .WithSimpleFileStorage(path);
                    break;
                case AgentStorageType.Qdrant:
                    _kernelBuilder.WithQdrantMemoryDb(GetConfigValue("QDRANT_ENDPOINT"));
                    break;
                case AgentStorageType.AzureAISearch:
                    _kernelBuilder.WithAzureAISearchMemoryDb(GetConfigValue("AZURE_SEARCH_ENDPOINT"), GetApiKey("AZURE_SEARCH_API_KEY"));
                    break;
                case AgentStorageType.Pinecone:
                    throw new NotSupportedException("Pinecone storage is not supported yet.");
                default:
                    throw new NotSupportedException($"Storage type {_config.StorageType} is not supported.");
            }
        }

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

        private static string GetApiKey(string envVar)
            => Environment.GetEnvironmentVariable(envVar)
               ?? throw new InvalidOperationException($"Environment variable {envVar} not found");

        private static string GetConfigValue(string envVar)
            => Environment.GetEnvironmentVariable(envVar)
               ?? throw new InvalidOperationException($"Environment variable {envVar} not found");
    }


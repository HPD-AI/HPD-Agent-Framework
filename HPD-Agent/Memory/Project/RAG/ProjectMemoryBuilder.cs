using System;
using System.IO;
using Microsoft.KernelMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;
using Microsoft.KernelMemory.Pipeline;
using System.Collections.Generic;
using System.Linq;
using HPD_Agent.MemoryRAG;


    /// <summary>
    /// Builder for creating project-scoped memory with persistent storage and multi-user access
    /// </summary>
    public class ProjectMemoryBuilder
    {
        private readonly IKernelMemoryBuilder _kernelBuilder;
        private readonly string _projectId;
        private readonly ProjectMemoryConfig _config;
        // Custom RAG extension points
        private IMemoryDb? _customMemoryDb;
        private ISearchClient? _customSearchClient;
        // Custom pipeline extension points
        private readonly List<(Type type, string name, Func<IServiceProvider, IPipelineStepHandler>? factory)> _customHandlers = new();
        private string[]? _customPipelineSteps;
        // Remote memory connection
        private string? _remoteEndpoint;
        private string? _remoteApiKey;
        // Document strategy configuration
        private ProjectDocumentStrategy _documentStrategy = ProjectDocumentStrategy.RAG; // Default to RAG

        public ProjectMemoryBuilder(string projectId)
        {
            _projectId = projectId;
            _kernelBuilder = new KernelMemoryBuilder();
            _config = new ProjectMemoryConfig(projectId);
        }

        public ProjectMemoryBuilder WithEmbeddingProvider(MemoryEmbeddingProvider provider, string? model = null)
        {
            _config.EmbeddingProvider = provider;
            _config.EmbeddingModel = model;
            return this;
        }

    public ProjectMemoryBuilder WithTextGenerationProvider(TextGenerationProvider provider, string? model = null)
        {
        _config.TextGenerationProvider = provider;
            _config.TextGenerationModel = model;
            return this;
        }

        public ProjectMemoryBuilder WithStorageOptimization(ProjectStorageType storageType)
        {
            _config.StorageType = storageType;
            return this;
        }

        public ProjectMemoryBuilder WithMultiUserAccess()
        {
            _config.MultiUserAccess = true;
            return this;
        }

        public ProjectMemoryBuilder WithRuntimeManagement()
        {
            _config.RuntimeManagement = true;
            return this;
        }

        public ProjectMemoryBuilder WithProjectContext(string projectId)
        {
            _config.ProjectId = projectId;
            return this;
        }

        /// <summary>
        /// Configures the document strategy for the project
        /// </summary>
        /// <param name="strategy">The document handling strategy</param>
        /// <returns>The builder instance for method chaining</returns>
        public ProjectMemoryBuilder WithDocumentStrategy(ProjectDocumentStrategy strategy)
        {
            _documentStrategy = strategy;
            return this;
        }

        /// <summary>
        /// Internal property to be read by the Project class
        /// </summary>
        internal ProjectDocumentStrategy DocumentStrategy => _documentStrategy;

        /// <summary>
        /// Builds the configured IKernelMemory for RAG usage.
        /// Returns null if the strategy is DirectInjection.
        /// </summary>
        public IKernelMemory? Build()
        {
            // Return null for DirectInjection strategy to optimize resource allocation
            if (_documentStrategy == ProjectDocumentStrategy.DirectInjection)
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
            var defaultIndex = GetProjectIndex();
            
            // Validate custom RAG configuration
            ValidateCustomImplementations();
            // Configure providers and storage
            ConfigureProviders();
            ConfigureStorage();
            // Register custom RAG components
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

        private string GetProjectIndex() => $"project-{_projectId.ToLowerInvariant()}";

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

        // Helper to get config value or environment variable
        private static string? GetConfigValue(string key, string? fallback = null)
        {
            // In a real app, replace with your config provider
            return fallback;
        }
        private static string? GetApiKey(string key)
        {
            // In a real app, replace with your config provider
            return null;
        }

        private void ConfigureProviders()
        {
            // Embedding provider configuration
            if (_config.EmbeddingProvider.HasValue)
            {
                switch (_config.EmbeddingProvider.Value)
                {
                    case MemoryEmbeddingProvider.OpenAI:
                        var openCfg = new OpenAIConfig { APIKey = GetApiKey("OPENAI_API_KEY") ?? string.Empty, EmbeddingModel = _config.EmbeddingModel ?? string.Empty };
                        _kernelBuilder.WithOpenAITextEmbeddingGeneration(openCfg);
                        break;
                    case MemoryEmbeddingProvider.AzureOpenAI:
                        var azCfg = new AzureOpenAIConfig { APIKey = GetApiKey("AZURE_OPENAI_API_KEY") ?? string.Empty, Endpoint = GetConfigValue("AZURE_OPENAI_ENDPOINT") ?? string.Empty, Deployment = _config.EmbeddingModel ?? string.Empty };
                        _kernelBuilder.WithAzureOpenAITextEmbeddingGeneration(azCfg);
                        break;
                    default:
                        throw new NotSupportedException($"Embedding provider {_config.EmbeddingProvider.Value} is not supported.");
                }
            }
            // Text generation provider configuration
            if (_config.TextGenerationProvider.HasValue)
            {
                switch (_config.TextGenerationProvider.Value)
                {
                    case TextGenerationProvider.OpenAI:
                        var oaCfg = new OpenAIConfig { APIKey = GetApiKey("OPENAI_API_KEY") ?? string.Empty, TextModel = _config.TextGenerationModel ?? string.Empty };
                        _kernelBuilder.WithOpenAITextGeneration(oaCfg);
                        break;
                    case TextGenerationProvider.OpenRouter:
                        var orCfg = new OpenRouterConfig { ApiKey = GetApiKey("OPENROUTER_API_KEY") ?? string.Empty, ModelName = _config.TextGenerationModel ?? string.Empty, Endpoint = GetConfigValue("OPENROUTER_ENDPOINT") ?? string.Empty };
                        var orGenerator = new OpenRouterTextGenerator(orCfg, new System.Net.Http.HttpClient(), NullLogger<OpenRouterTextGenerator>.Instance);
                        _kernelBuilder.WithCustomTextGenerator(orGenerator);
                        break;
                    default:
                        throw new NotSupportedException($"Text generation provider {_config.TextGenerationProvider.Value} is not supported.");
                }
            }
        }

        private void ConfigureStorage()
        {
            // Storage configuration based on project settings
            switch (_config.StorageType)
            {
                case ProjectStorageType.Persistent:
                    var path = Path.Combine("./project-memory", _projectId);
                    _kernelBuilder.WithSimpleVectorDb(path)
                                  .WithSimpleFileStorage(path);
                    break;
                default:
                    throw new NotSupportedException($"Storage type {_config.StorageType} is not supported.");
            }
        }
        
        // Register a custom memory database implementation
        public ProjectMemoryBuilder WithCustomRetrieval(IMemoryDb customMemoryDb)
        {
            _customMemoryDb = customMemoryDb ?? throw new ArgumentNullException(nameof(customMemoryDb));
            return this;
        }

        // Register a custom search client implementation
        public ProjectMemoryBuilder WithCustomSearchClient(ISearchClient customSearchClient)
        {
            _customSearchClient = customSearchClient ?? throw new ArgumentNullException(nameof(customSearchClient));
            return this;
        }

        // Register both custom search client and memory database
        public ProjectMemoryBuilder WithCustomRAGStrategy(ISearchClient searchClient, IMemoryDb memoryDb)
        {
            return WithCustomSearchClient(searchClient).WithCustomRetrieval(memoryDb);
        }

        // Register a custom handler type
        public ProjectMemoryBuilder WithCustomHandler<THandler>(string stepName) 
            where THandler : class, IPipelineStepHandler
        {
            _customHandlers.Add((typeof(THandler), stepName, null));
            return this;
        }

        // Register a custom handler instance factory
        public ProjectMemoryBuilder WithCustomHandler<THandler>(string stepName, Func<IServiceProvider, THandler> factory) 
            where THandler : class, IPipelineStepHandler
        {
            _customHandlers.Add((typeof(THandler), stepName, provider => factory(provider)));
            return this;
        }

        // Register a custom handler instance directly (for simple handlers without dependencies)
        public ProjectMemoryBuilder WithCustomHandler(string stepName, IPipelineStepHandler handler)
        {
            _customHandlers.Add((handler.GetType(), stepName, _ => handler));
            return this;
        }

        // Define the custom pipeline sequence
        public ProjectMemoryBuilder WithCustomPipeline(params string[] steps)
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
        public ProjectMemoryBuilder WithRemoteMemory(string endpoint, string? apiKey = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Remote endpoint cannot be null or empty.", nameof(endpoint));
            }
            _remoteEndpoint = endpoint;
            _remoteApiKey = apiKey;
            return this;
        }

        // Ensure custom RAG components are provided together
        private void ValidateCustomImplementations()
        {
            if (_customSearchClient != null && _customMemoryDb == null)
            {
                throw new InvalidOperationException(
                    "Custom search client requires custom memory db. They must work together.");
            }
        }

        // Register custom RAG implementations with the kernel builder
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


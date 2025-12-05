// HPD.Providers.Core/Discovery/ProviderAutoDiscovery.cs
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace HPD.Providers.Core;

/// <summary>
/// Auto-discovers and loads provider assemblies when HPD.Providers.Core is loaded.
/// This ModuleInitializer runs automatically in both JIT and AOT scenarios.
/// Works for ALL HPD products (Agent, Memory, etc.)
/// </summary>
internal static class ProviderAutoDiscovery
{
    private static bool _initialized = false;
    private static readonly object _lock = new();

    /// <summary>
    /// Module initializer that runs when HPD.Providers.Core assembly is first loaded.
    /// Attempts to load provider assemblies to trigger their ModuleInitializers.
    /// </summary>
#pragma warning disable CA2255 // ModuleInitializer is intentionally used in library for auto-discovery
    /// <summary>
    /// Module initializer that ensures provider modules are discovered and their module constructors run when HPD.Providers.Core is loaded.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and idempotent; it performs the discovery/initialization only once. In non-AOT builds it attempts to auto-discover and load provider assemblies from the runtime directory; in Native AOT builds it explicitly triggers initialization for a curated set of known provider modules to avoid trimming. Failures during discovery are swallowed to avoid impacting host startup.
    /// </remarks>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        lock (_lock)
        {
            if (_initialized) return;

#if !NATIVE_AOT
            // In non-AOT scenarios, try to auto-discover provider assemblies
            TryLoadProviderAssemblies();
#else
            // In AOT scenarios, explicitly trigger provider ModuleInitializers
            // This ensures they're not trimmed away by the AOT compiler
            TryInitializeKnownProviders();
#endif

            _initialized = true;
        }
    }

#if NATIVE_AOT
    /// <summary>
    /// Explicitly triggers ModuleInitializers for known provider modules in AOT scenarios.
    /// This prevents the AOT trimmer from removing providers that appear unused.
    /// Uses conditional compilation and weak references to only include providers
    /// that the app actually references.
    /// <summary>
    /// Attempts to trigger module initializers for a curated set of known provider modules so they are initialized in AOT builds.
    /// </summary>
    /// <remarks>
    /// Each provider is attempted independently; missing or unavailable providers are ignored.
    /// </remarks>
    private static void TryInitializeKnownProviders()
    {
        // Each provider is tried individually with weak references
        // If the app doesn't reference a provider, the weak reference will fail gracefully

        // Agent + Memory providers
        TryInitializeProviderByTypeName("HPD.Providers.OpenAI.OpenAIProviderModule, HPD.Providers.OpenAI");
        TryInitializeProviderByTypeName("HPD.Providers.Anthropic.AnthropicProviderModule, HPD.Providers.Anthropic");
        TryInitializeProviderByTypeName("HPD.Providers.GoogleAI.GoogleAIProviderModule, HPD.Providers.GoogleAI");
        TryInitializeProviderByTypeName("HPD.Providers.AzureAIInference.AzureAIInferenceProviderModule, HPD.Providers.AzureAIInference");
        TryInitializeProviderByTypeName("HPD.Providers.Bedrock.BedrockProviderModule, HPD.Providers.Bedrock");
        TryInitializeProviderByTypeName("HPD.Providers.Ollama.OllamaProviderModule, HPD.Providers.Ollama");
        TryInitializeProviderByTypeName("HPD.Providers.Mistral.MistralProviderModule, HPD.Providers.Mistral");
        TryInitializeProviderByTypeName("HPD.Providers.HuggingFace.HuggingFaceProviderModule, HPD.Providers.HuggingFace");
        TryInitializeProviderByTypeName("HPD.Providers.OnnxRuntime.OnnxRuntimeProviderModule, HPD.Providers.OnnxRuntime");
        TryInitializeProviderByTypeName("HPD.Providers.OpenRouter.OpenRouterProviderModule, HPD.Providers.OpenRouter");

        // Memory-only providers (future)
        TryInitializeProviderByTypeName("HPD.Providers.Qdrant.QdrantProviderModule, HPD.Providers.Qdrant");
        TryInitializeProviderByTypeName("HPD.Providers.Weaviate.WeaviateProviderModule, HPD.Providers.Weaviate");
        TryInitializeProviderByTypeName("HPD.Providers.Pinecone.PineconeProviderModule, HPD.Providers.Pinecone");
        TryInitializeProviderByTypeName("HPD.Providers.Neo4j.Neo4jProviderModule, HPD.Providers.Neo4j");
        TryInitializeProviderByTypeName("HPD.Providers.Sqlite.SqliteProviderModule, HPD.Providers.Sqlite");
    }

    /// <summary>
    /// Attempts to load and initialize a provider module by assembly-qualified type name.
    /// <summary>
    /// Attempts to initialize a provider module identified by its assembly-qualified type name by invoking its module initializer if the type is found; failures are ignored.
    /// </summary>
    /// <param name="assemblyQualifiedTypeName">The assembly-qualified type name of the provider module (for example, "Namespace.TypeName, AssemblyName").</param>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.OpenAI.OpenAIProviderModule", "HPD.Providers.OpenAI")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Anthropic.AnthropicProviderModule", "HPD.Providers.Anthropic")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.GoogleAI.GoogleAIProviderModule", "HPD.Providers.GoogleAI")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.AzureAIInference.AzureAIInferenceProviderModule", "HPD.Providers.AzureAIInference")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Bedrock.BedrockProviderModule", "HPD.Providers.Bedrock")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Ollama.OllamaProviderModule", "HPD.Providers.Ollama")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Mistral.MistralProviderModule", "HPD.Providers.Mistral")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.HuggingFace.HuggingFaceProviderModule", "HPD.Providers.HuggingFace")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.OnnxRuntime.OnnxRuntimeProviderModule", "HPD.Providers.OnnxRuntime")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.OpenRouter.OpenRouterProviderModule", "HPD.Providers.OpenRouter")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Qdrant.QdrantProviderModule", "HPD.Providers.Qdrant")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Weaviate.WeaviateProviderModule", "HPD.Providers.Weaviate")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Pinecone.PineconeProviderModule", "HPD.Providers.Pinecone")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Neo4j.Neo4jProviderModule", "HPD.Providers.Neo4j")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD.Providers.Sqlite.SqliteProviderModule", "HPD.Providers.Sqlite")]
    private static void TryInitializeProviderByTypeName(string assemblyQualifiedTypeName)
    {
        try
        {
            var type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
            if (type != null)
            {
                RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
            }
        }
        catch
        {
            // Silently ignore - provider might not be referenced or available
        }
    }
#endif

#if !NATIVE_AOT
    /// <summary>
    /// Attempts to scan and load provider assemblies in non-AOT scenarios.
    /// This provides automatic discovery without requiring user configuration.
    /// <summary>
    /// Attempts to discover and load provider assemblies from the runtime directory to trigger their module initializers.
    /// </summary>
    /// <remarks>
    /// This is a best-effort operation: it determines a directory for the HPD.Providers.Core assembly (with fallbacks for single-file and entry-assembly scenarios), scans for files matching "HPD.Providers.*.dll", loads each matching assembly except HPD.Providers.Core, and invokes the assembly module constructor to ensure any ModuleInitializers run. All errors are silently ignored so discovery does not impact host startup; providers can still be registered manually if needed.
    /// </remarks>
    private static void TryLoadProviderAssemblies()
    {
        try
        {
            // Get the directory containing HPD.Providers.Core assembly
            var coreAssembly = typeof(ProviderAutoDiscovery).Assembly;
#pragma warning disable IL3000 // Intentional fallback handling for single-file apps
            var assemblyPath = coreAssembly.Location;
#pragma warning restore IL3000

            if (string.IsNullOrEmpty(assemblyPath))
            {
                // In single-file publish, Location is empty. Use AppContext.BaseDirectory
                assemblyPath = AppContext.BaseDirectory;
            }

            if (string.IsNullOrEmpty(assemblyPath))
            {
                // Final fallback: try entry assembly
                var entryAssembly = Assembly.GetEntryAssembly();
                if (entryAssembly != null)
                {
#pragma warning disable IL3000 // Intentional fallback handling for single-file apps
                    assemblyPath = entryAssembly.Location;
#pragma warning restore IL3000
                }
            }

            if (string.IsNullOrEmpty(assemblyPath))
                return;

            var directory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            // Scan for provider assemblies with new naming pattern
            var providerPattern = "HPD.Providers.*.dll";
            var providerFiles = Directory.GetFiles(directory, providerPattern);

            foreach (var providerFile in providerFiles)
            {
                try
                {
                    // Skip the Core assembly itself
                    if (providerFile.Contains("HPD.Providers.Core.dll"))
                        continue;

                    // Load the assembly (triggers its ModuleInitializer)
                    var assemblyName = AssemblyName.GetAssemblyName(providerFile);
                    var loadedAssembly = Assembly.Load(assemblyName);

                    // Explicitly trigger module constructor to ensure ModuleInitializers run
                    RuntimeHelpers.RunModuleConstructor(loadedAssembly.ManifestModule.ModuleHandle);
                }
                catch
                {
                    // Silently ignore failures - provider might not be needed
                    // or might have dependency issues
                }
            }
        }
        catch
        {
            // Silently ignore - provider discovery is a best-effort feature
            // Providers can still be registered manually if needed
        }
    }
#endif
}
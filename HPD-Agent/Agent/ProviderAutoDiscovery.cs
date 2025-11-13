using System.Runtime.CompilerServices;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace HPD.Agent;

/// <summary>
/// Auto-discovers and loads provider assemblies when HPD-Agent library is loaded.
/// This ModuleInitializer runs automatically in both JIT and AOT scenarios.
/// </summary>
internal static class ProviderAutoDiscovery
{
    private static bool _initialized = false;
    private static readonly object _lock = new();

    /// <summary>
    /// Module initializer that runs when HPD-Agent assembly is first loaded.
    /// Attempts to load provider assemblies to trigger their ModuleInitializers.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
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
    /// </summary>
    private static void TryInitializeKnownProviders()
    {
        // Each provider is tried individually with weak references
        // If the app doesn't reference a provider, the weak reference will fail gracefully
        
        // Use reflection to dynamically check and load, which AOT will handle gracefully
        TryInitializeProviderByTypeName("HPD_Agent.Providers.OpenAI.OpenAIProviderModule, HPD-Agent.Providers.OpenAI");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.Anthropic.AnthropicProviderModule, HPD-Agent.Providers.Anthropic");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.GoogleAI.GoogleAIProviderModule, HPD-Agent.Providers.GoogleAI");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.AzureAIInference.AzureAIInferenceProviderModule, HPD-Agent.Providers.AzureAIInference");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.Bedrock.BedrockProviderModule, HPD-Agent.Providers.Bedrock");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.Ollama.OllamaProviderModule, HPD-Agent.Providers.Ollama");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.Mistral.MistralProviderModule, HPD-Agent.Providers.Mistral");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.HuggingFace.HuggingFaceProviderModule, HPD-Agent.Providers.HuggingFace");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.OnnxRuntime.OnnxRuntimeProviderModule, HPD-Agent.Providers.OnnxRuntime");
        TryInitializeProviderByTypeName("HPD_Agent.Providers.OpenRouter.OpenRouterProviderModule, HPD-Agent.Providers.OpenRouter");
    }

    /// <summary>
    /// Attempts to load and initialize a provider module by assembly-qualified type name.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.OpenAI.OpenAIProviderModule", "HPD-Agent.Providers.OpenAI")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.Anthropic.AnthropicProviderModule", "HPD-Agent.Providers.Anthropic")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.GoogleAI.GoogleAIProviderModule", "HPD-Agent.Providers.GoogleAI")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.AzureAIInference.AzureAIInferenceProviderModule", "HPD-Agent.Providers.AzureAIInference")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.Bedrock.BedrockProviderModule", "HPD-Agent.Providers.Bedrock")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.Ollama.OllamaProviderModule", "HPD-Agent.Providers.Ollama")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.Mistral.MistralProviderModule", "HPD-Agent.Providers.Mistral")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.HuggingFace.HuggingFaceProviderModule", "HPD-Agent.Providers.HuggingFace")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.OnnxRuntime.OnnxRuntimeProviderModule", "HPD-Agent.Providers.OnnxRuntime")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "HPD_Agent.Providers.OpenRouter.OpenRouterProviderModule", "HPD-Agent.Providers.OpenRouter")]
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
    /// </summary>
    private static void TryLoadProviderAssemblies()
    {
        try
        {
            // Get the directory containing HPD-Agent assembly
            var hpdAgentAssembly = typeof(AgentBuilder).Assembly;
            var assemblyPath = hpdAgentAssembly.Location;
            
            if (string.IsNullOrEmpty(assemblyPath))
            {
                // In some scenarios (like single-file publish), Location may be empty
                // Fall back to the entry assembly's directory
                var entryAssembly = Assembly.GetEntryAssembly();
                if (entryAssembly != null)
                    assemblyPath = entryAssembly.Location;
            }

            if (string.IsNullOrEmpty(assemblyPath))
                return;

            var directory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            // Scan for provider assemblies
            var providerPattern = "HPD-Agent.Providers.*.dll";
            var providerFiles = Directory.GetFiles(directory, providerPattern);

            foreach (var providerFile in providerFiles)
            {
                try
                {
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

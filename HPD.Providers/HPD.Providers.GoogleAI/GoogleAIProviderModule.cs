using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.GoogleAI;

/// <summary>
/// Auto-discovers and registers the Google AI provider on assembly load.
/// </summary>
public static class GoogleAIProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers the Google AI provider with the global ProviderRegistry during module initialization.
    /// </summary>
    /// <remarks>
    /// Executed automatically when the assembly is initialized due to the ModuleInitializer attribute.
    /// </remarks>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new GoogleAIProvider());
    }
}
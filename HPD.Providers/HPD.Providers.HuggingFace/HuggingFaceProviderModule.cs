using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.HuggingFace;

/// <summary>
/// Auto-discovers and registers the HuggingFace provider on assembly load.
/// </summary>
public static class HuggingFaceProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers the HuggingFaceProvider with the central ProviderRegistry during assembly initialization.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new HuggingFaceProvider());
    }
}
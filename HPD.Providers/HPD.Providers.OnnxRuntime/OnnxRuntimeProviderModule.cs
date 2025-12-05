using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.OnnxRuntime;

/// <summary>
/// Auto-discovers and registers the ONNX Runtime provider on assembly load.
/// </summary>
public static class OnnxRuntimeProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers the ONNX Runtime provider with the ProviderRegistry during assembly initialization.
    /// </summary>
    /// <remarks>
    /// Invoked automatically by the runtime as a module initializer to ensure the provider is discovered and available at startup.
    /// </remarks>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new OnnxRuntimeProvider());
    }
}
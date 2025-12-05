using System.Runtime.CompilerServices;
using HPD.Providers.Core;

namespace HPD.Providers.Bedrock;

/// <summary>
/// Auto-discovers and registers the AWS Bedrock provider on assembly load.
/// </summary>
public static class BedrockProviderModule
{
    #pragma warning disable CA2255
    /// <summary>
    /// Registers the Bedrock provider with the global ProviderRegistry when the assembly is loaded.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        ProviderRegistry.Instance.Register(new BedrockProvider());
    }
}
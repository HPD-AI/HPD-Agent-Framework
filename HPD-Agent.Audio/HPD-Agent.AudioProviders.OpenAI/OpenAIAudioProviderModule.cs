// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text.Json;
using HPD.Agent.Audio.Providers;

namespace HPD.Agent.AudioProviders.OpenAI;

/// <summary>
/// Auto-discovers and registers OpenAI audio provider on assembly load.
/// </summary>
public static class OpenAIAudioProviderModule
{
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    [ModuleInitializer]
#pragma warning restore CA2255 // The 'ModuleInitializer' attribute should not be used in libraries
    public static void Initialize()
    {
        // Register provider factory
        AudioProviderDiscovery.RegisterProviderFactory(() => new OpenAIAudioProvider());

        // Register config type for FFI/JSON serialization
        AudioProviderDiscovery.RegisterProviderConfigType<OpenAIAudioConfig>(
            "openai-audio",
            json => JsonSerializer.Deserialize<OpenAIAudioConfig>(json),
            config => JsonSerializer.Serialize(config)
        );
    }
}

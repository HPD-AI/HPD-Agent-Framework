// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Audio.Providers;

namespace HPD.Agent.Audio.ElevenLabs;

/// <summary>
/// Module for registering ElevenLabs audio provider features.
/// </summary>
public static class ElevenLabsAudioProviderModule
{
    /// <summary>
    /// Gets the ElevenLabs audio provider features implementation.
    /// </summary>
    public static IAudioProviderFeatures GetFeatures() => new ElevenLabsAudioProviderFeatures();
}

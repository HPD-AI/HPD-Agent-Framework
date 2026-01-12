// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio.Vad;

/// <summary>
/// Factory for creating VAD detectors.
/// Registered via VadProviderDiscovery in module initializer.
/// </summary>
public interface IVadProviderFactory
{
    /// <summary>
    /// Creates a VAD detector from configuration.
    /// </summary>
    IVoiceActivityDetector CreateDetector(VadConfig config, IServiceProvider? services = null);

    /// <summary>
    /// Gets metadata about this VAD provider's capabilities.
    /// </summary>
    VadProviderMetadata GetMetadata();

    /// <summary>
    /// Validates VAD configuration for this provider.
    /// </summary>
    ValidationResult Validate(VadConfig config);
}

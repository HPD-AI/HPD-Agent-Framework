// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.Extensions.AI;

namespace HPD.Agent.Audio.Stt;

/// <summary>
/// Factory for creating STT clients.
/// Registered via SttProviderDiscovery in module initializer.
/// </summary>
public interface ISttProviderFactory
{
    /// <summary>
    /// Creates an STT client from configuration.
    /// </summary>
    ISpeechToTextClient CreateClient(SttConfig config, IServiceProvider? services = null);

    /// <summary>
    /// Gets metadata about this STT provider's capabilities.
    /// </summary>
    SttProviderMetadata GetMetadata();

    /// <summary>
    /// Validates STT configuration for this provider.
    /// </summary>
    ValidationResult Validate(SttConfig config);
}

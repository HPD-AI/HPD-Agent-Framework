// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio.Vad;

/// <summary>
/// Metadata about a VAD provider's capabilities.
/// </summary>
public class VadProviderMetadata
{
    public string ProviderKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string[]? SupportedFormats { get; init; }
    public string? DocumentationUrl { get; init; }
    public Dictionary<string, object>? CustomProperties { get; init; }
}

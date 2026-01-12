// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio.Stt;

/// <summary>
/// Metadata about an STT provider's capabilities.
/// </summary>
public class SttProviderMetadata
{
    public string ProviderKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool SupportsStreaming { get; init; } = false;
    public string[]? SupportedLanguages { get; init; }
    public string[]? SupportedFormats { get; init; }
    public string? DocumentationUrl { get; init; }
    public Dictionary<string, object>? CustomProperties { get; init; }
}

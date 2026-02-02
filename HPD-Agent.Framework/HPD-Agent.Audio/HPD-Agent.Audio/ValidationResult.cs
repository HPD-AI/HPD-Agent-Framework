// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio;

/// <summary>
/// Result of provider configuration validation.
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    public static ValidationResult Success() => new() { IsValid = true };

    public static ValidationResult Failure(params string[] errors) =>
        new() { IsValid = false, Errors = new List<string>(errors) };
}

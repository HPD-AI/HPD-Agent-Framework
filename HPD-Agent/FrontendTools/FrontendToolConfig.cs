// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.FrontendTools;

/// <summary>
/// Configuration for frontend tool middleware behavior.
/// </summary>
public record FrontendToolConfig
{
    /// <summary>
    /// Timeout for waiting for frontend tool response.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan InvokeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Strategy for handling frontend disconnection mid-invocation.
    /// Default: FallbackMessage (returns error message to agent).
    /// </summary>
    public FrontendDisconnectionStrategy DisconnectionStrategy { get; init; }
        = FrontendDisconnectionStrategy.FallbackMessage;

    /// <summary>
    /// Maximum retry attempts for RetryWithBackoff strategy.
    /// Default: 3.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Message template for FallbackMessage strategy.
    /// {0} is replaced with tool name.
    /// </summary>
    public string FallbackMessageTemplate { get; init; }
        = "Frontend disconnected. Tool '{0}' is unavailable.";

    /// <summary>
    /// Maximum size for inline document content in skills.
    /// Documents exceeding this should use URL instead.
    /// Default: 50KB.
    /// </summary>
    public int MaxInlineDocumentSizeBytes { get; init; } = 50 * 1024;

    /// <summary>
    /// Whether to validate JSON Schema on tool registration.
    /// Recommended: true (catch errors early).
    /// Default: true.
    /// </summary>
    public bool ValidateSchemaOnRegistration { get; init; } = true;
}

/// <summary>
/// Strategy for handling frontend disconnection during tool invocation.
/// </summary>
public enum FrontendDisconnectionStrategy
{
    /// <summary>
    /// Throw exception immediately. Use when frontend availability is critical.
    /// </summary>
    FailFast,

    /// <summary>
    /// Retry with exponential backoff up to MaxRetries times.
    /// Use for transient network issues.
    /// </summary>
    RetryWithBackoff,

    /// <summary>
    /// Return error message to agent (default).
    /// Agent can decide how to proceed (e.g., inform user, try alternative).
    /// </summary>
    FallbackMessage
}

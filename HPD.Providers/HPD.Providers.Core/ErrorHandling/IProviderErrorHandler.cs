// HPD.Providers.Core/ErrorHandling/IProviderErrorHandler.cs
using System;

namespace HPD.Providers.Core;

/// <summary>
/// Interface for provider-specific error parsing and retry logic.
/// </summary>
public interface IProviderErrorHandler
{
    /// <summary>
    /// Parse provider-specific error details from an exception.
    /// </summary>
    /// <param name="exception">The exception thrown by the provider.</param>
    /// <summary>
/// Parses provider-specific error details from the given exception.
/// </summary>
/// <returns>Structured error details, or null if the exception format is not recognized.</returns>
    ProviderErrorDetails? ParseError(Exception exception);

    /// <summary>
    /// Calculate retry delay for this error based on attempt number.
    /// </summary>
    /// <param name="details">Structured error details.</param>
    /// <param name="attempt">Current attempt number (0-based).</param>
    /// <param name="initialDelay">Initial retry delay from configuration.</param>
    /// <param name="multiplier">Backoff multiplier from configuration.</param>
    /// <param name="maxDelay">Maximum retry delay cap from configuration.</param>
    /// <summary>
        /// Calculates the retry delay for a provider error using exponential backoff parameters and the current attempt.
        /// </summary>
        /// <param name="details">Parsed provider error details that influence retry eligibility and behavior.</param>
        /// <param name="attempt">Current retry attempt number (1-based).</param>
        /// <param name="initialDelay">Base delay used for the first retry.</param>
        /// <param name="multiplier">Factor applied to the delay on each subsequent attempt.</param>
        /// <param name="maxDelay">Maximum allowable delay; the result will not exceed this value.</param>
        /// <returns>The calculated retry delay, or `null` if the error should not be retried.</returns>
    TimeSpan? GetRetryDelay(
        ProviderErrorDetails details,
        int attempt,
        TimeSpan initialDelay,
        double multiplier,
        TimeSpan maxDelay);

    /// <summary>
    /// Check if error requires special handling (e.g., token refresh, model fallback).
    /// </summary>
    /// <param name="details">Structured error details.</param>
    /// <summary>
/// Determines whether the given provider error requires special handling such as token refresh or model fallback.
/// </summary>
/// <param name="details">Structured provider-specific error details to evaluate.</param>
/// <returns><c>true</c> if the error requires special handling, <c>false</c> otherwise.</returns>
    bool RequiresSpecialHandling(ProviderErrorDetails details);
}
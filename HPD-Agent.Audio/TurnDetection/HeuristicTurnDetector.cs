// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.Audio;

/// <summary>
/// Simple punctuation-based turn detector. No ML required.
/// Uses common sentence-ending patterns to estimate turn completion probability.
/// </summary>
public class HeuristicTurnDetector : ITurnDetector
{
    /// <summary>
    /// Gets the probability that the given text represents a complete utterance
    /// based on punctuation patterns.
    /// </summary>
    /// <param name="text">The transcribed text to analyze.</param>
    /// <returns>Probability between 0.0 and 1.0 that the turn is complete.</returns>
    public float GetCompletionProbability(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0.0f;

        var trimmed = text.Trim();

        // Strong sentence endings
        if (trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?'))
            return 0.9f;

        // Weak endings - likely mid-sentence
        if (trimmed.EndsWith(',') || trimmed.EndsWith(';') || trimmed.EndsWith(':'))
            return 0.3f;

        // Trailing off - uncertain
        if (trimmed.EndsWith("...") || trimmed.EndsWith("…"))
            return 0.2f;

        // Quotation marks - might be complete
        if (trimmed.EndsWith('"') || trimmed.EndsWith('\'') || trimmed.EndsWith('"'))
            return 0.7f;

        // Parenthesis - might be complete
        if (trimmed.EndsWith(')') || trimmed.EndsWith(']'))
            return 0.6f;

        // No punctuation - likely incomplete
        return 0.1f;
    }

    /// <summary>
    /// Resets internal state (no-op for heuristic detector).
    /// </summary>
    public void Reset()
    {
        // No state to reset for heuristic detection
    }
}

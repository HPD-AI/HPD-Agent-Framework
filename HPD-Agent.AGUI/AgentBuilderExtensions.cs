using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace HPD.Agent.AGUI;

/// <summary>
/// Extension methods for AgentBuilder to create AGUI protocol agents.
/// </summary>
public static class AgentBuilderExtensions
{
    /// <summary>
    /// Builds an AGUI protocol agent asynchronously from the current builder configuration.
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>An AGUI protocol adapter wrapping the core agent</returns>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public static async Task<Agent> BuildAGUIAsync(
        this AgentBuilder builder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Use the builder's internal Build method to get the core agent
        var coreAgent = await builder.BuildCoreAgentAsync(cancellationToken);

        // Wrap in AGUI protocol adapter
        return new Agent(coreAgent);
    }

    /// <summary>
    /// Builds an AGUI protocol agent synchronously (blocks thread until complete).
    /// </summary>
    /// <param name="builder">The agent builder instance</param>
    /// <returns>An AGUI protocol adapter wrapping the core agent</returns>
    [RequiresUnreferencedCode("Agent building may use plugin registration methods that require reflection.")]
    public static Agent BuildAGUI(this AgentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Use the builder's internal Build method to get the core agent
        var coreAgent = builder.BuildCoreAgent();

        // Wrap in AGUI protocol adapter
        return new Agent(coreAgent);
    }
}

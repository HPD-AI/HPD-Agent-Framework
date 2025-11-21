using HPD.Agent;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Tests.Filters;

/// <summary>
/// Helper methods for creating test iteration filter contexts.
/// </summary>
internal static class IterationFilterTestHelpers
{
    /// <summary>
    /// Creates a basic iteration filter context for testing.
    /// </summary>
    public static IterationFilterContext CreateContext(int iteration = 0)
    {
        var state = AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: "test-run-id",
            conversationId: "test-conv-id",
            agentName: "TestAgent");

        return new IterationFilterContext
        {
            Iteration = iteration,
            AgentName = "TestAgent",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            State = state,
            CancellationToken = CancellationToken.None
        };
    }
}

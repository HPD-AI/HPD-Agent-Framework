using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;


/// <summary>
/// Middleware that injects environment context into the conversation.
/// Based on Codex CLI's approach - provides the model with awareness of:
/// - Current working directory
/// - Shell type
/// - Platform
/// - Git repository status
/// - Writable directories
///
/// The context is injected as a user message at the start of each turn,
/// serialized as XML in the format:
/// <code>
/// &lt;environment_context&gt;
///   &lt;cwd&gt;/path/to/project&lt;/cwd&gt;
///   &lt;shell&gt;zsh&lt;/shell&gt;
///   &lt;platform&gt;darwin&lt;/platform&gt;
///   ...
/// &lt;/environment_context&gt;
/// </code>
/// </summary>
public class EnvironmentContextMiddleware : IAgentMiddleware
{
    private readonly IReadOnlyList<string>? _writableRoots;
    private EnvironmentContext? _lastContext;

    /// <summary>
    /// Creates a new EnvironmentContextMiddleware.
    /// </summary>
    /// <param name="writableRoots">Optional list of directories the agent can write to.
    /// If null, the current working directory is used as the writable root.</param>
    public EnvironmentContextMiddleware(IReadOnlyList<string>? writableRoots = null)
    {
        _writableRoots = writableRoots;
    }

    /// <summary>
    /// Called before each LLM iteration - injects environment context into Messages.
    /// On first iteration: inject full context after system messages
    /// On subsequent iterations: inject only if cwd changed
    /// </summary>
    public Task BeforeIterationAsync(BeforeIterationContext context, CancellationToken cancellationToken)
    {
        if (context.Messages == null)
            return Task.CompletedTask;

        var currentContext = CreateCurrentContext();
        string? contextXml = null;

        if (_lastContext == null)
        {
            // First time - inject full context
            contextXml = currentContext.SerializeToXml();
        }
        else if (_lastContext.Cwd != currentContext.Cwd)
        {
            // Cwd changed - inject updated context
            contextXml = $"[Environment Update]\n{currentContext.SerializeToXml()}";
        }

        if (contextXml != null)
        {
            var envMessage = new ChatMessage(
                ChatRole.User,
                contextXml
            );

            // Find the right position - after system messages but before user messages
            var insertIndex = 0;
            for (int i = 0; i < context.Messages.Count; i++)
            {
                if (context.Messages[i].Role == ChatRole.System)
                {
                    insertIndex = i + 1;
                }
                else
                {
                    break;
                }
            }

            context.Messages.Insert(insertIndex, envMessage);
        }

        _lastContext = currentContext;
        return Task.CompletedTask;
    }

    private EnvironmentContext CreateCurrentContext()
    {
        var writableRoots = _writableRoots ?? new[] { Directory.GetCurrentDirectory() };
        return EnvironmentContext.CreateCurrent(writableRoots);
    }
}

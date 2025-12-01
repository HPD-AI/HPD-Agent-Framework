using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Reusable event handler for console-based agent interaction.
/// This handler processes all agent events and displays them in a user-friendly console format.
/// Supports permissions, continuations, streaming text, reasoning tokens, and tool calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage - Simple:</b>
/// <code>
/// var handler = new ConsoleEventHandler();
/// var agent = new AgentBuilder(config)
///     .WithObserver(handler)
///     .Build();
/// handler.SetAgent(agent);
///
/// await agent.RunAsync(messages, thread: thread);
/// // All events displayed automatically!
/// </code>
/// </para>
/// <para>
/// <b>Usage - Multiple Agents:</b>
/// <code>
/// var handler = new ConsoleEventHandler();
/// var agent1 = new AgentBuilder(config1).WithObserver(handler).Build();
/// var agent2 = new AgentBuilder(config2).WithObserver(handler).Build();
/// handler.SetAgent(agent1);  // Or agent2, depending on which handles permissions
/// </code>
/// </para>
/// <para>
/// <b>Features:</b>
/// - Streaming text display with color coding
/// - Reasoning token display (for o1, Gemini-Thinking, DeepSeek-R1)
/// - Interactive permission prompts (Allow/Deny/Always/Never)
/// - Interactive continuation prompts (Yes/No)
/// - Tool call tracking with visual feedback
/// - Iteration counter for agentic loops
/// - Automatic section management (reasoning → text transitions)
/// </para>
/// </remarks>
public class ConsoleEventHandler : IAgentEventHandler
{
    private Agent? _agent;

    // Track display state
    private bool _isFirstReasoningChunk = true;
    private bool _isFirstTextChunk = true;
    private string? _currentMessageId = null;

    /// <summary>
    /// Creates a new console event handler.
    /// Call <see cref="SetAgent"/> after the agent is built to enable bidirectional events (permissions, continuations).
    /// </summary>
    public ConsoleEventHandler()
    {
    }

    /// <summary>
    /// Creates a new console event handler with a reference to the agent.
    /// Use this constructor when you've already built the agent.
    /// </summary>
    internal ConsoleEventHandler(Agent agent)
    {
        _agent = agent;
    }

    /// <summary>
    /// Sets the agent reference after construction.
    /// Call this after building the agent if you used the parameterless constructor.
    /// Required for bidirectional events (permissions, continuations) to work.
    /// </summary>
    /// <param name="agent">The agent instance</param>
    public void SetAgent(Agent agent)
    {
        _agent = agent;
    }

    /// <summary>
    /// Filter events - only process the ones we care about for console display.
    /// </summary>
    public bool ShouldProcess(AgentEvent evt)
    {
        return evt is PermissionRequestEvent
            or ContinuationRequestEvent
            or Reasoning
            or TextMessageStartEvent
            or TextDeltaEvent
            or TextMessageEndEvent
            or ToolCallStartEvent
            or ToolCallResultEvent
            or AgentTurnStartedEvent;
    }

    /// <summary>
    /// Handle filtered events asynchronously.
    /// Processes text streaming, permissions, continuations, tool calls, etc.
    /// </summary>
    public async Task OnEventAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        switch (evt)
        {
            case PermissionRequestEvent permReq:
                await HandlePermissionRequestAsync(permReq, cancellationToken);
                break;

            case ContinuationRequestEvent contReq:
                await HandleContinuationRequestAsync(contReq, cancellationToken);
                break;

            case Reasoning reasoning:
                HandleReasoningEvent(reasoning);
                break;

            case TextMessageStartEvent textStart:
                HandleTextMessageStart(textStart);
                break;

            case TextDeltaEvent textDelta:
                HandleTextDelta(textDelta);
                break;

            case TextMessageEndEvent:
                // Text message ended - no action needed
                break;

            case ToolCallStartEvent toolStart:
                HandleToolCallStart(toolStart);
                break;

            case ToolCallResultEvent toolResult:
                HandleToolCallResult(toolResult);
                break;

            case AgentTurnStartedEvent turnStart:
                HandleAgentTurnStarted(turnStart);
                break;
        }
    }

    private async Task HandlePermissionRequestAsync(PermissionRequestEvent permReq, CancellationToken ct)
    {
        if (_agent == null)
        {
            Console.WriteLine("\n⚠️  Agent not set - cannot handle permission requests");
            return;
        }

        // Close any open sections
        CloseOpenSections();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n🔐 Permission Request");
        Console.WriteLine($"   Function: {permReq.FunctionName}");
        if (!string.IsNullOrEmpty(permReq.Description))
            Console.WriteLine($"   Purpose: {permReq.Description}");
        Console.WriteLine($"   Options: [A]llow once, Allow [F]orever, [D]eny once, Deny F[o]rever");
        Console.Write("   Your choice (press Enter): ");
        Console.ResetColor();

        // Read user's permission choice
        var userInput = await Task.Run(() => Console.ReadLine(), ct);
        var choice = string.IsNullOrEmpty(userInput) ? 'd' : char.ToLower(userInput[0]);

        bool approved;
        PermissionChoice permChoice;

        switch (choice)
        {
            case 'A' or 'a':
                approved = true;
                permChoice = PermissionChoice.Ask;
                break;
            case 'F' or 'f':
                approved = true;
                permChoice = PermissionChoice.AlwaysAllow;
                break;
            case 'D' or 'd':
                approved = false;
                permChoice = PermissionChoice.Ask;
                break;
            case 'O' or 'o':
                approved = false;
                permChoice = PermissionChoice.AlwaysDeny;
                break;
            default:
                approved = false;
                permChoice = PermissionChoice.Ask;
                break;
        }

        // Send response back to the agent
        _agent.SendMiddlewareResponse(
            permReq.PermissionId,
            new PermissionResponseEvent(
                permReq.PermissionId,
                "Console",
                approved,
                approved ? null : "User denied permission",
                permChoice
            )
        );

        Console.ForegroundColor = approved ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   {(approved ? "✓ Approved" : "✗ Denied")}");
        Console.ResetColor();
    }

    private async Task HandleContinuationRequestAsync(ContinuationRequestEvent contReq, CancellationToken ct)
    {
        if (_agent == null)
        {
            Console.WriteLine("\n⚠️  Agent not set - cannot handle continuation requests");
            return;
        }

        // Close any open sections
        CloseOpenSections();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n⏱️  Continuation Request");
        Console.WriteLine($"   Iteration: {contReq.CurrentIteration} / {contReq.MaxIterations}");
        Console.WriteLine($"   Continue for more iterations?");
        Console.WriteLine($"   Options: [Y]es, [N]o");
        Console.Write("   Your choice: ");
        Console.ResetColor();

        var userInput = await Task.Run(() => Console.ReadLine(), ct);
        var approved = !string.IsNullOrEmpty(userInput) && char.ToLower(userInput[0]) == 'y';

        // Send response back to the agent
        _agent.SendMiddlewareResponse(
            contReq.ContinuationId,
            new ContinuationResponseEvent(
                contReq.ContinuationId,
                "Console",
                approved,
                approved ? 3 : 0
            )
        );

        Console.ForegroundColor = approved ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"{(approved ? "✓ Continuing" : "✗ Stopping")}");
        Console.ResetColor();
    }

    private void HandleReasoningEvent(Reasoning reasoning)
    {
        switch (reasoning.Phase)
        {
            case ReasoningPhase.MessageStart:
                // If transitioning from text to reasoning, close text section
                if (!_isFirstTextChunk)
                {
                    Console.WriteLine();
                    Console.ResetColor();
                    _isFirstTextChunk = true;
                }

                // Show header when starting new reasoning section
                if (_isFirstReasoningChunk)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("💭 Thinking: ");
                    _isFirstReasoningChunk = false;
                }
                _currentMessageId = reasoning.MessageId;
                break;

            case ReasoningPhase.Delta:
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(reasoning.Text);
                break;

            case ReasoningPhase.MessageEnd:
                // Reasoning section ended
                if (!_isFirstReasoningChunk)
                {
                    Console.WriteLine();
                    Console.ResetColor();
                    _isFirstReasoningChunk = true;
                }
                break;
        }
    }

    private void HandleTextMessageStart(TextMessageStartEvent textStart)
    {
        // If transitioning from reasoning to text, ensure reasoning is closed
        if (!_isFirstReasoningChunk)
        {
            Console.WriteLine();
            Console.ResetColor();
            _isFirstReasoningChunk = true;
        }

        // Show text header on first text chunk
        if (_isFirstTextChunk)
        {
            Console.WriteLine();
            Console.Write("📝 Response: ");
            _isFirstTextChunk = false;
        }
        _currentMessageId = textStart.MessageId;
    }

    private void HandleTextDelta(TextDeltaEvent textDelta)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(textDelta.Text);
    }

    private void HandleToolCallStart(ToolCallStartEvent toolStart)
    {
        // Close any open sections
        CloseOpenSections();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n🔧 Using tool: {toolStart.Name}");
        Console.ResetColor();
    }

    private void HandleToolCallResult(ToolCallResultEvent toolResult)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($" ✓");
        Console.ResetColor();
    }

    private void HandleAgentTurnStarted(AgentTurnStartedEvent turnStart)
    {
        if (turnStart.Iteration > 1) // Don't show for first iteration
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"\n🔄 Agent iteration {turnStart.Iteration}");
            Console.ResetColor();
        }
    }

    private void CloseOpenSections()
    {
        if (!_isFirstReasoningChunk || !_isFirstTextChunk)
        {
            Console.WriteLine();
            Console.ResetColor();
            _isFirstReasoningChunk = true;
            _isFirstTextChunk = true;
        }
    }

    /// <summary>
    /// Reset state for new turn.
    /// Call this between agent turns if reusing the same handler.
    /// </summary>
    public void ResetForNewTurn()
    {
        _isFirstReasoningChunk = true;
        _isFirstTextChunk = true;
        _currentMessageId = null;
    }
}

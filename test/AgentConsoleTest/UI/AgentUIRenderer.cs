using HPD.Agent;
using HPD.Events;
using HPD.MultiAgent;
using Spectre.Console;
using Spectre.Console.Rendering;
using StreamingMarkdown.Core;
using StreamingMarkdown.Spectre;
using System.Collections.Concurrent;
using System.Text;


/// <summary>
/// Component-based UI renderer for HPD Agent events.
/// Mirrors Gemini CLI's architecture with reusable components.
/// Uses UIState for state management and components for rendering.
/// </summary>
public class AgentUIRenderer
{
    private readonly UIStateManager _stateManager;
    private readonly ConcurrentDictionary<string, ToolMessage> _toolComponents = new();
    private readonly object _lock = new();
    private bool _isFirstOutput = true;

    // Streaming markdown support - Codex-style line accumulator
    // Using extracted StreamingMarkdown library for portable, reusable streaming logic
    private readonly StreamCollector<IRenderable> _lineCollector = new(new SpectreMarkdownRenderer());
    private bool _useStreamingMarkdown = true; // Enabled - using Codex append-only approach

    // Animation support - Codex uses 50ms ticks for smooth line-by-line reveals
    private AnimationController<IRenderable>? _animationController;
    private bool _useAnimatedStreaming = true; // Enabled - smooth 50ms reveals like Codex
    private readonly object _animationLock = new();

    // Command system - slash commands with autocomplete
    private readonly CommandRegistry _commandRegistry = new();

    // Agent reference for sending permission responses
    private Agent? _agent;

    public UIStateManager StateManager => _stateManager;
    public CommandRegistry CommandRegistry => _commandRegistry;

    /// <summary>
    /// Enable or disable streaming markdown rendering.
    /// When disabled, text streams as plain characters (original behavior).
    /// </summary>
    public bool UseStreamingMarkdown
    {
        get => _useStreamingMarkdown;
        set => _useStreamingMarkdown = value;
    }

    /// <summary>
    /// Enable or disable animated streaming (50ms per line like Codex).
    /// When disabled, lines are displayed immediately as they complete.
    /// </summary>
    public bool UseAnimatedStreaming
    {
        get => _useAnimatedStreaming;
        set => _useAnimatedStreaming = value;
    }

    public AgentUIRenderer()
    {
        _stateManager = new UIStateManager();

        // Register built-in slash commands
        BuiltInCommands.RegisterAll(_commandRegistry);
    }

    /// <summary>
    /// Sets the agent reference for handling bidirectional events like permissions.
    /// </summary>
    public void SetAgent(Agent agent) => _agent = agent;
    
    /// <summary>
    /// Display the app header on startup.
    /// </summary>
    public void ShowHeader(string version = "1.0.0", string? model = null)
    {
        var header = new AppHeader
        {
            Title = "HPD Agent",
            Version = version,
            Model = model
        };
        header.Display();
    }
    
    /// <summary>
    /// Display help panel with registered commands.
    /// </summary>
    public void ShowHelp()
    {
        var helpPanel = new HelpPanel
        {
            Commands = _commandRegistry.GetVisibleCommands()
        };
        helpPanel.Display();
    }
    
    /// <summary>
    /// Display session statistics.
    /// </summary>
    public void ShowStats()
    {
        var stats = new StatsDisplay
        {
            TotalTokens = _stateManager.State.Stats.TotalTokens,
            PromptTokens = _stateManager.State.Stats.PromptTokens,
            CompletionTokens = _stateManager.State.Stats.CompletionTokens,
            TotalTime = _stateManager.State.Stats.TotalTime,
            ToolCalls = _stateManager.State.Stats.ToolCalls
        };
        stats.Display();
    }
    
    /// <summary>
    /// Record user input and display.
    /// </summary>
    public void ShowUserMessage(string content)
    {
        _stateManager.AddUserMessage(content);
        AnsiConsole.WriteLine();
        new UserMessage { Content = content }.Display();
    }
    
    /// <summary>
    /// Process and render any HPD event (AgentEvent, including workflow events).
    /// </summary>
    public void RenderEvent(Event evt)
    {
        // All events are now AgentEvent-derived (workflow events wrap graph events)
        if (evt is AgentEvent agentEvt)
        {
            RenderAgentEvent(agentEvt);
        }
    }

    /// <summary>
    /// Process and render an agent event using components.
    /// </summary>
    public void RenderAgentEvent(AgentEvent evt)
    {
        lock (_lock)
        {
            // Update state
            _stateManager.ProcessEvent(evt);

            // Render based on event type
            switch (evt)
            {
                case MessageTurnStartedEvent turnStart:
                    RenderTurnStart(turnStart);
                    break;
                    
                case MessageTurnFinishedEvent turnEnd:
                    RenderTurnFinished(turnEnd);
                    break;
                    
                case MessageTurnErrorEvent error:
                    RenderError(error);
                    break;
                    
                case TextDeltaEvent textDelta:
                    RenderTextDelta(textDelta);
                    break;
                    
                case ToolCallStartEvent toolStart:
                    RenderToolStart(toolStart);
                    break;
                    
                case ToolCallArgsEvent toolArgs:
                    RenderToolArgs(toolArgs);
                    break;
                    
                case ToolCallResultEvent toolResult:
                    RenderToolResult(toolResult);
                    break;
                    
                case PermissionRequestEvent permissionRequest:
                    RenderPermissionRequest(permissionRequest);
                    break;

                case ReasoningMessageStartEvent:
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim italic]🧠 Thinking...[/]");
                    break;

                case ReasoningDeltaEvent delta:
                    AnsiConsole.Markup($"[dim]{Markup.Escape(delta.Text)}[/]");
                    break;

                case ReasoningMessageEndEvent:
                    AnsiConsole.WriteLine();
                    break;

                // Workflow events (multi-agent)
                case WorkflowStartedEvent workflowStart:
                    RenderWorkflowStarted(workflowStart);
                    break;

                case WorkflowCompletedEvent workflowComplete:
                    RenderWorkflowCompleted(workflowComplete);
                    break;

                case WorkflowNodeStartedEvent nodeStart:
                    RenderWorkflowNodeStarted(nodeStart);
                    break;

                case WorkflowNodeCompletedEvent nodeComplete:
                    RenderWorkflowNodeCompleted(nodeComplete);
                    break;

                case WorkflowNodeSkippedEvent nodeSkipped:
                    RenderWorkflowNodeSkipped(nodeSkipped);
                    break;

                case WorkflowEdgeTraversedEvent edge:
                    RenderWorkflowEdgeTraversed(edge);
                    break;

                case WorkflowLayerStartedEvent layerStart:
                    RenderWorkflowLayerStarted(layerStart);
                    break;

                case WorkflowLayerCompletedEvent layerComplete:
                    RenderWorkflowLayerCompleted(layerComplete);
                    break;

                case WorkflowDiagnosticEvent diagnostic:
                    RenderWorkflowDiagnostic(diagnostic);
                    break;
            }
        }
    }
    
    private void RenderTurnStart(MessageTurnStartedEvent evt)
    {
        _isFirstOutput = true;
        _toolComponents.Clear();
        _lineCollector.Clear(); // Reset line collector for new turn

        // Set up animation controller if animated streaming is enabled
        lock (_animationLock)
        {
            _animationController?.Dispose();
            if (_useAnimatedStreaming && _useStreamingMarkdown)
            {
                _animationController = new AnimationController<IRenderable>(
                    _lineCollector,
                    line => AnsiConsole.Write(line),
                    () => { } // Animation complete callback
                );
            }
            else
            {
                _animationController = null;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule($"[bold green]{Markup.Escape(evt.AgentName)}[/]")
                .LeftJustified()
                .RuleStyle("green")
        );
    }
    
    private void RenderTurnFinished(MessageTurnFinishedEvent evt)
    {
        // Stop animation and drain any remaining queued lines
        lock (_animationLock)
        {
            _animationController?.StopAndDrain();
            _animationController?.Dispose();
            _animationController = null;
        }

        // Finalize any remaining content (incomplete line without trailing newline)
        if (_useStreamingMarkdown)
        {
            var remaining = _lineCollector.Finalize();
            foreach (var line in remaining)
            {
                AnsiConsole.Write(line);
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]✓ Completed in {evt.Duration.TotalSeconds:F2}s[/]");
    }
    
    private void RenderError(MessageTurnErrorEvent evt)
    {
        AnsiConsole.WriteLine();
        new ErrorMessage { Message = evt.Message }.Display();
    }
    
    private void RenderTextDelta(TextDeltaEvent evt)
    {
        string text = evt.Text;
        if (_isFirstOutput)
        {
            _isFirstOutput = false;
            // Trim leading newlines from the first delta to avoid redundant blank lines after the rule.
            // The Rule component already handles its own line ending.
            text = text.TrimStart('\n', '\r');
            if (string.IsNullOrEmpty(text)) return;
        }

        if (_useStreamingMarkdown)
        {
            // Codex-style: buffer text, emit formatted lines on newlines
            _lineCollector.Push(text);

            // If we have complete lines, commit them to the queue
            if (_lineCollector.HasCompleteLines)
            {
                _lineCollector.CommitCompleteLines();

                // Check if we're using animated streaming
                lock (_animationLock)
                {
                    if (_animationController != null)
                    {
                        // Start animation if not already running
                        // Lines will be revealed at 50ms intervals
                        _animationController.StartAnimation();
                    }
                    else
                    {
                        // Immediate mode: drain and display all queued lines now
                        var queuedLines = _lineCollector.GetQueuedLines();
                        foreach (var line in queuedLines)
                        {
                            AnsiConsole.Write(line);
                        }
                    }
                }
            }
        }
        else
        {
            // Original behavior: stream text directly (unformatted)
            AnsiConsole.Markup(Markup.Escape(text));
        }
    }
    
    private void RenderToolStart(ToolCallStartEvent evt)
    {
        var toolMessage = new ToolMessage
        {
            Name = evt.Name,
            Status = ToolCallStatus.Executing
        };
        _toolComponents[evt.CallId] = toolMessage;
        
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]⚙ Calling:[/] [bold]{Markup.Escape(evt.Name)}[/]");
    }
    
    private void RenderToolArgs(ToolCallArgsEvent evt)
    {
        if (!_toolComponents.TryGetValue(evt.CallId, out var tool))
            return;
            
        tool.Args = evt.ArgsJson;
        
        // Display formatted args
        var formattedJson = UIHelpers.FormatJson(evt.ArgsJson);
        AnsiConsole.Write(
            new Panel(new Text(formattedJson))
                .Header($"[{Theme.Tool.Header}]Arguments[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Theme.Tool.Header)
                .Padding(1, 0)
        );
    }
    
    private void RenderToolResult(ToolCallResultEvent evt)
    {
        if (!_toolComponents.TryGetValue(evt.CallId, out var tool))
            return;
            
        tool.Result = evt.Result;
        
        var isError = evt.Result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        tool.Status = isError ? ToolCallStatus.Error : ToolCallStatus.Completed;
        
        // Display result using component
        AnsiConsole.Write(tool.Render());
        AnsiConsole.WriteLine();
        
        // Smart content-based rendering (Toolkit-agnostic)
        if (!isError)
        {
            RenderResultByType(evt.Result);
        }
        
        _toolComponents.TryRemove(evt.CallId, out _);
    }
    
    /// <summary>
    /// Smart content-based rendering. Detects result type and renders accordingly.
    /// This is Toolkit-agnostic: any tool outputting a diff gets diff rendering, etc.
    /// </summary>
    private void RenderResultByType(string result)
    {
        var resultType = ResultDetector.Detect(result);

        switch (resultType)
        {
            case ResultType.Diff:
                DisplayToolDiff(result);
                break;
            case ResultType.Json:
                // Future: DisplayJson(result);
                break;
            case ResultType.Table:
                // Future: DisplayTable(result);
                break;
            // Plain text already shown in tool.Render()
        }
    }

    private void DisplayToolDiff(string result)
    {
        try
        {
            // The result might contain diff information
            // Look for diff markers like +++ and --- (unified diff format)
            if (result.Contains("+++") && result.Contains("---"))
            {
                var lines = result.Split('\n');
                var diffContent = new StringBuilder();
                bool inDiff = false;
                string? fileName = null;
                
                foreach (var line in lines)
                {
                    // Extract filename from --- line
                    if (line.StartsWith("---") && fileName == null)
                    {
                        var parts = line.Split('\t');
                        if (parts.Length > 0)
                        {
                            fileName = parts[0].Substring(4).Trim(); // Remove "--- "
                        }
                        inDiff = true;
                    }
                    
                    if (inDiff)
                        diffContent.AppendLine(line);
                }
                
                if (diffContent.Length > 0)
                {
                    // Use DiffRenderer component for rich diff display
                    var diffRenderer = new DiffRenderer
                    {
                        DiffContent = diffContent.ToString(),
                        Filename = fileName,
                        MaxLines = 50
                    };
                    
                    AnsiConsole.Write(diffRenderer.Render());
                    AnsiConsole.WriteLine();
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[dim]Note: Could not parse diff: {Markup.Escape(ex.Message)}[/]");
        }
    }
    
    private void RenderPermissionRequest(PermissionRequestEvent evt)
    {
        AnsiConsole.WriteLine();
        var panel = new Panel(
            new Markup($"[yellow]Permission requested:[/]\n\n" +
                      $"[bold]{Markup.Escape(evt.FunctionName)}[/]\n" +
                      $"{Markup.Escape(evt.Description ?? "No description")}")
        )
        .Header("[yellow]🔒 Permission Required[/]")
        .Border(BoxBorder.Double)
        .BorderColor(Color.Yellow);

        AnsiConsole.Write(panel);

        // Prompt user for permission decision
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Grant permission?[/]")
                .AddChoices("Allow once", "Allow always", "Deny once", "Deny always"));

        var (approved, permChoice) = choice switch
        {
            "Allow once" => (true, PermissionChoice.Ask),
            "Allow always" => (true, PermissionChoice.AlwaysAllow),
            "Deny once" => (false, PermissionChoice.Ask),
            "Deny always" => (false, PermissionChoice.AlwaysDeny),
            _ => (false, PermissionChoice.Ask)
        };

        // Send response to unblock the middleware
        _agent?.SendMiddlewareResponse(
            evt.PermissionId,
            new PermissionResponseEvent(
                evt.PermissionId,
                "ConsoleUI",
                approved,
                approved ? null : "User denied permission",
                permChoice));

        AnsiConsole.MarkupLine(approved
            ? "[green]✓ Permission granted[/]"
            : "[red]✗ Permission denied[/]");
    }

    //
    // Workflow Event Renderers (multi-agent)
    //

    private void RenderWorkflowStarted(WorkflowStartedEvent evt)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Rule($"[bold cyan]Workflow: {Markup.Escape(evt.WorkflowName)}[/]")
                .LeftJustified()
                .RuleStyle("cyan"));
        AnsiConsole.MarkupLine($"[dim]Nodes: {evt.NodeCount}" +
            (evt.LayerCount.HasValue ? $", Layers: {evt.LayerCount}" : "") + "[/]");
    }

    private void RenderWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        AnsiConsole.WriteLine();
        var statusIcon = evt.Success ? "✓" : "✗";
        var statusColor = evt.Success ? "green" : "red";
        AnsiConsole.MarkupLine($"[{statusColor}]{statusIcon} Workflow '{Markup.Escape(evt.WorkflowName)}' completed in {evt.Duration.TotalSeconds:F2}s[/]");
        AnsiConsole.MarkupLine($"[dim]  Successful: {evt.SuccessfulNodes}, Failed: {evt.FailedNodes}, Skipped: {evt.SkippedNodes}[/]");
    }

    private void RenderWorkflowNodeStarted(WorkflowNodeStartedEvent evt)
    {
        AnsiConsole.WriteLine();
        var agentInfo = evt.AgentName != null ? $" ({Markup.Escape(evt.AgentName)})" : "";
        AnsiConsole.MarkupLine($"[yellow]▶ Starting:[/] [bold]{Markup.Escape(evt.NodeId)}[/]{agentInfo}" +
            (evt.LayerIndex.HasValue ? $" [dim](layer {evt.LayerIndex})[/]" : ""));
    }

    private void RenderWorkflowNodeCompleted(WorkflowNodeCompletedEvent evt)
    {
        var statusColor = evt.Success ? "green" : "red";
        var statusIcon = evt.Success ? "✓" : "✗";
        AnsiConsole.MarkupLine($"[{statusColor}]{statusIcon} Completed:[/] [bold]{Markup.Escape(evt.NodeId)}[/] [dim]({evt.Duration.TotalSeconds:F2}s)[/]");

        // Show error if failed
        if (!evt.Success && evt.ErrorMessage != null)
        {
            AnsiConsole.MarkupLine($"[red]  Error: {Markup.Escape(evt.ErrorMessage)}[/]");
        }

        // Show outputs if available
        if (evt.Outputs != null && evt.Outputs.Count > 0)
        {
            foreach (var kvp in evt.Outputs)
            {
                var displayValue = kvp.Value?.ToString() ?? "(null)";
                if (displayValue.Length > 100)
                    displayValue = displayValue[..100] + "...";
                AnsiConsole.MarkupLine($"[dim]  {Markup.Escape(kvp.Key)}: {Markup.Escape(displayValue)}[/]");
            }
        }
    }

    private void RenderWorkflowNodeSkipped(WorkflowNodeSkippedEvent evt)
    {
        AnsiConsole.MarkupLine($"[dim]⊘ Skipped:[/] {Markup.Escape(evt.NodeId)} - {Markup.Escape(evt.Reason)}");
    }

    private void RenderWorkflowEdgeTraversed(WorkflowEdgeTraversedEvent evt)
    {
        AnsiConsole.MarkupLine($"[blue]→ Edge:[/] {Markup.Escape(evt.FromNodeId)} → {Markup.Escape(evt.ToNodeId)}" +
            (evt.HasCondition ? $" [dim]({Markup.Escape(evt.ConditionDescription ?? "")})[/]" : ""));
    }

    private void RenderWorkflowLayerStarted(WorkflowLayerStartedEvent evt)
    {
        AnsiConsole.MarkupLine($"[cyan]◆ Layer {evt.LayerIndex}:[/] {evt.NodeCount} nodes");
    }

    private void RenderWorkflowLayerCompleted(WorkflowLayerCompletedEvent evt)
    {
        AnsiConsole.MarkupLine($"[dim]  Layer {evt.LayerIndex} completed in {evt.Duration.TotalSeconds:F2}s[/]");
    }

    private void RenderWorkflowDiagnostic(WorkflowDiagnosticEvent evt)
    {
        // Only show warnings and errors by default
        if (evt.Level >= LogLevel.Warning)
        {
            var color = evt.Level switch
            {
                LogLevel.Warning => "yellow",
                LogLevel.Error => "red",
                LogLevel.Critical => "red bold",
                _ => "dim"
            };
            AnsiConsole.MarkupLine($"[{color}][{Markup.Escape(evt.Source)}] {Markup.Escape(evt.Message)}[/]");
        }
    }
}

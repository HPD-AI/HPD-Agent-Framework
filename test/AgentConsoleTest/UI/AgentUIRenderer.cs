using HPD.Agent;
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
    /// Process and render an agent event using components.
    /// </summary>
    public void RenderEvent(AgentEvent evt)
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
                    
                case Reasoning reasoning:
                    RenderReasoning(reasoning);
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
        
        // Smart content-based rendering (plugin-agnostic)
        if (!isError)
        {
            RenderResultByType(evt.Result);
        }
        
        _toolComponents.TryRemove(evt.CallId, out _);
    }
    
    /// <summary>
    /// Smart content-based rendering. Detects result type and renders accordingly.
    /// This is plugin-agnostic: any tool outputting a diff gets diff rendering, etc.
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
    }
    
    private void RenderReasoning(Reasoning evt)
    {
        switch (evt.Phase)
        {
            case ReasoningPhase.SessionStart:
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim italic]🧠 Thinking...[/]");
                break;
                
            case ReasoningPhase.Delta when !string.IsNullOrEmpty(evt.Text):
                AnsiConsole.Markup($"[dim]{Markup.Escape(evt.Text)}[/]");
                break;
                
            case ReasoningPhase.SessionEnd:
                AnsiConsole.WriteLine();
                break;
        }
    }
}

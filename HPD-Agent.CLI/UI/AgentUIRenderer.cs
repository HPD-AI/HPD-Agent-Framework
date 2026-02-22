using HPD.Agent;
using HPD.Agent.Planning;
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
    private readonly ConcurrentDictionary<string, string?> _callIdToToolkit = new();
    private readonly ConcurrentDictionary<string, string?> _callIdToRenderedLine = new();
    private readonly object _lock = new();
    private bool _isFirstOutput = true;

    // Known CodingToolkit tools (for fallback detection when ToolkitName is null)
    private static readonly HashSet<string> CodingToolkitTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadFile", "read_file", "ReadManyFiles", "read_many_files",
        "EditFile", "edit_file", "WriteFile", "write_file",
        "ListDirectory", "list_directory", "GlobSearch", "glob_search",
        "Grep", "grep", "DiffFiles", "diff_files",
        "GetFileInfo", "get_file_info", "ExecuteCommand", "execute_command"
    };

    // Tools whose results should be hidden (rendered via dedicated events instead)
    private static readonly HashSet<string> HiddenResultTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreatePlanAsync", "create_plan_async", "CreatePlan", "create_plan",
        "UpdatePlanStepAsync", "update_plan_step_async", "UpdatePlanStep", "update_plan_step",
        "CodingToolkit", "MathToolkit"
    };

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

    // Current model info for display in response headers
    private string? _currentProvider;
    private string? _currentModel;

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
    /// Also extracts model info for display in response headers.
    /// </summary>
    public void SetAgent(Agent agent)
    {
        _agent = agent;
        _currentProvider = agent.Config.Provider?.ProviderKey;
        _currentModel = agent.Config.Provider?.ModelName;
    }

    /// <summary>
    /// Updates the model info displayed in response headers.
    /// Used when switching models via AgentRunConfig (without rebuilding agent).
    /// </summary>
    public void SetModelInfo(string provider, string model)
    {
        _currentProvider = provider;
        _currentModel = model;
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
                    AnsiConsole.MarkupLine("[dim italic] Thinking...[/]");
                    break;

                case ReasoningDeltaEvent delta:
                    // Reasoning text hidden - users find it too verbose
                    // AnsiConsole.Markup($"[dim]{Markup.Escape(delta.Text)}[/]");
                    break;

                case ReasoningMessageEndEvent:
                    AnsiConsole.WriteLine();
                    break;

                // Plan Mode events
                case PlanUpdatedEvent planUpdate:
                    RenderPlanUpdate(planUpdate);
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

                // History reduction events
                case HistoryReductionEvent historyReduction:
                    RenderHistoryReduction(historyReduction);
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

        // Build header: "AgentName - provider:model" or just "AgentName" if no model info
        var headerText = evt.AgentName;
        if (!string.IsNullOrEmpty(_currentProvider) && !string.IsNullOrEmpty(_currentModel))
        {
            headerText = $"{evt.AgentName} [dim]-[/] [cyan]{_currentProvider}[/]:[white]{_currentModel}[/]";
        }

        AnsiConsole.Write(
            new Rule($"[bold green]{headerText}[/]")
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
    }
    
    private void RenderError(MessageTurnErrorEvent evt)
    {
        AnsiConsole.WriteLine();

        // Show model-specific error with helpful suggestion
        if (evt.IsModelNotFound)
        {
            AnsiConsole.MarkupLine("[red bold]Model not found[/]");
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(evt.Message)}[/]");
            AnsiConsole.MarkupLine("[yellow]Tip:[/] Use [cyan]/models[/] to see available models, or check your model ID.");
        }
        else if (evt.Category != null)
        {
            // Show category-specific error
            AnsiConsole.MarkupLine($"[red bold][{evt.Category}][/]");
            new ErrorMessage { Message = evt.Message }.Display();

            if (evt.IsRetryable)
            {
                AnsiConsole.MarkupLine("[dim]This error may be temporary. Try again.[/]");
            }
        }
        else
        {
            new ErrorMessage { Message = evt.Message }.Display();
        }
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
        // Flush any pending streamed text BEFORE showing tool call
        // This ensures text appears in correct order relative to tool outputs
        FlushPendingText();

        var toolMessage = new ToolMessage
        {
            Name = evt.Name,
            Status = ToolCallStatus.Executing
        };
        _toolComponents[evt.CallId] = toolMessage;
        _callIdToToolkit[evt.CallId] = evt.ToolkitName;

        // Hide toolkit containers and tools with dedicated event rendering
        if (evt.Name.EndsWith("Toolkit") || HiddenResultTools.Contains(evt.Name))
        {
            return; // Completely hidden
        }

        // CodingToolkit tools: don't show anything on start - we'll show inline with result
        if (IsCodingToolkitTool(evt.Name, evt.ToolkitName))
        {
            return; // Buffer for inline display
        }

        // Default: show full tool call info for non-CodingToolkit tools
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]⚙ Calling:[/] [bold]{Markup.Escape(evt.Name)}[/]");
    }

    /// <summary>
    /// Detects if a tool belongs to CodingToolkit (by name or explicit ToolkitName)
    /// </summary>
    private static bool IsCodingToolkitTool(string toolName, string? toolkitName)
    {
        if (toolkitName == "CodingToolkit") return true;
        if (CodingToolkitTools.Contains(toolName)) return true;
        return false;
    }

    /// <summary>
    /// Flushes any pending streamed text from the line collector.
    /// Call this before rendering tool calls/results to maintain proper ordering.
    /// </summary>
    private void FlushPendingText()
    {
        if (!_useStreamingMarkdown) return;

        lock (_animationLock)
        {
            // Stop animation if running and drain queued lines
            _animationController?.StopAndDrain();

            // Finalize and display any buffered text (incomplete lines)
            var remaining = _lineCollector.Finalize();
            foreach (var line in remaining)
            {
                AnsiConsole.Write(line);
            }

            // Clear collector for fresh start after tool
            _lineCollector.Clear();

            // Recreate animation controller if animated streaming is enabled
            _animationController?.Dispose();
            if (_useAnimatedStreaming)
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
    }
    
    private void RenderToolArgs(ToolCallArgsEvent evt)
    {
        if (!_toolComponents.TryGetValue(evt.CallId, out var tool))
            return;

        tool.Args = evt.ArgsJson;
        _callIdToToolkit.TryGetValue(evt.CallId, out var toolkit);

        // For CodingToolkit tools: buffer the display line (will be shown with result)
        if (IsCodingToolkitTool(tool.Name, toolkit))
        {
            var displayLine = BuildCodingToolkitDisplayLine(tool.Name, evt.ArgsJson);
            _callIdToRenderedLine[evt.CallId] = displayLine;
        }
    }

    /// <summary>
    /// Builds the display line for a CodingToolkit tool call (returned as markup string)
    /// </summary>
    private static string BuildCodingToolkitDisplayLine(string toolName, string argsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            switch (toolName)
            {
                case "ReadFile" or "read_file":
                    var path = root.TryGetProperty("path", out var p) ? p.GetString() :
                               root.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
                    var startLine = root.TryGetProperty("startLine", out var sl) ? sl.GetInt32() : (int?)null;
                    var endLine = root.TryGetProperty("endLine", out var el) ? el.GetInt32() : (int?)null;
                    if (path != null)
                    {
                        var lineInfo = (startLine.HasValue && endLine.HasValue)
                            ? $" [dim](lines {startLine}-{endLine})[/]"
                            : startLine.HasValue ? $" [dim](from line {startLine})[/]" : "";
                        return $"[dim]⚙ ReadFile:[/] [blue]{Markup.Escape(path)}[/]{lineInfo}";
                    }
                    break;

                case "ReadManyFiles" or "read_many_files":
                    if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == System.Text.Json.JsonValueKind.Array)
                        return $"[dim]⚙ ReadManyFiles:[/] [blue]{paths.GetArrayLength()} files[/]";
                    break;

                case "EditFile" or "edit_file":
                case "WriteFile" or "write_file":
                    var editPath = root.TryGetProperty("path", out var ep) ? ep.GetString() :
                                   root.TryGetProperty("filePath", out var efp) ? efp.GetString() : null;
                    if (editPath != null)
                        return $"[dim]⚙ {Markup.Escape(toolName)}:[/] [blue]{Markup.Escape(editPath)}[/]";
                    break;

                case "ListDirectory" or "list_directory":
                    var dirPath = root.TryGetProperty("directoryPath", out var dp) ? dp.GetString() :
                                  root.TryGetProperty("path", out var dp2) ? dp2.GetString() : null;
                    var displayDir = string.IsNullOrWhiteSpace(dirPath) ? "." : dirPath;
                    return $"[dim]⚙ ListDirectory:[/] [blue]{Markup.Escape(displayDir)}[/]";

                case "GlobSearch" or "glob_search":
                    var pattern = root.TryGetProperty("pattern", out var pat) ? pat.GetString() : null;
                    if (pattern != null)
                        return $"[dim]⚙ GlobSearch:[/] [blue]{Markup.Escape(pattern)}[/]";
                    break;

                case "Grep" or "grep":
                    var query = root.TryGetProperty("pattern", out var q) ? q.GetString() :
                                root.TryGetProperty("query", out var qry) ? qry.GetString() : null;
                    if (query != null)
                        return $"[dim]⚙ Grep:[/] [blue]{Markup.Escape(query)}[/]";
                    break;

                case "ExecuteCommand" or "execute_command":
                    var cmd = root.TryGetProperty("command", out var c) ? c.GetString() : null;
                    if (cmd != null)
                    {
                        var displayCmd = cmd.Length > 60 ? cmd[..60] + "..." : cmd;
                        return $"[dim]⚙ ExecuteCommand:[/] [yellow]{Markup.Escape(displayCmd)}[/]";
                    }
                    break;
            }
        }
        catch { /* JSON parse failed */ }

        return $"[dim]⚙ {Markup.Escape(toolName)}[/]";
    }
    
    private void RenderToolResult(ToolCallResultEvent evt)
    {
        // Flush any pending streamed text before showing tool result
        FlushPendingText();

        if (!_toolComponents.TryGetValue(evt.CallId, out var tool))
            return;

        tool.Result = evt.Result;

        var isError = evt.Result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        tool.Status = isError ? ToolCallStatus.Error : ToolCallStatus.Completed;

        // Get toolkit name (from event or cached from start event)
        var toolkitName = evt.ToolkitName ?? (_callIdToToolkit.TryGetValue(evt.CallId, out var cached) ? cached : null);

        // Hide results for tools with dedicated event rendering
        if (HiddenResultTools.Contains(tool.Name) || tool.Name.EndsWith("Toolkit"))
        {
            _toolComponents.TryRemove(evt.CallId, out _);
            _callIdToToolkit.TryRemove(evt.CallId, out _);
            _callIdToRenderedLine.TryRemove(evt.CallId, out _);
            return;
        }

        // CodingToolkit tools: show inline with colored gear
        if (IsCodingToolkitTool(tool.Name, toolkitName))
        {
            RenderCodingToolkitResult(tool, evt.Result, isError, evt.CallId);
            _toolComponents.TryRemove(evt.CallId, out _);
            _callIdToToolkit.TryRemove(evt.CallId, out _);
            _callIdToRenderedLine.TryRemove(evt.CallId, out _);
            return;
        }

        // Default: show full result for non-CodingToolkit tools
        AnsiConsole.Write(tool.Render());
        if (!isError)
        {
            RenderResultByType(evt.Result);
        }

        _toolComponents.TryRemove(evt.CallId, out _);
        _callIdToToolkit.TryRemove(evt.CallId, out _);
    }

    private void RenderCodingToolkitResult(ToolMessage tool, string result, bool isError, string callId)
    {
        // Get buffered display line and colorize gear based on result
        _callIdToRenderedLine.TryRemove(callId, out var displayLine);
        displayLine ??= $"⚙ {Markup.Escape(tool.Name)}";

        // Replace dim gear with colored gear based on success/failure
        var coloredLine = isError
            ? displayLine.Replace("[dim]⚙", "[red]⚙")
            : displayLine.Replace("[dim]⚙", "[green]⚙");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(coloredLine);

        if (isError)
        {
            AnsiConsole.MarkupLine($"[red dim]  {Markup.Escape(TruncateResult(result, 100))}[/]");
            return;
        }

        // Show diff for write operations
        var isWriteOp = tool.Name is "EditFile" or "WriteFile" or "edit_file" or "write_file";
        var hasDiff = result.Contains("+++") && result.Contains("---");

        if (isWriteOp && hasDiff)
        {
            DisplayToolDiff(result);
        }
    }

    private static string TruncateResult(string result, int maxLength)
    {
        if (result.Length <= maxLength) return result;
        return result[..maxLength] + "...";
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

    private void RenderHistoryReduction(HistoryReductionEvent evt)
    {
        // Only show if reduction actually happened (not skipped)
        if (evt.Status == HistoryReductionStatus.Skipped)
        {
            // Optionally show skipped events in debug mode
            // AnsiConsole.MarkupLine($"[dim]⊘ History reduction skipped: {evt.Reason}[/]");
            return;
        }

        var icon = evt.Status switch
        {
            HistoryReductionStatus.CacheHit => "◇",
            HistoryReductionStatus.Performed => "≡",
            _ => "◈"
        };

        var color = evt.Status switch
        {
            HistoryReductionStatus.CacheHit => "cyan",
            HistoryReductionStatus.Performed => "yellow",
            _ => "dim"
        };

        // Show reduction summary
        AnsiConsole.MarkupLine($"[{color}]{icon} History Reduction ({evt.Status}):[/]");

        if (evt.OriginalMessageCount.HasValue && evt.ReducedMessageCount.HasValue)
        {
            AnsiConsole.MarkupLine($"[dim]  {evt.OriginalMessageCount} → {evt.ReducedMessageCount} messages[/]");
        }

        if (evt.MessagesRemoved.HasValue)
        {
            AnsiConsole.MarkupLine($"[dim]  Removed: {evt.MessagesRemoved} messages[/]");
        }

        if (evt.CacheAge.HasValue)
        {
            AnsiConsole.MarkupLine($"[dim]  Cache age: {evt.CacheAge.Value.TotalMinutes:F1}m[/]");
        }

        // Show summary content if available (for Summarizing strategy)
        if (evt.Strategy == HistoryReductionStrategy.Summarizing &&
            !string.IsNullOrEmpty(evt.SummaryContent))
        {
            var summaryPreview = evt.SummaryContent.Length > 200
                ? evt.SummaryContent[..200] + "..."
                : evt.SummaryContent;

            var panel = new Panel(Markup.Escape(summaryPreview))
            {
                Header = new PanelHeader($"Summary ({evt.SummaryLength} chars)", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(foreground: Color.Grey)
            };

            AnsiConsole.Write(panel);
        }

        AnsiConsole.MarkupLine($"[dim]  Duration: {evt.Duration.TotalMilliseconds:F0}ms[/]");
        AnsiConsole.WriteLine();
    }

    private void RenderPlanUpdate(PlanUpdatedEvent evt)
    {
        // Cast Plan to AgentPlanData
        if (evt.Plan is not HPD.Agent.Planning.AgentPlanData plan)
        {
            AnsiConsole.MarkupLine("[red]⚠ Invalid plan data in PlanUpdatedEvent[/]");
            return;
        }

        var icon = evt.UpdateType switch
        {
            PlanUpdateType.Created => "≡",
            PlanUpdateType.StepUpdated => "◐",
            PlanUpdateType.StepAdded => "+",
            PlanUpdateType.NoteAdded => "»",
            PlanUpdateType.Completed => "●",
            _ => "•"
        };

        var color = evt.UpdateType switch
        {
            PlanUpdateType.Created => "cyan",
            PlanUpdateType.StepUpdated => "yellow",
            PlanUpdateType.StepAdded => "green",
            PlanUpdateType.NoteAdded => "blue",
            PlanUpdateType.Completed => "green bold",
            _ => "white"
        };

        // Display plan update header
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{color}]{icon} Plan {evt.UpdateType}:[/] [dim]{Markup.Escape(evt.Explanation ?? "")}[/]");

        // Display plan details in a panel
        var panel = new Panel(BuildPlanDisplay(plan, evt.UpdateType))
        {
            Header = new PanelHeader($"Plan: {Markup.Escape(plan.Goal)}", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: Color.Grey)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private IRenderable BuildPlanDisplay(HPD.Agent.Planning.AgentPlanData plan, PlanUpdateType updateType)
    {
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").Width(3))
            .AddColumn(new TableColumn("").Width(12))
            .AddColumn(new TableColumn(""));

        // Show steps
        foreach (var step in plan.Steps)
        {
            var statusIcon = step.Status switch
            {
                HPD.Agent.Planning.PlanStepStatus.Pending => "○",
                HPD.Agent.Planning.PlanStepStatus.InProgress => "◐",
                HPD.Agent.Planning.PlanStepStatus.Completed => "●",
                HPD.Agent.Planning.PlanStepStatus.Blocked => "⊘",
                _ => "•"
            };

            var statusColor = step.Status switch
            {
                HPD.Agent.Planning.PlanStepStatus.Pending => "dim",
                HPD.Agent.Planning.PlanStepStatus.InProgress => "yellow",
                HPD.Agent.Planning.PlanStepStatus.Completed => "green",
                HPD.Agent.Planning.PlanStepStatus.Blocked => "red",
                _ => "white"
            };

            var statusText = $"[{statusColor}]{step.Status}[/]";
            var description = Markup.Escape(step.Description);

            // Highlight the step if it was just updated
            if (updateType == PlanUpdateType.StepUpdated || updateType == PlanUpdateType.StepAdded)
            {
                description = $"[bold]{description}[/]";
            }

            table.AddRow(
                $"[{statusColor}]{statusIcon}[/]",
                statusText,
                description
            );

            // Show notes if available
            if (!string.IsNullOrEmpty(step.Notes))
            {
                table.AddRow("", "", $"[dim italic]→ {Markup.Escape(step.Notes)}[/]");
            }
        }

        // Show context notes if any
        if (plan.ContextNotes.Count > 0)
        {
            table.AddEmptyRow();
            table.AddRow("[blue]»[/]", "[blue]Notes:[/]", "");
            foreach (var note in plan.ContextNotes)
            {
                table.AddRow("", "", $"[dim]• {Markup.Escape(note)}[/]");
            }
        }

        // Show completion status
        if (plan.IsComplete)
        {
            table.AddEmptyRow();
            table.AddRow("[green]●[/]", "[green bold]Complete[/]", $"[dim]{plan.CompletedAt:g}[/]");
        }

        return table;
    }
}

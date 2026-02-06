#pragma warning disable MEAI001 // Microsoft.Extensions.AI is experimental

using HPD.Agent;
using HPD.Agent.Audio;
using HPD.Agent.Audio.ElevenLabs;
using HPD.Agent.MCP;
using HPD.Agent.Memory;
using HPD_Agent.CLI.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using AgentConsoleTest;
using System.Threading.Channels;

// Capture user's working directory BEFORE changing it
var userWorkingDirectory = Directory.GetCurrentDirectory();

var appDirectory = AppContext.BaseDirectory;

// Print banner using FigletText - resizable and consistent across terminals
// Load ANSI Shadow font for block-style ASCII art (similar to original logo)
var fontPath = Path.Combine(appDirectory, "Fonts", "ANSIShadow.flf");
var font = File.Exists(fontPath)
    ? FigletFont.Load(fontPath)
    : FigletFont.Default;

var logo = new FigletText(font, "HPD AGENT")
    .LeftJustified()
    .Color(new Spectre.Console.Color(0, 255, 255)); // Cyan

AnsiConsole.Write(logo);
AnsiConsole.MarkupLine("[dim]Powered by HPD Agent Framework[/]");

// Initialize auth manager for OAuth/API key management
var authManager = new AuthManager();

// Load configuration from appsettings.json (use app directory for config file)
var configuration = new ConfigurationBuilder()
    .SetBasePath(appDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

// Set up session persistence
// Production: ~/Library/Application Support/HPD-Agent/sessions (user data)
// Development: /Users/einsteinessibu/Documents/HPD-Agent/test/AgentConsoleTest/sessions (project folder)

// Detect if running from installed location or development
var isProduction = appDirectory.Contains(".hpd-agent") || appDirectory.Contains("publish");

string sessionsPath;
if (isProduction)
{
    // Production: Use user data directory
    var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var hpdDataPath = Path.Combine(userDataPath, "HPD-Agent");
    sessionsPath = Path.Combine(hpdDataPath, "sessions");
}
else
{
    // Development: Use project sessions folder at the CLI project level
    // Find the .csproj file to identify project root
    var projectRoot = appDirectory;
    DirectoryInfo? parent = Directory.GetParent(projectRoot);

    while (parent != null)
    {
        // Check if this directory contains the .csproj file
        if (File.Exists(Path.Combine(parent.FullName, "HPD-Agent.CLI.csproj")))
        {
            projectRoot = parent.FullName;
            break;
        }
        parent = parent.Parent;
    }

    sessionsPath = Path.Combine(projectRoot, "sessions");
}

Directory.CreateDirectory(sessionsPath);

var sessionStore = new JsonSessionStore(sessionsPath);
var envLabel = isProduction ? "prod" : "dev";
AnsiConsole.MarkupLine($"[dim]Sessions ({envLabel}): {sessionsPath}[/]");

// Create agent configuration - generic base, specialized personas come from Toolkits
// NEW: Toolkits are now registered via config instead of builder calls
var config = new AgentConfig
{
    Name = "HPD Agent",
    MaxAgenticIterations = 50,
    // Generic base prompt - specialized personas are injected via [Collapse] postExpansionInstructions
    SystemInstructions = @"You are a helpful AI assistant with access to specialized tools and capabilities.

When the user asks you something:
1. Think carefully about what they're asking
2. Use the appropriate tools if needed
3. Provide clear, complete, and helpful responses
4. Explain your reasoning when relevant
5. Never respond with just 'Yes' or single words - always provide useful information

Be helpful, thorough, and conversational.",
    Collapsing = new CollapsingConfig { Enabled = true },
    // Enable history reduction to manage long conversations
    HistoryReduction = new HistoryReductionConfig
    {
        Enabled = true,
        Strategy = HistoryReductionStrategy.Summarizing,  // LLM summarizes old messages
        TargetMessageCount = 3,      // Keep last 3 messages (small for testing)
        SummarizationThreshold = 100,  // Re-summarize when 2 new messages added
        
    },
    
    // Toolkits resolved from source-generated registry at Build() time
    // Note: CodingToolkit is registered as an instance below for chat client injection
    Toolkits = new List<ToolkitReference>
    {
        "CodingToolkit",
        "MathToolkit"
    },
    // Provider configuration - Claude 3.5 Sonnet works well with OpenRouter
    Provider = new ProviderConfig
    {
        ProviderKey = "openrouter",
        ModelName = "z-ai/glm-4.7",  // Switched from Mistral to Claude - avoids chat_template issue
    }
};
// Configure logging to show Information level logs including HTTP details
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);  // Show trace logs for maximum verbosity
    builder.AddConsole();
    // Enable HTTP client logging to see exact requests/responses
    builder.AddFilter("System.Net.Http", LogLevel.Debug);
    builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Debug);
    builder.AddFilter("Microsoft.Extensions.AI", LogLevel.Debug);
    // Enable plan mode middleware logging
    builder.AddFilter("HPD.Agent.Memory.AgentPlanAgentMiddleware", LogLevel.Trace);
});



// Create CodingToolkit instance for chat client injection (LLM self-correction)
var codingToolkit = new CodingToolkit();

// MCP manifest path (same directory as appsettings.json)
var mcpManifestPath = Path.Combine(appDirectory, "MCP.json");

var agentBuilder = new AgentBuilder(config)
    //.WithLogging(loggerFactory)
    .WithMiddleware(new EnvironmentContextMiddleware(new[] { userWorkingDirectory }))
    // Register CodingToolkit as instance to enable chat client injection after build
    .WithSessionStore(sessionStore, persistAfterTurn: true)
    // Enable plan mode for multi-step task tracking
    .WithPlanMode()
    .WithPermissions()
    // Add MCP support for external tool servers
    .WithMCP(mcpManifestPath);

// Add audio pipeline if providers are available
var agent = await agentBuilder.Build();

AnsiConsole.WriteLine();

// Generate a unique session ID for this run
var sessionId = $"console-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";
var (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);

// Initialize UI with slash command support
var ui = new AgentUIRenderer();
ui.SetAgent(agent);

// Prepare context data for commands (sessions browsing, etc.)
var commandContextData = new Dictionary<string, object>
{
    { "SessionsPath", sessionsPath },
    { "Agent", agent },
    { "Configuration", configuration },
    { "CurrentSessionId", sessionId },
    { "Branch", branch },
    { "AuthManager", authManager }
};

// Add session switch callback (needs reference to commandContextData, so added after)
commandContextData["OnSessionSwitch"] = new Func<string, Task>(async (newSessionId) =>
{
    sessionId = newSessionId;
    (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);
    // Update context data with new session info
    commandContextData["CurrentSessionId"] = sessionId;
    commandContextData["Branch"] = branch;
    AnsiConsole.MarkupLine($"[green]✓ Switched to session:[/] [cyan]{sessionId}[/]");
    AnsiConsole.MarkupLine($"[dim]Messages in session: {branch.Messages.Count}[/]");

    // Render conversation history - group by conversation turns
    var allMessages = branch.Messages.ToList();

    if (allMessages.Count > 0)
    {
        AnsiConsole.WriteLine();

        // Group messages into conversation turns: user message -> all responses until next user message
        var turns = new List<(string UserMessage, List<string> ToolsUsed, string? AssistantResponse)>();
        string? currentUserMessage = null;
        var currentTools = new List<string>();
        string? lastAssistantResponse = null;

        foreach (var msg in allMessages)
        {
            var role = msg.Role.Value;

            if (role == "user")
            {
                // Save previous turn if exists
                if (currentUserMessage != null)
                {
                    turns.Add((currentUserMessage, new List<string>(currentTools), lastAssistantResponse));
                }
                // Start new turn
                currentUserMessage = msg.Text ?? "";
                currentTools.Clear();
                lastAssistantResponse = null;
            }
            else if (role == "assistant")
            {
                // Collect tool calls
                var toolCalls = msg.Contents?.OfType<Microsoft.Extensions.AI.FunctionCallContent>().ToList();
                if (toolCalls?.Count > 0)
                {
                    foreach (var tc in toolCalls)
                    {
                        if (!currentTools.Contains(tc.Name))
                            currentTools.Add(tc.Name);
                    }
                }

                // Track text response (last one wins - that's the final response)
                var textContent = msg.Text;
                if (!string.IsNullOrWhiteSpace(textContent))
                {
                    lastAssistantResponse = textContent;
                }
            }
            // Skip tool result messages
        }

        // Don't forget the last turn
        if (currentUserMessage != null)
        {
            turns.Add((currentUserMessage, new List<string>(currentTools), lastAssistantResponse));
        }

        AnsiConsole.Write(new Rule($"[yellow]Conversation History ({turns.Count} turns)[/]").LeftJustified().RuleStyle("yellow"));
        AnsiConsole.WriteLine();

        // Render each turn
        foreach (var turn in turns)
        {
            AnsiConsole.MarkupLine($"[bold cyan]You:[/] {Markup.Escape(turn.UserMessage)}");

            if (turn.ToolsUsed.Count > 0)
            {
                AnsiConsole.MarkupLine($"[dim]   {Markup.Escape(string.Join(", ", turn.ToolsUsed))}[/]");
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                AnsiConsole.MarkupLine($"[bold green]Assistant:[/] {Markup.Escape(turn.AssistantResponse)}");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.Write(new Rule("[dim]End of History[/]").LeftJustified().RuleStyle("dim"));
        AnsiConsole.WriteLine();
    }
});

var commandProcessor = new CommandProcessor(ui.CommandRegistry, ui, commandContextData);
var commandInput = new CommandAwareInput(commandProcessor);

// Current run options (for provider/model override)
AgentRunOptions? currentRunOptions = null;

while (true)
{
    // Read input using CommandAwareInput for proper paste handling and autocomplete
    // This uses ReadKey which buffers until Enter is pressed, not on newlines in pasted content
    var userInput = commandInput.ReadLine("You: ");

    if (string.IsNullOrWhiteSpace(userInput))
        continue;

    // If user typed just "/", show command selection menu
    if (userInput.Trim() == "/")
    {
        var commands = ui.CommandRegistry.GetVisibleCommands()
            .OrderBy(c => c.Name)
            .ToList();
        
        if (commands.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No commands available[/]");
            continue;
        }
        
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<SlashCommand>()
                .Title("[yellow]Select a command:[/]")
                .PageSize(10)
                .MoreChoicesText("[dim](Move up and down to see more commands)[/]")
                .AddChoices(commands)
                .UseConverter(cmd => 
                {
                    var aliases = cmd.AltNames.Count > 0 
                        ? $" [dim]({string.Join(", ", cmd.AltNames)})[/]" 
                        : "";
                    return $"/{cmd.Name}{aliases} - [dim]{cmd.Description}[/]";
                })
        );
        
        // Execute the selected command
        userInput = "/" + selected.Name;
        var result = await commandProcessor.ExecuteAsync(userInput);
        
        if (result.ShouldExit)
        {
            AnsiConsole.MarkupLine("\n[dim]Goodbye! ~[/]");
            break;
        }
        
        if (result.ShouldClearHistory)
        {
            (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);
        }

        AnsiConsole.WriteLine();
        continue;
    }

    // Auth commands - /auth login, logout, list, status
    if (userInput.StartsWith("/auth", StringComparison.OrdinalIgnoreCase))
    {
        var authArgs = userInput.Length > 5
            ? userInput[5..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        await AuthCommands.HandleAuthCommandAsync(authManager, authArgs);
        AnsiConsole.WriteLine();
        continue;
    }

    // Diff demo - showcase DiffPlex rendering
    if (userInput.Equals("/diff", StringComparison.OrdinalIgnoreCase))
    {
        DiffDemo.Run();
        AnsiConsole.WriteLine();
        continue;
    }

    // Smart workflow with classifier (routes to math or general)
    if (userInput.StartsWith("/ask ", StringComparison.OrdinalIgnoreCase))
    {
        var question = userInput[5..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /ask <your question>[/]");
            AnsiConsole.MarkupLine("[dim]Math questions → 3-agent consensus, General questions → single agent[/]");
        }
        else
        {
            var answer = await SmartQuantWorkflow.RunAsync(question);
        }
        AnsiConsole.WriteLine();
        continue;
    }

    // Smart workflow V2 using HPD.MultiAgent (same as /ask but using the new API)
    if (userInput.StartsWith("/askv2 ", StringComparison.OrdinalIgnoreCase))
    {
        var question = userInput[7..].Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /askv2 <your question>[/]");
            AnsiConsole.MarkupLine("[dim]Same as /ask but using HPD.MultiAgent fluent API[/]");
        }
        else
        {
            var answer = await SmartQuantWorkflowV2.RunAsync(question);
        }
        AnsiConsole.WriteLine();
        continue;
    }

    // Quant workflows - multi-agent consensus (stateless, no session needed)
    // Check these BEFORE the generic command processor to avoid "unknown command" error
    if (userInput.StartsWith("/quant-graph ", StringComparison.OrdinalIgnoreCase))
    {
        var question = userInput.Substring(13).Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /quant-graph <your question>[/]");
            AnsiConsole.MarkupLine("[dim]Example: /quant-graph What is the expected value of rolling two dice?[/]");
        }
        else
        {
            var answer = await GraphQuantWorkflow.RunAsync(question);
        }
        AnsiConsole.WriteLine();
        continue;
    }

    if (userInput.StartsWith("/quant ", StringComparison.OrdinalIgnoreCase))
    {
        var question = userInput.Substring(7).Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /quant <your question>[/]");
            AnsiConsole.MarkupLine("[dim]Example: /quant What is 15% of 240?[/]");
        }
        else
        {
            var answer = await SimpleQuantWorkflow.RunAsync(question);
        }
        AnsiConsole.WriteLine();
        continue;
    }

    // Check for image input BEFORE command processor
    var (hasImage, imageContent, imageQuestion) = await ImageInputHelper.TryParseImageInputAsync(userInput);
    if (hasImage && imageContent != null)
    {
        // Create message with both text and image
        // detail options: "low" (~85 tokens, cheap), "high" (best quality), null/auto (balanced)
        // Using auto (null) for balanced quality and cost - change to "low" for cheaper
        var message = ImageInputHelper.CreateImageMessage(
            imageQuestion ?? "Describe this image in detail",
            imageContent,
            detail: null); // auto/balanced

        // Stream agent response using the new ChatMessage overload
        using (var escape = new EscapeCancellation())
        {
            try
            {
                await foreach (var evt in agent.RunAsync(message, sessionId, branchId: null, currentRunOptions, escape.Token))
                {
                    ui.RenderEvent(evt);
                }
            }
            catch (OperationCanceledException) when (escape.WasCancelled)
            {
                // User cancelled with Escape - this is expected
            }
            catch (Exception ex)
            {
                // DEBUG: Write to file since console output may be suppressed
                var debugLog = $"""
                    [DEBUG] Time: {DateTime.Now}
                    [DEBUG] Exception Type: {ex.GetType().FullName}
                    [DEBUG] Message: {ex.Message}
                    [DEBUG] Full: {ex}
                    [DEBUG] Inner Type: {ex.InnerException?.GetType().FullName}
                    [DEBUG] Inner Message: {ex.InnerException?.Message}
                    ---
                    """;
                File.AppendAllText("/tmp/hpd-debug.log", debugLog);

                // Handle errors gracefully
                var errorMessage = ex.InnerException?.Message ?? ex.Message;

                var isModelError = HPD.Agent.ErrorHandling.ModelNotFoundDetector.IsModelNotFoundError(
                    status: null,
                    message: errorMessage,
                    errorCode: null,
                    errorType: null);

                if (isModelError)
                {
                    AnsiConsole.MarkupLine($"\n[red]⊘ Model not found:[/] [yellow]{Markup.Escape(errorMessage)}[/]");

                    if (currentRunOptions != null)
                    {
                        AnsiConsole.MarkupLine("[dim]Resetting to default model. Use /model to set a valid model.[/]");
                        currentRunOptions = null;
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[red]⊘ Error:[/] [yellow]{Markup.Escape(errorMessage)}[/]");
                }

                AnsiConsole.WriteLine();
            }
        }

        // Reload branch
        (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);
        AnsiConsole.WriteLine();
        continue;
    }

    // Check if it's a slash command (AFTER custom commands like /quant and /image)
    if (commandProcessor.IsCommand(userInput))
    {
        var result = await commandProcessor.ExecuteAsync(userInput);

        if (result.ShouldExit)
        {
            AnsiConsole.MarkupLine("\n[dim]Goodbye! ~[/]");
            break;
        }

        if (result.ShouldClearHistory)
        {
            // History cleared, reload branch
            (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);
        }

        // Handle model switch request
        if (result.ShouldSwitchModel && commandContextData.TryGetValue("ModelSwitchRequest", out var switchReqObj)
            && switchReqObj is BuiltInCommands.ModelSwitchRequest switchReq)
        {
            // Use AgentRunOptions for runtime provider switching (no rebuild needed)
            currentRunOptions = new AgentRunOptions
            {
                ProviderKey = switchReq.Provider,
                ModelId = switchReq.Model,
                ApiKey = switchReq.ApiKey,  // Pass the API key for the new provider
                ProviderEndpoint = switchReq.BaseUrl,  // Pass custom endpoint (e.g., Codex API)
                CustomHeaders = switchReq.CustomHeaders,  // Pass custom headers (e.g., ChatGPT-Account-Id)
                // Use OverrideChatClient for OAuth-based OpenAI (Codex API)
                // This bypasses the standard provider registry since Codex requires a specialized client
                OverrideChatClient = switchReq.OverrideChatClient
            };

            // Update UI to show new model in response headers
            ui.SetModelInfo(switchReq.Provider, switchReq.Model);

            AnsiConsole.MarkupLine($"[green]✓ Switched to [cyan]{switchReq.Provider}[/]:[white]{switchReq.Model}[/][/]");

            // Debug: show resolved credentials
            if (switchReq.IsOAuthBased)
            {
                AnsiConsole.MarkupLine($"[dim]→ Using Codex API (OAuth)[/]");
            }
            else if (!string.IsNullOrEmpty(switchReq.BaseUrl))
            {
                AnsiConsole.MarkupLine($"[dim]→ Using endpoint: {switchReq.BaseUrl}[/]");
            }
            if (switchReq.CustomHeaders?.Count > 0)
            {
                AnsiConsole.MarkupLine($"[dim]→ Custom headers: {string.Join(", ", switchReq.CustomHeaders.Keys)}[/]");
            }

            // Clear the switch request
            commandContextData["ModelSwitchRequest"] = null!;
        }

        // Handle new session request
        if (commandContextData.TryGetValue("NewSessionRequest", out var newSessionObj)
            && newSessionObj is string newSessionId && !string.IsNullOrEmpty(newSessionId))
        {
            sessionId = newSessionId;
            (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);
            commandContextData["CurrentSessionId"] = sessionId;
            commandContextData["Branch"] = branch;
            AnsiConsole.MarkupLine($"[green]✓ New session created:[/] [cyan]{sessionId}[/]");

            // Clear the request
            commandContextData["NewSessionRequest"] = null!;
        }

        AnsiConsole.WriteLine();
        continue;
    }

    // Regular message - legacy exit commands still work
    if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("\n[dim]Goodbye! ~[/]");
        break;
    }

    // Stream agent response with real-time rendering using sessionId for auto-save
    // DEBUG: Show session info before running (helps diagnose session restore issues)
    #if DEBUG
    var (debugSession, debugBranch) = await agent.LoadSessionAndBranchAsync(sessionId);
    AnsiConsole.MarkupLine($"[dim]≡ Session: {sessionId} ({debugBranch.Messages.Count} messages)[/]");
    if (currentRunOptions != null && !string.IsNullOrEmpty(currentRunOptions.ProviderKey))
    {
        AnsiConsole.MarkupLine($"[dim]→ Using override: {currentRunOptions.ProviderKey}:{currentRunOptions.ModelId}[/]");
    }
    #endif

    // Resolve credentials from auth storage if needed
    var effectiveRunOptions = currentRunOptions;
    if (effectiveRunOptions != null && !string.IsNullOrEmpty(effectiveRunOptions.ProviderKey))
    {
        var resolvedCreds = await authManager.ResolveCredentialsAsync(effectiveRunOptions.ProviderKey);
        if (resolvedCreds != null && string.IsNullOrEmpty(effectiveRunOptions.ApiKey))
        {
            // Debug: show what credentials were resolved
            AnsiConsole.MarkupLine($"[dim]→ Resolved BaseUrl: {resolvedCreds.BaseUrl ?? "(null)"}[/]");
            AnsiConsole.MarkupLine($"[dim]→ Resolved Headers: {(resolvedCreds.CustomHeaders != null ? string.Join(", ", resolvedCreds.CustomHeaders.Keys) : "(none)")}[/]");

            effectiveRunOptions = new AgentRunOptions
            {
                ProviderKey = effectiveRunOptions.ProviderKey,
                ModelId = effectiveRunOptions.ModelId,
                ApiKey = resolvedCreds.ApiKey,
                ProviderEndpoint = resolvedCreds.BaseUrl,
                CustomHeaders = resolvedCreds.CustomHeaders
            };
        }
    }

    using (var escape = new EscapeCancellation())
    {
        try
        {
            await foreach (var evt in agent.RunAsync(userInput, sessionId, branchId: null, effectiveRunOptions, escape.Token))
            {
                ui.RenderEvent(evt);
            }
        }
        catch (OperationCanceledException) when (escape.WasCancelled)
        {
            // User cancelled with Escape - this is expected
        }
        catch (Exception ex)
        {
            // DEBUG: Write to file since console output may be suppressed
            var debugLog2 = $"""
                [DEBUG-CATCH2] Time: {DateTime.Now}
                [DEBUG-CATCH2] Exception Type: {ex.GetType().FullName}
                [DEBUG-CATCH2] Message: {ex.Message}
                [DEBUG-CATCH2] Full: {ex}
                [DEBUG-CATCH2] Inner Type: {ex.InnerException?.GetType().FullName}
                [DEBUG-CATCH2] Inner Message: {ex.InnerException?.Message}
                ---
                """;
            File.AppendAllText("/tmp/hpd-debug.log", debugLog2);

            // Handle errors gracefully - don't crash the whole app
            var errorMessage = ex.InnerException?.Message ?? ex.Message;

            // Use HPD-Agent's error detection to properly categorize the error
            var isModelError = HPD.Agent.ErrorHandling.ModelNotFoundDetector.IsModelNotFoundError(
                status: null,
                message: errorMessage,
                errorCode: null,
                errorType: null);

            if (isModelError)
            {
                AnsiConsole.MarkupLine($"\n[red]⊘ Model not found:[/] [yellow]{Markup.Escape(errorMessage)}[/]");

                // Reset to default model if we were using an override
                if (currentRunOptions != null)
                {
                    AnsiConsole.MarkupLine("[dim]Resetting to default model. Use /model to set a valid model.[/]");
                    currentRunOptions = null;
                }
            }
            else
            {
                // For other errors, show the error and continue
                AnsiConsole.MarkupLine($"\n[red]⊘ Error:[/] [yellow]{Markup.Escape(errorMessage)}[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    // Reload branch
    (session, branch) = await agent.LoadSessionAndBranchAsync(sessionId);
}

// Helper class to log HTTP requests/responses
    /// <summary>
    /// HTTP handler for debugging HTTP requests and responses.
    /// </summary>
    public class DebugHttpHandler : HttpClientHandler
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DebugHttpHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger to use for debugging output.</param>
    public DebugHttpHandler(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sends an HTTP request asynchronously and logs the request and response.
    /// </summary>
    /// <param name="request">The HTTP request message.</param>
    /// <param name="cancellationToken">A cancellation token to cancel operation.</param>
    /// <returns>The HTTP response message.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== HTTP REQUEST ===");
        _logger.LogInformation($"Method: {request.Method} {request.RequestUri}");
        _logger.LogInformation($"Headers: {string.Join(", ", request.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}");
        
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation($"Body: {body}");
        }

        var response = await base.SendAsync(request, cancellationToken);

        _logger.LogInformation("=== HTTP RESPONSE ===");
        _logger.LogInformation($"Status: {response.StatusCode}");
        _logger.LogInformation($"Headers: {string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}");

        if (response.Content != null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation($"Body: {body}");
            // Reset content stream since we read it
            response.Content = new StringContent(body, response.Content.Headers.ContentEncoding.FirstOrDefault() != null 
                ? new System.Text.UTF8Encoding() 
                : System.Text.Encoding.UTF8);
        }

        return response;
    }
}

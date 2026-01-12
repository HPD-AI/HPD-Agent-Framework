#pragma warning disable MEAI001 // Microsoft.Extensions.AI is experimental

using HPD.Agent;
using HPD.Agent.Audio;
using HPD.Agent.Audio.ElevenLabs;
using HPD.Agent.Providers.Anthropic;
using HPD.Agent.Providers.AzureAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using AgentConsoleTest;

// Print banner
var logo = @"            ██╗  ██╗██████╗ ██████╗       █████╗  ██████╗ ███████╗███╗   ██╗████████╗
            ██║  ██║██╔══██╗██╔══██╗     ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝
            ███████║██████╔╝██║  ██║█████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   
            ██╔══██║██╔═══╝ ██║  ██║╚════╝██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   
            ██║  ██║██║     ██████╔╝      ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   
            ╚═╝  ╚═╝╚═╝     ╚═════╝       ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝";

AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(logo)}[/]");
AnsiConsole.MarkupLine("[dim]Powered by HPD Agent Framework[/]");
AnsiConsole.WriteLine();

var currentDirectory = Directory.GetCurrentDirectory();
AnsiConsole.MarkupLine($"[dim]Working Directory: {currentDirectory}[/]");

// Load configuration from appsettings.json
var configuration = new ConfigurationBuilder()
    .SetBasePath(currentDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

// Set up session persistence
var sessionsPath = Path.Combine(
    Directory.GetCurrentDirectory(),
    "sessions");
Directory.CreateDirectory(sessionsPath);

var sessionStore = new JsonSessionStore(sessionsPath);

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
    // Toolkits resolved from source-generated registry at Build() time
    Toolkits = new List<ToolkitReference>
    {
        "CodingToolkit",
        "MathToolkit"
    },
    // Provider configuration - Claude 3.5 Sonnet works well with OpenRouter
    Provider = new ProviderConfig
    {
        ProviderKey = "openrouter",
        ModelName = "anthropic/claude-3-5-sonnet",  // Switched from Mistral to Claude - avoids chat_template issue
    }
};
// Configure logging to show Information level logs including HTTP details
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);  // Show debug logs
    builder.AddConsole();
    // Enable HTTP client logging to see exact requests/responses
    builder.AddFilter("System.Net.Http", LogLevel.Debug);
    builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Debug);
    builder.AddFilter("Microsoft.Extensions.AI", LogLevel.Debug);
});



var agentBuilder = new AgentBuilder(config)
    .WithLogging(loggerFactory)
    // Toolkits and Provider now come from config - no need for .WithToolkit<>() or .WithProvider() calls
    .WithSessionStore(sessionStore, persistAfterTurn: true);

// Add audio pipeline if providers are available
var agent = await agentBuilder.Build();

// Generate a unique session ID for this run
var sessionId = $"console-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";
var thread = await agent.LoadSessionAsync(sessionId);

// Initialize UI with slash command support
var ui = new AgentUIRenderer();
ui.SetAgent(agent);

// Prepare context data for commands (sessions browsing, etc.)
var commandContextData = new Dictionary<string, object>
{
    { "SessionsPath", sessionsPath },
    { "Agent", agent },
    { "Configuration", configuration },
    { "OnSessionSwitch", new Func<string, Task>(async (newSessionId) =>
    {
        sessionId = newSessionId;
        thread = await agent.LoadSessionAsync(sessionId);
        AnsiConsole.MarkupLine($"[green]✓ Switched to session:[/] [cyan]{sessionId}[/]");
        AnsiConsole.MarkupLine($"[dim]Messages in session: {thread.Messages.Count}[/]");
    }) }
};

var commandProcessor = new CommandProcessor(ui.CommandRegistry, ui, commandContextData);
var commandInput = new CommandAwareInput(commandProcessor);

AnsiConsole.MarkupLine("[green]✓[/] Agent initialized successfully!");
AnsiConsole.MarkupLine($"[dim]Session ID: {sessionId}[/]");
AnsiConsole.MarkupLine($"[dim]Messages in session: {thread.Messages.Count}[/]");
AnsiConsole.MarkupLine("[dim]Sessions are automatically saved to: " + sessionsPath + "[/]");
AnsiConsole.MarkupLine("[dim]Type /help for commands, or just chat naturally[/]");
AnsiConsole.WriteLine();

while (true)
{
    // Show current directory context
    var cwd = Directory.GetCurrentDirectory();
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (cwd.StartsWith(home))
    {
        cwd = "~" + cwd.Substring(home.Length);
    }

    AnsiConsole.MarkupLine($"[dim]📂 {Markup.Escape(cwd)}[/]");
    
    // Read input - check for slash commands
    var userInput = AnsiConsole.Ask<string>("[bold cyan]You:[/]");

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
            AnsiConsole.MarkupLine("\n[dim]Goodbye! 👋[/]");
            break;
        }
        
        if (result.ShouldClearHistory)
        {
            thread = await agent.LoadSessionAsync(sessionId);
        }
        
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

    // Check if it's a slash command (AFTER custom commands like /quant)
    if (commandProcessor.IsCommand(userInput))
    {
        var result = await commandProcessor.ExecuteAsync(userInput);

        if (result.ShouldExit)
        {
            AnsiConsole.MarkupLine("\n[dim]Goodbye! 👋[/]");
            break;
        }

        if (result.ShouldClearHistory)
        {
            // History cleared, reload thread
            thread = await agent.LoadSessionAsync(sessionId);
        }

        AnsiConsole.WriteLine();
        continue;
    }

    // Regular message - legacy exit commands still work
    if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("\n[dim]Goodbye! 👋[/]");
        break;
    }

    // Stream agent response with real-time rendering using sessionId for auto-save
    await foreach (var evt in agent.RunAsync(userInput, sessionId))
    {
        ui.RenderEvent(evt);
    }

    // Reload thread to show updated message count
    thread = await agent.LoadSessionAsync(sessionId);
    AnsiConsole.MarkupLine($"[dim]💾 Session saved ({thread.Messages.Count} messages)[/]");
    AnsiConsole.WriteLine();
}

// Helper class to log HTTP requests/responses
public class DebugHttpHandler : HttpClientHandler
{
    private readonly ILogger _logger;

    public DebugHttpHandler(ILogger logger)
    {
        _logger = logger;
    }

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

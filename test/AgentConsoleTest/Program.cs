using HPD.Agent;
using Spectre.Console;

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

// Set up session persistence
var sessionsPath = Path.Combine(
    Directory.GetCurrentDirectory(),
    "sessions");
Directory.CreateDirectory(sessionsPath);

var sessionStore = new JsonSessionStore(sessionsPath);

// Create agent configuration - generic base, specialized personas come from plugins
var config = new AgentConfig
{
    Name = "HPD Agent",
    MaxAgenticIterations = 50,
    // Generic base prompt - specialized personas are injected via [Collapse] postExpansionInstructions
    SystemInstructions = @"You are a helpful assistant with access to specialized capabilities.
Use the appropriate tools based on the user's request.
Be concise and direct.",
    Collapsing = new CollapsingConfig { Enabled = true }
};

var agent = await new AgentBuilder(config)
    .WithProvider("openrouter", "nvidia/nemotron-3-nano-30b-a3b")
    .WithTools<CodingPlugin>()
    .WithMiddleware(new EnvironmentContextMiddleware())
    .WithMiddleware(new HPD.Agent.Middleware.ContainerErrorRecoveryMiddleware(null))
    .WithSessionStore(sessionStore, persistAfterTurn: true)
    .WithLogging()
    .Build();

// Generate a unique session ID for this run
var sessionId = $"console-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";
var thread = await agent.LoadSessionAsync(sessionId);

// Initialize UI with slash command support
var ui = new AgentUIRenderer();

// Prepare context data for commands (sessions browsing, etc.)
var commandContextData = new Dictionary<string, object>
{
    { "SessionsPath", sessionsPath },
    { "Agent", agent },
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

    // Check if it's a slash command
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

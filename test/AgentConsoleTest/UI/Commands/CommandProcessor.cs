using System;
using System.Threading.Tasks;
using Spectre.Console;

/// <summary>
/// Processes and executes slash commands.
/// Integrates with the input loop to handle command detection and execution.
/// </summary>
public class CommandProcessor
{
    private readonly CommandRegistry _registry;
    private readonly AgentUIRenderer _renderer;
    private readonly Dictionary<string, object> _contextData;
    
    public CommandProcessor(CommandRegistry registry, AgentUIRenderer renderer, Dictionary<string, object>? contextData = null)
    {
        _registry = registry;
        _renderer = renderer;
        _contextData = contextData ?? new Dictionary<string, object>();
    }
    
    /// <summary>
    /// Check if the input is a slash command.
    /// </summary>
    public bool IsCommand(string input)
    {
        return !string.IsNullOrWhiteSpace(input) && input.TrimStart().StartsWith("/");
    }
    
    /// <summary>
    /// Parse and execute a slash command.
    /// Returns the command result.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(string input)
    {
        input = input.TrimStart();
        
        if (!input.StartsWith("/"))
        {
            return CommandResult.Error("Not a valid command");
        }
        
        // Parse command and arguments
        var parts = input.Substring(1).Split(new[] { ' ' }, 2, StringSplitOptions.None);
        var commandName = parts[0].ToLowerInvariant();
        var arguments = parts.Length > 1 ? parts[1] : "";
        
        // Find the command
        var command = _registry.FindExact(commandName);
        
        if (command == null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown command:[/] [yellow]{Markup.Escape(commandName)}[/]");
            AnsiConsole.MarkupLine("[dim]Type /help to see available commands[/]");
            return CommandResult.Error($"Unknown command: {commandName}");
        }
        
        // Build command context
        var context = new CommandContext
        {
            RawInput = input,
            CommandName = commandName,
            Arguments = arguments,
            UIRenderer = _renderer,
            State = _renderer.StateManager.State,
            Data = new Dictionary<string, object>(_contextData)
        };
        
        // Execute the command
        try
        {
            if (command.Action == null)
            {
                return CommandResult.Error($"Command '{commandName}' has no action defined");
            }
            
            var result = await command.Action(context);
            
            // Display result message if any
            if (!string.IsNullOrEmpty(result.Message))
            {
                if (result.Success)
                {
                    AnsiConsole.MarkupLine($"[green]{Markup.Escape(result.Message)}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Message)}[/]");
                }
            }
            
            return result;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error executing command:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Error($"Command execution failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Get command suggestions for partial input.
    /// Used for autocomplete.
    /// </summary>
    public SuggestionManager CreateSuggestionManager()
    {
        return new SuggestionManager(_registry);
    }
}

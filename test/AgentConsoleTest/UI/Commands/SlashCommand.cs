using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Represents a slash command that can be executed in the console.
/// Inspired by Gemini CLI's command system with auto-execute, descriptions, and aliases.
/// </summary>
public class SlashCommand
{
    /// <summary>Primary command name (without the '/' prefix)</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Alternative names/aliases for the command</summary>
    public List<string> AltNames { get; set; } = new();
    
    /// <summary>Human-readable description shown in help and suggestions</summary>
    public string Description { get; set; } = "";
    
    /// <summary>
    /// If true, pressing Enter on the suggestion executes immediately.
    /// If false, it autocompletes the command name into the input.
    /// </summary>
    public bool AutoExecute { get; set; } = false;
    
    /// <summary>Hide from help and suggestions (for internal commands)</summary>
    public bool Hidden { get; set; } = false;
    
    /// <summary>Category for grouping (Built-in, Toolkit, MCP, etc.)</summary>
    public string Category { get; set; } = "Built-in";
    
    /// <summary>Sub-commands nested under this command</summary>
    public List<SlashCommand>? SubCommands { get; set; }
    
    /// <summary>
    /// Action to execute when the command is invoked.
    /// Receives the command context and returns an awaitable task.
    /// </summary>
    public Func<CommandContext, Task<CommandResult>>? Action { get; set; }
    
    /// <summary>
    /// Argument completion provider.
    /// Returns completion suggestions for partial arguments.
    /// </summary>
    public Func<CommandContext, string, Task<List<string>>>? ArgumentCompletion { get; set; }
    
    /// <summary>
    /// Check if this command matches a query (name or any alias).
    /// Case-insensitive.
    /// </summary>
    public bool Matches(string query)
    {
        query = query.ToLowerInvariant();
        
        if (Name.ToLowerInvariant() == query)
            return true;
            
        return AltNames.Exists(alt => alt.ToLowerInvariant() == query);
    }
    
    /// <summary>
    /// Get all searchable terms for fuzzy matching.
    /// </summary>
    public List<string> GetSearchableTerms()
    {
        var terms = new List<string> { Name };
        terms.AddRange(AltNames);
        return terms;
    }
}

/// <summary>
/// Context passed to command actions.
/// Provides access to UI, configuration, and state.
/// </summary>
public class CommandContext
{
    /// <summary>The raw input string from the user</summary>
    public string RawInput { get; set; } = "";
    
    /// <summary>The matched command name</summary>
    public string CommandName { get; set; } = "";
    
    /// <summary>Arguments string following the command</summary>
    public string Arguments { get; set; } = "";
    
    /// <summary>UI renderer for displaying output</summary>
    public AgentUIRenderer? UIRenderer { get; set; }
    
    /// <summary>Session state and statistics</summary>
    public UIState? State { get; set; }
    
    /// <summary>Additional context data</summary>
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// Result returned from command execution.
/// </summary>
public class CommandResult
{
    /// <summary>Success or failure</summary>
    public bool Success { get; set; } = true;

    /// <summary>Message to display to user</summary>
    public string? Message { get; set; }

    /// <summary>Should the application exit?</summary>
    public bool ShouldExit { get; set; } = false;

    /// <summary>Should the conversation be cleared?</summary>
    public bool ShouldClearHistory { get; set; } = false;

    /// <summary>Should the model/provider be switched?</summary>
    public bool ShouldSwitchModel { get; set; } = false;

    /// <summary>Additional result data</summary>
    public Dictionary<string, object> Data { get; set; } = new();

    public static CommandResult Ok(string? message = null) => new() { Success = true, Message = message };
    public static CommandResult Error(string message) => new() { Success = false, Message = message };
    public static CommandResult Exit(string? message = null) => new() { Success = true, ShouldExit = true, Message = message };
}

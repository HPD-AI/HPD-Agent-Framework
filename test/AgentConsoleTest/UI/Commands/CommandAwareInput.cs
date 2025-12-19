using Spectre.Console;
using System;
using System.Text;

/// <summary>
/// Enhanced input handler with command autocomplete support.
/// Shows suggestions as user types slash commands.
/// </summary>
public class CommandAwareInput
{
    private readonly CommandProcessor _processor;
    private readonly SuggestionManager _suggestions;
    private bool _showingSuggestions = false;
    
    public CommandAwareInput(CommandProcessor processor)
    {
        _processor = processor;
        _suggestions = processor.CreateSuggestionManager();
    }
    
    /// <summary>
    /// Read a line of input with command autocomplete support.
    /// Returns the complete input string or null if cancelled.
    /// </summary>
    public string? ReadLine(string prompt = "> ")
    {
        var input = new StringBuilder();
        int cursorPos = 0;
        
        AnsiConsole.Markup($"[cyan]{prompt}[/]");
        var startX = Console.CursorLeft;
        var startY = Console.CursorTop;
        
        while (true)
        {
            // Update suggestions if input starts with /
            var currentInput = input.ToString();
            if (currentInput.StartsWith("/"))
            {
                _suggestions.UpdateQuery(currentInput);
                _showingSuggestions = _suggestions.HasSuggestions;
            }
            else
            {
                if (_showingSuggestions)
                {
                    _suggestions.Clear();
                    _showingSuggestions = false;
                    // Clear suggestion area (simplified - in production would track exact area)
                }
            }
            
            // Display suggestions if available
            if (_showingSuggestions && Console.CursorTop < Console.WindowHeight - 10)
            {
                // Save cursor position
                var savedX = Console.CursorLeft;
                var savedY = Console.CursorTop;
                
                // Move below input line
                Console.SetCursorPosition(0, savedY + 1);
                
                // Render suggestions
                var rendered = _suggestions.Render(Console.WindowWidth - 4);
                AnsiConsole.Write(rendered);
                
                // Restore cursor position
                Console.SetCursorPosition(savedX, savedY);
            }
            
            var key = Console.ReadKey(intercept: true);
            
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    
                    // If showing suggestions and user presses Enter, complete with selected
                    if (_showingSuggestions)
                    {
                        var selected = _suggestions.GetSelected();
                        if (selected != null && selected.Command.AutoExecute)
                        {
                            // Auto-execute command
                            return "/" + selected.DisplayName;
                        }
                        else if (selected != null)
                        {
                            // Autocomplete the command name
                            var completedText = _suggestions.GetCompletedText();
                            if (completedText != null)
                            {
                                input.Clear();
                                input.Append(completedText);
                                input.Append(" "); // Add space for arguments
                                cursorPos = input.Length;
                                
                                // Clear suggestions and continue editing
                                _suggestions.Clear();
                                _showingSuggestions = false;
                                
                                // Redraw input
                                Console.SetCursorPosition(startX, startY);
                                AnsiConsole.Markup($"[white]{Markup.Escape(input.ToString())}[/]");
                                continue;
                            }
                        }
                    }
                    
                    return input.ToString();
                    
                case ConsoleKey.Backspace:
                    if (cursorPos > 0)
                    {
                        input.Remove(cursorPos - 1, 1);
                        cursorPos--;
                        
                        // Redraw line
                        Console.SetCursorPosition(startX, startY);
                        AnsiConsole.Markup($"[white]{Markup.Escape(input.ToString())} [/]"); // Extra space to clear
                        Console.SetCursorPosition(startX + cursorPos, startY);
                    }
                    break;
                    
                case ConsoleKey.LeftArrow:
                    if (cursorPos > 0)
                    {
                        cursorPos--;
                        Console.SetCursorPosition(startX + cursorPos, startY);
                    }
                    break;
                    
                case ConsoleKey.RightArrow:
                    if (cursorPos < input.Length)
                    {
                        cursorPos++;
                        Console.SetCursorPosition(startX + cursorPos, startY);
                    }
                    break;
                    
                case ConsoleKey.UpArrow:
                    if (_showingSuggestions)
                    {
                        _suggestions.NavigateUp();
                        // Suggestions will be redrawn on next iteration
                    }
                    break;
                    
                case ConsoleKey.DownArrow:
                    if (_showingSuggestions)
                    {
                        _suggestions.NavigateDown();
                        // Suggestions will be redrawn on next iteration
                    }
                    break;
                    
                case ConsoleKey.Tab:
                    // Tab completes the current suggestion
                    if (_showingSuggestions)
                    {
                        var completedText = _suggestions.GetCompletedText();
                        if (completedText != null)
                        {
                            input.Clear();
                            input.Append(completedText);
                            input.Append(" "); // Add space for arguments
                            cursorPos = input.Length;
                            
                            // Clear suggestions
                            _suggestions.Clear();
                            _showingSuggestions = false;
                            
                            // Redraw input
                            Console.SetCursorPosition(startX, startY);
                            AnsiConsole.Markup($"[white]{Markup.Escape(input.ToString())}[/]");
                        }
                    }
                    break;
                    
                case ConsoleKey.Escape:
                    // Clear suggestions on Escape
                    if (_showingSuggestions)
                    {
                        _suggestions.Clear();
                        _showingSuggestions = false;
                    }
                    break;
                    
                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    // Ctrl+C - cancel
                    Console.WriteLine();
                    return null;
                    
                default:
                    // Regular character input
                    if (!char.IsControl(key.KeyChar))
                    {
                        input.Insert(cursorPos, key.KeyChar);
                        cursorPos++;
                        
                        // Redraw line
                        Console.SetCursorPosition(startX, startY);
                        AnsiConsole.Markup($"[white]{Markup.Escape(input.ToString())}[/]");
                        Console.SetCursorPosition(startX + cursorPos, startY);
                    }
                    break;
            }
        }
    }
}

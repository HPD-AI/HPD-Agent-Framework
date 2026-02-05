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
        var windowWidth = Console.WindowWidth;

        // Helper to calculate cursor position accounting for line wrapping
        void SetCursorForPosition(int position)
        {
            var totalOffset = startX + position;
            var line = startY + (totalOffset / windowWidth);
            var col = totalOffset % windowWidth;
            Console.SetCursorPosition(col, line);
        }

        // Track previous input length to clear properly
        int previousLength = 0;
        int previousSuggestionLines = 0;

        // Helper to clear suggestion area
        void ClearSuggestionArea()
        {
            if (previousSuggestionLines > 0)
            {
                var savedX = Console.CursorLeft;
                var savedY = Console.CursorTop;

                var inputLines = ((startX + input.Length) / windowWidth) + 1;
                for (int i = 0; i < previousSuggestionLines; i++)
                {
                    var lineY = startY + inputLines + i;
                    if (lineY < Console.WindowHeight)
                    {
                        Console.SetCursorPosition(0, lineY);
                        Console.Write(new string(' ', windowWidth));
                    }
                }

                Console.SetCursorPosition(savedX, savedY);
            }
        }

        // Helper to redraw input text (handles line wrapping)
        void RedrawInput()
        {
            // Calculate how many lines the previous input occupied
            var previousTotalChars = startX + previousLength;
            var previousLines = (previousTotalChars / windowWidth) + 1;

            // Clear all lines that could have content
            for (int line = 0; line < previousLines; line++)
            {
                var lineY = startY + line;
                if (lineY < Console.WindowHeight)
                {
                    var clearStart = (line == 0) ? startX : 0;
                    Console.SetCursorPosition(clearStart, lineY);
                    Console.Write(new string(' ', windowWidth - clearStart));
                }
            }

            // Go back to start position and write current input
            Console.SetCursorPosition(startX, startY);
            Console.Write(input.ToString());

            // Update previous length
            previousLength = input.Length;

            // Set cursor to correct position
            SetCursorForPosition(cursorPos);
        }

        // Helper to update and render suggestions
        void UpdateSuggestions()
        {
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
                }
            }

            // Display suggestions if available
            if (_showingSuggestions && Console.CursorTop < Console.WindowHeight - 10)
            {
                // Save cursor position
                var savedX = Console.CursorLeft;
                var savedY = Console.CursorTop;

                // Clear previous suggestion area first
                ClearSuggestionArea();

                // Calculate how many lines input occupies
                var inputLines = ((startX + input.Length) / windowWidth) + 1;

                // Move below input line(s)
                Console.SetCursorPosition(0, startY + inputLines);

                // Render suggestions
                var rendered = _suggestions.Render(Console.WindowWidth - 4);
                var beforeRenderY = Console.CursorTop;
                AnsiConsole.Write(rendered);
                var afterRenderY = Console.CursorTop;

                // Track how many lines were used for suggestions
                previousSuggestionLines = afterRenderY - beforeRenderY + 1;

                // Restore cursor position
                Console.SetCursorPosition(savedX, savedY);
            }
            else if (previousSuggestionLines > 0)
            {
                // Clear old suggestions when no longer showing
                ClearSuggestionArea();
                previousSuggestionLines = 0;
            }
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();

                    // If showing suggestions and user presses Enter
                    if (_showingSuggestions)
                    {
                        var selected = _suggestions.GetSelected();
                        if (selected != null)
                        {
                            var completedText = _suggestions.GetCompletedText();
                            var currentText = input.ToString().TrimEnd();

                            // If user already typed the full command, execute it
                            // (e.g., typed "/sessions" and "sessions" is selected)
                            if (completedText != null && currentText.Equals(completedText, StringComparison.OrdinalIgnoreCase))
                            {
                                return currentText;
                            }

                            // Auto-execute commands that don't need arguments
                            if (selected.Command.AutoExecute)
                            {
                                return "/" + selected.DisplayName;
                            }

                            // Otherwise, autocomplete and wait for more input
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
                                RedrawInput();
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

                        // If deleting at end of input, just erase one character
                        if (cursorPos == input.Length)
                        {
                            // Move back, write space, move back again
                            Console.Write("\b \b");
                            previousLength = input.Length;
                        }
                        else
                        {
                            // Deleting in middle - need full redraw
                            RedrawInput();
                        }

                        // Only update suggestions for slash commands
                        if (input.Length > 0 && input[0] == '/')
                        {
                            UpdateSuggestions();
                        }
                        else if (_showingSuggestions)
                        {
                            // Clear suggestions if we backspaced out of a slash command
                            _suggestions.Clear();
                            _showingSuggestions = false;
                            UpdateSuggestions();
                        }
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursorPos > 0)
                    {
                        cursorPos--;
                        SetCursorForPosition(cursorPos);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorPos < input.Length)
                    {
                        cursorPos++;
                        SetCursorForPosition(cursorPos);
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (_showingSuggestions)
                    {
                        _suggestions.NavigateUp();
                        UpdateSuggestions();
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (_showingSuggestions)
                    {
                        _suggestions.NavigateDown();
                        UpdateSuggestions();
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
                            RedrawInput();
                        }
                    }
                    break;

                case ConsoleKey.Escape:
                    // Clear suggestions on Escape
                    if (_showingSuggestions)
                    {
                        _suggestions.Clear();
                        _showingSuggestions = false;
                        UpdateSuggestions(); // Clear the display
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
                        if (cursorPos == input.Length)
                        {
                            // Appending at end - just write the character directly
                            input.Append(key.KeyChar);
                            cursorPos++;
                            previousLength = input.Length;
                            Console.Write(key.KeyChar);
                        }
                        else
                        {
                            // Inserting in middle - need full redraw
                            input.Insert(cursorPos, key.KeyChar);
                            cursorPos++;
                            RedrawInput();
                        }
                        // Only update suggestions for slash commands
                        if (input.Length > 0 && input[0] == '/')
                        {
                            UpdateSuggestions();
                        }
                    }
                    break;
            }
        }
    }
}

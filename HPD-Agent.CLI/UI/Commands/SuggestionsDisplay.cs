using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Displays command suggestions with fuzzy match highlighting.
/// Inspired by Gemini CLI's SuggestionsDisplay with keyboard navigation.
/// </summary>
public class SuggestionsDisplay : UIComponent
{
    public List<CommandSuggestion> Suggestions { get; set; } = new();
    public int ActiveIndex { get; set; } = 0;
    public int MaxVisible { get; set; } = 8;
    public int Width { get; set; } = 60;
    public string Query { get; set; } = "";
    
    public override IRenderable Render()
    {
        if (Suggestions.Count == 0)
            return new Text("");
        
        var rows = new List<IRenderable>();
        
        // Calculate scroll window
        int scrollOffset = Math.Max(0, ActiveIndex - MaxVisible + 1);
        if (ActiveIndex < scrollOffset)
            scrollOffset = ActiveIndex;
        
        int endIndex = Math.Min(scrollOffset + MaxVisible, Suggestions.Count);
        var visibleSuggestions = Suggestions.Skip(scrollOffset).Take(endIndex - scrollOffset).ToList();
        
        // Show scroll indicator at top if needed
        if (scrollOffset > 0)
        {
            rows.Add(new Markup($"[{Theme.Text.Muted}]▲ ({scrollOffset} more above)[/]"));
        }
        
        // Render each suggestion
        for (int i = 0; i < visibleSuggestions.Count; i++)
        {
            var originalIndex = scrollOffset + i;
            var suggestion = visibleSuggestions[i];
            var isActive = originalIndex == ActiveIndex;
            
            rows.Add(RenderSuggestion(suggestion, isActive));
        }
        
        // Show scroll indicator at bottom if needed
        if (endIndex < Suggestions.Count)
        {
            var remaining = Suggestions.Count - endIndex;
            rows.Add(new Markup($"[{Theme.Text.Muted}]▼ ({remaining} more below)[/]"));
        }
        
        // Show position indicator
        if (Suggestions.Count > MaxVisible)
        {
            rows.Add(new Markup($"[{Theme.Text.Muted}]({ActiveIndex + 1}/{Suggestions.Count})[/]"));
        }
        
        return new Panel(new Rows(rows))
            .Header("[yellow]Commands[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Padding(1, 0);
    }
    
    private IRenderable RenderSuggestion(CommandSuggestion suggestion, bool isActive)
    {
        var color = isActive ? Theme.Text.Accent : Theme.Text.Secondary;
        var bgMark = isActive ? "[on grey23]" : "";
        var bgEnd = isActive ? "[/]" : "";
        
        // Build command name with highlighted matched characters
        var displayName = suggestion.DisplayName;
        var highlighted = HighlightMatches(displayName, suggestion.MatchedIndices, color);
        
        // Add category badge if not Built-in
        var categoryBadge = suggestion.Command.Category != "Built-in" 
            ? $" [{Theme.Text.Muted}][{suggestion.Command.Category}][/]" 
            : "";
        
        // Format: [prefix] /command [category] - description
        var prefix = isActive ? "●" : "○";
        var commandPart = $"[{color}]{prefix}[/] [{color}]/{highlighted}[/]{categoryBadge}";
        
        // Add description if available
        if (!string.IsNullOrEmpty(suggestion.Command.Description))
        {
            var desc = suggestion.Command.Description;
            if (desc.Length > 50)
                desc = desc.Substring(0, 47) + "...";
            
            commandPart += $" [{Theme.Text.Muted}]- {Markup.Escape(desc)}[/]";
        }
        
        return new Markup($"{bgMark}{commandPart}{bgEnd}");
    }
    
    /// <summary>
    /// Highlight matched characters in the display name.
    /// </summary>
    private string HighlightMatches(string text, List<int> matchedIndices, Color baseColor)
    {
        if (matchedIndices.Count == 0)
            return Markup.Escape(text);
        
        var result = "";
        var highlightColor = Color.Yellow;
        
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var escaped = Markup.Escape(ch.ToString());
            
            if (matchedIndices.Contains(i))
            {
                // Highlighted character
                result += $"[bold {highlightColor}]{escaped}[/]";
            }
            else
            {
                // Normal character
                result += $"[{baseColor}]{escaped}[/]";
            }
        }
        
        return result;
    }
}

/// <summary>
/// Manages command suggestion state and keyboard navigation.
/// </summary>
public class SuggestionManager
{
    private readonly CommandRegistry _registry;
    private List<CommandSuggestion> _currentSuggestions = new();
    private int _activeIndex = 0;
    private string _currentQuery = "";
    
    public bool HasSuggestions => _currentSuggestions.Count > 0;
    public int ActiveIndex => _activeIndex;
    public List<CommandSuggestion> Suggestions => _currentSuggestions;
    
    public SuggestionManager(CommandRegistry registry)
    {
        _registry = registry;
    }
    
    /// <summary>
    /// Update suggestions based on user input.
    /// </summary>
    public void UpdateQuery(string query)
    {
        query = query.TrimStart('/');
        _currentQuery = query;
        
        _currentSuggestions = _registry.FindSuggestions(query, maxResults: 20);
        _activeIndex = _currentSuggestions.Count > 0 ? 0 : -1;
    }
    
    /// <summary>
    /// Clear all suggestions.
    /// </summary>
    public void Clear()
    {
        _currentSuggestions.Clear();
        _activeIndex = -1;
        _currentQuery = "";
    }
    
    /// <summary>
    /// Navigate up in the suggestion list.
    /// </summary>
    public void NavigateUp()
    {
        if (_currentSuggestions.Count == 0)
            return;
            
        _activeIndex--;
        if (_activeIndex < 0)
            _activeIndex = _currentSuggestions.Count - 1; // Wrap to bottom
    }
    
    /// <summary>
    /// Navigate down in the suggestion list.
    /// </summary>
    public void NavigateDown()
    {
        if (_currentSuggestions.Count == 0)
            return;
            
        _activeIndex++;
        if (_activeIndex >= _currentSuggestions.Count)
            _activeIndex = 0; // Wrap to top
    }
    
    /// <summary>
    /// Get the currently selected suggestion.
    /// </summary>
    public CommandSuggestion? GetSelected()
    {
        if (_activeIndex < 0 || _activeIndex >= _currentSuggestions.Count)
            return null;
            
        return _currentSuggestions[_activeIndex];
    }
    
    /// <summary>
    /// Get the completed command text for the selected suggestion.
    /// </summary>
    public string? GetCompletedText()
    {
        var selected = GetSelected();
        if (selected == null)
            return null;
            
        // If command has no arguments or auto-executes, return just the command
        return "/" + selected.DisplayName;
    }
    
    /// <summary>
    /// Render the suggestions display.
    /// </summary>
    public IRenderable Render(int width = 60)
    {
        if (_currentSuggestions.Count == 0)
            return new Text("");
        
        var display = new SuggestionsDisplay
        {
            Suggestions = _currentSuggestions,
            ActiveIndex = _activeIndex,
            Width = width,
            Query = _currentQuery
        };
        
        return display.Render();
    }
}

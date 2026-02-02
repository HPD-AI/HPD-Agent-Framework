using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Registry for all slash commands.
/// Provides command lookup, fuzzy matching, and suggestion generation.
/// </summary>
public class CommandRegistry
{
    private readonly List<SlashCommand> _commands = new();
    
    /// <summary>
    /// Register a new command.
    /// </summary>
    public void Register(SlashCommand command)
    {
        _commands.Add(command);
    }
    
    /// <summary>
    /// Register multiple commands at once.
    /// </summary>
    public void RegisterMany(params SlashCommand[] commands)
    {
        _commands.AddRange(commands);
    }
    
    /// <summary>
    /// Get all non-hidden commands.
    /// </summary>
    public List<SlashCommand> GetVisibleCommands()
    {
        return _commands.Where(c => !c.Hidden).ToList();
    }
    
    /// <summary>
    /// Get all commands (including hidden).
    /// </summary>
    public List<SlashCommand> GetAllCommands()
    {
        return new List<SlashCommand>(_commands);
    }
    
    /// <summary>
    /// Find a command by exact name or alias (case-insensitive).
    /// </summary>
    public SlashCommand? FindExact(string name)
    {
        name = name.ToLowerInvariant().TrimStart('/');
        return _commands.FirstOrDefault(c => c.Matches(name));
    }
    
    /// <summary>
    /// Find commands matching a query using fuzzy search.
    /// Returns commands sorted by relevance.
    /// </summary>
    public List<CommandSuggestion> FindSuggestions(string query, int maxResults = 10)
    {
        query = query.ToLowerInvariant().TrimStart('/');
        
        if (string.IsNullOrWhiteSpace(query))
        {
            // No query - return all visible commands alphabetically
            return GetVisibleCommands()
                .OrderBy(c => c.Name)
                .Take(maxResults)
                .Select(c => new CommandSuggestion
                {
                    Command = c,
                    MatchScore = 100,
                    DisplayName = c.Name,
                    MatchedIndices = new List<int>()
                })
                .ToList();
        }
        
        var suggestions = new List<CommandSuggestion>();
        
        foreach (var command in GetVisibleCommands())
        {
            // Try matching against name
            var nameMatch = FuzzyMatch(command.Name.ToLowerInvariant(), query);
            if (nameMatch != null)
            {
                suggestions.Add(new CommandSuggestion
                {
                    Command = command,
                    MatchScore = nameMatch.Score,
                    DisplayName = command.Name,
                    MatchedIndices = nameMatch.MatchedIndices
                });
                continue;
            }
            
            // Try matching against aliases
            foreach (var alias in command.AltNames)
            {
                var aliasMatch = FuzzyMatch(alias.ToLowerInvariant(), query);
                if (aliasMatch != null)
                {
                    suggestions.Add(new CommandSuggestion
                    {
                        Command = command,
                        MatchScore = aliasMatch.Score,
                        DisplayName = alias,
                        MatchedIndices = aliasMatch.MatchedIndices
                    });
                    break;
                }
            }
        }
        
        // Sort by score (descending) then by name
        return suggestions
            .OrderByDescending(s => s.MatchScore)
            .ThenBy(s => s.DisplayName)
            .Take(maxResults)
            .ToList();
    }
    
    /// <summary>
    /// Simple fuzzy matching algorithm.
    /// Returns match score and positions of matched characters.
    /// Inspired by FZF but simplified for our needs.
    /// </summary>
    private FuzzyMatchResult? FuzzyMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
            return null;
            
        var matchedIndices = new List<int>();
        int textIndex = 0;
        int queryIndex = 0;
        int score = 0;
        int consecutiveMatches = 0;
        
        while (textIndex < text.Length && queryIndex < query.Length)
        {
            if (text[textIndex] == query[queryIndex])
            {
                matchedIndices.Add(textIndex);
                
                // Bonus for consecutive matches
                consecutiveMatches++;
                score += 10 + (consecutiveMatches * 5);
                
                // Bonus for matching at word boundaries
                if (textIndex == 0 || text[textIndex - 1] == '-' || text[textIndex - 1] == '_')
                {
                    score += 15;
                }
                
                queryIndex++;
            }
            else
            {
                consecutiveMatches = 0;
            }
            
            textIndex++;
        }
        
        // All query characters must be matched
        if (queryIndex != query.Length)
            return null;
        
        // Bonus for shorter text (more precise match)
        score += (100 - text.Length);
        
        // Penalty for gaps between matches
        if (matchedIndices.Count > 1)
        {
            int totalGap = matchedIndices[^1] - matchedIndices[0] - matchedIndices.Count + 1;
            score -= totalGap * 2;
        }
        
        return new FuzzyMatchResult
        {
            Score = Math.Max(0, score),
            MatchedIndices = matchedIndices
        };
    }
    
    private class FuzzyMatchResult
    {
        public int Score { get; set; }
        public List<int> MatchedIndices { get; set; } = new();
    }
}

/// <summary>
/// A command suggestion with match information.
/// </summary>
public class CommandSuggestion
{
    /// <summary>The matched command</summary>
    public SlashCommand Command { get; set; } = null!;
    
    /// <summary>Match relevance score (higher is better)</summary>
    public int MatchScore { get; set; }
    
    /// <summary>Display name (might be an alias)</summary>
    public string DisplayName { get; set; } = "";
    
    /// <summary>Character indices that matched the query (for highlighting)</summary>
    public List<int> MatchedIndices { get; set; } = new();
}

using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;

/// <summary>
/// Example filter demonstrating post-invocation memory extraction.
/// This filter extracts important information from assistant responses
/// and stores them as memories for future conversations.
/// </summary>
public class ExampleMemoryExtractionFilter : IPromptFilter
{
    private readonly DynamicMemoryStore _store;
    private readonly string _agentName;

    public ExampleMemoryExtractionFilter(DynamicMemoryStore store, string agentName)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
    }

    // Pre-processing: This filter doesn't modify the request
    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        // Just pass through - no pre-processing needed
        return await next(context);
    }

    // Post-processing: Extract memories from assistant response
    public async Task PostInvokeAsync(PostInvokeContext context, CancellationToken cancellationToken)
    {
        // Only process successful responses
        if (!context.IsSuccess || context.ResponseMessages == null)
        {
            return;
        }

        // Extract assistant messages
        var assistantMessages = context.ResponseMessages
            .Where(m => m.Role == ChatRole.Assistant)
            .ToList();

        foreach (var message in assistantMessages)
        {
            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Example: Extract facts marked with [REMEMBER: ...]
            var rememberedFacts = ExtractRememberTags(text);
            foreach (var fact in rememberedFacts)
            {
                await _store.CreateMemoryAsync(
                    _agentName,
                    title: $"Fact from {DateTime.UtcNow:yyyy-MM-dd}",
                    content: fact,
                    cancellationToken);
            }

            // Example: Extract user preferences
            var preferences = ExtractPreferences(text, context.RequestMessages);
            foreach (var preference in preferences)
            {
                await _store.CreateMemoryAsync(
                    _agentName,
                    title: "User Preference",
                    content: preference,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Extracts content from [REMEMBER: ...] tags in assistant response.
    /// </summary>
    private List<string> ExtractRememberTags(string text)
    {
        var facts = new List<string>();
        var regex = new Regex(@"\[REMEMBER:\s*(.+?)\]", RegexOptions.Singleline);
        var matches = regex.Matches(text);

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                facts.Add(match.Groups[1].Value.Trim());
            }
        }

        return facts;
    }

    /// <summary>
    /// Extracts user preferences from conversation context.
    /// Example: "I prefer", "I like", "I always", etc.
    /// </summary>
    private List<string> ExtractPreferences(string assistantText, IEnumerable<ChatMessage> requestMessages)
    {
        var preferences = new List<string>();

        // Look for user messages that express preferences
        var userMessages = requestMessages.Where(m => m.Role == ChatRole.User);
        foreach (var userMsg in userMessages)
        {
            var text = userMsg.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Simple pattern matching for preference statements
            var preferencePatterns = new[]
            {
                @"I prefer (.+?)(?:\.|$)",
                @"I like (.+?)(?:\.|$)",
                @"I always (.+?)(?:\.|$)",
                @"My favorite (.+?)(?:\.|$)"
            };

            foreach (var pattern in preferencePatterns)
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                var match = regex.Match(text);
                if (match.Success && match.Groups.Count > 1)
                {
                    preferences.Add($"User {match.Groups[0].Value.Trim()}");
                }
            }
        }

        return preferences;
    }
}

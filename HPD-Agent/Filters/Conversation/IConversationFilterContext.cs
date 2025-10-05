using Microsoft.Extensions.AI;

/// <summary>
/// Context provided to message turn filters containing completed turn information.
/// Rich context enables applications to make intelligent checkpoint decisions.
/// </summary>
public class MessageTurnFilterContext
{
    // Core data
    public string ConversationId { get; }
    public ChatMessage UserMessage { get; }
    public ChatResponse AgentResponse { get; }
    public ChatOptions? Options { get; }
    public CancellationToken CancellationToken { get; }

    // Function call tracking (core data)
    public Dictionary<string, List<string>> AgentFunctionCalls { get; }

    // Computed properties (no need to calculate in constructor)
    public List<string> AllFunctionCallsUsed => AgentFunctionCalls.Values.SelectMany(x => x).ToList();
    public bool HadFunctionCalls => AgentFunctionCalls.Any(kvp => kvp.Value.Any());
    public int ToolCallCount => AllFunctionCallsUsed.Count;

    // Extensibility
    public Dictionary<string, object> Properties { get; }

    public MessageTurnFilterContext(
        string conversationId,
        ChatMessage userMessage,
        ChatResponse agentResponse,
        Dictionary<string, List<string>>? agentFunctionCalls = null,
        Dictionary<string, object>? metadata = null,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
        UserMessage = userMessage ?? throw new ArgumentNullException(nameof(userMessage));
        AgentResponse = agentResponse ?? throw new ArgumentNullException(nameof(agentResponse));
        AgentFunctionCalls = agentFunctionCalls ?? new Dictionary<string, List<string>>();
        Options = options;
        CancellationToken = cancellationToken;
        Properties = new Dictionary<string, object>();

        // Copy metadata to Properties for extensibility
        if (metadata != null)
        {
            foreach (var kvp in metadata)
            {
                Properties[kvp.Key] = kvp.Value;
            }
        }
    }

    // Convenience methods
    public bool AgentUsedFunction(string agentName, string functionName)
        => AgentFunctionCalls.ContainsKey(agentName) && AgentFunctionCalls[agentName].Contains(functionName);

    public bool AnyAgentUsedFunction(string functionName)
        => AllFunctionCallsUsed.Contains(functionName);

    public IEnumerable<string> GetAgentsThatUsedFunction(string functionName)
        => AgentFunctionCalls.Where(kvp => kvp.Value.Contains(functionName)).Select(kvp => kvp.Key);

    public string GetUserText() => UserMessage.Contents.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
    public string GetAgentText() => string.Join(" ", AgentResponse.Messages.SelectMany(m => m.Contents.OfType<TextContent>()).Select(t => t.Text));
}
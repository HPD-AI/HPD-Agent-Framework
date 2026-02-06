using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;

namespace HPD.Agent;

/// <summary>
/// Branch represents a conversation path within a session.
/// Contains messages and branch-specific state.
/// Multiple branches can exist in one session (for exploring alternatives).
/// </summary>
/// <remarks>
/// <para><b>Mental Model:</b></para>
/// <para>
/// Think of branches like ChatGPT's message editing feature:
/// - User edits a message → creates a new branch from that point
/// - Each branch is an independent conversation path
/// - All branches share the same session (metadata, assets, session-scoped state)
/// </para>
///
/// <para><b>Relationship to Session:</b></para>
/// <para>
/// Branch belongs to a Session (via SessionId).
/// Multiple branches can exist in one session, all sharing:
/// - Session metadata
/// - Session assets (uploaded files)
/// - Session-scoped middleware state (permissions, preferences)
/// </para>
///
/// <para><b>Branch-Scoped vs Session-Scoped:</b></para>
/// <list type="bullet">
/// <item><b>Branch-scoped:</b> Messages, plan progress, history cache (diverges per branch)</item>
/// <item><b>Session-scoped:</b> Permissions, assets, user preferences (shared across branches)</item>
/// </list>
/// </remarks>
public class Branch
{
    /// <summary>Unique identifier for this branch</summary>
    public string Id { get; init; }

    /// <summary>Parent session ID</summary>
    public string SessionId { get; init; }

    /// <summary>
    /// Back-reference to the parent Session.
    /// Set by Session.CreateBranch() and by the framework when loading from store.
    /// Not serialized — reconstructed at load time.
    /// </summary>
    [JsonIgnore]
    public Session? Session { get; internal set; }

    /// <summary>Conversation messages in this branch</summary>
    public List<ChatMessage> Messages { get; init; }

    /// <summary>Source branch ID if this was forked (null for original branches)</summary>
    public string? ForkedFrom { get; init; }

    /// <summary>Message index where fork occurred (null for original branches)</summary>
    public int? ForkedAtMessageIndex { get; init; }

    /// <summary>When this branch was created</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Last time this branch was updated</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// Optional user-friendly description of this branch.
    /// Useful for explaining the purpose or approach of this conversation variant.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional tags for categorizing or filtering branches.
    /// Examples: ["draft", "formal-tone"], ["v1", "experiment"]
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Full ancestry chain for multi-level fork tracking.
    /// Key: depth (0 = root), Value: branch ID at that depth.
    /// Example: { "0": "main", "1": "experimental", "2": "formal" }
    /// Enables UI to show "main → experimental → formal" lineage.
    /// </summary>
    public Dictionary<string, string>? Ancestors { get; set; }

    /// <summary>
    /// Branch-scoped middleware persistent state.
    /// Stores state tied to this specific conversation path (e.g., plan progress, summarization cache).
    /// Only middleware marked with [MiddlewareState(Persistent = true, Scope = StateScope.Branch)]
    /// (or just [MiddlewareState(Persistent = true)] since Branch is the default) is persisted here.
    /// Session-scoped state (e.g., permissions) lives in Session.MiddlewareState instead.
    /// </summary>
    /// <remarks>
    /// <para><b>Examples of branch-scoped persistent state:</b></para>
    /// <list type="bullet">
    /// <item>PlanModePersistentState: Current plan steps and progress</item>
    /// <item>HistoryReductionState: Conversation summarization cache</item>
    /// </list>
    ///
    /// <para>
    /// State is serialized as JSON and saved per branch because different branches
    /// have different conversation contexts (different messages → different caches/progress).
    /// </para>
    ///
    /// <para><b>On fork:</b> Branch middleware state is COPIED from the source branch.</para>
    /// <para><b>After fork:</b> Each branch maintains its own copy and can diverge independently.</para>
    /// </remarks>
    public Dictionary<string, string> MiddlewareState { get; init; }

    /// <summary>Current execution state (for crash recovery, null when idle)</summary>
    [JsonIgnore]
    public AgentLoopState? ExecutionState { get; set; }

    /// <summary>
    /// Parameterless constructor for JSON deserialization.
    /// Properties are populated via init setters.
    /// </summary>
    [JsonConstructor]
    internal Branch()
    {
        Id = Guid.NewGuid().ToString();
        SessionId = string.Empty;
        Messages = [];
        MiddlewareState = [];
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new branch with a generated ID.
    /// </summary>
    internal Branch(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Id = Guid.NewGuid().ToString();
        SessionId = sessionId;
        Messages = [];
        MiddlewareState = [];
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new branch with a specific ID.
    /// </summary>
    internal Branch(string sessionId, string branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        Id = branchId;
        SessionId = sessionId;
        Messages = [];
        MiddlewareState = [];
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a branch with specific values (for deserialization).
    /// </summary>
    internal Branch(
        string id,
        string sessionId,
        List<ChatMessage> messages,
        string? forkedFrom,
        int? forkedAtMessageIndex,
        DateTime createdAt,
        DateTime lastActivity,
        string? description,
        List<string>? tags,
        Dictionary<string, string>? ancestors,
        Dictionary<string, string> middlewareState)
    {
        Id = id;
        SessionId = sessionId;
        Messages = messages;
        ForkedFrom = forkedFrom;
        ForkedAtMessageIndex = forkedAtMessageIndex;
        CreatedAt = createdAt;
        LastActivity = lastActivity;
        Description = description;
        Tags = tags;
        Ancestors = ancestors;
        MiddlewareState = middlewareState;
    }

    /// <summary>
    /// Gets the number of messages in this branch.
    /// </summary>
    public int MessageCount => Messages.Count;

    /// <summary>
    /// Adds a message to the branch.
    /// </summary>
    public void AddMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Messages.Add(message);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds multiple messages to the branch.
    /// </summary>
    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Messages.AddRange(messages);
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Sets branch-scoped middleware persistent state for a given key.
    /// </summary>
    internal void SetMiddlewareState(string key, string jsonValue)
    {
        MiddlewareState[key] = jsonValue;
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets branch-scoped middleware persistent state for a given key.
    /// </summary>
    internal string? GetMiddlewareState(string key)
    {
        return MiddlewareState.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Clear all messages from this branch.
    /// </summary>
    public void Clear()
    {
        Messages.Clear();
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Get a display name for this branch based on first user message or description.
    /// Useful for UI display in branch lists.
    /// </summary>
    public string GetDisplayName(int maxLength = 30)
    {
        // Check for explicit description first
        if (!string.IsNullOrEmpty(Description))
        {
            return Description.Length <= maxLength
                ? Description
                : Description.Substring(0, maxLength - 3) + "...";
        }

        // Fall back to first user message
        var firstUserMessage = Messages.FirstOrDefault(m => m.Role == ChatRole.User);
        if (firstUserMessage == null)
            return Id; // Use branch ID as last resort

        var text = firstUserMessage.Text ?? string.Empty;
        if (text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength - 3) + "...";
    }
}

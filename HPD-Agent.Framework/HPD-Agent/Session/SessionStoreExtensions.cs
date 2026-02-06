namespace HPD.Agent;

/// <summary>
/// Extension methods for ISessionStore to provide convenience operations.
/// </summary>
public static class SessionStoreExtensions
{
    /// <summary>
    /// Load session and set session.Store reference for infrastructure operations.
    /// Creates a new session if it doesn't exist.
    /// </summary>
    /// <param name="store">The session store to load from</param>
    /// <param name="sessionId">The session identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The loaded or newly created session with Store property set</returns>
    /// <remarks>
    /// <para>
    /// This extension method automatically sets session.Store to enable:
    /// </para>
    /// <list type="bullet">
    /// <item>session.SaveAsync() convenience method</item>
    /// <item>Middleware access to session.Store.GetAssetStore(sessionId)</item>
    /// <item>Multi-tenant storage scenarios</item>
    /// </list>
    /// <para><b>Example:</b></para>
    /// <code>
    /// var store = new JsonSessionStore("./data");
    /// var session = await store.LoadOrCreateSessionAsync("session-123");
    /// // session.Store is now set to the JsonSessionStore instance
    /// </code>
    /// </remarks>
    public static async Task<Session> LoadOrCreateSessionAsync(
        this ISessionStore store,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = await store.LoadSessionAsync(sessionId, cancellationToken);

        if (session == null)
        {
            session = new Session(sessionId);
        }

        // Set store reference for middleware/checkpointing
        session.Store = store;

        return session;
    }

    /// <summary>
    /// Load session and branch, creating them if they don't exist.
    /// Sets session.Store reference for infrastructure operations.
    /// </summary>
    /// <param name="store">The session store to load from</param>
    /// <param name="sessionId">The session identifier</param>
    /// <param name="branchId">The branch identifier (defaults to "main")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of (Session, Branch) with Store property set</returns>
    public static async Task<(Session session, Branch branch)> LoadOrCreateSessionAndBranchAsync(
        this ISessionStore store,
        string sessionId,
        string branchId = "main",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);

        var session = await store.LoadSessionAsync(sessionId, cancellationToken)
            ?? new Session(sessionId);
        session.Store = store;

        var branch = await store.LoadBranchAsync(sessionId, branchId, cancellationToken)
            ?? session.CreateBranch(branchId);

        // Ensure back-reference is set
        branch.Session = session;

        return (session, branch);
    }
}

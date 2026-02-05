using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace HPD.Agent;

/// <summary>
/// Interface for persisting and loading AgentSession state.
/// Supports three persistence concerns:
/// <list type="bullet">
/// <item><strong>Session:</strong> Conversation snapshots (messages + metadata, saved after turn completes)</item>
/// <item><strong>Uncommitted Turn:</strong> Crash recovery buffer (delta of in-flight turn)</item>
/// <item><strong>Assets:</strong> Binary content per session</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Design Note:</strong> This interface is a CRUD-only layer. The store is "dumb" -
/// it just reads/writes data. Crash recovery is automatic: if a session store is configured,
/// uncommitted turns are saved after each tool batch and deleted on turn completion.
/// </para>
/// </remarks>
public interface ISessionStore
{
    // ═══════════════════════════════════════════════════════════════════
    // SESSION (Metadata/Container)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load a session from persistent storage by ID.
    /// Returns the latest snapshot (conversation state, no ExecutionState).
    /// Returns null if session doesn't exist.
    /// </summary>
    Task<AgentSession?> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a session snapshot to persistent storage.
    /// This persists messages, metadata, and middleware persistent state.
    /// </summary>
    Task SaveSessionAsync(
        AgentSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a session and all its data (including uncommitted turns and assets).
    /// </summary>
    Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all session IDs in storage.
    /// </summary>
    Task<List<string>> ListSessionIdsAsync(
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // UNCOMMITTED TURN (Crash Recovery — one per session)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load the uncommitted turn for a session, if one exists.
    /// Returns null if no turn is in progress (session is idle).
    /// </summary>
    Task<UncommittedTurn?> LoadUncommittedTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save (overwrite) the uncommitted turn for a session.
    /// Called after each tool batch completes (fire-and-forget from agent loop).
    /// The UncommittedTurn object contains the sessionId and branchId internally.
    /// </summary>
    Task SaveUncommittedTurnAsync(
        UncommittedTurn turn,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the uncommitted turn for a session.
    /// Called when a message turn completes successfully.
    /// </summary>
    Task DeleteUncommittedTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════
    // ASSETS (Session-scoped, shared by all branches)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the asset store for a specific session.
    /// Returns null if this store doesn't support asset storage.
    /// </summary>
    IAssetStore? GetAssetStore(string sessionId);

    // ═══════════════════════════════════════════════════════════════════
    // CLEANUP
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Delete sessions inactive longer than the threshold.
    /// Also cleans up any orphaned uncommitted turns.
    /// </summary>
    /// <param name="inactivityThreshold">Sessions inactive longer than this will be deleted</param>
    /// <param name="dryRun">If true, returns count without deleting</param>
    /// <returns>Number of sessions deleted (or would be deleted in dry-run mode)</returns>
    Task<int> DeleteInactiveSessionsAsync(
        TimeSpan inactivityThreshold,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

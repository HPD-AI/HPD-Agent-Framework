using System;

namespace HPD.Agent.Session;

/// <summary>
/// Thrown when a checkpoint version is newer than the maximum supported version.
/// Indicates the user needs to upgrade HPD-Agent.
/// </summary>
public class CheckpointVersionTooNewException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointVersionTooNewException"/> class
    /// with a specified error message. This exception indicates the checkpoint data
    /// uses a newer version than the running agent supports and typically prompts
    /// the user to upgrade the application.
    /// </summary>
    /// <param name="message">A descriptive error message.</param>
    public CheckpointVersionTooNewException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointVersionTooNewException"/> class
    /// with a specified error message and a reference to the inner exception that
    /// is the cause of this exception.
    /// </summary>
    /// <param name="message">A descriptive error message.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CheckpointVersionTooNewException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a checkpoint is stale (conversation has progressed beyond checkpoint state).
/// This indicates the checkpoint was created earlier but the conversation has continued
/// without updating the checkpoint, making it inconsistent with current state.
/// </summary>
public class CheckpointStaleException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointStaleException"/> class
    /// with a specified error message. Thrown when a checkpoint is out of date
    /// relative to the current conversation state.
    /// </summary>
    /// <param name="message">A descriptive error message.</param>
    public CheckpointStaleException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointStaleException"/> class
    /// with a specified error message and a reference to the inner exception that
    /// is the cause of this exception.
    /// </summary>
    /// <param name="message">A descriptive error message.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CheckpointStaleException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when concurrent checkpoint modifications are detected (optimistic concurrency violation).
/// This occurs when two processes try to update the same checkpoint simultaneously,
/// and the ETag-based conflict detection identifies a mismatch.
/// </summary>
public class CheckpointConcurrencyException : Exception
{
    /// <summary>
    /// Gets the identifier of the session that attempted the update.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the expected ETag value that the updater thought was current.
    /// This value may be <c>null</c> if not provided by the caller.
    /// </summary>
    public string? ExpectedETag { get; }

    /// <summary>
    /// Gets the actual ETag value found when the checkpoint was examined.
    /// This value may be <c>null</c> if the checkpoint store did not return an ETag.
    /// </summary>
    public string? ActualETag { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointConcurrencyException"/> class
    /// for an optimistic concurrency conflict. Includes the session id and the expected/actual ETag values.
    /// </summary>
    /// <param name="sessionId">Identifier of the session that attempted the update.</param>
    /// <param name="expectedETag">The ETag value the updater expected to find.</param>
    /// <param name="actualETag">The ETag value that was actually present.</param>
    public CheckpointConcurrencyException(string sessionId, string? expectedETag, string? actualETag)
        : base($"Checkpoint concurrency conflict for session '{sessionId}'. " +
               $"Expected ETag '{expectedETag}' but found '{actualETag}'. " +
               $"Another process modified this checkpoint.")
    {
        SessionId = sessionId;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckpointConcurrencyException"/> class
    /// for an optimistic concurrency conflict and includes an inner exception.
    /// </summary>
    /// <param name="sessionId">Identifier of the session that attempted the update.</param>
    /// <param name="expectedETag">The ETag value the updater expected to find.</param>
    /// <param name="actualETag">The ETag value that was actually present.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CheckpointConcurrencyException(string sessionId, string? expectedETag, string? actualETag, Exception innerException)
        : base($"Checkpoint concurrency conflict for session '{sessionId}'. " +
               $"Expected ETag '{expectedETag}' but found '{actualETag}'. " +
               $"Another process modified this checkpoint.", innerException)
    {
        SessionId = sessionId;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }
}

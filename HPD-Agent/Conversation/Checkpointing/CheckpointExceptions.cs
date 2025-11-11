using System;

namespace HPD.Agent.Conversation.Checkpointing;

/// <summary>
/// Thrown when a checkpoint version is newer than the maximum supported version.
/// Indicates the user needs to upgrade HPD-Agent.
/// </summary>
public class CheckpointVersionTooNewException : Exception
{
    public CheckpointVersionTooNewException(string message) : base(message) { }

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
    public CheckpointStaleException(string message) : base(message) { }

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
    public string ThreadId { get; }
    public string? ExpectedETag { get; }
    public string? ActualETag { get; }

    public CheckpointConcurrencyException(string threadId, string? expectedETag, string? actualETag)
        : base($"Checkpoint concurrency conflict for thread '{threadId}'. " +
               $"Expected ETag '{expectedETag}' but found '{actualETag}'. " +
               $"Another process modified this checkpoint.")
    {
        ThreadId = threadId;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }

    public CheckpointConcurrencyException(string threadId, string? expectedETag, string? actualETag, Exception innerException)
        : base($"Checkpoint concurrency conflict for thread '{threadId}'. " +
               $"Expected ETag '{expectedETag}' but found '{actualETag}'. " +
               $"Another process modified this checkpoint.", innerException)
    {
        ThreadId = threadId;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }
}

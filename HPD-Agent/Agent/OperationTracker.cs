/// <summary>
/// Metadata about function call operations for thread-safe per-call tracking
/// </summary>
public class OperationMetadata
{
    public bool HadFunctionCalls { get; set; }
    public List<string> FunctionCalls { get; set; } = new();
    public int FunctionCallCount { get; set; }
}

/// <summary>
/// Manages the tracking of function call metadata for an operation.
/// </summary>
public class OperationTracker
{
    private readonly OperationMetadata _metadata = new();

    /// <summary>
    /// Tracks a function call.
    /// </summary>
    /// <param name="functionCalls">The function calls to track.</param>
    /// <param name="iteration">The current iteration of the operation.</param>
    public void TrackFunctionCall(IEnumerable<string> functionCalls, int iteration)
    {
        _metadata.HadFunctionCalls = true;
        _metadata.FunctionCalls.AddRange(functionCalls);
        _metadata.FunctionCallCount = iteration;
    }

    /// <summary>
    /// Gets the current operation metadata.
    /// </summary>
    /// <returns>The operation metadata.</returns>
    public OperationMetadata GetMetadata() => _metadata;
}
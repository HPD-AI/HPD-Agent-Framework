using HPDAgent.Graph.Abstractions.Context;
using HPDAgent.Graph.Abstractions.Execution;
using HPDAgent.Graph.Abstractions.Handlers;

namespace HPD.Graph.Examples.Handlers;

/// <summary>
/// Example handler that polls for file existence.
/// Demonstrates sensor rescheduling pattern with polling.
/// </summary>
/// <remarks>
/// This handler checks if a file exists at the specified path.
/// If the file doesn't exist, it suspends with polling to check again later.
/// Useful for workflows that need to wait for external file creation.
///
/// Example use cases:
/// - Wait for ETL job to write output file
/// - Wait for user to upload a file
/// - Wait for external system to create a report
/// </remarks>
[GraphNodeHandler(NodeName = "file_sensor")]
public partial class FileSensorHandler : IGraphNodeHandler<IGraphContext>
{
    /// <summary>
    /// Execute the file sensor check.
    /// </summary>
    /// <param name="filePath">Path to the file to check</param>
    /// <param name="pokeIntervalSeconds">Seconds between polling attempts (default: 30)</param>
    /// <param name="timeoutSeconds">Maximum time to wait in seconds (default: 3600 = 1 hour)</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 120)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// - Success with file path if file exists
    /// - Suspended for polling if file doesn't exist yet
    /// </returns>
    public async Task<NodeExecutionResult> ExecuteAsync(
        [InputSocket] string filePath,
        [InputSocket] int pokeIntervalSeconds = 30,
        [InputSocket] int timeoutSeconds = 3600,
        [InputSocket] int maxRetries = 120,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new NodeExecutionResult.Failure(
                Exception: new ArgumentException("File path cannot be null or empty"),
                Severity: ErrorSeverity.Fatal,
                IsTransient: false,
                Duration: TimeSpan.Zero
            );
        }

        // Check if file exists
        if (File.Exists(filePath))
        {
            // File found - get metadata
            var fileInfo = new FileInfo(filePath);

            return new NodeExecutionResult.Success(
                Outputs: new Dictionary<string, object>
                {
                    ["file_path"] = filePath,
                    ["found"] = true,
                    ["checked_at"] = DateTimeOffset.UtcNow,
                    ["file_size"] = fileInfo.Length,
                    ["last_modified"] = fileInfo.LastWriteTimeUtc
                },
                Duration: TimeSpan.Zero
            );
        }

        // File doesn't exist - suspend with polling
        return NodeExecutionResult.Suspended.ForPolling(
            suspendToken: Guid.NewGuid().ToString(),
            retryAfter: TimeSpan.FromSeconds(pokeIntervalSeconds),
            maxWaitTime: TimeSpan.FromSeconds(timeoutSeconds),
            maxRetries: maxRetries,
            message: $"Waiting for file: {filePath}"
        );
    }
}

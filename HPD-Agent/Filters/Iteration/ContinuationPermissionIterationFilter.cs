using HPD.Agent.Internal.Filters;

namespace HPD.Agent;

/// <summary>
/// Iteration filter that requests user permission to continue execution
/// when the maximum iteration limit is reached.
/// </summary>
/// <remarks>
/// This filter runs BEFORE each LLM call and checks if we've reached the iteration limit.
/// If the limit is reached, it emits a continuation request event and waits for user response.
///
/// Unlike function-level permission filters, this filter:
/// - Runs before EVERY LLM call (not just tool invocations)
/// - Checks iteration count rather than function-specific permissions
/// - Can terminate the agent loop before the LLM call happens
///
/// This is the correct location for continuation permission because:
/// 1. It needs to check iteration state (iteration filter concern)
/// 2. It must run before LLM calls to prevent exceeding limits
/// 3. It's part of agent loop control, not function execution
/// </remarks>
internal class ContinuationPermissionIterationFilter : IIterationFilter
{
    private readonly int _maxIterations;
    private readonly int _extensionAmount;
    private readonly string _filterName;
    private int _currentExtendedLimit;

    /// <summary>
    /// Creates a new continuation permission filter.
    /// </summary>
    /// <param name="maxIterations">Base maximum iterations before requiring permission</param>
    /// <param name="extensionAmount">How many iterations to add when user approves continuation</param>
    /// <param name="filterName">Name for this filter instance (for event correlation)</param>
    public ContinuationPermissionIterationFilter(
        int maxIterations = 20,
        int extensionAmount = 3,
        string? filterName = null)
    {
        _maxIterations = maxIterations;
        _extensionAmount = extensionAmount;
        _filterName = filterName ?? "ContinuationPermissionFilter";
        _currentExtendedLimit = maxIterations;
    }

    /// <summary>
    /// Called BEFORE the LLM call begins.
    /// Checks if iteration limit reached and requests continuation permission if needed.
    /// </summary>
    public async Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Check if we've EXCEEDED the iteration limit
        // Use > because iterations are 0-based: limit=2 means iterations 0,1 allowed; 2 exceeds it
        if (context.Iteration > _currentExtendedLimit - 1)
        {
            // Request continuation permission
            var shouldContinue = await RequestContinuationPermissionAsync(context);

            if (!shouldContinue)
            {
                // User denied continuation - terminate execution
                // Set SkipLLMCall to prevent LLM invocation
                context.SkipLLMCall = true;

                // Provide a final response explaining termination
                context.Response = new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    "Execution terminated: Maximum iteration limit reached and continuation was not approved.");

                // Empty tool calls (no further work)
                context.ToolCalls = Array.Empty<Microsoft.Extensions.AI.FunctionCallContent>();

                // Signal termination via properties
                context.Properties["IsTerminated"] = true;
                context.Properties["TerminationReason"] = "Continuation permission denied at iteration limit";
            }
            // If approved, execution continues normally
        }
    }

    /// <summary>
    /// Called AFTER the LLM call completes.
    /// No action needed in this filter (all logic is in before phase).
    /// </summary>
    public Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        // Nothing to do after iteration for continuation permission
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requests continuation permission via bidirectional events.
    /// </summary>
    /// <returns>True if user approves continuation, false otherwise</returns>
    private async Task<bool> RequestContinuationPermissionAsync(IterationFilterContext context)
    {
        var continuationId = Guid.NewGuid().ToString();

        try
        {
            if (context.Agent == null)
                return false;
            
            var evt = new InternalContinuationRequestEvent(
                continuationId,
                _filterName,
                context.Iteration + 1,  // Display as 1-based for user
                _currentExtendedLimit);
            
            // Emit continuation request event
            context.Emit(evt);

            // Wait for response from external handler (BLOCKS during user interaction)
            var response = await context.WaitForResponseAsync<InternalContinuationResponseEvent>(
                continuationId,
                TimeSpan.FromMinutes(2));

            if (response.Approved)
            {
                // Extend the limit for future iterations
                var extension = response.ExtensionAmount > 0
                    ? response.ExtensionAmount
                    : _extensionAmount;

                _currentExtendedLimit += extension;
                return true;
            }

            return false;
        }
        catch (TimeoutException)
        {
            // Timeout - default to deny
            return false;
        }
        catch (OperationCanceledException)
        {
            // Cancelled - default to deny
            return false;
        }
        catch (InvalidOperationException)
        {
            // Agent reference not configured
            return false;
        }
        catch
        {
            // Catch any other exception
            return false;
        }
    }
}

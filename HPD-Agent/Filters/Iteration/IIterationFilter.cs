namespace HPD.Agent.Internal.Filters;

/// <summary>
/// Filter that runs before and after each LLM call in the agentic loop.
/// Provides access to iteration state and can modify messages/options dynamically.
/// Uses explicit lifecycle methods instead of middleware pattern due to streaming constraints.
/// </summary>
/// <remarks>
/// Iteration filters complement prompt filters (which run once per message turn).
/// Use iteration filters when you need:
/// - Access to tool results from previous iterations
/// - Dynamic instruction modification per iteration
/// - Iteration-aware guidance
/// - Pre and post LLM call hooks
///
/// Performance Note: Iteration filters run multiple times per message turn (once per LLM call).
/// Keep operations lightweight (< 1ms). For heavy operations like RAG or memory retrieval,
/// use IPromptFilter instead (runs once per message turn).
///
/// Architecture Note: This uses a lifecycle pattern (BeforeIterationAsync/AfterIterationAsync)
/// instead of middleware pattern because the LLM call uses yield return for streaming,
/// which cannot be wrapped in a lambda expression.
/// </remarks>
/// <example>
/// <code>
/// public class MyIterationFilter : IIterationFilter
/// {
///     public Task BeforeIterationAsync(
///         IterationFilterContext context,
///         CancellationToken cancellationToken)
///     {
///         // Modify before LLM call
///         if (context.Iteration > 0)
///         {
///             context.Options.Instructions += "\nAnalyze the tool results and respond.";
///         }
///         return Task.CompletedTask;
///     }
///
///     public Task AfterIterationAsync(
///         IterationFilterContext context,
///         CancellationToken cancellationToken)
///     {
///         // React to response after LLM call
///         if (context.IsFinalIteration)
///         {
///             Console.WriteLine("Final iteration completed");
///         }
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
internal interface IIterationFilter
{
    /// <summary>
    /// Called BEFORE the LLM call begins.
    /// Filters can modify messages/options to inject dynamic context.
    /// Can skip the LLM call by setting context.SkipLLMCall = true.
    /// </summary>
    /// <param name="context">Iteration context with mutable messages/options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when pre-processing is done</returns>
    Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called AFTER the LLM call completes (streaming finished).
    /// Filters can inspect the response and signal state changes.
    /// Response, ToolCalls, and Exception properties are populated at this point.
    /// </summary>
    /// <param name="context">Iteration context with populated Response and ToolCalls</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when post-processing is done</returns>
    Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken);
}

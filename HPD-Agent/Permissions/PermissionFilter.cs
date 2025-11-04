using System;
using System.Threading.Tasks;

/// <summary>
/// Unified permission filter that works with any protocol (Console, AGUI, Web, etc.).
/// Emits standardized permission events that can be handled by application-specific UI code.
/// Replaces both ConsolePermissionFilter and AGUIPermissionFilter with a single, protocol-agnostic implementation.
/// </summary>
public class PermissionFilter : IPermissionFilter
{
    private readonly IPermissionStorage? _storage;
    private readonly AgentConfig? _config;
    private readonly string _filterName;

    /// <summary>
    /// Creates a new unified permission filter.
    /// </summary>
    /// <param name="storage">Optional permission storage for persistent decisions</param>
    /// <param name="config">Optional agent configuration for continuation settings</param>
    /// <param name="filterName">Optional name for this filter instance (defaults to "PermissionFilter")</param>
    public PermissionFilter(IPermissionStorage? storage = null, AgentConfig? config = null, string? filterName = null)
    {
        _storage = storage;
        _config = config;
        _filterName = filterName ?? "PermissionFilter";
    }

    public async Task InvokeAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // First check: Continuation permission if we're approaching limits
        if (context.RunContext != null && ShouldCheckContinuation(context.RunContext))
        {
            var continueDecision = await RequestContinuationPermissionAsync(context);
            if (!continueDecision)
            {
                context.RunContext.IsTerminated = true;
                context.RunContext.TerminationReason = "User chose to stop at iteration limit";
                context.Result = "Execution terminated by user at iteration limit.";
                context.IsTerminated = true;
                return;
            }
        }

        // Second check: Function-level permission (if required)
        if (context.Function is not HPDAIFunctionFactory.HPDAIFunction hpdFunction ||
            !hpdFunction.HPDOptions.RequiresPermission)
        {
            await next(context);
            return;
        }

        var functionName = context.ToolCallRequest.FunctionName;
        var conversationId = context.RunContext?.ConversationId ?? string.Empty;

        // Get the unique call ID for this specific tool invocation
        var callId = context.Metadata.TryGetValue("CallId", out var idObj)
            ? idObj?.ToString()
            : null;

        // Check if this tool call was already approved in this run (prevents duplicate prompts)
        if (callId != null && context.RunContext?.IsToolApproved(callId) == true)
        {
            await next(context);
            return;
        }

        // Extract project ID from run context metadata if available
        string? projectId = null;
        if (context.RunContext?.Metadata.TryGetValue("Project", out var projectObj) == true)
        {
            projectId = (projectObj as Project)?.Id;
        }

        // Check storage if available
        if (_storage != null && !string.IsNullOrEmpty(conversationId))
        {
            var storedChoice = await _storage.GetStoredPermissionAsync(functionName, conversationId, projectId);

            if (storedChoice == PermissionChoice.AlwaysAllow)
            {
                await next(context);
                return;
            }

            if (storedChoice == PermissionChoice.AlwaysDeny)
            {
                context.Result = $"Execution of '{functionName}' was denied by a stored user preference.";
                context.IsTerminated = true;
                return;
            }
        }

        // No stored preference - request permission via events
        var permissionId = Guid.NewGuid().ToString();

        // Emit permission request event (standardized, protocol-agnostic)
        context.Emit(new InternalPermissionRequestEvent(
            permissionId,
            _filterName,
            functionName,
            context.Function.Description ?? "No description available",
            callId ?? string.Empty,
            context.ToolCallRequest.Arguments));

        // Wait for response from external handler (BLOCKS HERE while event is processed)
        InternalPermissionResponseEvent response;
        try
        {
            response = await context.WaitForResponseAsync<InternalPermissionResponseEvent>(
                permissionId,
                timeout: TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException)
        {
            // Emit denial event for observability
            context.Emit(new InternalPermissionDeniedEvent(
                permissionId,
                _filterName,
                "Permission request timed out after 5 minutes"));

            context.Result = "Permission request timed out. Please respond to permission requests promptly.";
            context.IsTerminated = true;
            return;
        }
        catch (OperationCanceledException)
        {
            // Emit denial event for observability
            context.Emit(new InternalPermissionDeniedEvent(
                permissionId,
                _filterName,
                "Permission request was cancelled"));

            context.Result = "Permission request was cancelled.";
            context.IsTerminated = true;
            return;
        }

        // Process the response
        if (response.Approved)
        {
            // Emit approval event for observability
            context.Emit(new InternalPermissionApprovedEvent(permissionId, _filterName));

            // Store persistent choice if user requested it
            if (_storage != null && response.Choice != PermissionChoice.Ask)
            {
                // Determine scope based on available context
                var scope = DetermineScope(conversationId, projectId);

                await _storage.SavePermissionAsync(
                    functionName,
                    response.Choice,
                    scope,
                    conversationId,
                    projectId);
            }

            // Mark as approved to prevent duplicate prompts in parallel execution
            if (callId != null)
            {
                context.RunContext?.MarkToolApproved(callId);
            }

            // Continue execution
            await next(context);
        }
        else
        {
            // Emit denial event for observability
            context.Emit(new InternalPermissionDeniedEvent(
                permissionId,
                _filterName,
                response.Reason ?? "User denied permission"));

            context.Result = response.Reason ?? $"Execution of '{functionName}' was denied by the user.";
            context.IsTerminated = true;
        }
    }

    /// <summary>
    /// Determines if we should check for continuation permission.
    /// Only triggers when we've actually exceeded the limit.
    /// </summary>
    private static bool ShouldCheckContinuation(AgentRunContext runContext)
    {
        return runContext.CurrentIteration >= runContext.MaxIterations;
    }

    /// <summary>
    /// Requests continuation permission via events.
    /// </summary>
    private async Task<bool> RequestContinuationPermissionAsync(FunctionInvocationContext context)
    {
        var continuationId = Guid.NewGuid().ToString();

        // Emit continuation request event
        context.Emit(new InternalContinuationRequestEvent(
            continuationId,
            _filterName,
            context.RunContext!.CurrentIteration + 1, // Display as 1-based
            context.RunContext.MaxIterations));

        // Wait for response from external handler
        InternalContinuationResponseEvent response;
        try
        {
            response = await context.WaitForResponseAsync<InternalContinuationResponseEvent>(
                continuationId,
                timeout: TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            // Default to deny on timeout
            return false;
        }
        catch (OperationCanceledException)
        {
            // Default to deny on cancellation
            return false;
        }

        // Handle extension of limits based on response
        if (response.Approved)
        {
            var extensionAmount = response.ExtensionAmount > 0
                ? response.ExtensionAmount
                : (_config?.ContinuationExtensionAmount ?? 3);

            context.RunContext.MaxIterations += extensionAmount;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines the appropriate permission scope based on available context.
    /// </summary>
    private static PermissionScope DetermineScope(string conversationId, string? projectId)
    {
        // If we have a project, default to conversation scope (most restrictive with context)
        // User can override to Project or Global if they choose
        if (!string.IsNullOrEmpty(projectId))
        {
            return PermissionScope.Conversation;
        }

        // If we only have conversation, default to conversation scope
        if (!string.IsNullOrEmpty(conversationId))
        {
            return PermissionScope.Conversation;
        }

        // Fallback to global if no context available
        return PermissionScope.Global;
    }
}

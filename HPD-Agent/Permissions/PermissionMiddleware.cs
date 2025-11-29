using HPD.Agent.Middleware;
using HPD_Agent.Permissions;

namespace HPD.Agent.Permissions;

/// <summary>
/// Unified permission middleware that works with any protocol (Console, AGUI, Web, etc.).
/// Emits standardized permission events that can be handled by application-specific UI code.
/// </summary>
/// <remarks>
/// <para><b>How It Works:</b></para>
/// <para>
/// This middleware uses the <see cref="IAgentMiddleware.BeforeFunctionAsync"/> hook to check
/// permissions before each function executes. If a function requires permission and the user
/// hasn't granted it, the middleware blocks execution and sets the result to the denial reason.
/// </para>
///
/// <para><b>Permission Checking Order:</b></para>
/// <list type="number">
/// <item>Check if function has [RequiresPermission] attribute (or runtime override)</item>
/// <item>Check conversation-scoped stored permission (if available)</item>
/// <item>Check global stored permission (fallback)</item>
/// <item>If no stored permission, emit PermissionRequestEvent and wait for response</item>
/// </list>
///
/// <para><b>Bidirectional Events:</b></para>
/// <list type="bullet">
/// <item><see cref="PermissionRequestEvent"/>: Emitted to request user permission</item>
/// <item><see cref="PermissionResponseEvent"/>: Expected response from UI handler</item>
/// <item><see cref="PermissionApprovedEvent"/>: Emitted for observability when approved</item>
/// <item><see cref="PermissionDeniedEvent"/>: Emitted for observability when denied</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var agent = new AgentBuilder()
///     .WithMiddleware(new PermissionMiddleware(storage))
///     .Build();
/// </code>
/// </example>
public class PermissionMiddleware : IAgentMiddleware
{
    private readonly IPermissionStorage? _storage;
    private readonly AgentConfig? _config;
    private readonly string _middlewareName;
    private readonly PermissionOverrideRegistry? _overrideRegistry;

    /// <summary>
    /// Creates a new permission middleware.
    /// </summary>
    /// <param name="storage">Optional permission storage for persistent decisions</param>
    /// <param name="config">Optional agent configuration for default messages</param>
    /// <param name="middlewareName">Optional name for this middleware instance (for event correlation)</param>
    /// <param name="overrideRegistry">Optional registry for runtime permission overrides</param>
    public PermissionMiddleware(
        IPermissionStorage? storage = null,
        AgentConfig? config = null,
        string? middlewareName = null,
        PermissionOverrideRegistry? overrideRegistry = null)
    {
        _storage = storage;
        _config = config;
        _middlewareName = middlewareName ?? "PermissionMiddleware";
        _overrideRegistry = overrideRegistry;
    }

    /// <summary>
    /// Resets batch permission state at the start of each iteration.
    /// </summary>
    public Task BeforeIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        // Reset batch state for new iteration
        var newBatchState = new BatchPermissionStateData().Reset();
        context.UpdateState(s => s with
        {
            MiddlewareState = s.MiddlewareState.WithBatchPermission(newBatchState)
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks permissions before a function executes.
    /// Blocks execution if permission is required but not granted.
    /// For parallel execution, checks batch state first to avoid duplicate permission requests.
    /// </summary>
    public async Task BeforeFunctionAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        var function = context.Function;
        if (function == null)
            return;

        var functionName = function.Name;

        // Check if permission is required (attribute + overrides)
        var attributeRequiresPermission = function is HPDAIFunctionFactory.HPDAIFunction hpdFunction
            && hpdFunction.HPDOptions.RequiresPermission;

        var effectiveRequiresPermission = _overrideRegistry?.GetEffectivePermissionRequirement(
            functionName, attributeRequiresPermission)
            ?? attributeRequiresPermission;

        // No permission required - allow execution
        if (!effectiveRequiresPermission)
            return;

        var conversationId = context.ConversationId;
        var callId = context.FunctionCallId ?? string.Empty;

        // ═══════════════════════════════════════════════════════════════
        // CHECK BATCH PERMISSION STATE (for parallel execution optimization)
        // ═══════════════════════════════════════════════════════════════

        var batchState = context.State.MiddlewareState.BatchPermission ?? new BatchPermissionStateData();

        // If already approved in batch, allow execution immediately
        if (batchState.ApprovedFunctions.Contains(functionName))
        {
            return;
        }

        // If already denied in batch, block execution immediately
        if (batchState.DeniedFunctions.Contains(functionName))
        {
            context.BlockFunctionExecution = true;
            context.FunctionResult = batchState.DenialReasons.GetValueOrDefault(
                functionName,
                "Permission denied in batch approval");
            return;
        }

        // ═══════════════════════════════════════════════════════════════
        // HIERARCHICAL PERMISSION LOOKUP
        // ═══════════════════════════════════════════════════════════════

        if (_storage != null)
        {
            PermissionChoice? storedChoice = null;

            // 1. Try conversation-scoped permission first
            if (!string.IsNullOrEmpty(conversationId))
            {
                storedChoice = await _storage.GetStoredPermissionAsync(functionName, conversationId)
                    .ConfigureAwait(false);
            }

            // 2. Fallback to global permission
            storedChoice ??= await _storage.GetStoredPermissionAsync(functionName, conversationId: null)
                .ConfigureAwait(false);

            // 3. Apply stored choice if found
            if (storedChoice == PermissionChoice.AlwaysAllow)
            {
                // Record approval in batch state for parallel optimization
                var updatedBatchState = batchState.RecordApproval(functionName);
                context.UpdateState(s => s with
                {
                    MiddlewareState = s.MiddlewareState.WithBatchPermission(updatedBatchState)
                });

                // Approved via stored preference - allow execution
                return;
            }

            if (storedChoice == PermissionChoice.AlwaysDeny)
            {
                var denialReason = $"Execution of '{functionName}' was denied by a stored user preference.";

                // Record denial in batch state for parallel optimization
                var updatedBatchState = batchState.RecordDenial(functionName, denialReason);
                context.UpdateState(s => s with
                {
                    MiddlewareState = s.MiddlewareState.WithBatchPermission(updatedBatchState)
                });

                // Denied via stored preference - block execution
                context.BlockFunctionExecution = true;
                context.FunctionResult = denialReason;
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // REQUEST PERMISSION VIA BIDIRECTIONAL EVENTS
        // ═══════════════════════════════════════════════════════════════

        var permissionId = Guid.NewGuid().ToString();

        // Emit permission request event
        context.Emit(new PermissionRequestEvent(
            permissionId,
            _middlewareName,
            functionName,
            function.Description ?? "No description available",
            callId,
            context.FunctionArguments ?? new Dictionary<string, object?>()));

        // Wait for response from external handler
        PermissionResponseEvent response;
        try
        {
            response = await context.WaitForResponseAsync<PermissionResponseEvent>(
                permissionId,
                TimeSpan.FromMinutes(5))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            context.Emit(new PermissionDeniedEvent(
                permissionId,
                _middlewareName,
                "Permission request timed out after 5 minutes"));

            context.BlockFunctionExecution = true;
            context.FunctionResult = "Permission request timed out. Please respond to permission requests promptly.";
            return;
        }
        catch (OperationCanceledException)
        {
            context.Emit(new PermissionDeniedEvent(
                permissionId,
                _middlewareName,
                "Permission request was cancelled"));

            context.BlockFunctionExecution = true;
            context.FunctionResult = "Permission request was cancelled.";
            return;
        }

        // ═══════════════════════════════════════════════════════════════
        // PROCESS RESPONSE
        // ═══════════════════════════════════════════════════════════════

        if (response.Approved)
        {
            // Emit approval event for observability
            context.Emit(new PermissionApprovedEvent(permissionId, _middlewareName));

            // Store persistent choice if requested
            if (_storage != null && response.Choice != PermissionChoice.Ask)
            {
                await _storage.SavePermissionAsync(
                    functionName,
                    response.Choice,
                    conversationId)
                    .ConfigureAwait(false);
            }

            // Record approval in batch state (for parallel execution optimization)
            var updatedBatchState = batchState.RecordApproval(functionName);
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithBatchPermission(updatedBatchState)
            });

            // Allow execution (don't set BlockFunctionExecution)
        }
        else
        {
            // Determine denial reason
            var denialReason = response.Reason
                ?? _config?.Messages?.PermissionDeniedDefault
                ?? "Permission denied by user.";

            // Emit denial event for observability
            context.Emit(new PermissionDeniedEvent(
                permissionId,
                _middlewareName,
                denialReason));

            // Record denial in batch state (for parallel execution optimization)
            var updatedBatchState = batchState.RecordDenial(functionName, denialReason);
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithBatchPermission(updatedBatchState)
            });

            // Block execution with denial reason
            context.BlockFunctionExecution = true;
            context.FunctionResult = denialReason;
        }
    }
}

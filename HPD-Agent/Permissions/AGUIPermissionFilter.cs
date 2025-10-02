using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// AGUI-based permission filter for web applications.
/// Directly implements permission checking with event emission and optional storage.
/// </summary>
public class AGUIPermissionFilter : IPermissionFilter
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pendingPermissions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingContinuations = new();
    private readonly IPermissionEventEmitter _eventEmitter;
    private readonly IPermissionStorage? _storage;
    private readonly AgentConfig? _config;

    public AGUIPermissionFilter(IPermissionEventEmitter eventEmitter, IPermissionStorage? storage = null, AgentConfig? config = null)
    {
        _eventEmitter = eventEmitter ?? throw new ArgumentNullException(nameof(eventEmitter));
        _storage = storage;
        _config = config;
    }

    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        // First check: Continuation permission if we're approaching limits
        if (context.RunContext != null && ShouldCheckContinuation(context.RunContext))
        {
            var continueDecision = await RequestContinuationPermissionAsync(context.RunContext);
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

        // No stored preference, request permission via AGUI
        var decision = await RequestPermissionAsync(functionName, context.Function.Description, context.ToolCallRequest.Arguments, conversationId, projectId);

        // Store decision if user chose to remember
        if (_storage != null && decision.Storage != null)
        {
            await _storage.SavePermissionAsync(
                functionName,
                decision.Storage.Choice,
                decision.Storage.Scope,
                conversationId,
                projectId);
        }

        // Apply decision
        if (decision.Approved)
        {
            await next(context);
        }
        else
        {
            context.Result = $"Execution of '{functionName}' was denied by the user.";
            context.IsTerminated = true;
        }
    }

    private async Task<PermissionDecision> RequestPermissionAsync(string functionName, string functionDescription, System.Collections.Generic.IDictionary<string, object?> arguments, string conversationId, string? projectId)
    {
        var permissionId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<PermissionDecision>();
        _pendingPermissions[permissionId] = tcs;

        var permissionEvent = new FunctionPermissionRequestEvent
        {
            Type = "custom", // All non-standard events are "custom" in AGUI
            PermissionId = permissionId,
            FunctionName = functionName,
            FunctionDescription = functionDescription,
            Arguments = new Dictionary<string, object?>(arguments),
            AvailableScopes = GetAvailableScopes(conversationId, projectId)
        };

        await _eventEmitter.EmitAsync(permissionEvent);

        // Wait for a response with a timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _pendingPermissions.TryRemove(permissionId, out _);
            return new PermissionDecision { Approved = false }; // Default to deny on timeout
        }
    }

    /// <summary>
    /// Called by the application when it receives a permission response from the frontend.
    /// </summary>
    public void HandlePermissionResponse(PermissionResponsePayload response)
    {
        if (response.Type == "function" &&
            _pendingPermissions.TryRemove(response.PermissionId, out var functionTcs))
        {
            var decision = new PermissionDecision
            {
                Approved = response.Approved,
                Storage = response.RememberChoice ? new PermissionStorage
                {
                    Choice = response.Approved ? PermissionChoice.AlwaysAllow : PermissionChoice.AlwaysDeny,
                    Scope = response.Scope
                } : null
            };
            functionTcs.SetResult(decision);
        }
        else if (response.Type == "continuation" &&
                 _pendingContinuations.TryRemove(response.PermissionId, out var continuationTcs))
        {
            continuationTcs.SetResult(response.Approved);
        }
    }

    /// <summary>
    /// Determines if we should check for continuation permission.
    /// Only triggers when we've actually exceeded the limit and the LLM is trying to call more functions.
    /// </summary>
    private static bool ShouldCheckContinuation(AgentRunContext runContext)
    {
        // Only check if we've exceeded the limit
        return runContext.CurrentIteration >= runContext.MaxIterations;
    }

    /// <summary>
    /// Requests continuation permission via AGUI events.
    /// </summary>
    private async Task<bool> RequestContinuationPermissionAsync(AgentRunContext runContext)
    {
        var permissionId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<bool>();
        _pendingContinuations[permissionId] = tcs;

        var continuationEvent = new ContinuationPermissionRequestEvent
        {
            Type = "custom",
            PermissionId = permissionId,
            CurrentIteration = runContext.CurrentIteration + 1, // Display as 1-based
            MaxIterations = runContext.MaxIterations,
            CompletedFunctions = runContext.CompletedFunctions.ToArray(),
            ElapsedTime = runContext.ElapsedTime.ToString(@"mm\:ss")
        };

        await _eventEmitter.EmitAsync(continuationEvent);

        // Wait for a response with a timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var approved = await tcs.Task.WaitAsync(cts.Token);
            
            // Handle extension of limits based on response
            if (approved && runContext.CurrentIteration >= runContext.MaxIterations)
            {
                var extensionAmount = _config?.ContinuationExtensionAmount ?? 3;
                runContext.MaxIterations += extensionAmount;
            }
            
            return approved;
        }
        catch (OperationCanceledException)
        {
            _pendingContinuations.TryRemove(permissionId, out _);
            return false; // Default to deny on timeout
        }
    }

    private static PermissionScope[] GetAvailableScopes(string conversationId, string? projectId)
    {
        var scopes = new List<PermissionScope> { PermissionScope.Conversation };
        if (!string.IsNullOrEmpty(projectId))
        {
            scopes.Add(PermissionScope.Project);
        }
        scopes.Add(PermissionScope.Global);
        return scopes.ToArray();
    }
}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

    /// <summary>
    /// Interface for emitting permission events to an AGUI stream.
    /// This is implemented by the application's streaming infrastructure.
    /// </summary>
    public interface IPermissionEventEmitter
    {
        Task EmitAsync<T>(T eventData) where T : BaseEvent;
    }

    /// <summary>
    /// Default AGUI-based permission handler for web applications.
    /// Emits permission request events and waits for responses.
    /// </summary>
    public class AGUIPermissionHandler : IPermissionHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pendingFunctionPermissions = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ContinuationDecision>> _pendingContinuationPermissions = new();
        private readonly IPermissionEventEmitter _eventEmitter;

        public AGUIPermissionHandler(IPermissionEventEmitter eventEmitter)
        {
            _eventEmitter = eventEmitter ?? throw new ArgumentNullException(nameof(eventEmitter));
        }

        public async Task<PermissionDecision> RequestFunctionPermissionAsync(FunctionPermissionRequest request)
        {
            var permissionId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<PermissionDecision>();
            _pendingFunctionPermissions[permissionId] = tcs;

            var permissionEvent = new FunctionPermissionRequestEvent
            {
                Type = "custom", // All non-standard events are "custom" in AGUI
                PermissionId = permissionId,
                FunctionName = request.FunctionName,
                FunctionDescription = request.FunctionDescription,
                Arguments = new Dictionary<string, object?>(request.Arguments),
                AvailableScopes = GetAvailableScopes(request)
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
                _pendingFunctionPermissions.TryRemove(permissionId, out _);
                return new PermissionDecision { Approved = false }; // Default to deny on timeout
            }
        }

        public async Task<ContinuationDecision> RequestContinuationPermissionAsync(ContinuationPermissionRequest request)
        {
            var permissionId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<ContinuationDecision>();
            _pendingContinuationPermissions[permissionId] = tcs;

            var continuationEvent = new ContinuationPermissionRequestEvent
            {
                Type = "custom",
                PermissionId = permissionId,
                CurrentIteration = request.CurrentIteration,
                MaxIterations = request.MaxIterations,
                CompletedFunctions = request.CompletedFunctions.ToArray(),
                PlannedFunctions = request.PlannedFunctions.ToArray()
            };

            await _eventEmitter.EmitAsync(continuationEvent);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _pendingContinuationPermissions.TryRemove(permissionId, out _);
                return new ContinuationDecision { ShouldContinue = false, Reason = "Permission request timed out." };
            }
        }

        /// <summary>
        /// Called by the application when it receives a permission response from the frontend.
        /// </summary>
        public void HandlePermissionResponse(PermissionResponsePayload response)
        {
            if (response.Type == "function" && 
                _pendingFunctionPermissions.TryRemove(response.PermissionId, out var functionTcs))
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
                     _pendingContinuationPermissions.TryRemove(response.PermissionId, out var continuationTcs))
            {
                var decision = new ContinuationDecision
                {
                    ShouldContinue = response.Approved,
                    Reason = response.Approved ? null : "User chose to stop the operation."
                };
                continuationTcs.SetResult(decision);
            }
        }

        private static PermissionScope[] GetAvailableScopes(FunctionPermissionRequest request)
        {
            var scopes = new List<PermissionScope> { PermissionScope.Conversation };
            if (!string.IsNullOrEmpty(request.ProjectId))
            {
                scopes.Add(PermissionScope.Project);
            }
            scopes.Add(PermissionScope.Global);
            return scopes.ToArray();
        }
    }
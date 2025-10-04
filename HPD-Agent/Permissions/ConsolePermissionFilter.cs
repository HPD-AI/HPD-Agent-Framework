using System;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Console-based permission filter for command-line applications.
/// Directly implements permission checking with optional storage.
/// </summary>
public class ConsolePermissionFilter : IPermissionFilter
{
    private readonly IPermissionStorage? _storage;
    private readonly AgentConfig? _config;

    public ConsolePermissionFilter(IPermissionStorage? storage = null, AgentConfig? config = null)
    {
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

        // Get the unique call ID for this specific tool invocation
        // The call ID should be passed through the context or metadata
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

        // No stored preference, request permission via console
        var decision = await RequestPermissionAsync(functionName, context.Function.Description, context.ToolCallRequest.Arguments);

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
            // Mark as approved to prevent duplicate prompts in parallel execution
            if (callId != null)
            {
                context.RunContext?.MarkToolApproved(callId);
            }

            await next(context);
        }
        else
        {
            context.Result = $"Execution of '{functionName}' was denied by the user.";
            context.IsTerminated = true;
        }
    }

    private async Task<PermissionDecision> RequestPermissionAsync(string functionName, string functionDescription, System.Collections.Generic.IDictionary<string, object?> arguments)
    {
        // Offload the blocking Console.ReadLine to a background thread
        return await Task.Run(() =>
        {
            Console.WriteLine($"\n[PERMISSION REQUIRED]");
            Console.WriteLine($"Function: {functionName}");
            Console.WriteLine($"Description: {functionDescription}");

            if (arguments.Any())
            {
                Console.WriteLine("Arguments:");
                foreach (var arg in arguments)
                {
                    Console.WriteLine($"  {arg.Key}: {arg.Value}");
                }
            }

            Console.WriteLine("\nChoose an option:");
            Console.WriteLine("  [A]llow once");
            Console.WriteLine("  [D]eny once");
            Console.WriteLine("  [Y] Always allow (Global)");
            Console.WriteLine("  [N] Never allow (Global)");
            Console.Write("Choice: ");

            var response = Console.ReadLine()?.ToUpper();

            var decision = response switch
            {
                "A" => new PermissionDecision { Approved = true },
                "D" => new PermissionDecision { Approved = false },
                "Y" => new PermissionDecision
                {
                    Approved = true,
                    Storage = new PermissionStorage { Choice = PermissionChoice.AlwaysAllow, Scope = PermissionScope.Global }
                },
                "N" => new PermissionDecision
                {
                    Approved = false,
                    Storage = new PermissionStorage { Choice = PermissionChoice.AlwaysDeny, Scope = PermissionScope.Global }
                },
                _ => new PermissionDecision { Approved = false } // Default to deny
            };

            return decision;
        });
    }

    /// <summary>
    /// Determines if we should check for continuation permission.
    /// Only triggers when we've actually exceeded the limit and the LLM is trying to call more functions.
    /// </summary>
    private static bool ShouldCheckContinuation(AgentRunContext runContext)
    {
        // Check if this iteration would exceed the max (0-based iteration vs 1-based limit)
        return runContext.CurrentIteration >= runContext.MaxIterations;
    }

    /// <summary>
    /// Requests continuation permission via console for continuation beyond limits.
    /// </summary>
    private async Task<bool> RequestContinuationPermissionAsync(AgentRunContext runContext)
    {
        return await Task.Run(() =>
        {
            Console.WriteLine($"\n[CONTINUATION PERMISSION REQUIRED]");
            Console.WriteLine($"Function calling has exceeded the limit of {runContext.MaxIterations} turns");
            Console.WriteLine($"Current turns completed: {runContext.CompletedFunctions.Count}");
            Console.WriteLine($"Elapsed time: {runContext.ElapsedTime:mm\\:ss}");
            
            if (runContext.CompletedFunctions.Count > 0)
            {
                Console.WriteLine($"Completed functions: {string.Join(", ", runContext.CompletedFunctions)}");
            }

            Console.WriteLine("\nThe LLM wants to continue with more function calls.");
            Console.WriteLine("Choose an option:");
            Console.WriteLine("  [C]ontinue (allow more turns)");
            Console.WriteLine("  [S]top execution");
            Console.Write("Choice: ");

            var response = Console.ReadLine()?.ToUpper();

            switch (response)
            {
                case "C":
                    var extensionAmount = _config?.ContinuationExtensionAmount ?? 3;
                    runContext.MaxIterations += extensionAmount;
                    Console.WriteLine($"Continuing with extended turn limit.");
                    return true;
                case "S":
                default:
                    return false;
            }
        });
    }
}
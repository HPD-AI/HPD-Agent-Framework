using System;
using System.Threading.Tasks;

/// <summary>
/// Auto-approve permission filter for testing and automation scenarios.
/// Automatically approves all function executions that require permission.
/// </summary>
public class AutoApprovePermissionFilter : IPermissionFilter
{
    public async Task InvokeAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // Check if function requires permission
        if (context.Function is not HPDAIFunctionFactory.HPDAIFunction hpdFunction ||
            !hpdFunction.HPDOptions.RequiresPermission)
        {
            await next(context);
            return;
        }

        // Auto-approve all permission requests
        await next(context);
    }
}
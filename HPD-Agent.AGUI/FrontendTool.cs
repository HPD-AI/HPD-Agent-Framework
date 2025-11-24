using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
/// AOT-compatible frontend tool that integrates AGUI tools into Microsoft.Extensions.AI pipeline
/// Based on AGUIDotnet.Integrations.ChatClient.FrontendTool but made AOT-compatible
/// </summary>
/// <param name="tool">The AGUI tool definition provided to an agent</param>
public sealed class FrontendTool(Tool tool) : AIFunction
{
    public override string Name => tool.Name;
    public override string Description => tool.Description;
    public override JsonElement JsonSchema => tool.Parameters;

    [UnconditionalSuppressMessage("AOT", "IL2075", Justification = "FrontendTool reflection is optional fallback - tool will work without it")]
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        /*
        Following AGUI's pattern: The FunctionInvokingChatClient sets up a function invocation loop 
        where it intercepts function calls to invoke the appropriate .NET function.

        However, frontend tools should NOT be executed locally - they're executed by the frontend (human-in-the-loop).
        
        This function's "invocation" is a signal to the FunctionInvokingChatClient that it should terminate 
        the invocation loop and return out, which allows the AGUI agent to intervene and emit proper events.
        
        Unfortunately this means multiple tool call support is limited without either:
        - Finding a way to register a regular AITool with JSON schema serialization support
        - Or implementing a custom variation of FunctionInvokingChatClient with better async tool support
        */
        
        // Try to access the current context - this might not be available in all scenarios
        // but follows AGUI's pattern for termination
        try
        {
            // Use reflection to access FunctionInvokingChatClient.CurrentContext
            // This is needed for AOT compatibility since we can't reference the non-AOT AGUI library
            var functionInvokingType = Type.GetType("Microsoft.Extensions.AI.FunctionInvokingChatClient, Microsoft.Extensions.AI");
            if (functionInvokingType != null)
            {
                var currentContextProperty = functionInvokingType.GetProperty("CurrentContext", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                
                if (currentContextProperty?.GetValue(null) is object currentContext)
                {
                    var terminateProperty = currentContext.GetType().GetProperty("Terminate");
                    terminateProperty?.SetValue(currentContext, true);
                }
            }
        }
        catch
        {
            // If reflection fails, we continue - the tool will still not execute properly
            // but won't crash the system
        }

        // Return null to indicate "no execution" - the frontend will handle the actual execution
        return ValueTask.FromResult<object?>(null);
    }
}

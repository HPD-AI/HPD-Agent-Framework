using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Generates code for sub-agent registration
/// Converts SubAgent definitions into AIFunction wrappers that build and invoke AgentCore instances
/// </summary>
internal static class SubAgentCodeGenerator
{
    /// <summary>
    /// Generates AIFunction creation code for a sub-agent
    /// This wraps the AgentCore invocation similar to Microsoft's AsAIFunction() pattern
    /// </summary>
    public static string GenerateSubAgentFunction(SubAgentInfo subAgent, string pluginName)
    {
        var sb = new StringBuilder();

        // Generate method comment
        sb.AppendLine($"        // Sub-agent: {subAgent.SubAgentName}");
        sb.AppendLine($"        // Category: {subAgent.Category ?? "None"}");
        sb.AppendLine($"        // Thread Mode: {subAgent.ThreadMode}");
        sb.AppendLine();

        // Create AIFunction for this sub-agent
        sb.AppendLine($"        // Create AIFunction wrapper for {subAgent.SubAgentName} sub-agent");
        sb.AppendLine($"        {{");
        sb.AppendLine($"            async System.Threading.Tasks.Task<string> InvokeSubAgentAsync(");
        sb.AppendLine($"                [System.ComponentModel.Description(\"Query for the sub-agent\")] string query,");
        sb.AppendLine($"                System.Threading.CancellationToken cancellationToken)");
        sb.AppendLine($"            {{");
        sb.AppendLine($"                // Get sub-agent definition from method");
        sb.AppendLine($"                var subAgentDef = instance.{subAgent.MethodName}();");
        sb.AppendLine();
        sb.AppendLine($"                // Build agent from config");
        sb.AppendLine($"                var agentBuilder = new AgentBuilder(subAgentDef.AgentConfig);");
        sb.AppendLine();
        sb.AppendLine($"                // Register plugins if any are specified");
        sb.AppendLine($"                if (subAgentDef.PluginTypes != null && subAgentDef.PluginTypes.Length > 0)");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    foreach (var pluginType in subAgentDef.PluginTypes)");
        sb.AppendLine($"                    {{");
        sb.AppendLine($"                        agentBuilder.PluginManager.RegisterPlugin(pluginType);");
        sb.AppendLine($"                    }}");
        sb.AppendLine($"                }}");
        sb.AppendLine();
        sb.AppendLine($"                var agent = agentBuilder.BuildCoreAgent();");
        sb.AppendLine();

        // Handle thread mode
        sb.AppendLine($"                // Handle thread based on mode");
        sb.AppendLine($"                ConversationThread thread;");
        sb.AppendLine($"                switch (subAgentDef.ThreadMode)");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    case SubAgentThreadMode.SharedThread:");
        sb.AppendLine($"                        thread = subAgentDef.SharedThread ?? new ConversationThread();");
        sb.AppendLine($"                        break;");
        sb.AppendLine($"                    case SubAgentThreadMode.PerSession:");
        sb.AppendLine($"                        // For PerSession, use SharedThread if provided, else create new");
        sb.AppendLine($"                        thread = subAgentDef.SharedThread ?? new ConversationThread();");
        sb.AppendLine($"                        break;");
        sb.AppendLine($"                    case SubAgentThreadMode.Stateless:");
        sb.AppendLine($"                    default:");
        sb.AppendLine($"                        // Create new thread for each invocation");
        sb.AppendLine($"                        thread = new ConversationThread();");
        sb.AppendLine($"                        break;");
        sb.AppendLine($"                }}");
        sb.AppendLine();

        // Invoke agent
        sb.AppendLine($"                // Create user message and run agent");
        sb.AppendLine($"                var message = new ChatMessage(ChatMessageRole.User, query);");
        sb.AppendLine($"                var response = await agent.RunAsync(");
        sb.AppendLine($"                    thread,");
        sb.AppendLine($"                    new[] {{ message }},");
        sb.AppendLine($"                    cancellationToken: cancellationToken);");
        sb.AppendLine();

        // Return response
        sb.AppendLine($"                // Return last assistant message");
        sb.AppendLine($"                return response.Messages");
        sb.AppendLine($"                    .LastOrDefault(m => m.Role == ChatMessageRole.Assistant)");
        sb.AppendLine($"                    ?.Content ?? string.Empty;");
        sb.AppendLine($"            }}");
        sb.AppendLine();

        // Create AIFunction with metadata
        sb.AppendLine($"            var subAgentFunction = HPDAIFunctionFactory.Create(");
        sb.AppendLine($"                invocation: InvokeSubAgentAsync,");
        sb.AppendLine($"                options: new HPDAIFunctionFactoryOptions");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    Name = \"{subAgent.SubAgentName}\",");
        sb.AppendLine($"                    Description = \"{EscapeString(subAgent.Description)}\",");
        sb.AppendLine($"                    RequiresPermission = true, // Sub-agent invocations require permission");
        sb.AppendLine($"                    AdditionalProperties = new System.Collections.Generic.Dictionary<string, object>");
        sb.AppendLine($"                    {{");
        sb.AppendLine($"                        [\"IsSubAgent\"] = true,");
        sb.AppendLine($"                        [\"SubAgentCategory\"] = \"{EscapeString(subAgent.Category ?? "Uncategorized")}\",");
        sb.AppendLine($"                        [\"SubAgentPriority\"] = {subAgent.Priority},");
        sb.AppendLine($"                        [\"ThreadMode\"] = \"{subAgent.ThreadMode}\",");
        sb.AppendLine($"                        [\"PluginName\"] = \"{pluginName}\"");
        sb.AppendLine($"                    }}");
        sb.AppendLine($"                }});");
        sb.AppendLine();
        sb.AppendLine($"            functions.Add(subAgentFunction);");
        sb.AppendLine($"        }}");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Generates all sub-agent functions for a plugin
    /// </summary>
    public static string GenerateAllSubAgentFunctions(PluginInfo plugin)
    {
        if (!plugin.SubAgents.Any())
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("        // ═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"        // SUB-AGENTS ({plugin.SubAgents.Count} total)");
        sb.AppendLine("        // ═══════════════════════════════════════════════════════════════");
        sb.AppendLine();

        foreach (var subAgent in plugin.SubAgents)
        {
            sb.Append(GenerateSubAgentFunction(subAgent, plugin.Name));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes quotes in strings for code generation
    /// </summary>
    private static string EscapeString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        return input.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}

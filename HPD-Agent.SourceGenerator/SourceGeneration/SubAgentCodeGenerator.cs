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
        sb.AppendLine($"                // Get the current agent (parent) from AsyncLocal context");
        sb.AppendLine($"                var currentAgent = HPD.Agent.AgentCore.RootAgent;");
        sb.AppendLine();
        sb.AppendLine($"                // If no provider specified in SubAgent config, inherit parent's chat client");
        sb.AppendLine($"                if (subAgentDef.AgentConfig.Provider == null && currentAgent != null)");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    agentBuilder.WithChatClient(currentAgent.BaseClient);");
        sb.AppendLine($"                }}");
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

        // Set up event bubbling via parent-child linking
        sb.AppendLine($"                // ═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"                // SET UP EVENT BUBBLING (Parent-Child Linking)");
        sb.AppendLine($"                // ═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"                // currentAgent was already retrieved above for client inheritance");
        sb.AppendLine($"                if (currentAgent != null)");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    // Establish explicit parent-child relationship");
        sb.AppendLine($"                    // Events from this sub-agent will bubble to parent via _parentCoordinator");
        sb.AppendLine($"                    agent.EventCoordinator.SetParent(currentAgent.EventCoordinator);");
        sb.AppendLine($"                }}");
        sb.AppendLine();

        // Build execution context for event attribution
        sb.AppendLine($"                // ═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"                // BUILD EXECUTION CONTEXT (Event Attribution)");
        sb.AppendLine($"                // ═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"                // Build hierarchical execution context for event attribution");
        sb.AppendLine($"                var parentContext = currentAgent?.ExecutionContext;");
        sb.AppendLine($"                var randomId = System.Guid.NewGuid().ToString(\"N\")[..8];");
        sb.AppendLine($"                var sanitizedAgentName = System.Text.RegularExpressions.Regex.Replace(");
        sb.AppendLine($"                    \"{subAgent.SubAgentName}\",");
        sb.AppendLine($"                    @\"[^a-zA-Z0-9]\",");
        sb.AppendLine($"                    \"_\");");
        sb.AppendLine();
        sb.AppendLine($"                var agentId = parentContext != null");
        sb.AppendLine($"                    ? $\"{{parentContext.AgentId}}-{{sanitizedAgentName}}-{{randomId}}\"");
        sb.AppendLine($"                    : $\"{{sanitizedAgentName}}-{{randomId}}\";");
        sb.AppendLine();
        sb.AppendLine($"                var agentChain = parentContext != null");
        sb.AppendLine($"                    ? new System.Collections.Generic.List<string>(parentContext.AgentChain) {{ \"{subAgent.SubAgentName}\" }}");
        sb.AppendLine($"                    : new System.Collections.Generic.List<string> {{ \"{subAgent.SubAgentName}\" }};");
        sb.AppendLine();
        sb.AppendLine($"                agent.ExecutionContext = new HPD.Agent.AgentExecutionContext");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    AgentName = \"{subAgent.SubAgentName}\",");
        sb.AppendLine($"                    AgentId = agentId,");
        sb.AppendLine($"                    ParentAgentId = parentContext?.AgentId,");
        sb.AppendLine($"                    AgentChain = agentChain,");
        sb.AppendLine($"                    Depth = (parentContext?.Depth ?? -1) + 1");
        sb.AppendLine($"                }};");
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
        sb.AppendLine($"                var message = new ChatMessage(ChatRole.User, query);");
        sb.AppendLine($"                var responseMessages = new System.Collections.Generic.List<ChatMessage>();");
        sb.AppendLine($"                await foreach (var evt in agent.RunAsync(");
        sb.AppendLine($"                    new[] {{ message }},");
        sb.AppendLine($"                    options: null,");
        sb.AppendLine($"                    thread: thread,");
        sb.AppendLine($"                    cancellationToken: cancellationToken))");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    // We don't need to process events here, just let it run");
        sb.AppendLine($"                }}");
        sb.AppendLine();

        // Return response
        sb.AppendLine($"                // Return last assistant message from thread");
        sb.AppendLine($"                var messages = await thread.GetMessagesAsync(cancellationToken);");
        sb.AppendLine($"                return messages");
        sb.AppendLine($"                    .LastOrDefault(m => m.Role == ChatRole.Assistant)");
        sb.AppendLine($"                    ?.Text ?? string.Empty;");
        sb.AppendLine($"            }}");
        sb.AppendLine();

        // Create AIFunction with metadata
        sb.AppendLine($"            var subAgentFunction = HPDAIFunctionFactory.Create(");
        sb.AppendLine($"                (async (arguments, cancellationToken) =>");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    var jsonArgs = arguments.GetJson();");
        sb.AppendLine($"                    var query = jsonArgs.TryGetProperty(\"query\", out var queryProp)");
        sb.AppendLine($"                        ? queryProp.GetString() ?? string.Empty");
        sb.AppendLine($"                        : string.Empty;");
        sb.AppendLine($"                    return await InvokeSubAgentAsync(query, cancellationToken);");
        sb.AppendLine($"                }}),");
        sb.AppendLine($"                new HPDAIFunctionFactoryOptions");
        sb.AppendLine($"                {{");
        sb.AppendLine($"                    Name = \"{subAgent.SubAgentName}\",");
        sb.AppendLine($"                    Description = \"{EscapeString(subAgent.Description)}\",");
        sb.AppendLine($"                    RequiresPermission = true, // Sub-agent invocations require permission");
        sb.AppendLine($"                    SchemaProvider = () =>");
        sb.AppendLine($"                    {{");
        sb.AppendLine($"                        var options = new global::Microsoft.Extensions.AI.AIJsonSchemaCreateOptions {{ IncludeSchemaKeyword = false }};");
        sb.AppendLine($"                        var method = typeof({subAgent.ClassName}).GetMethod(\"{subAgent.MethodName}\")");
        sb.AppendLine($"                            ?.GetCustomAttributes(typeof(SubAgentAttribute), false)");
        sb.AppendLine($"                            ?.FirstOrDefault();");
        sb.AppendLine($"                        return global::Microsoft.Extensions.AI.AIJsonUtilities.CreateJsonSchema(");
        sb.AppendLine($"                            typeof({pluginName}SubAgentQueryArgs),");
        sb.AppendLine($"                            serializerOptions: global::Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions,");
        sb.AppendLine($"                            inferenceOptions: options");
        sb.AppendLine($"                        );");
        sb.AppendLine($"                    }},");
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

using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Converts between Microsoft.Extensions.AI types and AGUI protocol types
/// This enables dual interface implementation by translating between the two formats
/// 
/// AGUI TOOL ARCHITECTURE (Based on Official AGUI Implementation):
/// ================================================================
/// AGUI tools are FRONTEND-EXECUTED (human-in-the-loop):
/// 1. Frontend defines tools and passes them to agent
/// 2. Agent receives tools and creates FrontendTool AIFunction wrappers
/// 3. LLM calls tool → FrontendTool.InvokeCoreAsync() sets CurrentContext.Terminate = true
/// 4. FunctionInvokingChatClient stops execution loop
/// 5. Agent emits AGUI events (ToolCallStart/Args/End) for frontend
/// 6. Frontend executes tool and returns result as ToolMessage
/// 
/// Microsoft.Extensions.AI tools are BACKEND-EXECUTED:
/// 1. Backend defines real AIFunction objects with actual implementations
/// 2. ChatClient executes them directly during streaming
/// 3. Results are returned as FunctionResultContent
/// 4. Used for calculations, API calls, data processing, etc.
/// 
/// This converter implements the official AGUI pattern:
/// - Creates FrontendTool wrappers that terminate execution instead of throwing
/// - Combines frontend and backend tools in ChatOptions.Tools (AGUI pattern)
/// - Converts tool calls to proper AGUI event sequences
/// - Tracks frontend vs backend tools separately for proper handling
/// 
/// INSTANCE-BASED: Each converter instance maintains its own isolated tool tracking state
/// </summary>
public class AGUIEventConverter
{
    private readonly ToolCallTracker _toolTracker = new();
    
    /// <summary>
    /// Clears all tool tracking state for this converter instance
    /// </summary>
    public void ClearToolTracking() => _toolTracker.Clear();
    
    /// <summary>
    /// Converts AGUI RunAgentInput to Extensions.AI ChatMessage collection
    /// </summary>
    public IEnumerable<ChatMessage> ConvertToExtensionsAI(RunAgentInput input)
    {
        var messages = new List<ChatMessage>();
        
        foreach (var agUIMessage in input.Messages)
        {
            // Extract content from different message types
            var content = ExtractMessageContent(agUIMessage);
            var role = ExtractMessageRole(agUIMessage);
            
            messages.Add(new ChatMessage(role, content));
        }
        
        return messages;
    }
    
    /// <summary>
    /// Extracts content from AGUI BaseMessage
    /// </summary>
    private static string ExtractMessageContent(BaseMessage message)
    {
        return message switch
        {
            UserMessage userMsg => userMsg.Content,
            AssistantMessage assistantMsg => assistantMsg.Content ?? string.Empty,
            SystemMessage systemMsg => systemMsg.Content,
            DeveloperMessage devMsg => devMsg.Content,
            ToolMessage toolMsg => toolMsg.Content ?? string.Empty,
            _ => string.Empty
        };
    }
    
    /// <summary>
    /// Extracts ChatRole from AGUI BaseMessage
    /// </summary>
    private static ChatRole ExtractMessageRole(BaseMessage message)
    {
        return message switch
        {
            UserMessage => ChatRole.User,
            AssistantMessage => ChatRole.Assistant,
            SystemMessage => ChatRole.System,
            DeveloperMessage => ChatRole.System, // Map developer to system
            ToolMessage => ChatRole.Tool,
            _ => ChatRole.User // Default fallback
        };
    }
    
    /// <summary>
    /// Converts AGUI RunAgentInput tools to Extensions.AI ChatOptions
    /// Supports both frontend tools (from AGUI input) and backend tools (from existing ChatOptions)
    /// </summary>
    /// <param name="input">AGUI run agent input containing frontend tools</param>
    /// <param name="existingOptions">Existing chat options with backend tools</param>
    /// <param name="enableFrontendToolScoping">Enable plugin scoping for frontend tools (groups them in a container)</param>
    /// <param name="maxFunctionNamesInDescription">Maximum function names to show in container description</param>
    public ChatOptions ConvertToExtensionsAIChatOptions(
        RunAgentInput input,
        ChatOptions? existingOptions = null,
        bool enableFrontendToolScoping = false,
        int maxFunctionNamesInDescription = 10)
    {
        var options = existingOptions ?? new ChatOptions
        {
            ToolMode = ChatToolMode.Auto // Support function calls by default
        };
        
        // Start with existing backend tools (if any)
        var backendTools = new List<AIFunction>();
        if (options.Tools != null)
        {
            // Middleware out any FrontendTool instances and keep real backend tools
            backendTools.AddRange(options.Tools.OfType<AIFunction>().Where(f => f is not FrontendTool));
        }
        
        // Track backend tool names for conflict detection
        var backendToolNames = backendTools.Select(t => t.Name).ToHashSet();
        
        // Convert AGUI tools to frontend tool stubs
        var frontendTools = new List<AIFunction>();
        var frontendToolNames = new HashSet<string>();

        foreach (var tool in input.Tools)
        {
            // Check for tool name conflicts
            if (backendToolNames.Contains(tool.Name))
            {
                throw new InvalidOperationException(
                    $"Frontend tool '{tool.Name}' conflicts with backend tool name. " +
                    "Please ensure frontend and backend tool names are unique.");
            }

            // Create frontend tool stub that follows AGUI's termination pattern
            var frontendStub = CreateFrontendToolStub(tool);
            frontendTools.Add(frontendStub);
            frontendToolNames.Add(tool.Name);

            // Track this as a frontend tool for event processing
            _toolTracker.TrackFrontendTool("", tool.Name); // We'll get the actual callId later
        }

        // Apply plugin scoping to frontend tools if enabled
        if (enableFrontendToolScoping && frontendTools.Count > 0)
        {
            var (container, scopedTools) = ExternalToolScopingWrapper.WrapFrontendTools(
                frontendTools,
                maxFunctionNamesInDescription);

            // Track container as frontend tool too (it triggers expansion)
            _toolTracker.TrackFrontendTool("", container.Name);

            // Replace with scoped versions (container + scoped tools)
            frontendTools = new List<AIFunction> { container };
            frontendTools.AddRange(scopedTools);

            // Update tracking with scoped tool names
            frontendToolNames.Clear();
            frontendToolNames.Add(container.Name);
            foreach (var tool in scopedTools)
            {
                frontendToolNames.Add(tool.Name);
            }
        }

        // Track backend tools
        foreach (var backendTool in backendTools)
        {
            _toolTracker.TrackBackendTool("", backendTool.Name);
        }
        
        // Combine frontend and backend tools (following AGUI pattern)
        if (backendTools.Any() || frontendTools.Any())
        {
            options.Tools = [.. backendTools, .. frontendTools];
            options.AllowMultipleToolCalls = false; // AGUI pattern - one tool at a time
        }
        else
        {
            options.Tools = null;
            options.AllowMultipleToolCalls = null;
        }
        
        // Extract additional options from AGUI context or forwarded props
        if (input.ForwardedProps.ValueKind != JsonValueKind.Undefined)
        {
            ExtractChatOptionsFromForwardedProps(options, input.ForwardedProps);
        }
        
        return options;
    }
    
    /// <summary>
    /// Creates a frontend tool that follows AGUI's termination pattern
    /// The tool allows the LLM to call it, but execution terminates and defers to frontend
    /// </summary>
    private static AIFunction CreateFrontendToolStub(Tool tool)
    {
        // Use the proper FrontendTool implementation that follows AGUI's termination pattern
        return new FrontendTool(tool);
    }
    
    /// <summary>
    /// Extracts ChatOptions properties from AGUI ForwardedProps
    /// </summary>
    private static void ExtractChatOptionsFromForwardedProps(ChatOptions options, JsonElement forwardedProps)
    {
        if (forwardedProps.ValueKind != JsonValueKind.Object)
            return;
            
        foreach (var prop in forwardedProps.EnumerateObject())
        {
            switch (prop.Name.ToLowerInvariant())
            {
                case "temperature":
                    if (prop.Value.TryGetSingle(out var temp))
                        options.Temperature = temp;
                    break;
                    
                case "maxoutputtokens":
                case "max_tokens":
                    if (prop.Value.TryGetInt32(out var maxTokens))
                        options.MaxOutputTokens = maxTokens;
                    break;
                    
                case "topprobability":
                case "top_p":
                    if (prop.Value.TryGetSingle(out var topP))
                        options.TopP = topP;
                    break;
                    
                case "frequencypenalty":
                case "frequency_penalty":
                    if (prop.Value.TryGetSingle(out var freqPenalty))
                        options.FrequencyPenalty = freqPenalty;
                    break;
                    
                case "presencepenalty":
                case "presence_penalty":
                    if (prop.Value.TryGetSingle(out var presPenalty))
                        options.PresencePenalty = presPenalty;
                    break;
                    
                case "seed":
                    if (prop.Value.TryGetInt64(out var seed))
                        options.Seed = seed;
                    break;
                    
                case "stop":
                case "stopsequences":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var stopSequences = prop.Value.EnumerateArray()
                            .Where(el => el.ValueKind == JsonValueKind.String)
                            .Select(el => el.GetString()!)
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                        if (stopSequences.Any())
                            options.StopSequences = stopSequences;
                    }
                    break;
            }
        }
    }
    
    
    
    /// <summary>
    /// Instance-based tool call tracking for frontend/backend distinction
    /// Each converter instance maintains its own isolated tool tracking state
    /// </summary>
    public class ToolCallTracker
    {
        private readonly ConcurrentDictionary<string, string> _knownFrontendToolCalls = new();
        private readonly ConcurrentDictionary<string, string> _knownBackendToolCalls = new();
        
        // Track by name for initial setup - using ConcurrentDictionary as a thread-safe HashSet
        private readonly ConcurrentDictionary<string, byte> _frontendToolNames = new();
        
        public void TrackFrontendTool(string callId, string toolName) 
        {
            if (!string.IsNullOrEmpty(callId))
                _knownFrontendToolCalls.TryAdd(callId, toolName);
            _frontendToolNames.TryAdd(toolName, 0); // Value doesn't matter, using as HashSet
        }
            
        public void TrackBackendTool(string callId, string toolName) 
        {
            if (!string.IsNullOrEmpty(callId))
                _knownBackendToolCalls.TryAdd(callId, toolName);
            // Also track by name for initial setup
            // Note: We use a different approach than frontend tools to avoid conflicts
        }
            
        public bool IsFrontendTool(string callId) 
            => _knownFrontendToolCalls.ContainsKey(callId);
            
        public bool IsBackendTool(string callId) 
            => _knownBackendToolCalls.ContainsKey(callId);
            
        public bool IsFrontendToolByName(string toolName) 
            => _frontendToolNames.ContainsKey(toolName);
            
        /// <summary>
        /// Clears all tracking data for this converter instance
        /// </summary>
        public void Clear()
        {
            _knownFrontendToolCalls.Clear();
            _knownBackendToolCalls.Clear();
            _frontendToolNames.Clear();
        }
        
        /// <summary>
        /// Removes tracking for a specific call ID to prevent memory leaks
        /// </summary>
        public void RemoveCall(string callId)
        {
            _knownFrontendToolCalls.TryRemove(callId, out _);
            _knownBackendToolCalls.TryRemove(callId, out _);
        }
    }

    /// <summary>
    /// Creates a RunAgentInput from thread state, encapsulating the complex mapping logic.
    /// This centralizes the conversion between ConversationThread/ChatMessage format and AGUI format.
    /// </summary>
    public static RunAgentInput CreateRunAgentInput(
        string threadId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options)
    {
        return new RunAgentInput
        {
            ThreadId = threadId,
            RunId = Guid.NewGuid().ToString(),
            Messages = messages.Select(ConvertChatMessageToBaseMessage).ToList(),
            State = System.Text.Json.JsonDocument.Parse("{}").RootElement,
            Tools = ExtractToolsFromOptions(options), // TODO: Extract actual tools from agent/options
            Context = ExtractContextFromOptions(options),
            ForwardedProps = ExtractForwardedPropsFromOptions(options)
        };
    }

    public static BaseMessage ConvertChatMessageToBaseMessage(ChatMessage message)
    {
        var content = ExtractTextContent(message) ?? "";
        var role = message.Role.ToString().ToLowerInvariant();
        var id = message.MessageId ?? Guid.NewGuid().ToString();

        return role switch
        {
            "user" => new UserMessage { Id = id, Role = role, Content = content },
            "assistant" => new AssistantMessage { Id = id, Role = role, Content = content },
            "system" => new SystemMessage { Id = id, Role = role, Content = content },
            "tool" => new ToolMessage { Id = id, Role = role, Content = content },
            _ => new UserMessage { Id = id, Role = "user", Content = content }
        };
    }

    public static string? ExtractTextContent(ChatMessage message)
    {
        // Handle different content types from Microsoft.Extensions.AI
        var textContent = message.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
        if (!string.IsNullOrEmpty(textContent))
            return textContent;
            
        // Handle function call content
        var functionCallContent = message.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        if (functionCallContent != null)
        {
            var args = functionCallContent.Arguments?.Select(kvp => $"{kvp.Key}={kvp.Value}") ?? Enumerable.Empty<string>();
            return $"Function call: {functionCallContent.Name}({string.Join(", ", args)})";
        }
        
        // Handle function result content
        var functionResultContent = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (functionResultContent != null)
        {
            return functionResultContent.Result?.ToString() ?? "";
        }
        
        return null;
    }

    private static IReadOnlyList<Tool> ExtractToolsFromOptions(ChatOptions? options)
    {
        // For now, return empty list. This can be enhanced later to extract AGUI tools from options
        return new List<Tool>();
    }

    private static IReadOnlyList<Context> ExtractContextFromOptions(ChatOptions? options)
    {
        // For now, return empty list. This can be enhanced later to extract AGUI context from options
        return new List<Context>();
    }

    private static JsonElement ExtractForwardedPropsFromOptions(ChatOptions? options)
    {
        // For now, return empty object. This can be enhanced later to extract forwarded props from options
        return System.Text.Json.JsonDocument.Parse("{}").RootElement;
    }

    /// <summary>
    /// Creates AGUI run lifecycle events
    /// </summary>
    public static class LifecycleEvents
    {
        public static RunStartedEvent CreateRunStarted(RunAgentInput input) => 
            EventSerialization.CreateRunStarted(input.ThreadId, input.RunId);
        
        public static RunFinishedEvent CreateRunFinished(RunAgentInput input) => 
            EventSerialization.CreateRunFinished(input.ThreadId, input.RunId);
        
        public static RunErrorEvent CreateRunError(RunAgentInput input, Exception ex) => 
            EventSerialization.CreateRunError(ex.Message);
        
        public static TextMessageStartEvent CreateTextMessageStart(string messageId = "", string? role = null) => 
            EventSerialization.CreateTextMessageStart(messageId, role);
        
        public static TextMessageEndEvent CreateTextMessageEnd(string messageId = "") => 
            EventSerialization.CreateTextMessageEnd(messageId);
    }
}
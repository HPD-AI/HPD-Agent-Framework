using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace HPD_Agent.FFI;


/// <summary>
/// Delegate for streaming callback from C# to Rust
/// </summary>
/// <param name="context">Context pointer passed back to Rust</param>
/// <param name="eventJsonPtr">Pointer to UTF-8 JSON string of the event, or null to signal end of stream</param>
public delegate void StreamCallback(IntPtr context, IntPtr eventJsonPtr);

/// <summary>
/// Matches the Rust RustFunctionInfo structure
/// </summary>
public class RustFunctionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    
    [JsonPropertyName("wrapperFunctionName")]
    public string WrapperFunctionName { get; set; } = string.Empty;
    
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "{}";
    
    [JsonPropertyName("requiresPermission")]
    public bool RequiresPermission { get; set; }
    
    [JsonPropertyName("requiredPermissions")]
    public List<string> RequiredPermissions { get; set; } = new();
    
    [JsonPropertyName("plugin_name")]
    public string PluginName { get; set; } = string.Empty;
}

/// <summary>
/// Static class containing all C# functions exported to Rust via FFI.
/// This serves as the main entry point for the Rust wrapper library.
/// </summary>
public static partial class NativeExports
{
    /// <summary>
    /// Test function to verify FFI communication between C# and Rust.
    /// Accepts a UTF-8 string from Rust and returns a response.
    /// </summary>
    /// <param name="messagePtr">Pointer to a UTF-8 encoded string from Rust</param>
    /// <returns>Pointer to a UTF-8 encoded response string allocated by C#</returns>
    [UnmanagedCallersOnly(EntryPoint = "ping")]
    public static IntPtr Ping(IntPtr messagePtr)
    {
        try
        {
            // Marshal the string from Rust
            string? message = Marshal.PtrToStringUTF8(messagePtr);
            string response = $"Pong: You sent '{message}'";

            // Convert to UTF-8 bytes and allocate unmanaged memory
            byte[] responseBytes = Encoding.UTF8.GetBytes(response + '\0'); // null-terminated
            IntPtr responsePtr = Marshal.AllocHGlobal(responseBytes.Length);
            Marshal.Copy(responseBytes, 0, responsePtr, responseBytes.Length);
            
            return responsePtr;
        }
        catch (Exception ex)
        {
            // In case of error, return a pointer to an error message
            string errorResponse = $"Error in Ping: {ex.Message}";
            byte[] errorBytes = Encoding.UTF8.GetBytes(errorResponse + '\0'); // null-terminated
            IntPtr errorPtr = Marshal.AllocHGlobal(errorBytes.Length);
            Marshal.Copy(errorBytes, 0, errorPtr, errorBytes.Length);
            return errorPtr;
        }
    }

    /// <summary>
    /// Frees memory allocated by C# for strings returned to Rust.
    /// This must be called by Rust for every string pointer received from C#.
    /// </summary>
    /// <param name="stringPtr">Pointer to the string memory to free</param>
    [UnmanagedCallersOnly(EntryPoint = "free_string")]
    public static void FreeString(IntPtr stringPtr)
    {
        if (stringPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(stringPtr);
        }
    }

    /// <summary>
    /// Creates an agent with the given configuration and plugins.
    /// </summary>
    /// <param name="configJsonPtr">Pointer to JSON string containing AgentConfig</param>
    /// <param name="pluginsJsonPtr">Pointer to JSON string containing plugin definitions</param>
    /// <returns>Handle to the created Agent, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "create_agent_with_plugins")]
    [RequiresUnreferencedCode("Agent creation uses plugin registration methods that require reflection.")]
    public static IntPtr CreateAgentWithPlugins(IntPtr configJsonPtr, IntPtr pluginsJsonPtr)
    {
        try
        {
            string? configJson = Marshal.PtrToStringUTF8(configJsonPtr);
            if (string.IsNullOrEmpty(configJson)) return IntPtr.Zero;

            var agentConfig = JsonSerializer.Deserialize<AgentConfig>(configJson, HPDJsonContext.Default.AgentConfig);
            if (agentConfig == null) return IntPtr.Zero;

            var builder = new AgentBuilder(agentConfig);
            
            // Parse and add Rust plugins
            string? pluginsJson = Marshal.PtrToStringUTF8(pluginsJsonPtr);
            Console.WriteLine($"[FFI] Received plugins JSON: {pluginsJson}");
            
            if (!string.IsNullOrEmpty(pluginsJson))
            {
                try
                {
                    var rustFunctions = JsonSerializer.Deserialize(pluginsJson, HPDJsonContext.Default.ListRustFunctionInfo);
                    Console.WriteLine($"[FFI] Deserialized {rustFunctions?.Count ?? 0} Rust functions");
                    
                    if (rustFunctions != null && rustFunctions.Count > 0)
                    {
                        // Track unique plugin names
                        var pluginNames = new HashSet<string>();
                        
                        foreach (var rustFunc in rustFunctions)
                        {
                            Console.WriteLine($"[FFI] Adding Rust function: {rustFunc.Name} - {rustFunc.Description}");
                            var aiFunction = CreateRustFunctionWrapper(rustFunc);
                            builder.AddRustFunction(aiFunction);
                            
                            // Track plugin name for registration
                            if (!string.IsNullOrEmpty(rustFunc.PluginName))
                            {
                                pluginNames.Add(rustFunc.PluginName);
                            }
                        }
                        
                        // Register plugin executors on Rust side
                        foreach (var pluginName in pluginNames)
                        {
                            Console.WriteLine($"[FFI] Registering executors for plugin: {pluginName}");
                            bool success = RustPluginFFI.RegisterPluginExecutors(pluginName);
                            Console.WriteLine($"[FFI] Registration result for {pluginName}: {success}");
                        }
                        
                        Console.WriteLine($"[FFI] Successfully added {rustFunctions.Count} Rust functions to agent");
                    }
                }
                catch (Exception ex)
                {
                    // Log but don't fail - agent can still work without Rust functions
                    Console.WriteLine($"Failed to parse Rust plugins: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            }

            var agent = builder.Build();
            return ObjectManager.Add(agent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create agent: {ex.Message}");
            return IntPtr.Zero;
        }
    }


    /// <summary>
    /// Creates an AIFunction wrapper that calls back to Rust via FFI
    /// </summary>
    private static AIFunction CreateRustFunctionWrapper(RustFunctionInfo rustFunc)
    {
        return HPDAIFunctionFactory.Create(
            (arguments, cancellationToken) =>
            {
                // Convert AIFunctionArguments to a simple dictionary
                var argsDict = new Dictionary<string, object>();
                foreach (var kvp in arguments)
                {
                    if (kvp.Key != "__raw_json__" && kvp.Value != null) // Skip internal keys and null values
                    {
                        argsDict[kvp.Key] = kvp.Value;
                    }
                }
                
                // Execute the Rust function via FFI
                var result = RustPluginFFI.ExecuteFunction(rustFunc.Name, argsDict);
                
                if (!result.Success)
                {
                    // Return error as structured response for better AI understanding
                    return Task.FromResult<object?>(new { error = result.Error ?? "Unknown error", success = false });
                }
                
                // Parse the result
                if (result.Result != null)
                {
                    try
                    {
                        using (result.Result)
                        {
                            var root = result.Result.RootElement;
                            
                            // Check if it's a success/result envelope
                            if (root.TryGetProperty("success", out var successProp) && 
                                root.TryGetProperty("result", out var resultProp))
                            {
                                if (successProp.GetBoolean())
                                {
                                    // Return just the result value
                                    return Task.FromResult<object?>(resultProp.ValueKind == JsonValueKind.String 
                                        ? resultProp.GetString() 
                                        : resultProp.GetRawText());
                                }
                                else if (root.TryGetProperty("error", out var errorProp))
                                {
                                    return Task.FromResult<object?>(new { error = errorProp.GetString(), success = false });
                                }
                            }
                            
                            // Return raw response if not in envelope format
                            return Task.FromResult<object?>(root.GetRawText());
                        }
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult<object?>(new { error = $"Failed to parse result: {ex.Message}", success = false });
                    }
                }
                
                return Task.FromResult<object?>(null);
            },
            new HPDAIFunctionFactoryOptions
            {
                Name = rustFunc.Name,
                Description = rustFunc.Description,
                RequiresPermission = rustFunc.RequiresPermission,
                SchemaProvider = () => 
                {
                    try
                    {
                        // Parse the schema JSON from Rust
                        var schemaDoc = JsonDocument.Parse(rustFunc.Schema);
                        var rootSchema = schemaDoc.RootElement;
                        
                        // Check if this is an OpenAPI function calling format
                        if (rootSchema.TryGetProperty("function", out var functionElement) &&
                            functionElement.TryGetProperty("parameters", out var parametersElement))
                        {
                            // Extract just the parameters schema for Microsoft.Extensions.AI
                            return parametersElement.Clone();
                        }
                        else
                        {
                            // Use the schema as-is if it's already in the right format
                            return rootSchema.Clone();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error and fallback to empty object schema
                        Console.WriteLine($"Warning: Failed to parse schema for {rustFunc.Name}: {ex.Message}");
                        return JsonDocument.Parse("{}").RootElement;
                    }
                }
            }
        );
    }

    /// <summary>
    /// Destroys an agent and releases its resources.
    /// </summary>
    /// <param name="agentHandle">Handle to the agent to destroy</param>
    [UnmanagedCallersOnly(EntryPoint = "destroy_agent")]
    public static void DestroyAgent(IntPtr agentHandle)
    {
        ObjectManager.Remove(agentHandle);
    }

    /// <summary>
    /// Creates a conversation from one or more agents.
    /// </summary>
    /// <param name="agentHandlesPtr">Pointer to array of agent handles</param>
    /// <param name="agentCount">Number of agents in the array</param>
    /// <returns>Handle to the created Conversation, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "create_conversation")]
    public static IntPtr CreateConversation(IntPtr agentHandlesPtr, int agentCount)
    {
        try
        {
            var agentHandles = new IntPtr[agentCount];
            Marshal.Copy(agentHandlesPtr, agentHandles, 0, agentCount);

            var agents = agentHandles.Select(ObjectManager.Get<Agent>).OfType<Agent>().ToList();
            if (!agents.Any())
            {
                throw new InvalidOperationException("No valid agents provided to create conversation.");
            }

            var firstAgent = agents.First();

            var conversation = new Conversation(firstAgent); // Simplified for now
            return ObjectManager.Add(conversation);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Destroys a conversation and releases its resources.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation to destroy</param>
    [UnmanagedCallersOnly(EntryPoint = "destroy_conversation")]
    public static void DestroyConversation(IntPtr conversationHandle)
    {
        ObjectManager.Remove(conversationHandle);
    }

    /// <summary>
    /// Sends a message to a conversation and returns the final response text.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="messagePtr">Pointer to the UTF-8 message string</param>
    /// <returns>Pointer to the UTF-8 response string, or null on error</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_send")]
    public static IntPtr ConversationSend(IntPtr conversationHandle, IntPtr messagePtr)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            string? message = Marshal.PtrToStringUTF8(messagePtr);
            if (string.IsNullOrEmpty(message)) return IntPtr.Zero;

            // Block on the async method to get the final result for the synchronous FFI call.
            var response = conversation.SendAsync(message).GetAwaiter().GetResult();

            // Extract the primary text content from the agent's final response message.
            var responseText = response.Response.Messages.LastOrDefault()?.Text ?? "";
            
            return Marshal.StringToCoTaskMemAnsi(responseText);
        }
        catch (Exception)
        {
            return IntPtr.Zero; // Return null pointer to indicate an error.
        }
    }

    /// <summary>
    /// Sends a message to a conversation and streams AGUI events via callback.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="messagePtr">Pointer to the UTF-8 message string</param>
    /// <param name="callback">Function pointer to receive events</param>
    /// <param name="context">Context pointer passed back to callback</param>
    [UnmanagedCallersOnly(EntryPoint = "conversation_send_streaming")]
    public static void ConversationSendStreaming(IntPtr conversationHandle, IntPtr messagePtr, IntPtr callback, IntPtr context)
    {
        // CRITICAL: Read the message synchronously BEFORE starting the async task
        // This prevents the race condition where the Rust CString gets deallocated
        // before we can read it in the async task
        
        var conversation = ObjectManager.Get<Conversation>(conversationHandle);
        if (conversation == null) 
        {
            // Signal error through callback
            string errorJson = "{\"type\":\"ERROR\", \"message\":\"Invalid conversation handle\"}";
            var errorJsonPtr = Marshal.StringToCoTaskMemAnsi(errorJson);
            var errorCallback = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
            errorCallback(context, errorJsonPtr);
            Marshal.FreeCoTaskMem(errorJsonPtr);
            errorCallback(context, IntPtr.Zero); // End stream
            return;
        }

        string? message = Marshal.PtrToStringUTF8(messagePtr);
        if (string.IsNullOrEmpty(message)) 
        {
            // Signal error through callback
            string errorJson = "{\"type\":\"ERROR\", \"message\":\"Message is null or empty\"}";
            var errorJsonPtr = Marshal.StringToCoTaskMemAnsi(errorJson);
            var errorCallback = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
            errorCallback(context, errorJsonPtr);
            Marshal.FreeCoTaskMem(errorJsonPtr);
            errorCallback(context, IntPtr.Zero); // End stream
            return;
        }
        
        // Now run the streaming in a background thread with the captured message string
        Task.Run(async () =>
        {
            try
            {
                // Get the primary agent to stream events from
                var primaryAgent = conversation.PrimaryAgent;
                if (primaryAgent == null) 
                {
                    throw new InvalidOperationException("No agents in conversation.");
                }

                // Generate IDs for AGUI protocol
                var messageId = Guid.NewGuid().ToString();
                var runId = Guid.NewGuid().ToString();
                var threadId = conversation.Id; // Use conversation ID to maintain thread continuity
                
                var callbackDelegate = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
                
                // 1. Emit RUN_STARTED
                var runStartedJson = $"{{\"type\":\"RUN_STARTED\",\"runId\":\"{runId}\",\"threadId\":\"{threadId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                var runStartedPtr = Marshal.StringToCoTaskMemAnsi(runStartedJson);
                callbackDelegate(context, runStartedPtr);
                Marshal.FreeCoTaskMem(runStartedPtr);
                
                // 2. Emit TEXT_MESSAGE_START
                var messageStartJson = $"{{\"type\":\"TEXT_MESSAGE_START\",\"messageId\":\"{messageId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                var messageStartPtr = Marshal.StringToCoTaskMemAnsi(messageStartJson);
                callbackDelegate(context, messageStartPtr);
                Marshal.FreeCoTaskMem(messageStartPtr);
                
                // 3. Use streaming with the conversation's actual message history
                var streamResult = await conversation.SendStreamingAsync(message, null);
                await foreach (var evt in streamResult.EventStream)
                {
                    // Serialize the BaseEvent directly to JSON
                    string eventJson = SerializeBaseEvent(evt);

                    var eventJsonPtr = Marshal.StringToCoTaskMemAnsi(eventJson);
                    callbackDelegate(context, eventJsonPtr);
                    Marshal.FreeCoTaskMem(eventJsonPtr);
                }
                
                // 4. Emit TEXT_MESSAGE_END
                var messageEndJson = $"{{\"type\":\"TEXT_MESSAGE_END\",\"messageId\":\"{messageId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                var messageEndPtr = Marshal.StringToCoTaskMemAnsi(messageEndJson);
                callbackDelegate(context, messageEndPtr);
                Marshal.FreeCoTaskMem(messageEndPtr);
                
                // 5. Emit RUN_FINISHED
                var runFinishedJson = $"{{\"type\":\"RUN_FINISHED\",\"runId\":\"{runId}\",\"threadId\":\"{threadId}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                var runFinishedPtr = Marshal.StringToCoTaskMemAnsi(runFinishedJson);
                callbackDelegate(context, runFinishedPtr);
                Marshal.FreeCoTaskMem(runFinishedPtr);
                
                var endCallback = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
                endCallback(context, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                // Signal an error through the callback.
                string errorJson = $"{{\"type\":\"ERROR\", \"message\":\"{ex.Message.Replace("\"", "'")}\"}}";
                var errorJsonPtr = Marshal.StringToCoTaskMemAnsi(errorJson);
                var errorCallback = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
                errorCallback(context, errorJsonPtr);
                Marshal.FreeCoTaskMem(errorJsonPtr);
                errorCallback(context, IntPtr.Zero); // End stream after error.
            }
        });
    }

    /// <summary>
    /// Sends a message to a conversation with simple text streaming (no detailed events).
    /// This provides a clean, minimal output similar to ChatGPT.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="messagePtr">Pointer to the UTF-8 message string</param>
    /// <param name="callback">Function pointer to receive text content</param>
    /// <param name="context">Context pointer passed back to callback</param>
    [UnmanagedCallersOnly(EntryPoint = "conversation_send_simple")]
    public static void ConversationSendSimple(IntPtr conversationHandle, IntPtr messagePtr, IntPtr callback, IntPtr context)
    {
        var conversation = ObjectManager.Get<Conversation>(conversationHandle);
        if (conversation == null) 
        {
            // Signal error through callback
            string errorJson = "{\"type\":\"ERROR\", \"message\":\"Invalid conversation handle\"}";
            var errorJsonPtr = Marshal.StringToCoTaskMemAnsi(errorJson);
            var errorCallback = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
            errorCallback(context, errorJsonPtr);
            Marshal.FreeCoTaskMem(errorJsonPtr);
            errorCallback(context, IntPtr.Zero); // End stream
            return;
        }

        string? message = Marshal.PtrToStringUTF8(messagePtr);
        if (string.IsNullOrEmpty(message)) 
        {
            string errorJson = "{\"type\":\"ERROR\", \"message\":\"Message is null or empty\"}";
            var errorJsonPtr = Marshal.StringToCoTaskMemAnsi(errorJson);
            var errorCallback = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
            errorCallback(context, errorJsonPtr);
            Marshal.FreeCoTaskMem(errorJsonPtr);
            errorCallback(context, IntPtr.Zero); // End stream
            return;
        }
        
        Task.Run(async () =>
        {
            try
            {
                var callbackDelegate = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
                
                // Use the simple streaming method with a custom output handler
                await conversation.SendStreamingWithOutputAsync(message, text =>
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Send plain text content as simple JSON
                        var contentJson = $"{{\"type\":\"CONTENT\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text, HPDJsonContext.Default.String)}}}";
                        var contentPtr = Marshal.StringToCoTaskMemAnsi(contentJson);
                        callbackDelegate(context, contentPtr);
                        Marshal.FreeCoTaskMem(contentPtr);
                    }
                });
                
                // Signal end of stream
                callbackDelegate(context, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                var callbackDelegate = Marshal.GetDelegateForFunctionPointer<StreamCallback>(callback);
                string errorJson = $"{{\"type\":\"ERROR\", \"message\":{System.Text.Json.JsonSerializer.Serialize(ex.Message, HPDJsonContext.Default.String)}}}";
                var errorJsonPtr = Marshal.StringToCoTaskMemAnsi(errorJson);
                callbackDelegate(context, errorJsonPtr);
                Marshal.FreeCoTaskMem(errorJsonPtr);
                callbackDelegate(context, IntPtr.Zero); // End stream
            }
        });
    }

    /// <summary>
    /// Serializes BaseEvent to JSON for streaming
    /// </summary>
    private static string SerializeBaseEvent(BaseEvent evt)
    {
        // Use the agent's built-in serialization method for BaseEvents
        try
        {
            return EventSerialization.SerializeEvent(evt);
        }
        catch
        {
            // Fallback to simple JSON if serialization fails
            return $"{{\"type\":\"{evt.Type}\",\"timestamp\":{evt.Timestamp}}}";
        }
    }

    /// <summary>
    /// Creates a project with the specified name and optional storage directory.
    /// </summary>
    /// <param name="namePtr">Pointer to UTF-8 string containing project name</param>
    /// <param name="storageDirectoryPtr">Pointer to UTF-8 string containing storage directory path, or IntPtr.Zero for default</param>
    /// <returns>Handle to the created Project, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "create_project")]
    public static IntPtr CreateProject(IntPtr namePtr, IntPtr storageDirectoryPtr)
    {
        try
        {
            string? name = Marshal.PtrToStringUTF8(namePtr);
            if (string.IsNullOrEmpty(name)) return IntPtr.Zero;

            string? storageDirectory = storageDirectoryPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(storageDirectoryPtr) : null;

            var project = Project.Create(name, storageDirectory);
            return ObjectManager.Add(project);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates a conversation within a project using the provided agents.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <param name="agentHandlesPtr">Pointer to array of agent handles</param>
    /// <param name="agentCount">Number of agents in the array</param>
    /// <returns>Handle to the created Conversation, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_create_conversation")]
    public static IntPtr ProjectCreateConversation(IntPtr projectHandle, IntPtr agentHandlesPtr, int agentCount)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            var agentHandles = new IntPtr[agentCount];
            Marshal.Copy(agentHandlesPtr, agentHandles, 0, agentCount);

            var agents = agentHandles.Select(ObjectManager.Get<Agent>).OfType<Agent>().ToList();
            if (!agents.Any())
            {
                throw new InvalidOperationException("No valid agents provided to create conversation.");
            }

            var conversation = agents.Count == 1 
                ? project.CreateConversation(agents.First())
                : project.CreateConversation(agents);

            return ObjectManager.Add(conversation);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Destroys a project and releases its resources.
    /// </summary>
    /// <param name="projectHandle">Handle to the project to destroy</param>
    [UnmanagedCallersOnly(EntryPoint = "destroy_project")]
    public static void DestroyProject(IntPtr projectHandle)
    {
        ObjectManager.Remove(projectHandle);
    }

    /// <summary>
    /// Gets project information as a JSON string.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <returns>Pointer to UTF-8 JSON string containing project info, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "get_project_info")]
    public static IntPtr GetProjectInfo(IntPtr projectHandle)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            var projectInfo = new ProjectInfo
            {
                Id = project.Id,
                Name = project.Name,
                Description = project.Description,
                ConversationCount = project.ConversationCount,
                CreatedAt = project.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                LastActivity = project.LastActivity.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };

            string json = JsonSerializer.Serialize(projectInfo, HPDJsonContext.Default.ProjectInfo);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets a conversation by ID from a project.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <param name="conversationIdPtr">Pointer to UTF-8 string containing the conversation ID</param>
    /// <returns>Handle to the conversation, or IntPtr.Zero if not found</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_get_conversation")]
    public static IntPtr ProjectGetConversation(IntPtr projectHandle, IntPtr conversationIdPtr)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            string? conversationId = Marshal.PtrToStringUTF8(conversationIdPtr);
            if (string.IsNullOrEmpty(conversationId)) return IntPtr.Zero;

            var conversation = project.GetConversation(conversationId);
            if (conversation == null) return IntPtr.Zero;

            return ObjectManager.Add(conversation);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Removes a conversation by ID from a project.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <param name="conversationIdPtr">Pointer to UTF-8 string containing the conversation ID</param>
    /// <returns>1 if removed successfully, 0 if not found or error</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_remove_conversation")]
    public static int ProjectRemoveConversation(IntPtr projectHandle, IntPtr conversationIdPtr)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            string? conversationId = Marshal.PtrToStringUTF8(conversationIdPtr);
            if (string.IsNullOrEmpty(conversationId)) return 0;

            bool removed = project.RemoveConversation(conversationId);
            return removed ? 1 : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets all conversation IDs in a project as a JSON array.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <returns>Pointer to UTF-8 JSON array of conversation IDs, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_get_conversation_ids")]
    public static IntPtr ProjectGetConversationIds(IntPtr projectHandle)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            var conversationIds = project.Conversations.Select(c => c.Id).ToList();
            string json = JsonSerializer.Serialize(conversationIds);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Uploads a document to the project from a file path.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <param name="filePathPtr">Pointer to UTF-8 string containing the file path</param>
    /// <param name="descriptionPtr">Pointer to UTF-8 string containing description, or IntPtr.Zero for none</param>
    /// <returns>Pointer to UTF-8 JSON string containing ProjectDocument, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_upload_document")]
    public static IntPtr ProjectUploadDocument(IntPtr projectHandle, IntPtr filePathPtr, IntPtr descriptionPtr)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            string? filePath = Marshal.PtrToStringUTF8(filePathPtr);
            if (string.IsNullOrEmpty(filePath)) return IntPtr.Zero;

            string? description = descriptionPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(descriptionPtr) : null;

            // Block on the async method for FFI
            var document = project.UploadDocumentAsync(filePath, description).GetAwaiter().GetResult();

            string json = JsonSerializer.Serialize(document);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Uploads a document to the project from a URL.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <param name="urlPtr">Pointer to UTF-8 string containing the URL</param>
    /// <param name="descriptionPtr">Pointer to UTF-8 string containing description, or IntPtr.Zero for none</param>
    /// <returns>Pointer to UTF-8 JSON string containing ProjectDocument, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_upload_document_from_url")]
    public static IntPtr ProjectUploadDocumentFromUrl(IntPtr projectHandle, IntPtr urlPtr, IntPtr descriptionPtr)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            string? url = Marshal.PtrToStringUTF8(urlPtr);
            if (string.IsNullOrEmpty(url)) return IntPtr.Zero;

            string? description = descriptionPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(descriptionPtr) : null;

            // Block on the async method for FFI
            var document = project.UploadDocumentFromUrlAsync(url, description).GetAwaiter().GetResult();

            string json = JsonSerializer.Serialize(document);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets the project summary with aggregated statistics.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <returns>Pointer to UTF-8 JSON string containing ProjectSummary, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_get_summary")]
    public static IntPtr ProjectGetSummary(IntPtr projectHandle)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            // Block on the async method for FFI
            var summary = project.GetSummaryAsync().GetAwaiter().GetResult();

            string json = JsonSerializer.Serialize(summary);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets the most recent conversation in the project.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <returns>Handle to the most recent conversation, or IntPtr.Zero if no conversations exist</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_get_most_recent_conversation")]
    public static IntPtr ProjectGetMostRecentConversation(IntPtr projectHandle)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            var conversation = project.GetMostRecentConversation();
            if (conversation == null) return IntPtr.Zero;

            return ObjectManager.Add(conversation);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Searches conversations in the project by text content.
    /// </summary>
    /// <param name="projectHandle">Handle to the project</param>
    /// <param name="searchTermPtr">Pointer to UTF-8 string containing the search term</param>
    /// <param name="maxResults">Maximum number of results to return</param>
    /// <returns>Pointer to UTF-8 JSON array of conversation IDs, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "project_search_conversations")]
    public static IntPtr ProjectSearchConversations(IntPtr projectHandle, IntPtr searchTermPtr, int maxResults)
    {
        try
        {
            var project = ObjectManager.Get<Project>(projectHandle);
            if (project == null) throw new InvalidOperationException("Project handle is invalid.");

            string? searchTerm = Marshal.PtrToStringUTF8(searchTermPtr);
            if (string.IsNullOrEmpty(searchTerm)) return IntPtr.Zero;

            var results = project.SearchConversations(searchTerm, maxResults);
            var conversationIds = results.Select(c => c.Id).ToList();

            string json = JsonSerializer.Serialize(conversationIds);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets the conversation thread ID.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <returns>Pointer to UTF-8 string containing the thread ID, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_get_id")]
    public static IntPtr ConversationGetId(IntPtr conversationHandle)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            return Marshal.StringToCoTaskMemAnsi(conversation.Id);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets the number of messages in the conversation thread.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <returns>Number of messages, or -1 on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_get_message_count")]
    public static int ConversationGetMessageCount(IntPtr conversationHandle)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            return conversation.Thread.Messages.Count;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Gets the conversation messages as a JSON array.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <returns>Pointer to UTF-8 JSON string containing messages array, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_get_messages")]
    public static IntPtr ConversationGetMessages(IntPtr conversationHandle)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            var messages = conversation.Thread.Messages.Select(m => new
            {
                role = m.Role.ToString(),
                text = m.Text,
                contents = m.Contents.Select(c => new
                {
                    type = c switch
                    {
                        Microsoft.Extensions.AI.TextContent => "text",
                        Microsoft.Extensions.AI.FunctionCallContent => "function_call",
                        Microsoft.Extensions.AI.FunctionResultContent => "function_result",
                        Microsoft.Extensions.AI.DataContent => "data",
                        _ => "unknown"
                    },
                    text = c is Microsoft.Extensions.AI.TextContent tc ? tc.Text : null,
                    callId = c is Microsoft.Extensions.AI.FunctionCallContent fcc ? fcc.CallId : null,
                    name = c is Microsoft.Extensions.AI.FunctionCallContent fcc2 ? fcc2.Name : null,
                    result = c is Microsoft.Extensions.AI.FunctionResultContent frc ? frc.Result?.ToString() : null
                }).ToList()
            }).ToList();

            string json = JsonSerializer.Serialize(messages);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets the conversation metadata as a JSON object.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <returns>Pointer to UTF-8 JSON string containing metadata, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_get_metadata")]
    public static IntPtr ConversationGetMetadata(IntPtr conversationHandle)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            var metadata = new
            {
                threadId = conversation.Id,
                createdAt = conversation.Thread.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                lastActivity = conversation.Thread.LastActivity.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                messageCount = conversation.Thread.Messages.Count,
                metadata = conversation.Thread.Metadata
            };

            string json = JsonSerializer.Serialize(metadata);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Adds a message to the conversation thread.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="messageJsonPtr">Pointer to UTF-8 JSON string containing the message</param>
    /// <returns>1 on success, 0 on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_add_message")]
    public static int ConversationAddMessage(IntPtr conversationHandle, IntPtr messageJsonPtr)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            string? messageJson = Marshal.PtrToStringUTF8(messageJsonPtr);
            if (string.IsNullOrEmpty(messageJson)) return 0;

            var message = JsonSerializer.Deserialize<ChatMessage>(messageJson);
            if (message == null) return 0;

            conversation.AddMessage(message);
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Adds metadata to the conversation thread.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="keyPtr">Pointer to UTF-8 string containing the metadata key</param>
    /// <param name="valueJsonPtr">Pointer to UTF-8 JSON string containing the metadata value</param>
    /// <returns>1 on success, 0 on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_add_metadata")]
    public static int ConversationAddMetadata(IntPtr conversationHandle, IntPtr keyPtr, IntPtr valueJsonPtr)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            string? key = Marshal.PtrToStringUTF8(keyPtr);
            if (string.IsNullOrEmpty(key)) return 0;

            string? valueJson = Marshal.PtrToStringUTF8(valueJsonPtr);
            if (string.IsNullOrEmpty(valueJson)) return 0;

            var value = JsonSerializer.Deserialize<object>(valueJson);
            if (value == null) return 0;

            conversation.AddMetadata(key, value);
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Clears all messages and metadata from the conversation thread.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <returns>1 on success, 0 on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_clear")]
    public static int ConversationClear(IntPtr conversationHandle)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            conversation.Thread.Clear();
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Applies history reduction to the conversation thread.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="summaryMessageJsonPtr">Pointer to UTF-8 JSON string containing the summary message, or IntPtr.Zero for none</param>
    /// <param name="removedCount">Number of messages to remove</param>
    /// <returns>1 on success, 0 on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_apply_reduction")]
    public static int ConversationApplyReduction(IntPtr conversationHandle, IntPtr summaryMessageJsonPtr, int removedCount)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            ChatMessage? summaryMessage = null;
            if (summaryMessageJsonPtr != IntPtr.Zero)
            {
                string? summaryJson = Marshal.PtrToStringUTF8(summaryMessageJsonPtr);
                if (!string.IsNullOrEmpty(summaryJson))
                {
                    summaryMessage = JsonSerializer.Deserialize<ChatMessage>(summaryJson);
                }
            }

            conversation.Thread.ApplyReduction(summaryMessage, removedCount);
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets a display name for the conversation thread.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <param name="maxLength">Maximum length of the display name</param>
    /// <returns>Pointer to UTF-8 string containing the display name, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_get_display_name")]
    public static IntPtr ConversationGetDisplayName(IntPtr conversationHandle, int maxLength)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            string displayName = conversation.Thread.GetDisplayName(maxLength);
            return Marshal.StringToCoTaskMemAnsi(displayName);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Serializes the conversation thread to a JSON snapshot.
    /// </summary>
    /// <param name="conversationHandle">Handle to the conversation</param>
    /// <returns>Pointer to UTF-8 JSON string containing the thread snapshot, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "conversation_serialize_thread")]
    public static IntPtr ConversationSerializeThread(IntPtr conversationHandle)
    {
        try
        {
            var conversation = ObjectManager.Get<Conversation>(conversationHandle);
            if (conversation == null) throw new InvalidOperationException("Conversation handle is invalid.");

            var snapshot = conversation.Thread.Serialize();
            string json = JsonSerializer.Serialize(snapshot);
            return Marshal.StringToCoTaskMemAnsi(json);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates a conversation with an existing thread from a JSON snapshot.
    /// </summary>
    /// <param name="agentHandlesPtr">Pointer to array of agent handles</param>
    /// <param name="agentCount">Number of agents in the array</param>
    /// <param name="threadSnapshotJsonPtr">Pointer to UTF-8 JSON string containing the thread snapshot</param>
    /// <returns>Handle to the created Conversation, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "create_conversation_with_thread")]
    public static IntPtr CreateConversationWithThread(IntPtr agentHandlesPtr, int agentCount, IntPtr threadSnapshotJsonPtr)
    {
        try
        {
            var agentHandles = new IntPtr[agentCount];
            Marshal.Copy(agentHandlesPtr, agentHandles, 0, agentCount);

            var agents = agentHandles.Select(ObjectManager.Get<Agent>).OfType<Agent>().ToList();
            if (!agents.Any())
            {
                throw new InvalidOperationException("No valid agents provided to create conversation.");
            }

            string? snapshotJson = Marshal.PtrToStringUTF8(threadSnapshotJsonPtr);
            if (string.IsNullOrEmpty(snapshotJson))
            {
                throw new InvalidOperationException("Thread snapshot JSON is null or empty.");
            }

            var snapshot = JsonSerializer.Deserialize<ConversationThreadSnapshot>(snapshotJson);
            if (snapshot == null)
            {
                throw new InvalidOperationException("Failed to deserialize thread snapshot.");
            }

            var thread = ConversationThread.Deserialize(snapshot);
            var conversation = new Conversation(agents, thread);

            return ObjectManager.Add(conversation);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }
}

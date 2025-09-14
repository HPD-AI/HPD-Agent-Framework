using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HPD_Agent.FFI;

/// <summary>
/// Delegate for streaming callback from C# to Rust
/// </summary>
/// <param name="context">Context pointer passed back to Rust</param>
/// <param name="eventJsonPtr">Pointer to UTF-8 JSON string of the event, or null to signal end of stream</param>
public delegate void StreamCallback(IntPtr context, IntPtr eventJsonPtr);

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
    /// <param name="pluginsJsonPtr">Pointer to JSON string containing plugin definitions (currently unused)</param>
    /// <returns>Handle to the created Agent, or IntPtr.Zero on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "create_agent_with_plugins")]
    public static IntPtr CreateAgentWithPlugins(IntPtr configJsonPtr, IntPtr pluginsJsonPtr)
    {
        try
        {
            string? configJson = Marshal.PtrToStringUTF8(configJsonPtr);
            if (string.IsNullOrEmpty(configJson)) return IntPtr.Zero;

            // Deserialize the config from Rust
            var agentConfig = JsonSerializer.Deserialize<AgentConfig>(configJson, HPDJsonContext.Default.AgentConfig);
            if (agentConfig == null) return IntPtr.Zero;

            var builder = new AgentBuilder(agentConfig);
            
            // This is a placeholder for where Rust plugins will be added.
            // builder.AddRustPlugins(...); 

            var agent = builder.Build();
            return ObjectManager.Add(agent);
        }
        catch (Exception ex)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Helper method to get a plugin Type by its name.
    /// This searches loaded assemblies for the plugin type.
    /// </summary>
    private static Type? GetPluginTypeByName(string pluginTypeName)
    {
        try
        {
            // First try exact type name
            var type = Type.GetType(pluginTypeName);
            if (type != null) return type;

            // Search through loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(pluginTypeName);
                if (type != null) return type;

                // Also try just the class name (without namespace)
                type = assembly.GetTypes().FirstOrDefault(t => t.Name == pluginTypeName);
                if (type != null) return type;
            }

            return null;
        }
        catch (Exception ex)
        {
            return null;
        }
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

            var agents = agentHandles.Select(ObjectManager.Get<Agent>).Where(a => a != null).ToList();
            if (!agents.Any())
            {
                throw new InvalidOperationException("No valid agents provided to create conversation.");
            }

            var conversation = new Conversation(agents.First()); // Simplified for now
            return ObjectManager.Add(conversation);
        }
        catch (Exception ex)
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
        catch (Exception ex)
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
                await foreach (var evt in conversation.SendStreamingAsync(message, null))
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
        catch (Exception ex)
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

            var agents = agentHandles.Select(ObjectManager.Get<Agent>).Where(a => a != null).ToList();
            if (!agents.Any())
            {
                throw new InvalidOperationException("No valid agents provided to create conversation.");
            }

            var conversation = agents.Count == 1 
                ? project.CreateConversation(agents.First())
                : project.CreateConversation(agents);

            return ObjectManager.Add(conversation);
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            return IntPtr.Zero;
        }
    }
}

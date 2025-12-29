using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using HPD.Agent;
using HPD.Agent;
using HPD.Agent.ClientTools;
using HPD.Agent.Memory;

var builder = WebApplication.CreateSlimBuilder(args);


// Configure JSON serialization - chain app context with library context
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, HPDJsonContext.Default);
});

// CORS Configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClient", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(origin => new Uri(origin).Host == "localhost")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.WithOrigins("http://localhost:5173", "http://localhost:4173", "http://localhost:3000")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// Register checkpointing services
var threadStore = new JsonSessionStore(
    Path.Combine(Environment.CurrentDirectory, "threads"));

builder.Services.AddSingleton<ISessionStore>(threadStore);
builder.Services.AddSingleton<ConversationManager>();

var app = builder.Build();

// Validate checkpointing configuration on startup
ValidateCheckpointingConfiguration(app.Services);

app.UseCors("AllowClient");
app.UseWebSockets();

// Conversation API
var conversationsApi = app.MapGroup("/conversations").WithTags("Conversations");

conversationsApi.MapPost("/", async (ConversationManager cm) =>
{
    var thread = await cm.CreateConversationAsync();
    return Results.Created($"/conversations/{thread.Id}", new ConversationDto(
        thread.Id,
        thread.DisplayName ?? "New Conversation",
        thread.CreatedAt,
        thread.LastActivity,
        thread.MessageCount));
});

conversationsApi.MapGet("/{conversationId}", async (string conversationId, ConversationManager cm) =>
{
    var thread = await cm.GetConversationAsync(conversationId);
    return thread is not null
        ? Results.Ok(new ConversationDto(
            thread.Id,
            thread.DisplayName ?? "New Conversation",
            thread.CreatedAt,
            thread.LastActivity,
            thread.MessageCount))
        : Results.NotFound();
});

conversationsApi.MapDelete("/{conversationId}", async (string conversationId, ConversationManager cm) =>
{
    var deleted = await cm.DeleteConversationAsync(conversationId);
    return deleted ? Results.NoContent() : Results.NotFound();
});

// List all persisted conversations
conversationsApi.MapGet("/", async (ConversationManager cm) =>
{
    var ids = await cm.ListConversationIdsAsync();
    var conversations = new List<ConversationDto>();

    foreach (var id in ids)
    {
        var thread = await cm.GetConversationAsync(id);
        if (thread != null)
        {
            conversations.Add(new ConversationDto(
                thread.Id,
                thread.DisplayName ?? "Conversation",
                thread.CreatedAt,
                thread.LastActivity,
                thread.MessageCount));
        }
    }

    return Results.Ok(conversations);
});

// Get messages for a conversation
conversationsApi.MapGet("/{conversationId}/messages", async (string conversationId, ConversationManager cm) =>
{
    Console.WriteLine($"[MESSAGES] Getting messages for conversation {conversationId}");
    
    var thread = await cm.GetConversationAsync(conversationId);
    if (thread == null)
    {
        Console.WriteLine($"[MESSAGES] Conversation not found");
        return Results.NotFound(new ErrorResponse("Conversation not found"));
    }

    Console.WriteLine($"[MESSAGES] Thread has {thread.MessageCount} messages");
    var messages = thread.Messages.Select((m, i) => new MessageDto(
        i,
        m.Role.Value,
        m.Text ?? "",
        m.AdditionalProperties?.TryGetValue("thinking", out var thinking) == true ? thinking?.ToString() : null
    )).ToList();

    foreach (var msg in messages.Take(10))
    {
        var preview = msg.Content?.Length > 50 ? msg.Content.Substring(0, 50) + "..." : msg.Content;
        Console.WriteLine($"[MESSAGES]   [{msg.Index}] {msg.Role}: {preview}");
    }

    return Results.Ok(messages);
});

// Agent API
var agentApi = app.MapGroup("/agent").WithTags("Agent");

// Permission response endpoint
agentApi.MapPost("/conversations/{conversationId}/permissions/respond",
    (string conversationId, PermissionResponseRequest request, ConversationManager cm) =>
{
    var agent = cm.GetRunningAgent(conversationId);
    if (agent == null)
        return Results.NotFound(new ErrorResponse("No active agent for this conversation"));

    // Convert string choice to PermissionChoice enum
    var choice = request.Choice?.ToLower() switch
    {
        "allow_always" => PermissionChoice.AlwaysAllow,
        "deny_always" => PermissionChoice.AlwaysDeny,
        _ => PermissionChoice.Ask
    };

    // Send response to waiting permission middleware
    agent.SendMiddlewareResponse(
        request.PermissionId,
        new PermissionResponseEvent(
            request.PermissionId,
            "PermissionMiddleware",
            request.Approved,
            request.Reason,
            choice));

    return Results.Ok(new SuccessResponse(true));
});

// Client tool response endpoint
agentApi.MapPost("/conversations/{conversationId}/Client-tools/respond",
    (string conversationId, ClientToolResponseRequest request, ConversationManager cm) =>
{
    var agent = cm.GetRunningAgent(conversationId);
    if (agent == null)
        return Results.NotFound(new ErrorResponse("No active agent for this conversation"));

    // Convert content to IToolResultContent list
    var content = request.Content?.Select<ClientToolContentDto, HPD.Agent.ClientTools.IToolResultContent>(c => c.Type switch
    {
        "text" => new HPD.Agent.ClientTools.TextContent(c.Text ?? ""),
        "json" => new HPD.Agent.ClientTools.JsonContent(
            JsonSerializer.SerializeToElement(c.Value ?? new object())),
        "binary" => new BinaryContent(
            c.MimeType ?? "application/octet-stream",
            c.Data,
            c.Url,
            c.Id,
            c.Filename),
        _ => new HPD.Agent.ClientTools.TextContent(c.Text ?? "")
    }).ToList() ?? new List<IToolResultContent>();

    // Send response to waiting ClientToolMiddleware
    agent.SendMiddlewareResponse(
        request.RequestId,
        new ClientToolInvokeResponseEvent(
            RequestId: request.RequestId,
            Content: content,
            Success: request.Success,
            ErrorMessage: request.ErrorMessage,
            Augmentation: null)); // TODO: Add augmentation support if needed

    return Results.Ok(new SuccessResponse(true));
});

// Streaming SSE endpoint
agentApi.MapPost("/conversations/{conversationId}/stream",
    async (string conversationId, StreamRequest request, ConversationManager cm, HttpContext context) =>
{
    var thread = await cm.GetConversationAsync(conversationId);
    if (thread == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Ensure events use conversationId (not internal threadId)
    // Client expects consistent conversationId across all requests
    thread.ConversationId = conversationId;

    // SSE headers
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var writer = new StreamWriter(context.Response.Body, System.Text.Encoding.UTF8, 8192, leaveOpen: true);
    var sseHandler = new SseEventHandler(writer);

    try
    {
        // Create agent WITHOUT SSE handler - we'll handle events manually in the loop below
        var agent = await cm.GetAgentAsync(conversationId);
        var chatMessages = request.Messages.Select(m => new ChatMessage(ChatRole.User, m.Content)).ToList();

        // Convert StreamRequest to AgentRunInput for Client tools
        var runInput = BuildRunInput(request);

        Console.WriteLine($"[ENDPOINT] Starting agent.RunAsync for conversation {conversationId}");
        if (runInput?.ClientToolGroups?.Count > 0)
        {
            Console.WriteLine($"[ENDPOINT] Registered {runInput.ClientToolGroups.Count} Client plugin(s)");
            foreach (var plugin in runInput.ClientToolGroups)
            {
                Console.WriteLine($"[ENDPOINT]   - {plugin.Name}: {plugin.Tools.Count} tools");
            }
        }

        // Run agent - manually send each event through SSE handler
        int eventCount = 0;
        var runOptions = runInput != null ? new AgentRunOptions { ClientToolInput = runInput } : null;
        await foreach (var evt in agent.RunAsync(chatMessages, session: thread, options: runOptions, cancellationToken: context.RequestAborted))
        {
            eventCount++;
            Console.WriteLine($"[ENDPOINT] Yielded event #{eventCount}: {evt.GetType().Name}");

            // Send event to Client via SSE
            await sseHandler.OnEventAsync(evt, context.RequestAborted);
        }

        Console.WriteLine($"[ENDPOINT] Completed agent.RunAsync - total events: {eventCount}");

        // Send completion event using standard format
        await writer.WriteAsync("data: {\"version\":\"1.0\",\"type\":\"COMPLETE\"}\n\n");
        await writer.FlushAsync();
    }
    catch (Exception ex)
    {
        // Send error event using standard format
        var errorMessage = ex.Message.Replace("\"", "\\\"");
        await writer.WriteAsync($"data: {{\"version\":\"1.0\",\"type\":\"ERROR\",\"message\":\"{errorMessage}\"}}\n\n");
        await writer.FlushAsync();
    }
    finally
    {
        await writer.DisposeAsync();
    }
});

// WebSocket endpoint
agentApi.MapGet("/conversations/{conversationId}/ws",
    async (string conversationId, HttpContext context, ConversationManager cm) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var thread = await cm.GetConversationAsync(conversationId);

    if (thread == null)
    {
        await webSocket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "Conversation not found",
            CancellationToken.None);
        return;
    }

    // Ensure events use conversationId (not internal threadId)
    thread.ConversationId = conversationId;

    try
    {
        var agent = await cm.GetAgentAsync(conversationId);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };

        // Wait for initial message
        var buffer = new byte[1024 * 4];
        var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var userMessage = System.Text.Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

        var chatMessage = new ChatMessage(ChatRole.User, userMessage);

        await foreach (var evt in agent.RunAsync(new[] { chatMessage }, session: thread, options: null, cancellationToken: CancellationToken.None))
        {
            if (evt is TextDeltaEvent textDelta)
            {
                var response = new { text = textDelta.Text };
                var message = JsonSerializer.Serialize(response, jsonOptions);
                var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    System.Net.WebSockets.WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            else if (evt is MessageTurnFinishedEvent)
            {
                var response = new { finished = true, reason = "Stop" };
                var finishMessage = JsonSerializer.Serialize(response, jsonOptions);
                var finishBytes = System.Text.Encoding.UTF8.GetBytes(finishMessage);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(finishBytes),
                    System.Net.WebSockets.WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        await webSocket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "Completed",
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        await webSocket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.InternalServerError,
            $"Error: {ex.Message}",
            CancellationToken.None);
    }
});

// Build AgentRunInput directly from StreamRequest
static AgentRunInput? BuildRunInput(StreamRequest request)
{
    if (request.ClientToolGroups == null && request.Context == null &&
        request.State == null && request.ExpandedContainers == null && request.HiddenTools == null)
    {
        return null;
    }

    return new AgentRunInput
    {
        ClientToolGroups = request.ClientToolGroups?.ToList(),
        Context = request.Context?.ToList(),
        State = request.State,
        ExpandedContainers = request.ExpandedContainers?.ToHashSet(),
        HiddenTools = request.HiddenTools?.ToHashSet(),
        ResetClientState = request.ResetClientState
    };
}

app.Run();

/// <summary>
/// Validates that checkpointing services are properly configured.
/// Validates that thread store is properly configured.
/// </summary>
static void ValidateCheckpointingConfiguration(IServiceProvider services)
{
    try
    {
        var threadStore = services.GetRequiredService<ISessionStore>();
        Console.WriteLine("\n✓ Thread store configured:");
        Console.WriteLine($"  - Thread store: {threadStore.GetType().Name}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n⚠ Warning: Could not validate thread store configuration: {ex.Message}\n");
    }
}

// Conversation Manager
internal class ConversationManager
{
    private readonly ISessionStore _threadStore;
    private readonly Dictionary<string, AgentSession> _threadCache = new(); // In-memory cache
    private readonly Dictionary<string, Agent> _runningAgents = new(); // Track active agents
    // Note: Permission storage is now automatic per-session in MiddlewareState

    public ConversationManager(ISessionStore threadStore)
    {
        _threadStore = threadStore;
    }

    public async Task<AgentSession> CreateConversationAsync()
    {
        var thread = new AgentSession();
        await _threadStore.SaveSessionAsync(thread);
        _threadCache[thread.Id] = thread;
        return thread;
    }

    public async Task<AgentSession?> GetConversationAsync(string conversationId)
    {
        // Check cache first
        if (_threadCache.TryGetValue(conversationId, out var cached))
            return cached;

        var thread = await _threadStore.LoadSessionAsync(conversationId);
        if (thread != null)
            _threadCache[conversationId] = thread;

        return thread;
    }

    public async Task<bool> DeleteConversationAsync(string conversationId)
    {
        _threadCache.Remove(conversationId);
        _runningAgents.Remove(conversationId);
        await _threadStore.DeleteSessionAsync(conversationId);
        return true;
    }

    public async Task<List<string>> ListConversationIdsAsync()
    {
        return await _threadStore.ListSessionIdsAsync();
    }

    public async Task<Agent> GetAgentAsync(string conversationId, IAgentEventHandler? eventHandler = null)
    {
        var builder = new AgentBuilder()
            .WithProvider("openrouter", "z-ai/glm-4.6")
            .WithName("AI Assistant")
            .WithInstructions("You are a helpful AI assistant with file system access and memory capabilities.")
            .WithDynamicMemory(opts => opts
                .WithStorageDirectory("./agent-memory-storage")
                .WithMaxTokens(6000))
            .WithTools<MathTools>()
            .WithPermissions(); // ← Enable permission system! (Automatic per-session persistence)

        if (eventHandler != null)
            builder = builder.WithEventHandler(eventHandler);

        var agent = await builder.Build();

        // Store agent reference for permission responses
        _runningAgents[conversationId] = agent;

        return agent;
    }

    public Agent? GetRunningAgent(string conversationId) =>
        _runningAgents.GetValueOrDefault(conversationId);
}

// DTOs
public record ConversationDto(
    string Id,
    string Name,
    DateTime CreatedAt,
    DateTime LastActivity,
    int MessageCount,
    string? ActiveBranch = null,
    List<string>? BranchNames = null);
public record StreamRequest(
    StreamMessage[] Messages,
    ClientToolGroupDefinition[]? ClientToolGroups = null,
    ContextItem[]? Context = null,
    JsonElement? State = null,
    string[]? ExpandedContainers = null,
    string[]? HiddenTools = null,
    bool ResetClientState = false);
public record StreamMessage(string Content);
public record PermissionResponseRequest(
    string PermissionId,
    bool Approved,
    string? Choice = null,
    string? Reason = null);

// API Response DTOs for AOT serialization
public record SuccessResponse(bool Success);
public record ErrorResponse(string Message);

// Client tool response DTOs
public record ClientToolResponseRequest(
    string RequestId,
    ClientToolContentDto[]? Content,
    bool Success = true,
    string? ErrorMessage = null);

public record ClientToolContentDto(
    string Type,
    string? Text = null,
    object? Value = null,
    string? MimeType = null,
    string? Data = null,
    string? Url = null,
    string? Id = null,
    string? Filename = null);

public record MessageDto(int Index, string Role, string Content, string? Thinking = null);


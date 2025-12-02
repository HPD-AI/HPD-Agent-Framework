using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using HPD.Agent;
using HPD.Agent.Permissions;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// CORS Configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
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

// Register services
builder.Services.AddSingleton<ConversationManager>();

var app = builder.Build();
app.UseCors("AllowFrontend");
app.UseWebSockets();

// Conversation API
var conversationsApi = app.MapGroup("/conversations").WithTags("Conversations");

conversationsApi.MapPost("/", (ConversationManager cm) =>
{
    var thread = cm.CreateConversation();
    return Results.Created($"/conversations/{thread.Id}", new ConversationDto(
        thread.Id,
        thread.DisplayName ?? "New Conversation",
        thread.CreatedAt,
        thread.LastActivity,
        thread.MessageCount));
});

conversationsApi.MapGet("/{conversationId}", (string conversationId, ConversationManager cm) =>
    cm.GetConversation(conversationId) is { } thread
        ? Results.Ok(new ConversationDto(
            thread.Id,
            thread.DisplayName ?? "New Conversation",
            thread.CreatedAt,
            thread.LastActivity,
            thread.MessageCount))
        : Results.NotFound());

conversationsApi.MapDelete("/{conversationId}", (string conversationId, ConversationManager cm) =>
    cm.DeleteConversation(conversationId) ? Results.NoContent() : Results.NotFound());

// Agent API
var agentApi = app.MapGroup("/agent").WithTags("Agent");

// Permission response endpoint
agentApi.MapPost("/conversations/{conversationId}/permissions/respond",
    (string conversationId, PermissionResponseRequest request, ConversationManager cm) =>
{
    var agent = cm.GetRunningAgent(conversationId);
    if (agent == null)
        return Results.NotFound(new { message = "No active agent for this conversation" });

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

    return Results.Ok(new { success = true });
});

// Streaming SSE endpoint
agentApi.MapPost("/conversations/{conversationId}/stream",
    async (string conversationId, StreamRequest request, ConversationManager cm, HttpContext context) =>
{
    var thread = cm.GetConversation(conversationId);
    if (thread == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    // SSE headers
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var writer = new StreamWriter(context.Response.Body, System.Text.Encoding.UTF8, 8192, leaveOpen: true);
    var sseHandler = new SseEventHandler(writer);
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    try
    {
        // Create agent with SSE handler
        var agent = await cm.GetAgentAsync(conversationId, sseHandler);
        var chatMessages = request.Messages.Select(m => new ChatMessage(ChatRole.User, m.Content)).ToList();

        Console.WriteLine($"[ENDPOINT] 🚀 Starting agent.RunAsync for conversation {conversationId}");

        // Run agent - events are automatically formatted as SSE by the handler
        int eventCount = 0;
        await foreach (var evt in agent.RunAsync(chatMessages, options: null, thread: thread, cancellationToken: context.RequestAborted))
        {
            eventCount++;
            Console.WriteLine($"[ENDPOINT] 🔄 Yielded event #{eventCount}: {evt.GetType().Name}");
        }

        Console.WriteLine($"[ENDPOINT] ✅ Completed agent.RunAsync - total events: {eventCount}");

        // Send completion event
        var completeJson = JsonSerializer.Serialize(new { type = "complete" }, jsonOptions);
        await writer.WriteAsync($"data: {completeJson}\n\n");
        await writer.FlushAsync();
    }
    catch (Exception ex)
    {
        var errorJson = JsonSerializer.Serialize(new { type = "error", data = new { message = ex.Message } }, jsonOptions);
        await writer.WriteAsync($"data: {errorJson}\n\n");
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
    var thread = cm.GetConversation(conversationId);

    if (thread == null)
    {
        await webSocket.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
            "Conversation not found",
            CancellationToken.None);
        return;
    }

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

        await foreach (var evt in agent.RunAsync(new[] { chatMessage }, options: null, thread: thread, cancellationToken: CancellationToken.None))
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

app.Run();

// Conversation Manager
internal class ConversationManager
{
    private readonly Dictionary<string, ConversationThread> _conversations = new();
    private readonly Dictionary<string, Agent> _runningAgents = new(); // Track active agents
    private readonly InMemoryPermissionStorage _permissionStorage = new();

    public ConversationThread CreateConversation()
    {
        var thread = new ConversationThread();
        _conversations[thread.Id] = thread;
        return thread;
    }

    public ConversationThread? GetConversation(string conversationId) =>
        _conversations.GetValueOrDefault(conversationId);

    public bool DeleteConversation(string conversationId)
    {
        _conversations.Remove(conversationId);
        _runningAgents.Remove(conversationId); // Clean up agent reference
        return true;
    }

    public async Task<Agent> GetAgentAsync(string conversationId, IAgentEventHandler? eventHandler = null)
    {
        var builder = new AgentBuilder()
            .WithProvider("openrouter", "google/gemini-2.5-pro")
            .WithName("AI Assistant")
            .WithInstructions("You are a helpful AI assistant with file system access and memory capabilities.")
            .WithDynamicMemory(opts => opts
                .WithStorageDirectory("./agent-memory-storage")
                .WithMaxTokens(6000))
            .WithPlugin<MathPlugin>()
            .WithPermissions(_permissionStorage); // ← Enable permission system!

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

// Simple in-memory permission storage
internal class InMemoryPermissionStorage : IPermissionStorage
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PermissionChoice> _permissions = new();

    public Task<PermissionChoice?> GetStoredPermissionAsync(string functionName, string? conversationId = null)
    {
        // Try conversation-scoped first
        if (!string.IsNullOrEmpty(conversationId))
        {
            var conversationKey = $"conv:{conversationId}:{functionName}";
            if (_permissions.TryGetValue(conversationKey, out var conversationPerm))
                return Task.FromResult<PermissionChoice?>(conversationPerm);
        }

        // Try global-scoped
        var globalKey = $"global:{functionName}";
        if (_permissions.TryGetValue(globalKey, out var globalPerm))
            return Task.FromResult<PermissionChoice?>(globalPerm);

        return Task.FromResult<PermissionChoice?>(null);
    }

    public Task SavePermissionAsync(string functionName, PermissionChoice choice, string? conversationId = null)
    {
        if (choice != PermissionChoice.Ask)
        {
            var key = string.IsNullOrEmpty(conversationId)
                ? $"global:{functionName}"
                : $"conv:{conversationId}:{functionName}";
            _permissions[key] = choice;
        }
        return Task.CompletedTask;
    }
}

// DTOs
public record ConversationDto(string Id, string Name, DateTime CreatedAt, DateTime LastActivity, int MessageCount);
public record StreamRequest(StreamMessage[] Messages);
public record StreamMessage(string Content);
public record PermissionResponseRequest(
    string PermissionId,
    bool Approved,
    string? Choice = null,
    string? Reason = null);


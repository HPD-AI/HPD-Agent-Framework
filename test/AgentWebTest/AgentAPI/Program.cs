using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using HPD.Agent;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonResolvers.Combined);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        // During local development allow local frontends to access the API.
        // If running in Production, restrict origins explicitly.
        if (builder.Environment.IsDevelopment())
        {
            // Allow any local origin (useful for different localhost ports)
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

// ✨ SIMPLIFIED: Register services with clean architecture
builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
builder.Services.AddSingleton<ConversationManager>();

var app = builder.Build();
app.UseCors("AllowFrontend");

// 🚀 WEBSOCKET SUPPORT: Enable WebSocket middleware
app.UseWebSockets();

// 🎯 CLEAN CONVERSATION API
var conversationsApi = app.MapGroup("/conversations").WithTags("Conversations");

conversationsApi.MapGet("/", (ConversationManager cm) =>
    Results.Ok(cm.ListConversations().Select(ToThreadDto)));

conversationsApi.MapPost("/", (CreateConversationRequest request, ConversationManager cm) =>
{
    var thread = cm.CreateConversation();
    if (!string.IsNullOrEmpty(request.Name))
        thread.AddMetadata("DisplayName", request.Name);

    return Results.Created($"/conversations/{thread.Id}", ToThreadDto(thread));
});

conversationsApi.MapGet("/{conversationId}", (string conversationId, ConversationManager cm) =>
    cm.GetConversation(conversationId) is { } thread
        ? Results.Ok(ToThreadWithMessagesDto(thread))
        : Results.NotFound());

conversationsApi.MapDelete("/{conversationId}", (string conversationId, ConversationManager cm) =>
{
    if (cm.DeleteConversation(conversationId))
        return Results.NoContent();

    return Results.NotFound();
});

// 🎯 CLEAN AGENT API
var agentApi = app.MapGroup("/agent").WithTags("Agent");

// ✨ SIMPLIFIED: Context-aware chat using CORE agent with AgentEvent
agentApi.MapPost("/conversations/{conversationId}/chat",
    async (string conversationId, ChatRequest request, ConversationManager cm) =>
{
    if (cm.GetConversation(conversationId) is not { } thread)
        return Results.NotFound();

    // Create agent for this request
    var agent = cm.CreateAgent();

    // 🚀 Use CORE agent RunAsync directly with thread - collect events
    var userMessage = new ChatMessage(ChatRole.User, request.Message);

    string responseText = "";
    await foreach (var evt in agent.RunAsync(
        new[] { userMessage },
        options: null,
        thread: thread))
    {
        // Collect text content from TextDeltaEvent
        if (evt is TextDeltaEvent textDelta)
        {
            responseText += textDelta.Text;
        }
    }

    // Return collected response
    var chatResponse = new AgentChatResponse(
        Response: responseText,
        Model: "google/gemini-2.5-pro",
        Usage: new UsageInfo(0, 0, 0)); // Usage not available in streaming mode

    return Results.Ok(chatResponse);
});

// ✨ STREAMING: Server-Sent Events endpoint using CORE agent AgentEvent stream
agentApi.MapPost("/conversations/{conversationId}/stream",
    async (string conversationId, StreamRequest streamRequest, ConversationManager cm, HttpContext context) =>
{
    var thread = cm.GetConversation(conversationId);
    if (thread == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Create agent for this request
    var agent = cm.CreateAgent();

    // Prepare SSE headers (CORS handled by middleware)
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    // Use UTF8 encoding with larger buffer to prevent truncation of large JSON payloads
    var writer = new StreamWriter(context.Response.Body, encoding: System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);

    try
    {
        // Convert request messages to ChatMessage
        var chatMessages = streamRequest.Messages.Select(m =>
            new ChatMessage(ChatRole.User, m.Content)
        ).ToList();

        // ✅ Use CORE agent RunAsync - stream AgentEvent
        await foreach (var evt in agent.RunAsync(chatMessages, options: null, thread: thread, cancellationToken: context.RequestAborted))
        {
            // Convert AgentEvent to SSE format
            string? eventType = null;
            object? eventData = null;

            switch (evt)
            {
                case TextDeltaEvent textDelta:
                    eventType = "text_delta";
                    eventData = new { text = textDelta.Text };
                    break;

                case Reasoning reasoning when reasoning.Phase == ReasoningPhase.Delta:
                    eventType = "reasoning_delta";
                    eventData = new { text = reasoning.Text };
                    break;

                case ToolCallStartEvent toolStart:
                    eventType = "tool_call_start";
                    eventData = new { name = toolStart.Name, call_id = toolStart.CallId };
                    break;

                case ToolCallResultEvent toolResult:
                    eventType = "tool_call_result";
                    eventData = new { call_id = toolResult.CallId };
                    break;

                case AgentTurnStartedEvent turnStart:
                    eventType = "agent_turn_started";
                    eventData = new { iteration = turnStart.Iteration };
                    break;

                case AgentTurnFinishedEvent turnFinished:
                    eventType = "agent_turn_finished";
                    eventData = new { iteration = turnFinished.Iteration };
                    break;

                case MessageTurnFinishedEvent:
                    eventType = "message_turn_finished";
                    eventData = new { };
                    break;
            }

            if (eventType != null && eventData != null)
            {
                var eventJson = JsonSerializer.Serialize(new { type = eventType, data = eventData });
                await writer.WriteAsync($"data: {eventJson}\n\n");
                await writer.FlushAsync();
            }
        }

        // Send completion event
        var completeJson = JsonSerializer.Serialize(new { type = "complete" });
        await writer.WriteAsync($"data: {completeJson}\n\n");
        await writer.FlushAsync();
    }
    catch (Exception ex)
    {
        var errorJson = JsonSerializer.Serialize(new { type = "error", data = new { message = ex.Message } });
        await writer.WriteAsync($"data: {errorJson}\n\n");
        await writer.FlushAsync();
    }
    finally
    {
        await writer.DisposeAsync();
    }
});

// 🚀 WEBSOCKET: Real-time bi-directional streaming using CORE agent
agentApi.MapGet("/conversations/{conversationId}/ws",
    async (string conversationId, HttpContext context, ConversationManager cm) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
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
            // Create agent for this request
            var agent = cm.CreateAgent();

            // Wait for an initial message from the client to start the stream
            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            var userMessage = System.Text.Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

            // Use CORE agent.RunAsync directly with thread
            var chatMessage = new ChatMessage(ChatRole.User, userMessage);

            // Stream AgentEvent to WebSocket
            await foreach (var evt in agent.RunAsync(new[] { chatMessage }, options: null, thread: thread, cancellationToken: CancellationToken.None))
            {
                // Extract text content and send via WebSocket
                if (evt is TextDeltaEvent textDelta)
                {
                    var response = new StreamContentResponse(textDelta.Text);
                    var message = JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.StreamContentResponse);
                    var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(messageBytes),
                        System.Net.WebSockets.WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                else if (evt is MessageTurnFinishedEvent)
                {
                    // Send finish event when turn completes
                    var response = new StreamFinishResponse(true, "Stop");
                    var finishMessage = JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.StreamFinishResponse);
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
            // Handle errors and ensure the socket is closed gracefully
            await webSocket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.InternalServerError,
                $"An error occurred: {ex.Message}",
                CancellationToken.None);
        }
    }
    else
    {
        context.Response.StatusCode = 400; // Bad Request
    }
});


app.Run();

// ✨ CLEAN HELPER FUNCTIONS (Updated for stateless pattern)
static ConversationDto ToThreadDto(ConversationThread t) => new(t.Id, t.DisplayName ?? "New Conversation", t.CreatedAt, t.LastActivity, Task.Run(() => t.GetMessageCountAsync()).Result);

static ConversationWithMessagesDto ToThreadWithMessagesDto(ConversationThread t)
{
    var messageCount = Task.Run(() => t.GetMessageCountAsync()).Result;
    return new(
        t.Id,
        t.DisplayName ?? "New Conversation",
        t.CreatedAt,
        t.LastActivity,
        messageCount,
        Array.Empty<ConversationMessageDto>());  // Messages not accessible via public API
}

static string ExtractTextFromMessage(ChatMessage message)
{
    var textContents = message.Contents
        .OfType<TextContent>()
        .Select(tc => tc.Text)
        .Where(text => !string.IsNullOrEmpty(text));
    return string.Join(" ", textContents);
}

// ✨ CLEAN CONVERSATION MANAGER (Simplified using CORE agent)
// Internal since it uses internal types (Agent, ConversationThread) via InternalsVisibleTo
internal class ConversationManager
{
    private readonly ConcurrentDictionary<string, ConversationThread> _conversations = new();
    private readonly IConfiguration _config;
    private Agent? _cachedAgent;

    public ConversationManager(IConfiguration config) => _config = config;

    // ✨ Create a new conversation thread
    public ConversationThread CreateConversation()
    {
        var agent = CreateAgent();
        var thread = agent.CreateThread();
        _conversations[thread.Id] = thread;
        return thread;
    }

    public ConversationThread? GetConversation(string conversationId) =>
        _conversations.GetValueOrDefault(conversationId);

    public IEnumerable<ConversationThread> ListConversations() =>
        _conversations.Values.OrderByDescending(t => t.LastActivity);

    public bool DeleteConversation(string conversationId) =>
        _conversations.TryRemove(conversationId, out _);

    // ✨ CLEAN: Agent creation using CORE agent (internal access via InternalsVisibleTo)
    [RequiresUnreferencedCode("AgentBuilder.Build uses reflection for plugin registration")]
    public Agent CreateAgent()
    {
        if (_cachedAgent != null)
            return _cachedAgent;

        // ✨ Load configuration from appsettings.json
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        _cachedAgent = AgentBuilder.Create()
            .WithAPIConfiguration(config)
            .WithName("AI Assistant")
            .WithLogging() // Global logging filter
            .WithProvider("openrouter", "google/gemini-2.5-pro")
            .WithInstructions("You are a helpful AI assistant with memory, knowledge base, and web search capabilities.")
            .WithDynamicMemory(opts => opts
                .WithStorageDirectory("./agent-memory-storage")
                .WithMaxTokens(6000))
            .WithTavilyWebSearch()
            .WithPlugin<MathPlugin>()
            .WithMCP("./MCP.json")
            .WithMaxFunctionCallTurns(6)
            .Build();  // ✨ Build CORE agent (internal access)

        return _cachedAgent;
    }
}

// ✅ FIXED: DTOs moved to end for top-level statements (CS8803)
public record ConversationDto(string Id, string Name, DateTime CreatedAt, DateTime LastActivity, int MessageCount);
public record ConversationWithMessagesDto(string Id, string Name, DateTime CreatedAt, DateTime LastActivity, int MessageCount, ConversationMessageDto[] Messages);
public record ConversationMessageDto(string Role, string Content, DateTime Timestamp);
public record CreateConversationRequest(string Name = "");
public record ChatRequest(string Message);
public record StreamRequest(string? ThreadId, StreamMessage[] Messages);
public record StreamMessage(string Content);
public record AgentChatResponse(string Response, string Model, UsageInfo Usage);
public record UsageInfo(long InputTokens, long OutputTokens, long TotalTokens);
public record SttResponse(string Transcript);
public record ErrorResponse(string Error);

// Note: StreamContentResponse, StreamFinishResponse, StreamErrorResponse, MathPlugin
// are already defined in AppJsonSerializerContext.cs and MathPlugin.cs

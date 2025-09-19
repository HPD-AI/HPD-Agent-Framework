using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using A2A;
using A2A.AspNetCore;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Combined);
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
builder.Services.AddSingleton<ProjectManager>();

var app = builder.Build();
app.UseCors("AllowFrontend");

// 🚀 WEBSOCKET SUPPORT: Enable WebSocket middleware
app.UseWebSockets();

// 🎯 A2A INTEGRATION: Set up A2A protocol support
var projectManager = app.Services.GetRequiredService<ProjectManager>();
var agent = projectManager.CreateAgent();
var taskManager = new TaskManager(taskStore: new InMemoryTaskStore());
var a2aHandler = new A2AHandler(agent, taskManager);

var agentPath = "/a2a-agent"; // Define a unique path for the A2A endpoint

// Map the A2A endpoints
app.MapA2A(taskManager, agentPath);

// 🎯 CLEAN PROJECT API
var projectsApi = app.MapGroup("/projects").WithTags("Projects");

projectsApi.MapGet("/", (ProjectManager pm) => 
    Results.Ok(pm.ListProjects().Select(ToProjectDto)));

projectsApi.MapPost("/", (CreateProjectRequest request, ProjectManager pm) =>
{
    var project = pm.CreateProject(request.Name, request.Description);
    return Results.Created($"/projects/{project.Id}", ToProjectDto(project));
});

projectsApi.MapGet("/{projectId}", (string projectId, ProjectManager pm) =>
    pm.GetProject(projectId) is { } project 
        ? Results.Ok(ToProjectDto(project))
        : Results.NotFound());

// 🎯 CLEAN CONVERSATION API  
projectsApi.MapGet("/{projectId}/conversations", (string projectId, ProjectManager pm) =>
    pm.GetProject(projectId) is { } project
        ? Results.Ok(project.Conversations.Select(ToConversationDto))
        : Results.NotFound());

projectsApi.MapPost("/{projectId}/conversations", (string projectId, CreateConversationRequest request, ProjectManager pm) =>
{
    if (pm.GetProject(projectId) is not { } project)
        return Results.NotFound();
    
    // ✨ CLEAN: Create agent and conversation together
    var agent = pm.CreateAgent();
    var conversation = project.CreateConversation(agent);
    if (!string.IsNullOrEmpty(request.Name))
        conversation.AddMetadata("DisplayName", request.Name);
    
    return Results.Created($"/projects/{projectId}/conversations/{conversation.Id}", ToConversationDto(conversation));
});

projectsApi.MapGet("/{projectId}/conversations/{conversationId}", (string projectId, string conversationId, ProjectManager pm) =>
    pm.GetConversation(projectId, conversationId) is { } conversation
        ? Results.Ok(ToConversationWithMessagesDto(conversation))
        : Results.NotFound());

projectsApi.MapDelete("/{projectId}/conversations/{conversationId}", (string projectId, string conversationId, ProjectManager pm) =>
{
    if (pm.GetProject(projectId) is not { } project)
        return Results.NotFound();
    
    if (project.RemoveConversation(conversationId))
        return Results.NoContent();
    
    return Results.NotFound();
});

// 🎯 CLEAN AGENT API
var agentApi = app.MapGroup("/agent").WithTags("Agent");

// ✨ SIMPLIFIED: Context-aware chat (no complex setup)
agentApi.MapPost("/projects/{projectId}/conversations/{conversationId}/chat", 
    async (string projectId, string conversationId, ChatRequest request, ProjectManager pm) =>
{
    if (pm.GetConversation(projectId, conversationId) is not { } conversation)
        return Results.NotFound();

    // 🚀 ONE LINE: Everything handled by conversation
    // For future document support: conversation.SendAsync(request.Message, documentPaths: request.DocumentPaths)
    var response = await conversation.SendAsync(request.Message);
    return Results.Ok(ToAgentResponse(response));
});

// ✨ SIMPLIFIED: Streaming with default Microsoft.Extensions.AI approach
agentApi.MapPost("/projects/{projectId}/conversations/{conversationId}/stream", 
    async (string projectId, string conversationId, StreamRequest request, ProjectManager pm, HttpContext context) =>
{
    var conversation = pm.GetConversation(projectId, conversationId);
    if (conversation == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Prepare SSE headers (CORS handled by middleware)
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var userMessage = request.Messages.FirstOrDefault()?.Content ?? "";
    var messages = conversation.Messages.ToList();
    messages.Add(new ChatMessage(ChatRole.User, userMessage));

    var agent = pm.CreateAgent();
    var writer = new StreamWriter(context.Response.Body, leaveOpen: true);

    try
    {
        // ✅ 1. Stream AG-UI events directly from agent
        var streamResult = await agent.ExecuteStreamingTurnAsync(messages, null, context.RequestAborted);
        await foreach (var baseEvent in streamResult.EventStream.WithCancellation(context.RequestAborted))
        {
            // Stream the BaseEvent as JSON directly (AG-UI format)
            var serializerOptions = new JsonSerializerOptions { TypeInfoResolver = AppJsonSerializerContext.Combined };
            var eventJson = System.Text.Json.JsonSerializer.Serialize(baseEvent, serializerOptions);
            await writer.WriteAsync($"data: {eventJson}\n\n");
            await writer.FlushAsync();
        }

        // ✅ 2. Update conversation with the user message that was sent
        conversation.AddMessage(new ChatMessage(ChatRole.User, userMessage));
        // Note: AG-UI events don't provide final history directly, 
        // so we'll need to track the assistant response as we receive content events
    }
    catch (Exception ex)
    {
        var response = new StreamErrorResponse(ex.Message);
        await writer.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.StreamErrorResponse)}\n\n");
        await writer.FlushAsync();
    }
    finally
    {
        await writer.DisposeAsync();
    }
});

// 🚀 WEBSOCKET: Real-time bi-directional streaming using new ConversationStreamingResult
agentApi.MapGet("/projects/{projectId}/conversations/{conversationId}/ws", 
    async (string projectId, string conversationId, HttpContext context, ProjectManager pm) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var conversation = pm.GetConversation(projectId, conversationId);

        if (conversation == null)
        {
            await webSocket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 
                "Conversation not found", 
                CancellationToken.None);
            return;
        }

        try
        {
            // Wait for an initial message from the client to start the stream
            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            var userMessage = System.Text.Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

            // Use new streaming API that returns ConversationStreamingResult
            // For future document support: pass document paths as 4th parameter
            var streamResult = await conversation.SendStreamingAsync(userMessage, null, null, null, CancellationToken.None);
            
            bool isFinished = false;
            await foreach (var evt in streamResult.EventStream.WithCancellation(CancellationToken.None))
            {
                switch (evt)
                {
                    case TextMessageContentEvent textEvent:
                        if (!string.IsNullOrEmpty(textEvent.Delta))
                        {
                            var response = new StreamContentResponse(textEvent.Delta);
                            var message = System.Text.Json.JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.StreamContentResponse);
                            var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
                            await webSocket.SendAsync(
                                new ArraySegment<byte>(messageBytes),
                                System.Net.WebSockets.WebSocketMessageType.Text,
                                true,
                                CancellationToken.None);
                        }
                        break;

                    case StepFinishedEvent:
                        // Mark as finished when we see a step finished event
                        isFinished = true;
                        break;
                }
            }

            // Send final metadata as completion event
            var finalResult = await streamResult.FinalResult;
            var metadataResponse = new StreamMetadataResponse(
                finalResult.Usage?.TotalTokens ?? 0,
                finalResult.Duration.TotalSeconds,
                finalResult.RespondingAgent.Name,
                finalResult.Usage?.EstimatedCost);
            var metadataMessage = System.Text.Json.JsonSerializer.Serialize(metadataResponse, AppJsonSerializerContext.Default.StreamMetadataResponse);
            var metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadataMessage);
            await webSocket.SendAsync(
                new ArraySegment<byte>(metadataBytes),
                System.Net.WebSockets.WebSocketMessageType.Text,
                true,
                CancellationToken.None);

            if (isFinished)
            {
                var response = new StreamFinishResponse(true, "Stop");
                var finishMessage = System.Text.Json.JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.StreamFinishResponse);
                var finishBytes = System.Text.Encoding.UTF8.GetBytes(finishMessage);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(finishBytes),
                    System.Net.WebSockets.WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
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

// ✨ CLEAN HELPER FUNCTIONS (Fixed CS1998)
static ProjectDto ToProjectDto(Project p) => new(p.Id, p.Name, p.Description, p.CreatedAt, p.LastActivity, p.ConversationCount);

static ConversationDto ToConversationDto(Conversation c) => new(c.Id, c.GetDisplayName(), c.CreatedAt, c.LastActivity, c.Messages.Count);

static ConversationWithMessagesDto ToConversationWithMessagesDto(Conversation c) => new(
    c.Id, 
    c.GetDisplayName(),  // ✨ Direct method call like console app
    c.CreatedAt, 
    c.LastActivity, 
    c.Messages.Count,
    c.Messages.Select(msg => new ConversationMessageDto(
        msg.Role.ToString().ToLowerInvariant(),
        c.ExtractTextContent(msg),  // ✨ Use conversation method like console app
        DateTime.UtcNow)).ToArray());

static AgentChatResponse ToAgentResponse(ChatResponse response) => new(
    Response: ExtractTextFromResponse(response),  // ✨ Simple like console app
    Model: response.ModelId ?? "google/gemini-2.5-pro", 
    Usage: new UsageInfo(
        response.Usage?.InputTokenCount ?? 0,
        response.Usage?.OutputTokenCount ?? 0, 
        response.Usage?.TotalTokenCount ?? 0));

// ✨ SIMPLE: One helper method like console app (same as your console code)
static string ExtractTextFromResponse(ChatResponse response)
{
    var lastMessage = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
    var textContent = lastMessage?.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
    return textContent ?? "No response received.";
}

// ✨ CLEAN PROJECT MANAGER (Fixed CS7036 and IL2026)
// ✨ CLEAN PROJECT MANAGER (Fixed cascading IL2026)
public class ProjectManager
{
    private readonly ConcurrentDictionary<string, Project> _projects = new();
    private readonly IConfiguration _config;

    public ProjectManager(IConfiguration config) => _config = config;

    // ✅ FIXED: Added RequiresUnreferencedCode to calling method
    [RequiresUnreferencedCode("Creates agent with reflection-based plugin registration")]
    public Project CreateProject(string name, string description = "")
    {
        // 🚀 CLEAN: Create project without agent coupling
        var project = CreateSimpleProject(name, description);
        _projects[project.Id] = project;
        return project;
    }

    // ✨ CLEAN: Just create projects, agents created per conversation
    private Project CreateSimpleProject(string name, string description)
    {
        var project = Project.Create(name);
        project.Description = description;
        return project;
    }

    // ✨ CLEAN: Agent creation separated from project creation
    [RequiresUnreferencedCode("AgentBuilder.Build uses reflection for plugin registration")]
    public Agent CreateAgent()
    {
        return CreateAndCacheAgent();
    }

    // Cache the agent instance for A2A integration
    private Agent? _cachedAgent;
    
    [RequiresUnreferencedCode("AgentBuilder.Build uses reflection for plugin registration")]
    private Agent CreateAndCacheAgent()
    {
        if (_cachedAgent != null)
            return _cachedAgent;

        _cachedAgent = CreateAgentInternal();
        return _cachedAgent;
    }

    [RequiresUnreferencedCode("AgentBuilder.Build uses reflection for plugin registration")]
    private Agent CreateAgentInternal()
    {
        // ✨ Load configuration from appsettings.json
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        return AgentBuilder.Create()
            .WithAPIConfiguration(config)
            .WithName("AI Assistant")
            .WithProvider(ChatProvider.OpenRouter, "google/gemini-2.5-pro")
            .WithInstructions("You are a helpful AI assistant with memory, knowledge base, and web search capabilities.")
            .WithInjectedMemory(opts => opts
                .WithStorageDirectory("./agent-memory-storage")
                .WithMaxTokens(6000))
            .WithFilter(new LoggingAiFunctionFilter())
            .WithTavilyWebSearch()    
            .WithPlugin<MathPlugin>()
            .WithMCP("./MCP.json")
            .WithMaxFunctionCalls(6)
            .Build();
    }

    public Project? GetProject(string projectId) => _projects.GetValueOrDefault(projectId);
    public IEnumerable<Project> ListProjects() => _projects.Values.OrderByDescending(p => p.LastActivity);
    public bool DeleteProject(string projectId) => _projects.TryRemove(projectId, out _);
    public Conversation? GetConversation(string projectId, string conversationId) =>
        GetProject(projectId)?.GetConversation(conversationId);
}

// ✅ FIXED: DTOs moved to end for top-level statements (CS8803)
public record ProjectDto(string Id, string Name, string Description, DateTime CreatedAt, DateTime LastActivity, int ConversationCount);
public record ConversationDto(string Id, string Name, DateTime CreatedAt, DateTime LastActivity, int MessageCount);
public record ConversationWithMessagesDto(string Id, string Name, DateTime CreatedAt, DateTime LastActivity, int MessageCount, ConversationMessageDto[] Messages);
public record ConversationMessageDto(string Role, string Content, DateTime Timestamp);
public record CreateProjectRequest(string Name, string Description = "");
public record CreateConversationRequest(string Name = "");
public record ChatRequest(string Message);
public record StreamRequest(string? ThreadId, StreamMessage[] Messages);
public record StreamMessage(string Content);
public record AgentChatResponse(string Response, string Model, UsageInfo Usage);
public record UsageInfo(long InputTokens, long OutputTokens, long TotalTokens);
public record SttResponse(string Transcript);
public record ErrorResponse(string Error);
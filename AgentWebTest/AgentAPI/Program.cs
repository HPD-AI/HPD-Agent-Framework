using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using A2A;
using A2A.AspNetCore;

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

    // 🚀 Use new AIAgent RunAsync interface
    var userMessage = new ChatMessage(ChatRole.User, request.Message);
    var agentResponse = await conversation.RunAsync([userMessage]);

    // Convert AgentRunResponse to our API response format
    return Results.Ok(ToAgentResponse(agentResponse));
});

// ✨ NEW: AG-UI Protocol streaming endpoint (using RunAgentInput overload)
agentApi.MapPost("/projects/{projectId}/conversations/{conversationId}/stream",
    async (string projectId, string conversationId, RunAgentInput aguiInput, ProjectManager pm, HttpContext context) =>
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

    // Use UTF8 encoding with larger buffer to prevent truncation of large JSON payloads
    var writer = new StreamWriter(context.Response.Body, encoding: System.Text.Encoding.UTF8, bufferSize: 8192, leaveOpen: true);

    try
    {
        // ✅ Use the new Conversation.RunStreamingAsync(RunAgentInput) AGUI overload
        await foreach (var update in conversation.RunStreamingAsync(aguiInput, context.RequestAborted))
        {
            // Serialize AgentRunResponseUpdate to JSON
            var serializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = JsonResolvers.Combined,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var eventJson = System.Text.Json.JsonSerializer.Serialize(update, update.GetType(), serializerOptions);

            // Write complete SSE event in one operation to prevent truncation
            await writer.WriteAsync($"data: {eventJson}\n\n");
            await writer.FlushAsync();
        }
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

            // Use new AIAgent RunStreamingAsync interface
            var chatMessage = new ChatMessage(ChatRole.User, userMessage);

            bool isFinished = false;
            long totalTokens = 0;
            await foreach (var update in conversation.RunStreamingAsync([chatMessage], cancellationToken: CancellationToken.None))
            {
                // Extract text content from update
                foreach (var content in update.Contents ?? [])
                {
                    if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        var response = new StreamContentResponse(textContent.Text);
                        var message = System.Text.Json.JsonSerializer.Serialize(response, AppJsonSerializerContext.Default.StreamContentResponse);
                        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(messageBytes),
                            System.Net.WebSockets.WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                }

                // Note: AgentRunResponseUpdate doesn't have Usage property
                // Usage will be available in the final response, not individual updates
            }

            // Mark as finished after stream completes
            isFinished = true;

            // Send final metadata as completion event
            var metadataResponse = new StreamMetadataResponse(
                totalTokens,
                0.0, // Duration not available from AgentRunResponseUpdate
                conversation.Agent.Config?.Name ?? "AI Assistant",
                null); // Cost not available from AgentRunResponseUpdate
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
        ExtractTextFromMessage(msg),  // ✨ Use helper method to extract text
        DateTime.UtcNow)).ToArray());

static AgentChatResponse ToAgentResponse(AgentRunResponse response) => new(
    Response: response.Text,  // ✨ Use built-in Text property from AgentRunResponse
    Model: "google/gemini-2.5-pro", // ✨ AgentRunResponse doesn't expose ModelId, use default
    Usage: new UsageInfo(
        response.Usage?.InputTokenCount ?? 0,
        response.Usage?.OutputTokenCount ?? 0,
        response.Usage?.TotalTokenCount ?? 0));

static string ExtractTextFromMessage(ChatMessage message)
{
    var textContents = message.Contents
        .OfType<TextContent>()
        .Select(tc => tc.Text)
        .Where(text => !string.IsNullOrEmpty(text));
    return string.Join(" ", textContents);
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
            .WithLogging() // Global logging filter
            .WithProvider(ChatProvider.OpenRouter, "google/gemini-2.5-pro")
            .WithInstructions("You are a helpful AI assistant with memory, knowledge base, and web search capabilities.")
            .WithDynamicMemory(opts => opts
                .WithStorageDirectory("./agent-memory-storage")
                .WithMaxTokens(6000))
            .WithTavilyWebSearch()    
            .WithPlugin<MathPlugin>()
            .WithMCP("./MCP.json")
            .WithMaxFunctionCallTurns(6)
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

// ✨ AG-UI Protocol types (for API deserialization)
// Note: These mirror the types from HPD-Agent/Agent/AGUI/AOTCompatibleTypes.cs
// but are defined here for API layer independence
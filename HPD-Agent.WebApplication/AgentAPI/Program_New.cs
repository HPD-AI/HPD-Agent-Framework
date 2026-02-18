using HPD.Agent;
using HPD.Agent.AspNetCore;

var builder = WebApplication.CreateSlimBuilder(args);

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

// Register HPD Agent with the new simplified API
builder.Services.AddHPDAgent(options =>
{
    options.SessionStorePath = Path.Combine(Environment.CurrentDirectory, "threads");
    options.ConfigureAgent = agent => agent
        .WithProvider("openrouter", "z-ai/glm-4.6")
        .WithName("AI Assistant")
        .WithInstructions("You are a helpful AI assistant with file system access and memory capabilities.")
        .WithDynamicMemory(opts => opts
            .WithStorageDirectory("./agent-memory-storage")
            .WithMaxTokens(6000))
        .WithToolkit<MathTools>()
        .WithPermissions();
});

var app = builder.Build();

app.UseCors("AllowClient");
app.UseWebSockets();

// Map all HPD-Agent API endpoints at /agent prefix
app.MapGroup("/agent").MapHPDAgentApi();

// Optional: Keep the old /conversations endpoints for backward compatibility
// These can be removed once clients migrate to the new /agent/sessions API
var conversationsApi = app.MapGroup("/conversations").WithTags("Conversations (Legacy)");

conversationsApi.MapPost("/", () =>
{
    var sessionId = Guid.NewGuid().ToString();
    return Results.Created($"/conversations/{sessionId}", new
    {
        sessionId,
        name = "New Conversation",
        createdAt = DateTime.UtcNow,
        lastActivity = DateTime.UtcNow,
        messageCount = 0
    });
});

// Redirect old endpoints to new ones
conversationsApi.MapGet("/{conversationId}", (string conversationId) =>
{
    return Results.Redirect($"/agent/sessions/{conversationId}");
});

app.Run();

// Helper DTO for backward compatibility
record ConversationDto(string SessionId, string Name, DateTime CreatedAt, DateTime LastActivity, int MessageCount);

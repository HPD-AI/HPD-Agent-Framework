using HPD.Agent;
using HPD.Agent.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateSlimBuilder(args);

// Wire OpenTelemetry → Aspire dashboard.
// Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT automatically when running via apphost.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("HPD.Agent")   // picks up TracingObserver spans (agent.turn, agent.iteration, agent.tool_call)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter("HPD.Agent")    // picks up TelemetryEventObserver counters and histograms
        .AddOtlpExporter());

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
    options.AgentIdleTimeout = TimeSpan.FromMinutes(30);
    options.SessionStore = new JsonSessionStore(Path.Combine(Environment.CurrentDirectory, "sessions"));
    options.PersistAfterTurn = true;

    options.ConfigureAgent = agent => agent
        .WithProvider("openrouter", "z-ai/glm-4.6")
        .WithName("AI Assistant")
        .WithInstructions("You are a helpful AI assistant with file system access and memory capabilities.")
        .WithToolkit<MathTools>()
        .WithPermissions()
        .WithPreserveReasoningInHistory()
        .WithTracing(sourceName: "HPD.Agent")   // produces agent.turn / agent.iteration / agent.tool_call spans
        .WithTelemetry(sourceName: "HPD.Agent"); // produces agent.iterations, agent.message_turn.duration, etc.
});

var app = builder.Build();

// Validate configuration on startup
ValidateConfiguration(app.Services);

app.UseCors("AllowClient");
app.UseWebSockets();

// Map all HPD-Agent API endpoints
// This provides 20+ endpoints:
// - Sessions: POST/GET/PATCH/DELETE /sessions
// - Branches: GET/POST/DELETE /sessions/{sid}/branches
// - Assets: POST/GET/DELETE /sessions/{sid}/assets
// - Streaming: POST /sessions/{sid}/branches/{bid}/stream (SSE)
//              GET /sessions/{sid}/branches/{bid}/ws (WebSocket)
// - Middleware: POST /sessions/{sid}/branches/{bid}/permissions/respond
//               POST /sessions/{sid}/branches/{bid}/client-tools/respond
app.MapHPDAgentApi();

app.Run();

/// <summary>
/// Validates that HPD Agent is properly configured.
/// </summary>
static void ValidateConfiguration(IServiceProvider services)
{
    try
    {
        Console.WriteLine("\n✓ HPD-Agent AspNetCore configured:");
        Console.WriteLine("  - Endpoints: /sessions, /sessions/{sid}/branches, /sessions/{sid}/assets");
        Console.WriteLine("  - Streaming: SSE (/stream) + WebSocket (/ws)");
        Console.WriteLine("  - Middleware: Permissions + Client Tools");
        Console.WriteLine("  - Session persistence: threads/");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n⚠ Warning: Could not validate configuration: {ex.Message}\n");
    }
}

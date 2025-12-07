using HPD.Agent;
using HPD.Agent.Checkpointing;
using HPD.Agent.Checkpointing.Services;
using HPD.Agent.MCP;
using HPD.Agent.Memory;

Console.WriteLine("🚀 HPD-Agent Console Test\n");

// Setup checkpoint store for persistence across restarts
var checkpointPath = Path.Combine(Environment.CurrentDirectory, "console-checkpoints");
var store = new JsonConversationThreadStore(checkpointPath);
Console.WriteLine($"📁 Checkpoints: {checkpointPath}");

// Configure agent
var config = new AgentConfig
{
    Name = "AI Assistant",
    SystemInstructions = "You are a helpful AI assistant.",
    MaxAgenticIterations = 50,
    Provider = new ProviderConfig
    {
        ProviderKey = "openrouter",
        ModelName = "google/gemini-2.5-pro"
    },
    Mcp = new McpConfig { ManifestPath = "./MCP.json" },
    Scoping = new ScopingConfig { Enabled = true }
};

// Build agent with event handler (synchronous, ordered for UI)
var eventHandler = new ConsoleEventHandler();
var agent = await new AgentBuilder(config)
    .WithEventHandler(eventHandler)
    .WithPlugin<MathPlugin>()
    .WithPlugin<FinancialAnalysisPlugin>()
    .WithPlugin<FinancialAnalysisSkills>()
    .WithPlanMode()
    .WithPermissions()
    .WithCircuitBreaker(maxConsecutiveCalls: 3)
    .WithErrorTracking(maxConsecutiveErrors: 3)
    .WithTotalErrorThreshold(maxTotalErrors: 10)
    .WithMCP("./MCP.json")
    .WithLogging()
    .WithCheckpointStore(store)
    .WithDurableExecution(CheckpointFrequency.PerTurn, RetentionPolicy.LatestOnly)
    .Build();

eventHandler.SetAgent(agent);

// Check for existing conversations to resume
var existingThreads = await store.ListThreadIdsAsync();
ConversationThread? thread = null;

if (existingThreads.Count > 0)
{
    Console.WriteLine($"\n📋 Found {existingThreads.Count} saved conversation(s):");
    for (int i = 0; i < existingThreads.Count; i++)
    {
        var existingThread = await store.LoadThreadAsync(existingThreads[i]);
        var msgCount = existingThread?.MessageCount ?? 0;
        var lastActivity = existingThread?.LastActivity.ToString("g") ?? "Unknown";
        Console.WriteLine($"  [{i + 1}] {existingThreads[i]} ({msgCount} messages, last: {lastActivity})");
    }
    Console.WriteLine($"  [N] Start new conversation");
    Console.Write("\nSelect option: ");

    var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

    if (choice != "N" && int.TryParse(choice, out var idx) && idx >= 1 && idx <= existingThreads.Count)
    {
        thread = await store.LoadThreadAsync(existingThreads[idx - 1]);
        if (thread != null)
        {
            Console.WriteLine($"\n✅ Resumed conversation {thread.Id}");
            Console.WriteLine($"   Messages: {thread.MessageCount}, Last activity: {thread.LastActivity:g}");
        }
    }
}

// Create new thread if not resuming
if (thread == null)
{
    thread = agent.CreateThread();
    await store.SaveThreadAsync(thread);
    Console.WriteLine($"\n✅ Created new conversation: {thread.Id}");
}

Console.WriteLine($"✅ {config.Name} ready ({config.Provider.ModelName})\n");

// Chat loop
while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    try
    {
        await foreach (var _ in agent.RunAsync(input, thread)) { }
        // Agent auto-checkpoints via DurableExecutionService
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ {ex.Message}\n");
    }
}

Console.WriteLine("👋 Goodbye!");

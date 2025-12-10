using HPD.Agent;
using HPD.Agent.MCP;
using HPD.Agent.Memory;

Console.WriteLine("🚀 HPD-Agent Console Test\n");

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
    Mcp = new McpConfig { ManifestPath = "./MCP.json" }
};

// Build agent with event handler (synchronous, ordered for UI)
var eventHandler = new ConsoleEventHandler();
var agent = await new AgentBuilder(config)
    .WithEventHandler(eventHandler)
    .WithPlugin<FinancialAnalysisSkills>()
    .WithPlanMode()
    .WithPermissions()
    .WithCircuitBreaker(maxConsecutiveCalls: 3)
    .WithErrorTracking(maxConsecutiveErrors: 3)
    .WithTotalErrorThreshold(maxTotalErrors: 10)
    .WithMCP("./MCP.json")
    .WithLogging()
    .Build();

eventHandler.SetAgent(agent);

// Create new conversation thread
var thread = agent.CreateThread();
Console.WriteLine($"\n✅ Created new conversation: {thread.Id}");

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

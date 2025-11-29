using HPD.Agent;

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
        ModelName = "google/gemini-2.5-flash"
    },
    Mcp = new McpConfig { ManifestPath = "./MCP.json" },
    Scoping = new ScopingConfig { Enabled = true }
};

// Build agent with observer
var eventHandler = new ConsoleEventHandler();
var agent = new AgentBuilder(config)
    .WithObserver(eventHandler)
    .WithPlugin<MathPlugin>()
    .WithLogging()
    .WithPlanMode()
    .WithPermissions()
    .WithCircuitBreaker(maxConsecutiveCalls: 3)
    .WithErrorTracking(maxConsecutiveErrors: 3)
    .WithTotalErrorThreshold(maxTotalErrors: 10)
    .BuildCoreAgent();

eventHandler.SetAgent(agent);
var thread = agent.CreateThread();

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
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ {ex.Message}\n");
    }
}

Console.WriteLine("👋 Goodbye!");

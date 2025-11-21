using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using HPD.Agent;

// ═══════════════════════════════════════════════════════════════
// LOGGING SETUP (Required for Console Apps)
// ═══════════════════════════════════════════════════════════════
using var loggerFactory = LoggerFactory.Create(builder =>
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    builder
        .AddConsole()
        .AddConfiguration(configuration.GetSection("Logging"));
});

Console.WriteLine("🚀 HPD-Agent Console Test (Core Agent - Direct Access)");

// ✨ ONE-LINER: Create complete AI assistant using CORE agent (not Microsoft adapter)
var result = await CreateAIAssistant(loggerFactory);
var (thread, agent) = result;
if (agent is null) throw new InvalidOperationException("Failed to create AI assistant");

Console.WriteLine($"✅ AI Assistant ready: {agent.Config?.Name ?? "Unknown"}");
Console.WriteLine();

// 🎯 Interactive Chat Loop
await RunInteractiveChat(agent, thread);

// ✨ CONFIG-FIRST APPROACH: Using AgentConfig pattern with AUTO-CONFIGURATION
static Task<(ConversationThread, AgentCore)> CreateAIAssistant(ILoggerFactory loggerFactory)
{
    // ✨ CREATE SERVICE PROVIDER WITH LOGGER FACTORY
    var services = new ServiceCollection();
    services.AddSingleton(loggerFactory);
    var serviceProvider = services.BuildServiceProvider();

    // ✨ CREATE AGENT CONFIG OBJECT FIRST
    var agentConfig = new AgentConfig
    {
        Name = "AI Assistant",
        SystemInstructions = "You are an accountant agent. You can do sequential and parallel tool calls. You can also plan out stuff before you start if the task requires sub steps. If you open a skill, it will give you instructions of how to use the skill and what to read.",
        MaxAgenticIterations = 20,  // Set to 2 to test continuation filter
        Provider = new ProviderConfig
        {
            ProviderKey = "openrouter",
            ModelName = "z-ai/glm-4.6", // 🧠 Reasoning model - FREE on OpenRouter!
        },
        DynamicMemory = new DynamicMemoryConfig
        {
            StorageDirectory = "./agent-dynamic-memory",
            MaxTokens = 6000,
            EnableAutoEviction = true,
            AutoEvictionThreshold = 85
        },
        Mcp = new McpConfig
        {
            ManifestPath = "./MCP.json"
        },
        // 🎯 Plugin Scoping: OFF by default (set Enabled = true to enable)
        // When enabled, plugin functions are hidden behind container functions to reduce token usage by up to 87.5%
        // The agent must first call the container (e.g., MathPlugin) before individual functions (Add, Multiply) become visible
        Scoping = new ScopingConfig
        {
            Enabled = true,              // Scope C# plugins (MathPlugin, etc.)      // Scope MCP tools by server (MCP_filesystem, MCP_github, etc.)
            ScopeFrontendTools = false,   // Scope Frontend/AGUI tools (FrontendTools container)
            MaxFunctionNamesInDescription = 10  // Max function names shown in container descriptions
        },
        // 💭 Reasoning Token Preservation: Controls whether reasoning from models like o1/Gemini is saved in history
        // Default: false (reasoning shown in UI but excluded from history to save tokens/cost)
        // Set to true: Reasoning preserved in conversation history for complex multi-turn scenarios
        PreserveReasoningInHistory = true  // 🧪 Try setting to true to preserve reasoning tokens!
    };

    // ✨ BUILD CORE AGENT - Direct access to internal Agent class
    // Auto-loads from appsettings.json, environment variables, and user secrets
    var agent = new AgentBuilder(agentConfig)
        .WithLogging()
        .WithPlanMode()  // ✨ Financial analysis plugin (explicitly registered)  // ✨ Financial analysis skills (that reference the plugin)
        .WithPlugin<FinancialAnalysisSkills>()  // ✨ Math plugin (basic math functions)
        .WithPlugin<MathPlugin>()  // ✨ Math plugin (basic math functions)
        .WithPermissions() // ✨ NEW: Unified permission filter - events handled in streaming loop
        .BuildCoreAgent();  // ✨ Build CORE agent (internal access via InternalsVisibleTo)

    // 💬 Create thread using agent directly
    var thread = agent.CreateThread();

    // ✨ Show config info
    Console.WriteLine($"✨ Agent created with config-first pattern!");
    Console.WriteLine($"📋 Config: {agentConfig.Name} - {agentConfig.Provider?.ModelName}");
    Console.WriteLine($"🧠 Memory: {agentConfig.DynamicMemory?.StorageDirectory}");
    Console.WriteLine($"🔧 Max Function Call Turns: {agentConfig.MaxAgenticIterations}");

    return Task.FromResult((thread, agent));
}

// 🎯 Interactive Chat Loop using CORE agent.RunAsync with InternalAgentEvent stream
static async Task RunInteractiveChat(AgentCore agent, ConversationThread thread)
{
    Console.WriteLine("==========================================");
    Console.WriteLine("🤖 Interactive Chat Mode (Core Agent)");
    Console.WriteLine("==========================================");
    Console.WriteLine("Commands:");
    Console.WriteLine("  • Type your message and press Enter");
    Console.WriteLine("  • Press ESC during AI response to stop current turn");
    Console.WriteLine("  • 'exit' or 'quit' - End conversation");
    Console.WriteLine("------------------------------------------\n");

    while (true)
    {
        Console.Write("You: ");
        var input = Console.ReadLine();

        if (input?.ToLower() is "exit" or "quit")
        {
            Console.WriteLine("👋 Goodbye!");
            break;
        }

        if (string.IsNullOrWhiteSpace(input)) continue;

        try
        {
            Console.Write("AI: ");

            // Create cancellation token source for this turn
            using var cts = new CancellationTokenSource();

            // Start background task to listen for ESC key
            var cancelTask = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.Write("\n⚠️  Stopping current turn...");
                            Console.ResetColor();
                            cts.Cancel();
                            break;
                        }
                    }
                    Thread.Sleep(50); // Check every 50ms
                }
            });

            // Track state for better display
            bool isFirstReasoningChunk = true;
            bool isFirstTextChunk = true;
            string? currentMessageId = null;

            try
            {
                // ✨ Use CORE agent.RunAsync - returns InternalAgentEvent stream
                var userMessage = new ChatMessage(ChatRole.User, input);
                await foreach (var evt in agent.RunAsync(
                    new[] { userMessage },
                    options: null,
                    thread: thread,
                    cancellationToken: cts.Token))
                {
                    // ✨ Handle INTERNAL permission events from the unified filter
                    if (evt is InternalPermissionRequestEvent permReq)
                    {
                        // Close any open sections
                        if (!isFirstReasoningChunk || !isFirstTextChunk)
                        {
                            Console.WriteLine(); // End section
                            Console.ResetColor();
                            isFirstReasoningChunk = true;
                            isFirstTextChunk = true;
                        }

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n🔐 Permission Request");
                        Console.WriteLine($"   Function: {permReq.FunctionName}");
                        if (!string.IsNullOrEmpty(permReq.Description))
                            Console.WriteLine($"   Purpose: {permReq.Description}");
                        Console.WriteLine($"   Options: [A]llow once, Allow [F]orever, [D]eny once, Deny F[o]rever");
                        Console.Write("   Your choice (press Enter): ");
                        Console.ResetColor();

                        // Read user's permission choice (first character of input line)
                        var userInput = Console.ReadLine();
                        var choice = string.IsNullOrEmpty(userInput) ? 'd' : char.ToLower(userInput[0]);

                        bool approved;
                        PermissionChoice permChoice;

                        switch (choice)
                        {
                            case 'A' or 'a':
                                approved = true;
                                permChoice = PermissionChoice.Ask;
                                break;
                            case 'F' or 'f':
                                approved = true;
                                permChoice = PermissionChoice.AlwaysAllow;
                                break;
                            case 'D' or 'd':
                                approved = false;
                                permChoice = PermissionChoice.Ask;
                                break;
                            case 'O' or 'o':
                                approved = false;
                                permChoice = PermissionChoice.AlwaysDeny;
                                break;
                            default:
                                approved = false;
                                permChoice = PermissionChoice.Ask;
                                break;
                        }

                        // Send response back to the filter via agent
                        agent.SendFilterResponse(
                            permReq.PermissionId,
                            new InternalPermissionResponseEvent(
                                permReq.PermissionId,
                                "Console", // SourceName
                                approved,
                                approved ? null : "User denied permission",
                                permChoice
                            )
                        );

                        Console.ForegroundColor = approved ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.WriteLine($"   {(approved ? "✓ Approved" : "✗ Denied")}");
                        Console.ResetColor();
                    }

                    // ✨ Handle continuation request events
                    else if (evt is InternalContinuationRequestEvent contReq)
                    {
                        // Close any open sections
                        if (!isFirstReasoningChunk || !isFirstTextChunk)
                        {
                            Console.WriteLine();
                            Console.ResetColor();
                            isFirstReasoningChunk = true;
                            isFirstTextChunk = true;
                        }

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n⏱️  Continuation Request");
                        Console.WriteLine($"   Iteration: {contReq.CurrentIteration} / {contReq.MaxIterations}");
                        Console.WriteLine($"   Continue for more iterations?");
                        Console.WriteLine($"   Options: [Y]es, [N]o");
                        Console.Write("   Your choice: ");
                        Console.ResetColor();

                        var userInput = Console.ReadLine();
                        var approved = !string.IsNullOrEmpty(userInput) && char.ToLower(userInput[0]) == 'y';

                        // Send response back to the filter
                        agent.SendFilterResponse(
                            contReq.ContinuationId,
                            new InternalContinuationResponseEvent(
                                contReq.ContinuationId,
                                "Console",
                                approved,
                                approved ? 3 : 0  // Default 3 iterations if approved
                            )
                        );

                        Console.ForegroundColor = approved ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.WriteLine($"{(approved ? "✓ Continuing" : "✗ Stopping")}");
                        Console.ResetColor();

                    }

                    // ✨ Handle text content events (reasoning and regular text)
                    // REASONING EVENTS
                    else if (evt is InternalReasoningMessageStartEvent reasoningStart)
                    {
                        // If transitioning from text to reasoning, close text section
                        if (!isFirstTextChunk)
                        {
                            Console.WriteLine(); // End text section
                            Console.ResetColor();
                            isFirstTextChunk = true;
                        }

                        // Show header when starting new reasoning section
                        if (isFirstReasoningChunk)
                        {
                            Console.WriteLine(); // Add spacing
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write("💭 Thinking: ");
                            isFirstReasoningChunk = false;
                        }
                        currentMessageId = reasoningStart.MessageId;
                    }
                    else if (evt is InternalReasoningDeltaEvent reasoningDelta)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write(reasoningDelta.Text);
                    }
                    else if (evt is InternalReasoningMessageEndEvent)
                    {
                        // Reasoning section ended - will transition to text if any
                        if (!isFirstReasoningChunk)
                        {
                            Console.WriteLine(); // End reasoning section
                            Console.ResetColor();
                            isFirstReasoningChunk = true;
                        }
                    }

                    // TEXT CONTENT EVENTS
                    else if (evt is InternalTextMessageStartEvent textStart)
                    {
                        // If transitioning from reasoning to text, ensure reasoning is closed
                        if (!isFirstReasoningChunk)
                        {
                            Console.WriteLine();
                            Console.ResetColor();
                            isFirstReasoningChunk = true;
                        }

                        // Show text header on first text chunk
                        if (isFirstTextChunk)
                        {
                            Console.WriteLine(); // Add spacing
                            Console.Write("📝 Response: ");
                            isFirstTextChunk = false;
                        }
                        currentMessageId = textStart.MessageId;
                    }
                    else if (evt is InternalTextDeltaEvent textDelta)
                    {
                        Console.Write(textDelta.Text);
                    }
                    else if (evt is InternalTextMessageEndEvent)
                    {
                        // Text message ended
                    }

                    // TOOL CALL EVENTS
                    else if (evt is InternalToolCallStartEvent toolStart)
                    {
                        // Close any open sections
                        if (!isFirstReasoningChunk || !isFirstTextChunk)
                        {
                            Console.WriteLine(); // End section
                            Console.ResetColor();
                            isFirstReasoningChunk = true;
                            isFirstTextChunk = true;
                        }

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"\n🔧 Using tool: {toolStart.Name}");
                        Console.ResetColor();
                    }
                    else if (evt is InternalToolCallResultEvent toolResult)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write($" ✓");
                        Console.ResetColor();
                    }

                    // AGENT TURN EVENTS (optional - for debugging)
                    else if (evt is InternalAgentTurnStartedEvent turnStart)
                    {
                        if (turnStart.Iteration > 1)  // Don't show for first iteration
                        {
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.WriteLine($"\n🔄 Agent iteration {turnStart.Iteration}");
                            Console.ResetColor();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n\n🛑 Turn stopped. You can continue the conversation.\n");
                Console.ResetColor();
            }
            finally
            {
                // Signal cancellation task to stop and wait for it
                cts.Cancel();
                await cancelTask;
            }

            // Display message count after each turn
            var messageCount = await thread.GetMessageCountAsync();
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"\n💬 Messages in thread: {messageCount}");
            Console.ResetColor();
            Console.WriteLine(); // Add spacing after response
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ Error: {ex.Message}\n");
        }
    }
}

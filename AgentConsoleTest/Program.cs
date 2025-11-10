using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using HPD.Agent.Plugins.FileSystem;
using HPD.Agent;
using HPD.Agent.Microsoft;
Console.WriteLine("🚀 HPD-Agent Console Test");

// ✨ ONE-LINER: Create complete AI assistant
// No need to manually create config - agent auto-loads from appsettings.json, env vars, and user secrets!
var (project, thread, agent) = await CreateAIAssistant();

Console.WriteLine($"✅ AI Assistant ready: {agent.Name}");
Console.WriteLine($"📁 Project: {project.Name}");
/*
// 📄 Upload documents from FY25q4-zip folder to project
try
{
    Console.WriteLine("� Uploading all documents from FY25q4-zip folder to project...");
    var documents = await project.UploadDirectoryAsync(
        "/Users/einsteinessibu/Documents/HPD-Agent/AgentConsoleTest/FY25q4-zip"
    );
    Console.WriteLine($"✅ {documents.Count} documents uploaded successfully:");
    foreach (var doc in documents)
    {
        Console.WriteLine($"   📄 {doc.FileName} (ID: {doc.Id})");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to upload documents: {ex.Message}");
}
*/
Console.WriteLine();

// 🎯 Interactive Chat Loop
await RunInteractiveChat(agent, thread);

// ✨ CONFIG-FIRST APPROACH: Using AgentConfig pattern with AUTO-CONFIGURATION
static Task<(Project, ConversationThread, Agent)> CreateAIAssistant()
{
    // ✨ CREATE AGENT CONFIG OBJECT FIRST
    var agentConfig = new AgentConfig
    {
        Name = "AI Assistant",
        SystemInstructions = "You are an accountant agent. You can do sequential and parallel tool calls. You can also plan out stuff ebfore you start if the task requires sub steps",
        MaxAgenticIterations = 20,  // Reduced from 50 to avoid rate limits
        Provider = new ProviderConfig
        {
            ProviderKey = "openrouter",
            ModelName = "google/gemini-2.5-pro", // 🧠 Reasoning model - FREE on OpenRouter!
            // Alternative reasoning models:
            // "deepseek/deepseek-r1-distill-qwen-32b" - smaller/faster
            // "openai/o1" - OpenAI's reasoning model (expensive)

            // ✨ NO API KEY NEEDED HERE!
            // AgentBuilder automatically resolves from (in priority order):
            // 1. ConnectionStrings:Agent (appsettings.json) - NEW Microsoft pattern
            // 2. OpenRouter:ApiKey (appsettings.json) - Legacy pattern
            // 3. OPENROUTER_API_KEY (environment variable)
            // 4. User secrets (dotnet user-secrets)
        },
        DynamicMemory = new DynamicMemoryConfig
        {
            StorageDirectory = "./agent-memory-storage",
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
        PluginScoping = new PluginScopingConfig
        {
            Enabled = true,              // Scope C# plugins (MathPlugin, etc.)
            ScopeMCPTools = false,        // Scope MCP tools by server (MCP_filesystem, MCP_github, etc.)
            ScopeFrontendTools = false,   // Scope Frontend/AGUI tools (FrontendTools container)
            MaxFunctionNamesInDescription = 10  // Max function names shown in container descriptions
        }
    };

    // ✨ BUILD AGENT - NO .WithAPIConfiguration() NEEDED!
    // Auto-loads from appsettings.json, environment variables, and user secrets
    var agent = new AgentBuilder(agentConfig)
        .WithLogging()
        .WithTavilyWebSearch()
        .WithDynamicMemory(opts => opts
            .WithStorageDirectory("./agent-memory-storage")
            .WithMaxTokens(6000))
        .WithPlanMode() // Plan mode enabled with defaults
        .WithPlugin<ExpandMathPlugin>()
        .WithPlugin<FinancialAnalysisPlugin>()
        .WithPermissions() // ✨ NEW: Unified permission filter - events handled in streaming loop
        .Build();

    // 🎯 Project with smart defaults
    var project = Project.Create("AI Chat Session");

    // 💬 Create thread using agent directly
    var thread = project.CreateThread();

    // ✨ Show config info
    Console.WriteLine($"✨ Agent created with config-first pattern!");
    Console.WriteLine($"📋 Config: {agentConfig.Name} - {agentConfig.Provider?.ModelName}");
    Console.WriteLine($"🧠 Memory: {agentConfig.DynamicMemory?.StorageDirectory}");
    Console.WriteLine($"🔧 Max Function Call Turns: {agentConfig.MaxAgenticIterations}");

    return Task.FromResult((project, thread, agent));
}

// 🎯 Interactive Chat Loop using Microsoft agent.RunStreamingAsync
static async Task RunInteractiveChat(Agent agent, ConversationThread thread)
{
    Console.WriteLine("==========================================");
    Console.WriteLine("🤖 Interactive Chat Mode");
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
            
            // Create user message for streaming
            var userMessage = new ChatMessage(ChatRole.User, input);
            
            // Track state for better reasoning display
            bool isFirstReasoningChunk = true;
            bool isFirstTextChunk = true;
            
            try
            {
                // Use agent.RunStreamingAsync with thread parameter
                await foreach (var update in agent.RunStreamingAsync([userMessage], thread, cancellationToken: cts.Token))
                {
                    // ✨ Handle permission requests from the unified filter
                    if (update.IsPermissionEvent && update.PermissionData?.Type == PermissionEventType.Request)
                    {
                        // Close any open sections
                        if (!isFirstReasoningChunk || !isFirstTextChunk)
                        {
                            Console.WriteLine(); // End section
                            Console.ResetColor();
                            isFirstReasoningChunk = true;
                            isFirstTextChunk = true;
                        }

                        var perm = update.PermissionData;
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n🔐 Permission Request");
                        Console.WriteLine($"   Function: {perm.FunctionName}");
                        if (!string.IsNullOrEmpty(perm.Description))
                            Console.WriteLine($"   Purpose: {perm.Description}");
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
                            perm.PermissionId!,
                            new InternalPermissionResponseEvent(
                                perm.PermissionId!,
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

                    // Display different content types from the streaming updates
                    foreach (var content in update.Contents ?? [])
                    {
                        // Display reasoning content (thinking process) in gray - streams naturally
                        // CHECK REASONING FIRST since TextReasoningContent derives from TextContent
                        if (content is TextReasoningContent reasoningContent && !string.IsNullOrWhiteSpace(reasoningContent.Text))
                        {
                            // If transitioning from text to reasoning, close text section
                            if (!isFirstTextChunk)
                            {
                                Console.WriteLine(); // End text section
                                Console.ResetColor();
                                isFirstTextChunk = true; // Reset for next text block
                            }

                            // Show header when starting new reasoning section
                            if (isFirstReasoningChunk)
                            {
                                Console.WriteLine(); // Add spacing
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.Write("💭 Thinking: ");
                                isFirstReasoningChunk = false;
                            }

                            // Stream reasoning text naturally
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(reasoningContent.Text);
                        }
                        // Display text content (final answer)
                        else if (content is TextContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
                        {
                            // If transitioning from reasoning to text, end reasoning section
                            if (!isFirstReasoningChunk)
                            {
                                Console.WriteLine(); // End reasoning section
                                Console.ResetColor();
                                isFirstReasoningChunk = true; // Reset for next reasoning block
                            }

                            // Show text header on first text chunk
                            if (isFirstTextChunk)
                            {
                                Console.WriteLine(); // Add spacing
                                Console.Write("📝 Response: ");
                                isFirstTextChunk = false;
                            }

                            Console.Write(textContent.Text);
                        }
                        // Display tool calls
                        else if (content is FunctionCallContent toolCall)
                        {
                            // Close any open sections
                            if (!isFirstReasoningChunk || !isFirstTextChunk)
                            {
                                Console.WriteLine(); // End section
                                Console.ResetColor();
                                isFirstReasoningChunk = true; // Reset for next reasoning block
                                isFirstTextChunk = true; // Reset for next text block
                            }

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write($"\n🔧 Using tool: {toolCall.Name}");
                            Console.ResetColor();
                        }
                        // Display tool results
                        else if (content is FunctionResultContent toolResult)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write($" ✓");
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


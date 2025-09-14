using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

Console.WriteLine("🚀 HPD-Agent Console Test");

// ✨ Load configuration from appsettings.json
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// ✨ ONE-LINER: Create complete AI assistant
var (project, conversation, agent) = await CreateAIAssistant(config);

Console.WriteLine($"✅ AI Assistant ready: {agent.Name}");
Console.WriteLine($"📁 Project: {project.Name}\n");

// Debug: list registered tools (plugins + MCP tools)
var registeredTools = agent.DefaultOptions?.Tools;
if (registeredTools != null && registeredTools.Count > 0)
{
    Console.WriteLine("🔧 Registered tools:");
    foreach (var t in registeredTools.OfType<AIFunction>())
    {
        Console.WriteLine($" - {t.Name} : {t.Description}");
    }
}
else
{
    Console.WriteLine("🔧 No registered tools found on the agent.");
}

// 🎯 Simple chat loop
await RunInteractiveChat(conversation);

// ✨ NEW CONFIG-FIRST APPROACH: Using AgentConfig pattern
static async Task<(Project, Conversation, Agent)> CreateAIAssistant(IConfiguration config)
{
    // ✨ CREATE AGENT CONFIG OBJECT FIRST
    var agentConfig = new AgentConfig
    {
        Name = "AI Assistant",
        SystemInstructions = "You are a helpful AI assistant with memory, knowledge base, and web search capabilities.",
        MaxFunctionCalls = 6,
        MaxConversationHistory = 20,
        Provider = new ProviderConfig
        {
            Provider = ChatProvider.OpenRouter,
            ModelName = "google/gemini-2.5-pro"
            // No ApiKey here - will use appsettings.json via ResolveApiKey
        },
        InjectedMemory = new InjectedMemoryConfig
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
        Audio = new AudioConfig
        {
            // ElevenLabs will be configured from environment va`les
        }
    };

    // ✨ BUILD AGENT FROM CONFIG + FLUENT PLUGINS/FILTERS
    var agent = new AgentBuilder(agentConfig)
        .WithAPIConfiguration(config) // Pass appsettings.json for API key resolution
        .WithFilter(new LoggingAiFunctionFilter())
        .WithTavilyWebSearch()
        .WithInjectedMemory(opts => opts
            .WithStorageDirectory("./agent-memory-storage")
            .WithMaxTokens(6000))
        .WithPlugin<MathPlugin>()
        .WithElevenLabsAudio() // Will use environment variables or config
        .WithFullPermissions(new ConsolePermissionHandler())
        .WithMCP(agentConfig.Mcp.ManifestPath)
        .Build();

// 🎯 Project with smart defaults
var project = Project.Create("AI Chat Session");

    // 💬 Conversation just works
    var conversation = project.CreateConversation(agent);

    // ✨ Show config info
    Console.WriteLine($"✨ Agent created with config-first pattern!");
    Console.WriteLine($"📋 Config: {agentConfig.Name} - {agentConfig.Provider?.ModelName}");
    Console.WriteLine($"🧠 Memory: {agentConfig.InjectedMemory?.StorageDirectory}");
    Console.WriteLine($"🔧 Max Function Calls: {agentConfig.MaxFunctionCalls}");
    
    return (project, conversation, agent);
}

// ✨ CLEAN CHAT LOOP: Fixed response handling
static async Task RunInteractiveChat(Conversation conversation)
{
    Console.WriteLine("==========================================");
    Console.WriteLine("🤖 Interactive Chat Mode");
    Console.WriteLine("==========================================");
    Console.WriteLine("Commands:");
    Console.WriteLine("  • 'exit' or 'quit' - End conversation");
    Console.WriteLine("  • 'audio' - Test audio capabilities");
    Console.WriteLine("  • 'memory' - Show stored memories");
    Console.WriteLine("  • 'remember [text]' - Store a memory");
    Console.WriteLine("------------------------------------------\n");
    
    while (true)
    {
        Console.Write("You: ");
        var input = Console.ReadLine();
        
        if (input?.ToLower() is "exit" or "quit") break;
        if (string.IsNullOrWhiteSpace(input)) continue;

        try
        {
            Console.Write("AI: ");
            
            // 🎯 Handle special commands with streaming
            switch (input.ToLower())
            {
                case "audio":
                    await HandleAudioCommandStreaming(conversation);
                    break;
                case "memory":
                    await StreamResponse(conversation, "Show me my stored memories");
                    break;
                case var cmd when cmd.StartsWith("remember "):
                    await StreamResponse(conversation, $"Please remember this: {input[9..]}");
                    break;
                default:
                    await StreamResponse(conversation, input);
                    break;
            }
            
            Console.WriteLine(); // Add newline after streaming
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}\n");
        }
    }
}

static async Task StreamResponse(Conversation conversation, string message)
{
    await foreach (var evt in conversation.SendStreamingAsync(message))
    {
        switch (evt)
        {
            case RunStartedEvent runStart:
                Console.Write($"\n🚀 Run {runStart.RunId} started");
                break;
            case RunFinishedEvent runFinish:
                Console.Write($"\n✅ Run {runFinish.RunId} completed");
                break;
            case RunErrorEvent runError:
                Console.Write($"\n❌ Run failed: {runError.Message}");
                break;
            case StepStartedEvent step:
                Console.Write($"\n💭 {step.StepName}: ");
                break;
            case TextMessageStartEvent msgStart:
                Console.Write($"\n🤖 ");
                break;
            case TextMessageContentEvent text:
                Console.Write(text.Delta);
                break;
            case TextMessageEndEvent msgEnd:
                // Just add a newline after message completes
                break;
            case ToolCallStartEvent toolStart:
                Console.Write($"\n🔧 {toolStart.ToolCallName}");
                break;
            case ToolCallArgsEvent toolArgs:
                Console.Write($"({toolArgs.Delta})");
                break;
            case ToolCallEndEvent toolEnd:
                Console.Write("... ✅");
                break;
        }
    }
}

// ✨ NEW: Streaming audio handler  
static async Task HandleAudioCommandStreaming(Conversation conversation)
{
    Console.Write("Enter audio file path: ");
    var path = Console.ReadLine();
    
    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
    {
        // ✨ Create TextExtractionUtility instance for document processing
        var textExtractor = new TextExtractionUtility();
        
        // 🎯 Process documents, then stream response
        var uploads = await conversation.ProcessDocumentUploadsAsync([path], textExtractor);
        var enhancedMessage = ConversationDocumentHelper.FormatMessageWithDocuments(
            "Please transcribe this audio and provide a helpful response", uploads);
        
        await StreamResponse(conversation, enhancedMessage);
    }
    else
    {
        await StreamResponse(conversation, "No valid audio file provided.");
    }
}
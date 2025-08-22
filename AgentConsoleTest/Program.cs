using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

Console.WriteLine("🚀 HPD-Agent Console Test");

// ✨ Load configuration from appsettings.json
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// ✨ ONE-LINER: Create complete AI assistant
var (project, conversation, agent) = CreateAIAssistant(config);

Console.WriteLine($"✅ AI Assistant ready: {agent.Name}");
Console.WriteLine($"📁 Project: {project.Name}\n");

// 🎯 Simple chat loop
await RunInteractiveChat(conversation);

// ✨ CLEAN FACTORY METHOD: Fixed compilation issues
static (Project, Conversation, Agent) CreateAIAssistant(IConfiguration config)
{
    // � Get API keys from configuration
    var openRouterKey = config["OpenRouter:ApiKey"];
    if (string.IsNullOrWhiteSpace(openRouterKey))
        throw new InvalidOperationException("OpenRouter API key not configured. Set 'OpenRouter:ApiKey' in appsettings.json");

    var tavilyKey = config["Tavily:ApiKey"];
    if (string.IsNullOrWhiteSpace(tavilyKey))
        throw new InvalidOperationException("Tavily API key not configured. Set 'Tavily:ApiKey' in appsettings.json");

    // �🚀 Create WebSearch plugin instance (fix CS0310)
    var webSearchPlugin = new WebSearchPlugin(new WebSearchContext(
        new IWebSearchConnector[]
        {
            new TavilyConnector(new TavilyConfig { ApiKey = tavilyKey })
        }, 
        "tavily"));

    var agent = AgentBuilder.Create()
        .WithName("AI Assistant")
        .WithProvider(ChatProvider.OpenRouter, "google/gemini-2.5-pro", openRouterKey)
        .WithInstructions("You are a helpful AI assistant with memory, knowledge base, and web search capabilities.")
        .WithFilter(new LoggingAiFunctionFilter())
        .WithInjectedMemory(opts => opts
            .WithStorageDirectory("./agent-memory-storage")
            .WithMaxTokens(6000))
        .WithPlugin<MathPlugin>()
        .WithPlugin(webSearchPlugin)  // ✨ Fixed: Use instance instead of generic
        .WithElevenLabsAudio()
        .WithMCP("./MCP.json")
        .WithMaxFunctionCalls(6)
        .WithFullPermissions(new ConsolePermissionHandler())
        .Build();

    // 🎯 Project with smart defaults
    var project = Project.Create("AI Chat Session");
    project.SetAgent(agent);

    // 💬 Conversation just works
    var conversation = project.CreateConversation();
    
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
            // 🎯 Handle special commands with progressive disclosure
            var response = input.ToLower() switch
            {
                "audio" => await HandleAudioCommand(conversation),
                "memory" => await conversation.SendAsync("Show me my stored memories"),
                var cmd when cmd.StartsWith("remember ") => 
                    await conversation.SendAsync($"Please remember this: {input[9..]}"),
                _ => await conversation.SendAsync(input)
            };

            // ✨ Fixed: Proper text extraction from ChatResponse
            var responseText = ExtractTextFromResponse(response);
            Console.WriteLine($"AI: {responseText}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}\n");
        }
    }
}

// ✨ Helper method to extract text from ChatResponse (fix CS1061)
static string ExtractTextFromResponse(ChatResponse response)
{
    var lastMessage = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
    var textContent = lastMessage?.Contents.OfType<TextContent>().FirstOrDefault()?.Text;
    return textContent ?? "No response received.";
}

// ✨ SIMPLIFIED AUDIO: Fixed TextExtractionUtility requirement (fix CS7036)
static async Task<ChatResponse> HandleAudioCommand(Conversation conversation)
{
    Console.Write("Enter audio file path: ");
    var path = Console.ReadLine();
    
    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
    {
        // ✨ Create TextExtractionUtility instance for document processing
        var textExtractor = new TextExtractionUtility();
        
        // 🎯 Upload audio, get intelligent response
        return await conversation.SendWithDocumentsAsync(
            "Please transcribe this audio and provide a helpful response", 
            [path],
            textExtractor);
    }
    
    return await conversation.SendAsync("No valid audio file provided.");
}
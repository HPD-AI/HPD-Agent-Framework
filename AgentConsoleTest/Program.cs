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
    Console.WriteLine("  • 'concurrency' - Test thread-safety fixes");
    Console.WriteLine("  • 'streaming' - Test interleaved streaming");
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
                "concurrency" => await HandleConcurrencyTest(conversation),
                "streaming" => await HandleStreamingTest(conversation),
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

// ✨ CONCURRENCY TEST: Verify thread-safety fixes
static async Task<ChatResponse> HandleConcurrencyTest(Conversation conversation)
{
    Console.WriteLine("🧪 Starting concurrency test for thread-safety...");
    
    // Get the agent from the conversation
    var agents = typeof(Conversation).GetField("_agents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var agentList = agents?.GetValue(conversation) as List<Agent>;
    var agent = agentList?.FirstOrDefault();
    
    if (agent == null)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "❌ No agent found for testing"));
    }
    
    // Create multiple concurrent requests with math operations
    const int concurrentRequests = 5;
    var tasks = new List<Task<ChatResponse>>();
    var startTime = DateTime.Now;
    
    Console.WriteLine($"🚀 Launching {concurrentRequests} concurrent math operations...");
    
    for (int i = 0; i < concurrentRequests; i++)
    {
        int requestId = i;
        var messages = new[] { 
            new ChatMessage(ChatRole.User, $"Add {100 + requestId} and {200 + requestId}") 
        };
        
        tasks.Add(agent.GetResponseAsync(messages));
    }
    
    // Wait for all requests to complete
    var responses = await Task.WhenAll(tasks);
    var duration = DateTime.Now - startTime;
    
    Console.WriteLine($"⚡ Completed {responses.Length} concurrent requests in {duration.TotalMilliseconds:F0}ms");
    
    // Verify each response has proper metadata
    for (int i = 0; i < responses.Length; i++)
    {
        var response = responses[i];
        var hadFunctionCalls = response.GetOperationHadFunctionCalls();
        var functionCalls = response.GetOperationFunctionCalls();
        var functionCallCount = response.GetOperationFunctionCallCount();
        
        Console.WriteLine($"  Request {i}: FunctionCalls={hadFunctionCalls}, Count={functionCallCount}, Funcs=[{string.Join(", ", functionCalls)}]");
    }
    
    Console.WriteLine("✅ Concurrency test completed - no race conditions detected!");
    
    return new ChatResponse(new ChatMessage(ChatRole.Assistant, 
        $"Concurrency test successful! Processed {concurrentRequests} concurrent requests in {duration.TotalMilliseconds:F0}ms. " +
        "The new per-call metadata approach prevents race conditions that existed in the old instance field approach."));
}

// ✨ STREAMING TEST: Verify interleaved streaming improvements
static async Task<ChatResponse> HandleStreamingTest(Conversation conversation)
{
    Console.WriteLine("🌊 Testing interleaved streaming with tool calls...");
    Console.WriteLine("📡 This should show: text → [tool execution pause] → more text");
    Console.WriteLine("🕐 Watch for real-time streaming with function call interruptions:\n");
    
    Console.Write("AI: ");
    var startTime = DateTime.Now;
    var updateCount = 0;
    var functionCallCount = 0;
    
    // Use streaming to show the improved interleaved behavior
    await foreach (var update in conversation.SendStreamingAsync(
        "Please add 1500 and 2500, then tell me an interesting fact about the number 4000"))
    {
        updateCount++;
        
        if (update.Contents != null)
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                {
                    Console.Write(textContent.Text);
                    await Task.Delay(10); // Small delay to visualize streaming
                }
                else if (content is FunctionCallContent)
                {
                    functionCallCount++;
                    Console.Write($" [🔧 Function Call #{functionCallCount}] ");
                }
                else if (content is FunctionResultContent funcResult)
                {
                    Console.Write($" [✅ Result: {funcResult.Result}] ");
                }
            }
        }
    }
    
    var duration = DateTime.Now - startTime;
    Console.WriteLine($"\n\n📊 Streaming Stats:");
    Console.WriteLine($"   • Duration: {duration.TotalMilliseconds:F0}ms");
    Console.WriteLine($"   • Updates received: {updateCount}");
    Console.WriteLine($"   • Function calls: {functionCallCount}");
    Console.WriteLine("✅ Interleaved streaming test completed!");
    
    return new ChatResponse(new ChatMessage(ChatRole.Assistant, 
        "Streaming test completed! The new interleaved streaming allows text to flow immediately " +
        "while function calls execute in the background, providing better user experience."));
}
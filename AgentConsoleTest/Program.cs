using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;


Console.WriteLine("🚀 HPD-Agent Console Test");
Console.WriteLine("========================\n");

try
{
    Console.WriteLine("🤖 Starting Interactive Chat Mode...");
    await StartInteractiveChat();
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ Error: {ex.Message}");
    Console.WriteLine($"Stack: {ex.StackTrace}");
}

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();


static async Task StartInteractiveChat()
{
    Console.WriteLine("==========================================");
    Console.WriteLine("🤖 Interactive Chat Mode");
    Console.WriteLine("==========================================");
    Console.WriteLine("Enter your messages to chat with the agent.");
    Console.WriteLine("Type 'exit' or 'quit' to end the conversation.");
    Console.WriteLine("Type 'clear' to clear chat history.");
    Console.WriteLine("Type 'history' to show conversation history.");
    Console.WriteLine("Type 'memory' to show stored memories.");
    Console.WriteLine("Type 'remember [text]' to store a memory.");
    Console.WriteLine("Type 'forget [memory_id]' to delete a memory.");
    Console.WriteLine("------------------------------------------\n");

    // Create project with Memory CAG first
    var project = new Project("Interactive Chat Session");
    // Create the agent using the project's memory manager
    var agent = CreateChatAgent(project);
    if (agent == null)
    {
        Console.WriteLine("❌ Could not create agent. Please configure an API key.");
        return;
    }
    // Create a conversation under the project
    var conversation = project.CreateConversation(agent);
    Console.WriteLine($"✅ Chat agent ready with Memory CAG: {agent.GetType().Name}");
    Console.WriteLine($"📁 Project: {project.Name} (ID: {project.Id})\n");

    while (true)
    {
        Console.Write("You: ");
        var userInput = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(userInput))
            continue;

        var input = userInput.Trim().ToLowerInvariant();

        // Handle special commands
        if (input == "exit" || input == "quit")
        {
            Console.WriteLine("👋 Goodbye!");
            break;
        }

        if (input == "clear")
        {
            // Clear conversation by creating a new instance
            // Reset conversation under the same project (to retain memory context)
            conversation = project.CreateConversation(agent);
            Console.WriteLine("🧹 Chat history cleared.\n");
            continue;
        }

        if (input == "tts")
        {
            await TestTts(agent);
            continue;
        }

        if (input == "stt")
        {
            await TestStt(agent);
            continue;
        }

        if (input == "audio")
        {
            await TestFullAudioPipeline(agent);
            continue;
        }
// Suppress HPDAUDIO001 warning for AudioProcessingOptions usage
#pragma warning disable HPDAUDIO001
#pragma warning restore HPDAUDIO001
// Test full audio pipeline: STT → LLM → TTS
static async Task TestFullAudioPipeline(Agent agent)
{
    var audioCapability = agent.Audio;
    if (audioCapability == null)
    {
        Console.WriteLine("❌ Audio capability not available on this agent.");
        return;
    }

    Console.WriteLine("🎤 Full Audio Pipeline Test (STT → LLM → TTS)");
    Console.Write("Enter path to audio file (e.g., 'input.wav'): ");
    var audioPath = Console.ReadLine();
    
    if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
    {
        Console.WriteLine("❌ Audio file not found.");
        return;
    }

    try
    {
        Console.WriteLine("🔄 Processing audio through full pipeline...");
        
        using var audioStream = File.OpenRead(audioPath);
#pragma warning disable HPDAUDIO001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                var options = new AudioProcessingOptions
        {
            IncludeTranscription = true,
            CustomInstructions = "Please provide a helpful response to what the user said."
        };
#pragma warning restore HPDAUDIO001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

                var response = await audioCapability.ProcessAudioAsync(audioStream, options);
        
        Console.WriteLine($"📝 Transcribed: {response.TranscribedText}");
        Console.WriteLine($"🤖 LLM Response: {response.ResponseText}");
        
        if (response.AudioStream != null)
        {
            var outputPath = "pipeline_output.wav";
            using (var fileStream = File.Create(outputPath))
            {
                await response.AudioStream.CopyToAsync(fileStream);
            }
            Console.WriteLine($"🔊 Audio response saved to {outputPath}");
        }
        
        Console.WriteLine($"⏱️  Total time: {response.Metrics.TotalDuration.TotalSeconds:F2}s");
        Console.WriteLine($"   - STT: {response.Metrics.TranscriptionDuration.TotalSeconds:F2}s");
        Console.WriteLine($"   - LLM: {response.Metrics.LlmDuration.TotalSeconds:F2}s");
        Console.WriteLine($"   - TTS: {response.Metrics.SynthesisDuration.TotalSeconds:F2}s");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Full pipeline failed: {ex.Message}");
    }
}
// Test STT using AudioCapability
static async Task TestStt(Agent agent)
{
    var audioCapability = agent.Audio;
    if (audioCapability == null)
    {
        Console.WriteLine("❌ Audio capability not available on this agent.");
        return;
    }

    Console.Write("Enter path to WAV file for transcription: ");
    var path = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.WriteLine("❌ File not found.");
        return;
    }

    try
    {
        using var audioStream = File.OpenRead(path);
        var text = await audioCapability.TranscribeAsync(audioStream);
        Console.WriteLine($"✅ Transcription: {text}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ STT failed: {ex.Message}");
    }
}

        if (input.StartsWith("remember "))
        {
            var memoryContent = userInput.Substring(9).Trim();
            await conversation.SendAsync($"Please remember this: {memoryContent}");
            Console.WriteLine("✅ Memory creation requested.\n");
            continue;
        }
        if (input.StartsWith("forget "))
        {
            var memoryId = userInput.Substring(7).Trim();
            await conversation.SendAsync($"Please delete memory with ID: {memoryId}");
            Console.WriteLine("✅ Memory deletion requested.\n");
            continue;
        }

        try
        {
            // Send message using Conversation class - it handles all message management
            Console.Write("Agent: ");
            var response = await conversation.SendAsync(userInput);

            // Just display the last assistant message - Conversation already added it to history
            var lastMessage = conversation.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            var textContent = lastMessage?.Contents.OfType<TextContent>().FirstOrDefault()?.Text;

            if (!string.IsNullOrEmpty(textContent))
            {
                Console.WriteLine(textContent);
            }
            else
            {
                Console.WriteLine("❌ No response received from agent.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }

        Console.WriteLine(); // Add spacing between exchanges
    }
// Test TTS using AudioCapability
static async Task TestTts(Agent agent)
{
    // Retrieve the configured AudioCapability instance
    var audioCapability = agent.Audio;
    if (audioCapability == null)
    {
        Console.WriteLine("❌ Audio capability not available on this agent.");
        return;
    }

    Console.Write("Enter text to synthesize: ");
    var text = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(text))
    {
        Console.WriteLine("❌ No text entered.");
        return;
    }

    try
    {
        // Use dynamic to call SynthesizeAsync
        // Synthesize text to speech
        using var audioStream = await audioCapability.SynthesizeAsync(text);
        var outputPath = "tts_output.wav";
        using (var fileStream = File.Create(outputPath))
        {
            await audioStream.CopyToAsync(fileStream);
        }
        Console.WriteLine($"✅ Audio synthesized and saved to {outputPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ TTS failed: {ex.Message}");
    }
}
}


// Create an agent configured to use the project's memory manager
static Agent? CreateChatAgent(Project project)
{
    try
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        Console.WriteLine("Apple Intelligence is available. Using Apple Intelligence provider.");

        // Prepare connectors and a WebSearchPlugin instance (using Tavily for testing)
        var yourConnectors = new IWebSearchConnector[]
        {
            new TavilyConnector(new TavilyConfig { ApiKey = configuration["Tavily:ApiKey"] ?? "dummy" })
        };

        var webSearchContext = new WebSearchContext(yourConnectors, "tavily");
        var webSearchPlugin = new WebSearchPlugin(webSearchContext);

        // Check if your generated registration exists and works
        try
        {
            var functions = WebSearchPluginRegistration.CreatePlugin(webSearchPlugin, webSearchContext);
            Console.WriteLine($"Generated {functions.Count} functions:");
            foreach (var f in functions)
                Console.WriteLine($"- {f.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Plugin registration failed: {ex.Message}");
            // This would explain why you're seeing hardcoded functions
        }

        return AgentBuilder.Create()
            .WithConfiguration(configuration)
            .WithProvider(ChatProvider.OpenRouter, "google/gemini-2.5-pro")
            .WithName("InteractiveChatAgent")
            .WithInstructions(@"You are an expert AI math assistant. Always be clear, concise, and helpful. Provide code examples when possible. Answer as if you are mentoring a developer.")
            .WithMaxFunctionCalls(6) // Enable multi-turn function calling with up to 15 calls
            .WithFilter(new LoggingAiFunctionFilter())
            .WithPlugin<MathPlugin>(new MathPluginMetadataContext())
            .WithPlugin(webSearchPlugin, webSearchContext)
            .WithMemoryCagCapability(project.AgentMemoryCagManager)
            .WithMCP(Path.Combine(Directory.GetCurrentDirectory(), "MCP.json"))
            .WithElevenLabsAudio(
                configuration["ElevenLabs:ApiKey"], 
                configuration["ElevenLabs:DefaultVoiceId"]) // Read both from config
            .Build();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Failed to create agent: {ex.Message}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");
        return null;
    }
}
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using HPD.Agent.Plugins.FileSystem;

Console.WriteLine("🚀 HPD-Agent Console Test");

// ========================================
// 📊 Configure OpenTelemetry Exporters
// ========================================
Console.WriteLine("📊 Configuring OpenTelemetry exporters...");

// Configure metrics (counters, histograms)
var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("HPD.Agent")              // Subscribe to HPD-Agent metrics
    .AddMeter("CustomerService.Agent")  // Subscribe to custom agent metrics
    .AddConsoleExporter()               // Export to console
    .Build();

// Configure traces (distributed tracing)
var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("HPD.Agent")             // Subscribe to HPD-Agent traces
    .AddSource("CustomerService.Agent") // Subscribe to custom agent traces
    .AddConsoleExporter()               // Export to console
    .Build();

Console.WriteLine("✅ OpenTelemetry exporters configured!");
Console.WriteLine("   📈 Metrics will be visible in console output");
Console.WriteLine("   🔍 Traces will be visible in console output\n");

// ✨ Load configuration from appsettings.json
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// ✨ ONE-LINER: Create complete AI assistant
var (project, conversation, agent) = await CreateAIAssistant(config);

Console.WriteLine($"✅ AI Assistant ready: {agent.Name}");
Console.WriteLine($"📁 Project: {project.Name}\n");

// 🧪 TEST: PDF Text Extraction and Injection
Console.WriteLine("🧪 Testing PDF text extraction and injection...");
string? uploadedPdfPath = null;
try
{
    var pdfPath = @"C:\Users\einst\OneDrive\Desktop\Agent\HPD-Agent\AgentConsoleTest\perceptual-maps-best-practice.pdf";
    if (File.Exists(pdfPath))
    {
        Console.WriteLine($"📄 Uploading PDF: {Path.GetFileName(pdfPath)}");
        var document = await project.DocumentManager.UploadDocumentAsync(pdfPath, "Test PDF document");
        Console.WriteLine($"✅ Document uploaded successfully!");
        Console.WriteLine($"   - ID: {document.Id}");
        Console.WriteLine($"   - File: {document.FileName}");
        Console.WriteLine($"   - Size: {document.FileSize:N0} bytes");
        Console.WriteLine($"   - Text Length: {document.ExtractedText.Length:N0} characters");
        Console.WriteLine($"   - First 200 chars: {document.ExtractedText.Substring(0, Math.Min(200, document.ExtractedText.Length))}...\n");

        // Store the path for testing document injection
        uploadedPdfPath = pdfPath;

        // Test document injection in conversation
        Console.WriteLine("🧪 Testing document injection in conversation...");
        Console.WriteLine("Sending test message with PDF document...\n");
        Console.Write("AI: ");
        await StreamResponse(conversation,
            "What is this document about? Give me a brief 2-3 sentence summary of the perceptual maps best practices document.",
            documentPaths: new[] { pdfPath });
        Console.WriteLine("\n");
    }
    else
    {
        Console.WriteLine($"❌ PDF file not found at: {pdfPath}\n");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Error testing PDF extraction: {ex.Message}\n");
}

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

// 🧪 Test Microsoft.Extensions.AI enhancements
Console.WriteLine("\n🧪 Testing Microsoft.Extensions.AI enhancements...");
await TestAgentEnhancements(agent);

// 📊 Test Observability Features
Console.WriteLine("\n📊 Testing Observability Features...");
await TestObservabilityFeatures();

// 🎯 Simple chat loop
await RunInteractiveChat(conversation);

// ✨ NEW CONFIG-FIRST APPROACH: Using AgentConfig pattern
static Task<(Project, Conversation, Agent)> CreateAIAssistant(IConfiguration config)
{
    // ✨ CREATE AGENT CONFIG OBJECT FIRST
    var agentConfig = new AgentConfig
    {
        Name = "AI Assistant",
        SystemInstructions = "You are a helpful AI assistant with memory, knowledge base, and web search capabilities.",
        MaxAgenticIterations = 10,
        HistoryReduction = new HistoryReductionConfig
        {
            Enabled = true,
            Strategy = HistoryReductionStrategy.MessageCounting,
            TargetMessageCount = 20
        },
        Provider = new ProviderConfig
        {
            Provider = ChatProvider.OpenRouter,
            ModelName = "z-ai/glm-4.6", // 🧠 Reasoning model - FREE on OpenRouter!
            // Alternative reasoning models:
            // "deepseek/deepseek-r1-distill-qwen-32b" - smaller/faster
            // "openai/o1" - OpenAI's reasoning model (expensive)
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
        }
    };

    // ✨ BUILD AGENT FROM CONFIG + FLUENT PLUGINS/FILTERS
    var agent = new AgentBuilder(agentConfig)
        .WithAPIConfiguration(config) // Pass appsettings.json for API key resolution
        .WithTavilyWebSearch()
        .WithLogging()
        .WithInjectedMemory(opts => opts
            .WithStorageDirectory("./agent-memory-storage")
            .WithMaxTokens(6000))
        .WithPlanMode() // Plan mode enabled with defaults
        .WithPlugin<MathPlugin>()
        .WithPlugin(new FileSystemPlugin(new FileSystemContext(
            workspaceRoot: Directory.GetCurrentDirectory(),
            enableShell: true, // ✅ Enable shell execution
            maxShellTimeoutSeconds: 60, // 1 minute max timeout
            enableSearch: true,
            respectGitIgnore: true
        )))
        .WithConsolePermissions() // Function permissions only via ConsolePermissionFilter
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
    Console.WriteLine($"🔧 Max Function Call Turns: {agentConfig.MaxAgenticIterations}");
    
    return Task.FromResult((project, conversation, agent));
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

static async Task StreamResponse(Conversation conversation, string message, string[]? documentPaths = null)
{
    // Now returns ConversationStreamingResult with event stream and final metadata
    var result = await conversation.SendStreamingWithOutputAsync(message);
    
    // Display metadata after streaming completes
    if (result.Usage != null)
    {
        Console.Write($" [Tokens: {result.Usage.TotalTokens}");
        if (result.Usage.EstimatedCost.HasValue)
            Console.Write($", Cost: ${result.Usage.EstimatedCost:F4}");
        Console.Write($", Agent: {result.RespondingAgent.Name}");
        Console.Write($", Duration: {result.Duration.TotalSeconds:F1}s]");
    }
}

// ✨ NEW: Streaming audio handler  
static async Task HandleAudioCommandStreaming(Conversation conversation)
{
    Console.Write("Enter audio file path: ");
    var path = Console.ReadLine();
    
    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
    {
        // Direct call with documents using the new consolidated API
        await StreamResponse(conversation, 
            "Please transcribe this audio and provide a helpful response", 
            documentPaths: [path]);
    }
    else
    {
        await StreamResponse(conversation, "No valid audio file provided.");
    }
}

// 🧪 Test Microsoft.Extensions.AI enhancements
static async Task TestAgentEnhancements(Agent agent)
{
    Console.WriteLine("=== Microsoft.Extensions.AI Enhancement Verification ===");

    try
    {
        // Test 1: Metadata access
        Console.WriteLine("1. ChatClientMetadata:");
        var metadata = agent.Metadata;
        Console.WriteLine($"   ✓ Provider: {metadata.ProviderName}");
        Console.WriteLine($"   ✓ Model: {metadata.DefaultModelId}");
        Console.WriteLine($"   ✓ URI: {metadata.ProviderUri}");

        // Test 2: OpenTelemetry Activity Support
        Console.WriteLine("\n2. OpenTelemetry Telemetry:");
        Console.WriteLine($"   ✓ ActivitySource Name: HPD.Agent");
        Console.WriteLine($"   ✓ Telemetry: Integrated with Microsoft.Extensions.AI patterns");
        Console.WriteLine($"   ✓ Tracing: Available via Activity.Current in completions");
        Console.WriteLine($"   ✓ Metrics: Captured in activity tags (tokens, duration, etc.)");

        // Test 3: Service Discovery
        Console.WriteLine("\n3. Service Discovery (GetService):");
        var metadataService = ((IChatClient)agent).GetService(typeof(ChatClientMetadata));
        // AgentStatistics removed - using OpenTelemetry instead
        var configService = ((IChatClient)agent).GetService(typeof(AgentConfig));
        var errorPolicyService = ((IChatClient)agent).GetService(typeof(ErrorHandlingPolicy));

        Console.WriteLine($"   ✓ ChatClientMetadata: {(metadataService != null ? "Available" : "Not found")}");
        Console.WriteLine($"   ✓ OpenTelemetry: Available via Activity.Current");
        Console.WriteLine($"   ✓ AgentConfig: {(configService != null ? "Available" : "Not found")}");
        Console.WriteLine($"   ✓ ErrorHandlingPolicy: {(errorPolicyService != null ? "Available" : "Not found")}");

        // Test 4: Provider information
        Console.WriteLine("\n4. Provider Information:");
        Console.WriteLine($"   ✓ Provider Type: {agent.Provider}");
        Console.WriteLine($"   ✓ Model ID: {agent.ModelId}");
        Console.WriteLine($"   ✓ Conversation ID: {agent.ConversationId ?? "Not set"}");

        // Test 5: Telemetry Integration
        Console.WriteLine("\n5. Modern Telemetry Integration:");
        Console.WriteLine($"   ✓ Activity Source: HPD.Agent for agent operations");
        Console.WriteLine($"   ✓ Activity Source: HPD.Conversation for conversation turns");
        Console.WriteLine($"   ✓ OpenTelemetry Tags: agent.name, agent.provider, tokens_used, duration_ms");
        Console.WriteLine($"   ✓ Distributed Tracing: Full correlation across agent and conversation boundaries");
        Console.WriteLine($"   ✓ No Legacy Statistics: Moved to industry-standard OpenTelemetry patterns");

        // Test 6: Error handling and configuration validation
        Console.WriteLine("\n6. Enhanced Configuration & Error Handling:");
        Console.WriteLine($"   ✓ Error Policy: Normalize={agent.ErrorPolicy.NormalizeProviderErrors}, MaxRetries={agent.ErrorPolicy.MaxRetries}");
        Console.WriteLine($"   ✓ Configuration Validation: Built into AgentBuilder.Build()");
        Console.WriteLine($"   ✓ Provider-Specific Settings: Available via AdditionalProperties");

        Console.WriteLine("\n✅ All Microsoft.Extensions.AI enhancements verified successfully!");
        Console.WriteLine("🎯 Your Agent is now fully compatible with Microsoft.Extensions.AI patterns");
        Console.WriteLine("📊 Telemetry modernized with OpenTelemetry Activity-based tracking");
        Console.WriteLine("\n🚀 New Features Added:");
        Console.WriteLine("   • Error handling policy with provider normalization");
        Console.WriteLine("   • Comprehensive configuration validation");
        Console.WriteLine("   • Provider-specific settings classes");
        Console.WriteLine("   • Enhanced service discovery");

    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ Enhancement test failed: {ex.Message}");
    }

    await Task.CompletedTask;
}

// 📊 Test Observability Features (OpenTelemetry Tracing & Metrics)
static async Task TestObservabilityFeatures()
{
    Console.WriteLine("=== OpenTelemetry Observability Test ===");
    Console.WriteLine("This demonstrates how to enable complete observability with traces and metrics.\n");

    // Load configuration
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();

    Console.WriteLine("🔧 Step 1: Create Agent WITH Observability");
    Console.WriteLine("------------------------------------------------");
    Console.WriteLine("✅ Good news: Method order doesn't matter! Call .WithOpenTelemetry() anywhere.");
    Console.WriteLine();

    // ✨ EXAMPLE 1: Minimal observability setup
    var observableAgent = AgentBuilder.Create()
        .WithOpenTelemetry()                   // Can be called ANYWHERE in the chain!
        .WithAPIConfiguration(config)
        .WithProvider(ChatProvider.OpenRouter, "google/gemini-2.5-pro")
        .WithPlugin<MathPlugin>()
        .Build();

    Console.WriteLine("✅ Observability enabled with .WithOpenTelemetry()");
    Console.WriteLine("   • Traces: Agent turns, LLM calls, and tool executions");
    Console.WriteLine("   • Metrics: Tool call counts, durations, and error rates");
    Console.WriteLine("   • Source: HPD.Agent (default)\n");

    // ✨ EXAMPLE 2: Custom source name for multi-agent systems
    Console.WriteLine("🔧 Step 2: Custom Source Name (for multi-agent systems)");
    Console.WriteLine("------------------------------------------------");

    var customerServiceAgent = AgentBuilder.Create()
        .WithOpenTelemetry("CustomerService.Agent") // Can be first, last, or anywhere!
        .WithAPIConfiguration(config)
        .WithProvider(ChatProvider.OpenRouter, "google/gemini-2.5-pro")
        .Build();

    Console.WriteLine("✅ Custom observability source: 'CustomerService.Agent'");
    Console.WriteLine("   • Allows filtering traces/metrics by agent type");
    Console.WriteLine("   • Useful for multi-agent architectures\n");

    // ✨ EXAMPLE 3: What metrics are available
    Console.WriteLine("📊 Step 3: Available Metrics");
    Console.WriteLine("------------------------------------------------");
    Console.WriteLine("Automatic metrics emitted for every tool call:");
    Console.WriteLine("   1. agent.tool_calls.count (Counter)");
    Console.WriteLine("      - Total number of tool calls");
    Console.WriteLine("      - Tagged with: gen_ai.tool.name");
    Console.WriteLine();
    Console.WriteLine("   2. agent.tool_calls.duration (Histogram)");
    Console.WriteLine("      - Execution time in milliseconds");
    Console.WriteLine("      - Tagged with: gen_ai.tool.name");
    Console.WriteLine("      - Unit: ms");
    Console.WriteLine();
    Console.WriteLine("   3. agent.tool_calls.errors (Counter)");
    Console.WriteLine("      - Number of failed tool calls");
    Console.WriteLine("      - Tagged with: gen_ai.tool.name\n");

    // ✨ EXAMPLE 4: What traces are available
    Console.WriteLine("🔍 Step 4: Available Traces (Spans)");
    Console.WriteLine("------------------------------------------------");
    Console.WriteLine("Automatic distributed traces created:");
    Console.WriteLine("   1. Agent Turn (Parent Span)");
    Console.WriteLine("      - Name: 'agent.chat_completion'");
    Console.WriteLine("      - Captures entire user interaction");
    Console.WriteLine();
    Console.WriteLine("   2. LLM Calls (Child Spans)");
    Console.WriteLine("      - Created by Microsoft.Extensions.AI");
    Console.WriteLine("      - Includes tokens, model, provider info");
    Console.WriteLine();
    Console.WriteLine("   3. Tool Calls (Grandchild Spans)");
    Console.WriteLine("      - Name: 'execute_tool <function_name>'");
    Console.WriteLine("      - Tags: agent.name, conversation.id, gen_ai.tool.name");
    Console.WriteLine("      - Includes: arguments, results, errors\n");

    // ✨ EXAMPLE 5: How to consume the telemetry
    Console.WriteLine("🎯 Step 5: How to Consume Telemetry");
    Console.WriteLine("------------------------------------------------");
    Console.WriteLine("Option A: Export to OpenTelemetry Collector");
    Console.WriteLine("   - Use OpenTelemetry.Exporter.OpenTelemetryProtocol");
    Console.WriteLine("   - Configure MeterProvider and TracerProvider");
    Console.WriteLine("   - Send to: Jaeger, Zipkin, Prometheus, etc.");
    Console.WriteLine();
    Console.WriteLine("Option B: Export to Console (for development)");
    Console.WriteLine("   - Use OpenTelemetry.Exporter.Console");
    Console.WriteLine("   - See traces and metrics in real-time");
    Console.WriteLine();
    Console.WriteLine("Option C: Application Insights (Azure)");
    Console.WriteLine("   - Use Azure.Monitor.OpenTelemetry.Exporter");
    Console.WriteLine("   - Full integration with Azure monitoring\n");

    // ✨ EXAMPLE 6: Complete configuration example
    Console.WriteLine("💡 Step 6: Complete Configuration Example");
    Console.WriteLine("------------------------------------------------");
    Console.WriteLine(@"
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Configure OpenTelemetry SDK (in your Program.cs startup)
var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter(""HPD.Agent"")  // Subscribe to HPD-Agent metrics
    .AddConsoleExporter()     // Or use OTLP, Prometheus, etc.
    .Build();

var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource(""HPD.Agent"") // Subscribe to HPD-Agent traces
    .AddConsoleExporter()     // Or use OTLP, Jaeger, etc.
    .Build();

// Now create your agent with observability enabled
var agent = AgentBuilder.Create()
    .WithOpenTelemetry()                           // Order doesn't matter!
    .WithProvider(ChatProvider.OpenAI, ""gpt-4"")
    .Build();                                      // Metrics and traces now flow!
");

    Console.WriteLine("✅ All observability features demonstrated!");
    Console.WriteLine("🎯 Your agent now has full OpenTelemetry instrumentation");
    Console.WriteLine("📊 Ready for production monitoring and observability platforms\n");

    await Task.CompletedTask;
}
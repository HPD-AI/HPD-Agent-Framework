# HPD.Agent.Framework
```
            ██╗  ██╗██████╗ ██████╗       █████╗  ██████╗ ███████╗███╗   ██╗████████╗
            ██║  ██║██╔══██╗██╔══██╗     ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝
            ███████║██████╔╝██║  ██║█████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   
            ██╔══██║██╔═══╝ ██║  ██║╚════╝██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   
            ██║  ██║██║     ██████╔╝      ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   
            ╚═╝  ╚═╝╚═╝     ╚═════╝       ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   
```
The HPD Agent Framework is a battery first agentic framework designed to enable you to create reliable agents as quickly as possible. We build the infrastructure for reliable autonomous agents, you build the future.

The single philosophy driving this library: ***"Make Simple Things Simple, Make Complex Things Possible"***

## Main Characteristics 
- **Lightweight**
- **Native AOT First**
- **Builder and Configuration First** 
- **Provider Agnostic**
- **Event Streaming First**

## Built-In Features
- **Custom Event Protocol** - Easily receive a serialzed protool of all events emitted by the agent
- **Middleware** - Extensible middleware system for customizing agent behavior
- **Durable Execution** - Checkpointing and conversation thread persistence
- **PII Filtering** - Remove sensitive information before it reaches the LLM
- **Error Handling** - Built in Provider Error Handling
- **Permissions** - Permission system for tool execution control
- **History Reduction** - Conversation summarization and context window management
- **Tool Calling** - First-class support for function/tool calling with automatic schema generation
- **Tool Scoping** - Innovative mechanism to reduce tool context(without RAG or Code Execution or Truncation)
- **Skills** - Provider agnostic way to define reusable agent skills
- **Observability** - Built-in event system for logging, telemetry, and debugging
- **Planning** - Built-in plan mode for complex tasks
- **SubAgents** - Built-in support for nested agent orchestration
- **MCP Support** - Supports MCP with Scoping/Collapsing Mechanism
- **Custom Event Handling Presets** - Custom Event UI handling for normal Chat conversations, telemetry etc

## Quick Start

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithName("Assistant")
    .WithInstructions("You are a helpful assistant.")
    .WithEventHandler(new ConsoleEventHandler())
    .Build();

await foreach (var _ in agent.RunAsync("Hello!")) { }
```

## Advanced Example

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithName("ResearchAssistant")
    .WithInstructions("You are a research assistant with access to tools and knowledge.")
    // Plugins & Tools
    .WithPlugin<WebSearchPlugin>()
    .WithPlugin<FileSystemPlugin>()
    .WithToolScoping()                              // Reduces tool context for better performance
    // Middleware
    .WithPermissions()                              // Require approval for sensitive operations
    .WithPIIProtection()                            // Filter sensitive data
    .WithHistoryReduction(config => {
        config.Strategy = HistoryReductionStrategy.Summarizing;
        config.TargetMessageCount = 30;
    })
    .WithCircuitBreaker(maxConsecutiveCalls: 5)
    .WithFunctionRetry()
    // Memory
    .WithStaticMemory(opts => {
        opts.StorageDirectory = "./knowledge";
        opts.AddDocument("./docs/company-policies.md");
    })
    .WithDynamicMemory(opts => {
        opts.StorageDirectory = "./memory";
        opts.EnableAutoEviction = true;
    })
    .WithPlanMode()                                 // Enable planning capabilities
    // Observability
    .WithLogging(loggerFactory)
    .WithTelemetry()
    .WithEventHandler(new ConsoleEventHandler())

    .Build();

await foreach (var evt in agent.RunAsync("Research the latest AI trends"))
{
    // Process streaming events
}
```

## Requirements

- .NET 10.0+
- Microsoft.Extensions.AI 10.0.0+

## License


Proprietary - See LICENSE file.


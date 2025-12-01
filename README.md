# HPD.Agent.Framework
```
                    ██╗  ██╗██████╗ ██████╗       █████╗  ██████╗ ███████╗███╗   ██╗████████╗
                    ██║  ██║██╔══██╗██╔══██╗     ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝
                    ███████║██████╔╝██║  ██║█████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   
                    ██╔══██║██╔═══╝ ██║  ██║╚════╝██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   
                    ██║  ██║██║     ██████╔╝      ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   
                    ╚═╝  ╚═╝╚═╝     ╚═════╝       ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   
```
The HPD Agent Framework is a battery first agentic framework designed to enable you to create reliable agents as quickly as possible.

The single philosophy driving this library: ***"Make Simple Things Simple, Make Complex Things Possible"***

## Main Characteristics 
- **Native AOT First**
- **Configuration First** 
- **Provider Agnostic**
- **Event Streaming First** - No built non streaming mechanisms due to the event architecture

## Built-In Features
- **Custom Event Protocol** - Standardizes how AI agents connect to UI
- **Middleware** - Extensible middleware system for customizing agent behavior
- **Durable Execution** - Checkpointing and conversation thread persistence
- **PII Filtering** - Remove sensitive information before it reaches the LLM
- **Error Handling** - Built in Provider Error Handling
- **Document Handling** - Automatic text extraction from PDFs, DOCX, Powerpoint, Excel, HTML, and more
- **Permissions** - Permission system for tool execution control
- **History Reduction** - Conversation summarization and context window management
- **Tool Calling** - First-class support for function/tool calling with automatic schema generation
- **Tool Scoping** - Innovative mechanism to reduce tool context(without RAG or Code Execution or Truncation)
- **Skills** - Provider agnostic way to define reusable agent skills
- **Observability** - Built-in event system for logging, telemetry, and debugging
- **Memory** - Static, Dynamic for agent knowledge persistence
- **Planning** - Built-in plan mode for complex tasks
- **SubAgents** - Built-in support for nested agent orchestration
- **MCP Support** - Supports MCP
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

## Future Features
- **Audio TTS->LLM->STT Support**
- **Streaming Structured Output**
- **Dedicated Observability Platform**
- **Evaluators**
- **Graph Support**
- **A2A and AGUI Support**

## Future Language Support(Not Guaranteed)
- **Python**
- **Rust**
- **Swift**


## Requirements

- .NET 10.0+
- Microsoft.Extensions.AI 10.0.0+

## License

Proprietary - See LICENSE file.

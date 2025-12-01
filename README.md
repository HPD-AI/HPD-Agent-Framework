# HPD.Agent.Framework

A middleware-driven agentic AI framework built on Microsoft.Extensions.AI.

## Features

- **Native AOT First** - Designed for ahead-of-time compilation with minimal reflection
- **Middleware Pipeline** - Extensible middleware system for customizing agent behavior
- **Tool Calling** - First-class support for function/tool calling with automatic schema generation
- **History Reduction** - Built-in conversation summarization and context window management
- **Observability** - Built-in event system for logging, telemetry, and debugging
- **Permissions** - Built-in permission system for tool execution control
- **Durable Execution** - Built-in checkpointing and conversation thread persistence
- **Memory Systems** - Static and dynamic memory for agent knowledge persistence
- **Document Handling** - Automatic text extraction from PDFs, DOCX, HTML, and more
- **Skills System** - Define reusable agent capabilities with source-generated metadata
- **SubAgents** - Built-in support for nested agent orchestration
- **Configuration** - Flexible config object for serializable agent definitions
- **Provider Agnostic** - Works with any LLM provider via Microsoft.Extensions.AI

## Quick Start

```csharp
using HPD.Agent;

var agent = new AgentBuilder()
    .WithChatClient(yourChatClient)
    .WithName("Assistant")
    .WithInstructions("You are a helpful assistant.")
    .Build();

await foreach (var _ in agent.RunAsync("Hello!")) { }
```

## Requirements

- .NET 10.0+
- Microsoft.Extensions.AI 10.0.0+

## License

Proprietary - See LICENSE file.

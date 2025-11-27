# HPD-Agent

**A production-ready .NET agent framework with durable execution, intelligent tool management, and multi-protocol support.**

[![NuGet](https://img.shields.io/nuget/v/HPD-Agent.svg)](https://www.nuget.org/packages/HPD-Agent/)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Proprietary-blue.svg)](LICENSE.md)

---

## What is HPD-Agent?

HPD-Agent is a comprehensive framework for building AI agents in .NET. It provides everything you need to create production-grade agents: durable execution with crash recovery, intelligent token management, multi-provider support, and protocol adapters for seamless integration.

```csharp
// Create a production-ready agent in minutes
var agent = new AgentBuilder()
    .WithInstructions("You are a helpful assistant.")
    .WithProvider("openai", "gpt-4o", apiKey)
    .RegisterPlugin<FileSystemPlugin>()
    .RegisterPlugin<WebSearchPlugin>()
    .WithThreadStore(new InMemoryConversationThreadStore())  // Durable execution
    .Build();

// Run with automatic checkpointing
var thread = agent.CreateThread();
await foreach (var response in agent.RunAsync(messages, thread))
{
    Console.Write(response.Text);
}
```

---

## Key Features

### Durable Execution
- **Automatic Checkpointing** - Agent state saved during execution, not just after
- **Mid-Run Recovery** - Resume from exact iteration after crashes
- **Pending Writes** - Partial failure recovery for parallel tool calls
- **Time-Travel Debugging** - Full checkpoint history with `FullHistory` mode

### Intelligent Token Management
- **Scoping System** - 87.5% token reduction by hierarchically organizing tools
- **History Reduction** - Automatic conversation compression with cache-aware optimization
- **Skills System** - Load specialized knowledge only when needed

### Multi-Provider Support
11 LLM providers out of the box:
> OpenAI • Anthropic • Azure OpenAI • Azure AI Inference • Google AI • Mistral • Ollama • HuggingFace • AWS Bedrock • OnnxRuntime • OpenRouter

### Protocol Support
- **A2A Protocol** - Agent-to-agent communication
- **AG-UI Protocol** - Real-time streaming for frontends
- **MCP Protocol** - Model Context Protocol integration

### Production Features
- **Permissions** - Function-level authorization with human-in-the-loop
- **Error Handling** - Provider-aware retry, circuit breakers, Retry-After headers
- **Observability** - OpenTelemetry integration, structured logging
- **Native AOT** - Full compatibility for ahead-of-time compilation

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  HPD-Agent Architecture                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐         │
│  │  A2A        │    │  AG-UI      │    │  MCP        │         │
│  │  Protocol   │    │  Protocol   │    │  Protocol   │         │
│  └──────┬──────┘    └──────┬──────┘    └──────┬──────┘         │
│         │                  │                  │                 │
│         └──────────────────┼──────────────────┘                 │
│                            ▼                                    │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  AgentCore (Protocol-Agnostic)                          │   │
│  │                                                          │   │
│  │  • Stateless execution engine                           │   │
│  │  • Internal checkpointing (fire-and-forget)             │   │
│  │  • Middleware pipeline                                   │   │
│  │  • Event-driven observability                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                            │                                    │
│         ┌──────────────────┼──────────────────┐                │
│         ▼                  ▼                  ▼                 │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐         │
│  │ Plugins     │    │ Skills      │    │ Memory      │         │
│  │ (Tools)     │    │ (Knowledge) │    │ Systems     │         │
│  └─────────────┘    └─────────────┘    └─────────────┘         │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ConversationThread + IConversationThreadStore          │   │
│  │                                                          │   │
│  │  • Full execution state (AgentLoopState)                │   │
│  │  • Message history with token tracking                  │   │
│  │  • Checkpoint metadata and versioning                   │   │
│  │  • Pending writes for partial recovery                  │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Providers (11+)                                         │   │
│  │  OpenAI • Anthropic • Azure • Google • Mistral • ...    │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

## Core Concepts

### ConversationThread
The unit of conversation state. Contains messages, metadata, and execution state for durable execution.

```csharp
var thread = agent.CreateThread();

// Thread persists across runs
await agent.RunAsync(messages1, thread);
await agent.RunAsync(messages2, thread);  // Continues conversation

// Serialize for storage
var snapshot = thread.Serialize();
```

### IConversationThreadStore
Persistence layer for threads with full checkpointing support.

```csharp
// Development
var store = new InMemoryConversationThreadStore();

// Production (implement your own)
var store = new PostgresConversationThreadStore(connectionString);

// Configure agent
var agent = new AgentBuilder()
    .WithThreadStore(store)
    .WithCheckpointFrequency(CheckpointFrequency.PerIteration)
    .Build();
```

### Plugins
Tools for the agent, with source-generated schemas for Native AOT.

```csharp
[HPDPlugin]
public class FileSystemPlugin
{
    [HPDFunction("Read a file from disk")]
    public string ReadFile(string path) => File.ReadAllText(path);

    [HPDFunction("Write content to a file")]
    public void WriteFile(string path, string content) => File.WriteAllText(path, content);
}
```

### Skills
Package domain expertise with functions - load knowledge only when needed.

```csharp
var codeReviewSkill = Skill.Create(
    name: "code_review",
    description: "Activate for code review tasks",
    instructions: "Follow these code review guidelines...",
    references: new[] { "CodeAnalysisPlugin.AnalyzeCode", "GitPlugin.GetDiff" }
);
```

---

## Memory Systems

### Dynamic Memory
Agent-controlled working memory with automatic eviction.

```csharp
config.DynamicMemory = new DynamicMemoryConfig
{
    Enabled = true,
    MaxTokens = 4000,
    EvictionThreshold = 0.85  // Evict when 85% full
};
```

### Static Memory
Read-only knowledge base - RAG without vector databases.

```csharp
config.StaticMemory = new StaticMemoryConfig
{
    Enabled = true,
    Strategy = StaticMemoryStrategy.FullTextInjection,
    MaxTokens = 8000
};
```

### Plan Mode
Goal-oriented execution with step tracking.

```csharp
config.PlanMode = new PlanModeConfig
{
    Enabled = true,
    AutoCreatePlan = true
};
```

---

## Durable Execution

HPD-Agent checkpoints execution state internally, enabling recovery from any point:

```csharp
// Configure durable execution
var config = new AgentConfig
{
    ThreadStore = new InMemoryConversationThreadStore(),
    CheckpointFrequency = CheckpointFrequency.PerIteration,
    EnablePendingWrites = true  // Partial failure recovery
};

// First run - crashes at iteration 5
var thread = agent.CreateThread();
await agent.RunAsync(messages, thread);  // Checkpoints saved internally

// After restart - resume from iteration 5
var restored = await store.LoadThreadAsync(thread.Id);
if (restored?.ExecutionState != null)
{
    await agent.RunAsync(Array.Empty<ChatMessage>(), restored);  // Resumes!
}
```

**What gets checkpointed:**
- Full message history
- Current iteration number
- Expanded plugins/skills (scoping state)
- Circuit breaker state
- Pending writes (completed tool calls)
- Active history reduction state

---

## Documentation

### User Guides
- **[Quick Start](docs/QUICK_START.md)** - Get running in 5 minutes
- **[Conversation Architecture](docs/CONVERSATION_ARCHITECTURE.md)** - Thread and persistence model
- **[Skills Guide](docs/skills/SKILLS_GUIDE.md)** - Package domain expertise
- **[Permissions Guide](docs/permissions/PERMISSION_SYSTEM_GUIDE.md)** - Authorization and human-in-the-loop

### API Reference
- **[ConversationThread API](docs/ConversationThread-API-Reference.md)** - Thread operations
- **[Skills API](docs/skills/SKILLS_API_REFERENCE.md)** - Skill configuration
- **[Permissions API](docs/permissions/PERMISSION_SYSTEM_API.md)** - Permission management
- **[Event Handling API](docs/EventHandling/API_REFERENCE.md)** - Observability

### Architecture
- **[Architecture Overview](docs/ARCHITECTURE_OVERVIEW.md)** - System design
- **[Scoping System](docs/SCOPING_SYSTEM.md)** - Token reduction via tool hierarchy
- **[Durable Execution](docs/THREAD_SCOPED_DURABLE_EXECUTION.md)** - Checkpointing deep-dive
- **[Message Store Architecture](docs/MESSAGE_STORE_ARCHITECTURE.md)** - Storage internals

### Developer Guides
- **[Agent Developer Guide](docs/Agent-Developer-Documentation.md)** - Build agents
- **[Plugin Development](docs/PLUGIN_CLARIFICATION.md)** - Create plugins
- **[SubAgents](docs/SubAgents/ARCHITECTURE.md)** - Multi-agent patterns

---

## Example: Full-Featured Agent

```csharp
var agent = new AgentBuilder()
    // Core configuration
    .WithInstructions("You are a senior software engineer assistant.")
    .WithProvider("anthropic", "claude-sonnet-4-20250514", apiKey)

    // Plugins (tools)
    .RegisterPlugin<FileSystemPlugin>()
    .RegisterPlugin<GitPlugin>()
    .RegisterPlugin<CodeAnalysisPlugin>()

    // Skills (knowledge)
    .RegisterSkill(Skill.Create(
        name: "code_review",
        description: "Comprehensive code review expertise",
        instructions: await File.ReadAllTextAsync("skills/code-review.md"),
        references: new[] { "CodeAnalysisPlugin.*", "GitPlugin.GetDiff" }
    ))

    // Memory
    .WithDynamicMemory(maxTokens: 4000)
    .WithStaticMemory(strategy: StaticMemoryStrategy.FullTextInjection)

    // Durable execution
    .WithThreadStore(new PostgresConversationThreadStore(connectionString))
    .WithCheckpointFrequency(CheckpointFrequency.PerIteration)
    .WithPendingWrites(true)

    // Scoping (token reduction)
    .WithScoping(enabled: true)

    // Error handling
    .WithErrorHandling(config => {
        config.MaxRetries = 3;
        config.UseProviderRetryDelays = true;
    })

    .Build();

// Run with full observability
var thread = agent.CreateThread();
await foreach (var evt in agent.RunAsync(messages, thread))
{
    switch (evt)
    {
        case TextDeltaEvent text:
            Console.Write(text.Delta);
            break;
        case ToolCallEvent tool:
            Console.WriteLine($"Calling: {tool.FunctionName}");
            break;
        case CheckpointEvent cp:
            Console.WriteLine($"Checkpoint saved at iteration {cp.Iteration}");
            break;
    }
}
```

---

## Why HPD-Agent?

| Challenge | HPD-Agent Solution |
|-----------|-------------------|
| Agent crashes mid-execution | **Durable execution** - Resume from any iteration |
| Token limits with many tools | **Scoping** - 87.5% reduction via hierarchy |
| Long conversations | **History reduction** - Automatic compression |
| Production reliability | **Circuit breakers**, retries, observability |
| Multi-provider support | **11 providers** with unified API |
| Native AOT deployment | **100% compatible** |

---

## Getting Started

```bash
# Install the package
dotnet add package HPD-Agent

# Install a provider (e.g., OpenAI)
dotnet add package HPD-Agent.Providers.OpenAI
```

```csharp
using HPD.Agent;

var agent = new AgentBuilder()
    .WithInstructions("You are a helpful assistant.")
    .WithProvider("openai", "gpt-4o", Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
    .Build();

var thread = agent.CreateThread();
await foreach (var response in agent.RunAsync(
    new[] { new ChatMessage(ChatRole.User, "Hello!") },
    thread))
{
    Console.Write(response.Text);
}
```

See the **[Quick Start Guide](docs/QUICK_START.md)** for more.

---

## License

Proprietary. See [LICENSE.md](LICENSE.md) for details.

---

## Support

- **Documentation**: [docs.hpd-agent.com](https://docs.hpd-agent.com)
- **Email**: [support@hpd-agent.com](mailto:support@hpd-agent.com)

---

<div align="center">

**Production-Ready .NET Agent Framework**

*Durable Execution · Intelligent Token Management · Multi-Protocol Support*

[Quick Start](docs/QUICK_START.md) · [Documentation](docs/) · [Examples](examples/)

</div>

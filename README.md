# HPD-Agent Framework

[![GitHub](https://img.shields.io/badge/GitHub-HPD--AI%2FHPD--Agent--Framework-181717?logo=github)](https://github.com/HPD-AI/HPD-Agent-Framework)
[![Docs](https://img.shields.io/badge/Docs-hpd--ai.github.io-blue)](https://hpd-ai.github.io/HPD-Agent-Framework/)
[![NuGet](https://img.shields.io/nuget/v/HPD-Agent.Framework?label=NuGet&color=004880&logo=nuget)](https://www.nuget.org/packages/HPD-Agent.Framework)

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="architecture-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="architecture.svg">
  <img alt="HPD-Agent Architecture" src="architecture.svg">
</picture>

A full-stack framework for building AI agents — C# backend with tools, middleware, multi-turn conversations, and multi-agent workflows, paired with TypeScript/Svelte UI libraries for building rich, streaming chat interfaces.

> [!WARNING]
> **HPD.Agent.Framework is currently in an early development phase.**
>
> Until the release of version **1.0**, the API, inner mechanisms and naming are subject to significant changes without notice.

## Install

```bash
dotnet add package HPD-Agent.Framework
```

## Quick Start

```csharp
using HPD.Agent;

var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSystemInstructions("You are a helpful assistant.")
    .Build();

await foreach (var evt in agent.RunAsync("Hello!"))
{
    if (evt is TextDeltaEvent textDelta)
        Console.Write(textDelta.Text);
}
```

## Core Concepts

### Tools

Give your agent capabilities by registering toolkits:

```csharp
public class CalculatorToolkit
{
    [AIFunction(Description = "Add two numbers")]
    public int Add(int a, int b) => a + b;
}

var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithToolkit<CalculatorToolkit>()
    .Build();
```

Tools can also be loaded from MCP servers, OpenAPI specs, or provided by the client at runtime. See [Tools documentation](documentation/Tools/).

### Multi-Turn Conversations

Use sessions and branches to maintain conversation history:

```csharp
var (session, branch) = agent.CreateSession("user-123");

await foreach (var evt in agent.RunAsync("Add 10 and 20", branch)) { }
await foreach (var evt in agent.RunAsync("Now multiply that by 5", branch)) { }
// Agent remembers the previous result
```

For persistent conversations that survive process restarts:

```csharp
var agent = new AgentBuilder()
    .WithSessionStore(new JsonSessionStore("./sessions"))
    .Build();

await foreach (var evt in agent.RunAsync("Message", sessionId: "user-123")) { }
```

See [02 Multi-Turn Conversations](documentation/Getting%20Started/02%20Multi-Turn%20Conversations.md).

### Middleware

Intercept and customize agent behavior at every stage of execution:

```csharp
public class LoggingMiddleware : IAgentMiddleware
{
    public Task BeforeFunctionAsync(AgentMiddlewareContext context, CancellationToken ct)
    {
        Console.WriteLine($"Calling: {context.Function?.Name}");
        return Task.CompletedTask;
    }
}

var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithMiddleware(new LoggingMiddleware())
    .Build();
```

Built-in middleware includes: `CircuitBreaker`, `RetryMiddleware`, `HistoryReduction`, `PIIMiddleware`, `FunctionTimeout`, `Logging`, and more. See [Middleware documentation](documentation/Middleware/).

### Agent Configuration

The recommended pattern for production is **Builder + Config** — define configuration as data, then layer runtime customization on top:

```csharp
// Define config once (can also be loaded from JSON)
var config = new AgentConfig
{
    Name = "SupportAgent",
    SystemInstructions = "You are a support assistant.",
    Provider = new ProviderConfig { ProviderKey = "openai", ModelName = "gpt-4o" },
    MaxAgenticIterations = 15,
    Toolkits = ["KnowledgeToolkit"],
    Middlewares = ["LoggingMiddleware"]
};

// Layer runtime-only concerns on top
var agent = new AgentBuilder(config)
    .WithServiceProvider(services)
    .WithToolkit<MyCompiledTool>()
    .Build();
```

Or load directly from a JSON file:

```csharp
var agent = new AgentBuilder("agent-config.json")
    .WithServiceProvider(services)
    .Build();
```

See [01 Customizing an Agent](documentation/Getting%20Started/01%20Customizing%20an%20Agent.md).

### Multi-Agent Workflows

Compose multiple specialized agents in a directed graph:

```csharp
using HPD.MultiAgent;

var workflow = await AgentWorkflow.Create()
    .AddAgent("researcher", new AgentConfig
    {
        SystemInstructions = "Research the topic thoroughly."
    })
    .AddAgent("writer", new AgentConfig
    {
        SystemInstructions = "Write a clear, concise answer."
    })
    .From("researcher").To("writer")
    .BuildAsync();

var result = await workflow.RunAsync("Explain quantum entanglement");
Console.WriteLine(result.FinalAnswer);
```

Edges can be conditional, routing based on agent output fields:

```csharp
.From("triage")
    .To("billing", when => when.Field("intent").Equals("billing"))
    .To("support", when => when.Field("intent").Equals("support"))
    .To("fallback")
```

See [Multi-Agent documentation](documentation/Multi-Agent/).

## Event Streaming

Agents stream events as they work — text output, tool calls, turn lifecycle, and more:

```csharp
await foreach (var evt in agent.RunAsync("Do something", branch))
{
    switch (evt)
    {
        case TextDeltaEvent textDelta:
            Console.Write(textDelta.Text);
            break;
        case ToolCallStartEvent toolCall:
            Console.WriteLine($"\n[Tool: {toolCall.Name}]");
            break;
        case TurnCompletedEvent:
            Console.WriteLine("\n[Done]");
            break;
    }
}
```

See [05 Event Handling](documentation/Getting%20Started/05%20Event%20Handling.md).

## Building Apps

- **Console apps** — See [07 Building Console Apps](documentation/Getting%20Started/07%20Building%20Console%20Apps.md)
- **Web apps (ASP.NET Core)** — See [08 Building Web Apps](documentation/Getting%20Started/08%20Building%20Web%20Apps.md)

## TypeScript Libraries

Two companion packages for building frontends that connect to an HPD-Agent backend.

### `@hpd/hpd-agent-client`

A lightweight, zero-dependency TypeScript client for consuming the agent's event stream. Works in both browser and Node.js.

```bash
npm install @hpd/hpd-agent-client
```

```ts
import { AgentClient } from '@hpd/hpd-agent-client';

const client = new AgentClient('https://your-api/agent');

await client.stream('Explain quantum entanglement', {
    onTextDelta: (evt) => process.stdout.write(evt.text),
    onToolCallStart: (evt) => console.log(`Tool: ${evt.name}`),
    onError: (err) => console.error(err),
});
```

The client supports SSE (default), WebSocket, and Maui transports, and handles bidirectional flows for permissions, clarifications, continuations, and client-side tool invocation. It covers all 71 HPD protocol event types.

### `@hpd/hpd-agent-headless-ui`

A Svelte 5 headless component library for building AI chat interfaces. Zero CSS — you control all styling. Designed specifically for AI-specific primitives: streaming text, tool execution, permissions, branching, and voice.

```bash
npm install @hpd/hpd-agent-headless-ui
```

```svelte
<script>
  import { createMockAgent } from '@hpd/hpd-agent-headless-ui';

  const agent = createMockAgent();
  let input = '';

  async function send() {
    await agent.send(input);
    input = '';
  }
</script>
```

Key components (not exhaustive — see [typescript/](typescript/) for the full library):

| Component | Purpose |
|-----------|---------|
| `Message` / `MessageList` | Streaming-aware message display with thinking, tool, and reasoning states |
| `MessageActions` | Edit, retry, and copy buttons attached to messages |
| `MessageEdit` | Inline message editing with save/cancel |
| `ChatInput` | Compositional input with leading/trailing/top/bottom accessory slots |
| `ToolExecution` | Display and track in-progress tool calls |
| `PermissionDialog` | Handle AI permission requests |
| `BranchSwitcher` | Navigate sibling conversation branches |
| `SessionList` | Display and manage conversation sessions |
| `Artifact` | Teleport rich content (code, documents, charts) into a side panel |
| `SplitPanel` | Arbitrarily nested resizable layout panels with persistence and undo/redo |
| `AudioPlayer` / `Transcription` | Voice playback and speech-to-text streaming |
| `VoiceActivityIndicator` | Visual feedback during voice input |
| `AudioVisualizer` | Waveform/level visualization for audio streams |
| `InterruptionIndicator` / `TurnIndicator` | Voice turn and interruption state display |
| `Input` | Base AI-aware message input primitive |

Total bundle: < 20 KB gzipped. Located at [typescript/](typescript/).

## Documentation

| Topic | Location |
|-------|----------|
| Agents overview & first steps | [Getting Started/](documentation/Getting%20Started/) |
| Agent configuration | [01 Customizing an Agent](documentation/Getting%20Started/01%20Customizing%20an%20Agent.md) |
| Multi-turn conversations & sessions | [02 Multi-Turn Conversations](documentation/Getting%20Started/02%20Multi-Turn%20Conversations.md) |
| Tool calling | [03 Tool Calling](documentation/Getting%20Started/03%20Tool%20Calling.md) |
| Middleware pipeline | [Middleware/](documentation/Middleware/) |
| Event handling | [05 Event Handling](documentation/Getting%20Started/05%20Event%20Handling.md) |
| Memory & content store | [06 Memory & Content Store](documentation/Getting%20Started/06%20Memory%20%26%20Content%20Store.md) |
| Multi-agent workflows | [Multi-Agent/](documentation/Multi-Agent/) |
| Per-invocation run config | [09 Run Config](documentation/Getting%20Started/09%20Run%20Config.md) |

# HPD-Agent Framework

[![GitHub](https://img.shields.io/badge/GitHub-HPD--AI%2FHPD--Agent--Framework-181717?logo=github)](https://github.com/HPD-AI/HPD-Agent-Framework)
[![Docs](https://img.shields.io/badge/Docs-hpd--ai.github.io-blue)](https://hpd-ai.github.io/HPD-Agent-Framework/)
[![NuGet](https://img.shields.io/nuget/v/HPD-Agent.Framework?label=NuGet&color=004880&logo=nuget)](https://www.nuget.org/packages/HPD-Agent.Framework)

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="architecture-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="architecture.svg">
  <img alt="HPD-Agent Architecture" src="architecture.svg">
</picture>

A C# framework for building production AI agents — tools, multi-turn conversations, middleware, sub-agents, multi-agent workflows, audio, and more. Paired with TypeScript/Svelte UI libraries for streaming chat interfaces.

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

var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithInstructions("You are a helpful assistant.")
    .BuildAsync();

await foreach (var evt in agent.RunAsync("Hello!"))
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Text);
}
```

---

## Core Concepts

### Tools (AIFunctions, Skills, SubAgents)

Mark C# methods with attributes and the source generator wires them up automatically:

```csharp
public class MyToolkit
{
    [AIFunction(Description = "Add two numbers")]
    public int Add(int a, int b) => a + b;

    [Skill(Description = "Research a topic and write a report")]
    [FunctionResult("You are now in research mode. Use Search and ReadPage, then call WriteReport.")]
    public void ResearchAndWrite() { }

    [SubAgent]
    public SubAgent Summarizer() => SubAgentFactory.Create(
        name: "Summarize",
        description: "Summarizes long content",
        agentConfig: new AgentConfig { SystemInstructions = "Summarize concisely." }
    );
}

var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithToolkit<MyToolkit>()
    .BuildAsync();
```

Tools can also come from **MCP servers**, **OpenAPI specs**, or be provided by the client at runtime.

See [Tools documentation](documentation/Tools/).

### Sessions & Branches

Sessions track conversation history. Branches let you fork and explore alternative paths:

```csharp
// Simple: explicit session ID
var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSessionStore(new JsonSessionStore("./sessions"))
    .BuildAsync();

await agent.CreateSessionAsync("user-123");
await foreach (var evt in agent.RunAsync("Hello", "user-123")) { }
await foreach (var evt in agent.RunAsync("Follow up", "user-123")) { } // remembers context

// Fork at any point
await agent.ForkBranchAsync("user-123", "main", "experiment", fromMessageIndex: 4);
await foreach (var evt in agent.RunAsync("Try this instead", "user-123", "experiment")) { }
```

Crash recovery is automatic — if the process dies mid-turn, the next `RunAsync` resumes from where it left off.

See [Multi-Turn Conversations](documentation/Getting%20Started/02%20Multi-Turn%20Conversations.md).

### Sub-Agents

Delegate complex tasks to child agents, each with their own tools, provider, and memory:

```csharp
[SubAgent]
public SubAgent CodeReviewer() => SubAgentFactory.Create(
    name: "Review Code",
    description: "Reviews code for bugs and style",
    agentConfig: new AgentConfig
    {
        SystemInstructions = "You are an expert code reviewer...",
        Provider = new ProviderConfig { ProviderKey = "anthropic", ModelName = "claude-opus-4-6" }
    },
    typeof(FileSystemToolkit)
);

// Stateful: remembers across invocations
[SubAgent]
public SubAgent ProjectAssistant() => SubAgentFactory.CreateStateful(
    name: "Project Assistant",
    description: "Ongoing project collaborator with memory",
    agentConfig: new AgentConfig { SystemInstructions = "..." }
);

// PerSession: inherits the parent's conversation as read-only context
[SubAgent]
public SubAgent Summarizer() => SubAgentFactory.CreatePerSession(
    name: "Summarize",
    description: "Summarizes the conversation so far",
    agentConfig: new AgentConfig { SystemInstructions = "Summarize everything discussed." }
);
```

See [SubAgents](documentation/Tools/02.1.3%20SubAgents.md).

### Middleware

Intercept and customize every stage of agent execution:

```csharp
public class LoggingMiddleware : IAgentMiddleware
{
    public Task BeforeFunctionAsync(BeforeFunctionContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"Calling: {ctx.Function?.Name}");
        return Task.CompletedTask;
    }
}

var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithMiddleware(new LoggingMiddleware())
    .BuildAsync();
```

Built-in middleware: `CircuitBreaker`, `RetryMiddleware`, `HistoryReduction`, `PIIMiddleware`, `FunctionTimeout`, `Logging`, and more.

See [Middleware documentation](documentation/Middleware/).

### Multi-Agent Workflows

Compose specialized agents in a directed graph with conditional routing:

```csharp
using HPD.MultiAgent;

var workflow = await AgentWorkflow.Create()
    .AddAgent("triage", triageConfig)
    .AddAgent("billing", billingConfig)
    .AddAgent("support", supportConfig)
    .From("triage")
        .To("billing").WhenEquals("intent", "billing")
        .To("support").WhenEquals("intent", "support")
    .BuildAsync();

var result = await workflow.RunAsync("I need help with my invoice");
```

Workflows can also be used as a `[MultiAgent]` toolkit capability inside a parent agent.

See [Multi-Agent documentation](documentation/Multi-Agent/).

### Event Streaming

Agents stream 50+ event types in real time — text, tool calls, turn lifecycle, permissions, and more:

```csharp
await foreach (var evt in agent.RunAsync("Do something", "session-id"))
{
    switch (evt)
    {
        case TextDeltaEvent delta:
            Console.Write(delta.Text);
            break;
        case ToolCallStartEvent tool:
            Console.WriteLine($"\n[Tool: {tool.Name}]");
            break;
        case PermissionRequestEvent perm:
            // Bidirectional: send approval back to the agent
            await agent.SendMiddlewareResponse(perm.RequestId, approved: true);
            break;
        case MessageTurnFinishedEvent:
            Console.WriteLine("\n[Done]");
            break;
    }
}
```

See [Event Handling](documentation/Getting%20Started/05%20Event%20Handling.md).

### Memory & Content Store

Three distinct storage abstractions:

| Store | Purpose |
|-------|---------|
| `ISessionStore` | Conversation history — sessions and branches |
| `IContentStore` | Files and documents — knowledge, uploads, artifacts, agent memory notes |
| Agent Memory | `/memory` folder — the agent's own working notes across turns |

```csharp
var agent = await new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSessionStore(new JsonSessionStore("./sessions"))
    .WithContentStore(new LocalFileContentStore("./content"))
    .BuildAsync();
```

Agents can read, write, and search their content store using built-in tools (`content_read`, `content_write`, `content_list`, `content_glob`, etc.).

See [Memory documentation](documentation/Getting%20Started/06%20Memory.md).

### Agent Configuration

Define configuration as data, layer runtime concerns on top:

```csharp
var config = new AgentConfig
{
    Name = "SupportAgent",
    SystemInstructions = "You are a support assistant.",
    Provider = new ProviderConfig { ProviderKey = "openai", ModelName = "gpt-4o" },
    MaxAgenticIterations = 15,
    Toolkits = ["KnowledgeToolkit"],
    Middlewares = ["LoggingMiddleware"]
};

var agent = await new AgentBuilder(config)
    .WithServiceProvider(services)
    .BuildAsync();

// Or load from JSON
var agent = await new AgentBuilder("agent-config.json")
    .WithServiceProvider(services)
    .BuildAsync();
```

See [Agent Builder & Config](documentation/Agent%20Builder%20%26%20Config/).

---

## Providers

| Provider | Builder method |
|----------|---------------|
| OpenAI (GPT-4o, o1, audio) | `.WithOpenAI("key", "gpt-4o")` |
| Anthropic (Claude) | `.WithAnthropic("key", "claude-opus-4-6")` |
| Azure OpenAI | `.WithAzureOpenAI(endpoint, "key", "deployment")` |
| Google AI (Gemini) | `.WithGoogleAI("key", "gemini-2.0-flash")` |
| Azure AI Foundry | `.WithAzureAI(endpoint, "key", "model")` |
| Azure AI Inference | `.WithAzureAIInference(endpoint, "key", "model")` |
| Ollama (local) | `.WithOllama("llama3.2")` |
| Mistral | `.WithMistral("key", "mistral-large")` |
| AWS Bedrock | `.WithBedrock("region", "model-id")` |
| HuggingFace | `.WithHuggingFace("key", "model-id")` |
| OpenRouter | `.WithOpenRouter("key", "model")` |
| ONNX Runtime (local) | `.WithOnnxRuntime("model-path")` |

Providers can also be overridden per-call via `AgentRunConfig.ProviderKey`.

See [Providers documentation](documentation/Agent%20Builder%20%26%20Config/).

---

## Advanced Features

### Collapsing

Hide groups of tools behind a container that the agent must explicitly expand — reduces token usage and guides tool selection:

```csharp
[Collapse(Description = "File system operations — expand to read or write files")]
[FunctionResult("You now have access to file system tools.")]
public class FileToolkit { ... }
```

### Context Engineering

Dynamic descriptions and conditional tool visibility based on runtime metadata:

```csharp
public class SearchContext : IToolMetadata
{
    public bool HasBrave { get; set; }
    public bool HasBing { get; set; }
}

[AIFunction<SearchContext>(Description = "Search using {metadata.Provider}")]
[ConditionalFunction("HasBrave || HasBing")]
public SearchResult Search(string query) { ... }
```

### Observability

OpenTelemetry integration with automatic span hierarchy (turn → iteration → tool_call):

```csharp
var agent = await new AgentBuilder()
    .WithTracing(tracer)
    .WithMetrics(meter)
    .BuildAsync();
```

### Crash Recovery

Automatic — configure a session store and the framework snapshots each turn. On the next `RunAsync` after a crash, execution resumes from the exact point of failure.

### Audio

Native audio input/output support for voice agents:

```csharp
var agent = await new AgentBuilder()
    .WithOpenAI("key", "gpt-4o-audio-preview")
    .WithAudioPipeline(options => options.ProcessingMode = AudioProcessingMode.Native)
    .BuildAsync();
```

### MCP Servers

Connect external tool servers via Model Context Protocol — configure via `MCP.json` or C# attributes:

```csharp
[MCPServer(Name = "filesystem", Command = "npx", Args = "-y @modelcontextprotocol/server-filesystem /workspace")]
public class WorkspaceTools { }
```

### OpenAPI Tools

Turn any REST API into agent tools automatically from an OpenAPI spec:

```csharp
var agent = await new AgentBuilder()
    .WithOpenApiTools("https://api.example.com/openapi.json", opts =>
    {
        opts.AuthCallback = req => req.Headers.Add("Authorization", $"Bearer {token}");
    })
    .BuildAsync();
```

---

## Building Apps

- **Console apps** — See [07 Building Console Apps](documentation/Getting%20Started/07%20Building%20Console%20Apps.md)
- **Web apps (ASP.NET Core)** — See [08 Building Web Apps](documentation/Getting%20Started/08%20Building%20Web%20Apps.md)

---

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

Supports SSE (default), WebSocket, and MAUI transports. Handles bidirectional flows for permissions, clarifications, continuations, and client-side tool invocation. Covers all 71 HPD protocol event types.

### `@hpd/hpd-agent-headless-ui`

A Svelte 5 headless component library for building AI chat interfaces. Zero CSS — you control all styling.

```bash
npm install @hpd/hpd-agent-headless-ui
```

| Component | Purpose |
|-----------|---------|
| `Message` / `MessageList` | Streaming-aware message display with thinking, tool, and reasoning states |
| `MessageActions` | Edit, retry, and copy buttons |
| `MessageEdit` | Inline message editing |
| `ChatInput` | Compositional input with accessory slots |
| `ToolExecution` | Display in-progress tool calls |
| `PermissionDialog` | Handle AI permission requests |
| `BranchSwitcher` | Navigate sibling conversation branches |
| `SessionList` | Display and manage sessions |
| `Artifact` | Teleport rich content into a side panel |
| `SplitPanel` | Resizable layout panels with persistence |
| `AudioPlayer` / `Transcription` | Voice playback and speech-to-text streaming |
| `VoiceActivityIndicator` / `AudioVisualizer` | Visual feedback for voice input |

Total bundle: < 20 KB gzipped. See [typescript/](typescript/).

---

## Documentation

| Topic | Location |
|-------|----------|
| Agents overview | [Getting Started/](documentation/Getting%20Started/) |
| Agent configuration | [01 Customizing an Agent](documentation/Getting%20Started/01%20Customizing%20an%20Agent.md) |
| Sessions & branches | [02 Multi-Turn Conversations](documentation/Getting%20Started/02%20Multi-Turn%20Conversations.md) |
| Tool calling | [03 Tool Calling](documentation/Getting%20Started/03%20Tool%20Calling.md) |
| Middleware pipeline | [Middleware/](documentation/Middleware/) |
| Event handling | [05 Event Handling](documentation/Getting%20Started/05%20Event%20Handling.md) |
| Memory & content store | [06 Memory](documentation/Getting%20Started/06%20Memory.md) |
| Builder & config | [Agent Builder & Config/](documentation/Agent%20Builder%20%26%20Config/) |
| Tools (AIFunctions, Skills, SubAgents) | [Tools/](documentation/Tools/) |
| Multi-agent workflows | [Multi-Agent/](documentation/Multi-Agent/) |
| Cookbook / examples | [Cookbook/](documentation/Cookbook/) |

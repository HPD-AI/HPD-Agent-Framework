# HPD.Agent.Framework
```
            ██╗  ██╗██████╗ ██████╗       █████╗  ██████╗ ███████╗███╗   ██╗████████╗
            ██║  ██║██╔══██╗██╔══██╗     ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝
            ███████║██████╔╝██║  ██║█████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║
            ██╔══██║██╔═══╝ ██║  ██║╚════╝██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║
            ██║  ██║██║     ██████╔╝      ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║
            ╚═╝  ╚═╝╚═╝     ╚═════╝       ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝
```
The HPD Agent Framework is a battery-first agentic framework designed to enable you to create reliable agents as quickly as possible. We build the infrastructure for reliable autonomous agents, you build the future.

**[ Full Documentation](https://hpd-ai.github.io/HPD-Agent-Framework/)** | [Getting Started](https://hpd-ai.github.io/HPD-Agent-Framework/Getting%20Started/00%20Agents%20Overview) | [Examples](#examples)

The single philosophy driving this library: ***"Make Simple Things Simple, Make Complex Things Possible"***

> [!WARNING]
> **HPD.Agent.Framework is currently in an early development phase.**
>
> Until the release of version **1.0**, the API, inner mechanisms and naming are subject to significant changes without notice.

---

## Why HPD?

The AI agent framework landscape is crowded — LangChain, AutoGen, CrewAI, Semantic Kernel. Most of them help you *prompt* an LLM. HPD is built to safely **host, halt, resume, secure, and version-control** an LLM in a user-facing production application.

### Git-Style Version Control for Conversations

Most frameworks treat conversation history as a flat array of messages. HPD treats session state like a Git repository.

- **Branching & Forking** — Fork a conversation mid-session to explore a "what if" scenario without losing the original context.
- **Uncommitted Turns** — If an LLM crashes or a tool throws mid-generation, the state rolls back cleanly. No corrupted, half-finished responses poisoning the history.

### True Bidirectional Client Tools & Durable Pauses

Most frameworks struggle with Human-in-the-Loop because their execution loops are synchronous or strictly server-bound.

- **Client-Provided Tools** — The frontend (browser, mobile app) can expose its own local functions to the backend AI agent.
- **Durable Suspensions** — When an agent needs user approval, the Graph Engine checkpoints its state to disk, suspends execution, and frees the server thread entirely. When the user responds, the graph rehydrates and resumes exactly where it left off.

### Native OS-Level Sandboxing (`HPD-Agent.Sandbox.Local`)

Executing AI-generated code is dangerous. Most frameworks say "run it in Docker." HPD has built-in, near-instant native sandboxing with no heavyweight VMs required.

- **Linux** — Bubblewrap + Seccomp BPF syscall filtering
- **macOS** — Seatbelt profiles via `sandbox-exec`

### Token-Saving Tool Collapsing & Response Optimization

Giving an agent access to 500+ tools or a massive enterprise OpenAPI spec breaks LLMs — hallucinations, blown context windows.

- **`[Collapse]` Attribute** — Rolls hundreds of tools into a single Meta-Tool. Routing instructions are injected into the prompt only when needed.
- **`ResponseOptimizationMiddleware`** — Intercepts bloated enterprise REST API responses and filters, truncates, and extracts only the data the LLM needs before it hits the context window.

### Production-Grade Real-Time Voice (`HPD-Agent.Audio`)

Purpose-built real-time audio pipeline, not just a TTS wrapper bolted on at the end.

- **Preemptive Generation** — Starts generating the LLM response before the user finishes speaking.
- **Filler Audio** — Plays dynamic thinking sounds while the LLM processes, eliminating dead air.
- **Interruption Handling** — `BackchannelStrategy` with false-interruption recovery using Silero/WebRTC Voice Activity Detection.

### Incremental Graph Execution (`HPD.Graph`)

Graph-based orchestration with **Affected-Node Detection**. If a 10-step multi-agent workflow fails at step 8, HPD uses content-addressable caching to re-run only the nodes that failed or changed — saving compute and API costs.

### Native AOT & .NET Ecosystem

- Full **.NET Native AOT** support — compiles to a single binary with near-zero startup time and minimal memory footprint.
- Viable for desktop apps, mobile apps (via MAUI), and high-throughput microservices.
- Strong typing and enterprise-grade tooling, not a Python prototype.

---

## Core Characteristics

- **Lightweight**
- **Native AOT First**
- **Builder and Configuration First**
- **Provider Agnostic**
- **Event Streaming First**

---

## Built-In Features

| Feature | Description |
|---|---|
| **VCS** | Git-style branching, forking, and rollback for conversation state |
| **Durable Execution** | Checkpoint and resume agent graphs across server threads |
| **Tool Collapsing** | Collapse hundreds of tools into meta-tools without RAG or truncation |
| **Client Tools** | Let the frontend expose local functions to the backend agent |
| **Local Sandbox** | Native OS sandboxing for AI-generated code execution |
| **Real-Time Audio** | Preemptive generation, filler audio, and interruption handling |
| **Incremental Graph** | Re-run only affected nodes in a failed multi-step workflow |
| **MCP Support** | Model Context Protocol with collapsing support |
| **Middleware** | Extensible pipeline: PII filtering, permissions, circuit breaker, retries |
| **History Reduction** | Summarization and context window management |
| **OpenAPI Integration** | Response optimization middleware for enterprise API specs |
| **Observability** | Built-in event system for logging, telemetry, and debugging |
| **Planning** | Built-in plan mode for complex multi-step tasks |
| **SubAgents** | Nested agent orchestration |
| **Skills** | Provider-agnostic reusable agent skill definitions |

---

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
    // Toolkits & Tools
     .WithToolkit<WebSearchToolkit>()
     .WithToolkit<FileSystemToolkit>()
    .WithToolCollapsing()                              // Reduces tool context for better performance
    // Middleware
    .WithPermissions()                              // Require approval for sensitive operations
    .WithPIIProtection()                            // Filter sensitive data
    .WithHistoryReduction(config => {
        config.Strategy = HistoryReductionStrategy.Summarizing;
        config.TargetCount = 30;
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

---

## Requirements

- .NET 8.0, 9.0, or 10.0
- Microsoft.Extensions.AI 9.0.0+

## License

Proprietary - See LICENSE file.

# HPD-Agent

**The Foundation Every Agent Needs**

[![NuGet](https://img.shields.io/nuget/v/HPD-Agent.svg)](https://www.nuget.org/packages/HPD-Agent/)
[![License](https://img.shields.io/badge/license-Proprietary-blue.svg)](LICENSE.md)

Build any agent. The foundation just works.

```csharp
// Everything works out of the box
var agent = new AgentBuilder()
    .WithName("MyAgent")
    .Build();

// Memory? ✓ On
// Error handling? ✓ On
// Retries? ✓ On
// Token tracking? ✓ On
// 11 providers? ✓ Ready
```

---

## Why HPD-Agent?

**Building a coding agent?** Foundation ready.
**Building a customer service agent?** Foundation ready.
**Building a research agent?** Foundation ready.

Every agent needs memory, error handling, and resilience.
HPD-Agent provides all of it. **Always on. Zero setup.**

---

## Everything On By Default™

HPD-Agent is the only framework where **everything just works**:

| Feature | Other Frameworks | HPD-Agent |
|---------|------------------|-----------|
| Memory | Configure & enable | ✓ Already working |
| Error Handling | Setup retry logic | ✓ Already working |
| Multiple Providers | Choose & configure | ✓ All 11 ready |
| Token Tracking | Add middleware | ✓ Already working |
| Circuit Breakers | Install & setup | ✓ Already working |
| Permissions | Build from scratch | ✓ Already working |

**Want to turn something off?** Check the config guide.
**Otherwise?** Just build your agent.

---

## Install. Build. Deploy.

```bash
dotnet add package HPD-Agent
```

```csharp
using HPD.Agent;

// That's it. Everything works.
var agent = new AgentBuilder()
    .WithName("MyAgent")
    .Build();

// Memory management - working
// Error handling - working
// Multi-provider support - working
// Token tracking - working
// Retries & circuit breakers - working

await agent.RunAsync("Hello!");
```

---

## Build Any Agent in 5 Minutes

### Coding Agent

```csharp
var codingAgent = new AgentBuilder()
    .WithName("CodeAssistant")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<GitPlugin>()
    .Build();

// Foundation handles:
// - Memory of conversation
// - Error recovery from API failures
// - Token usage tracking
// - Provider failover
```

### Customer Service Agent

```csharp
var supportAgent = new AgentBuilder()
    .WithName("CustomerSupport")
    .WithPlugin<KnowledgeBasePlugin>()
    .WithPlugin<TicketingPlugin>()
    .Build();

// Foundation handles:
// - Conversation history
// - Graceful error handling
// - Multi-provider redundancy
// - Cost tracking
```

### Research Agent

```csharp
var researchAgent = new AgentBuilder()
    .WithName("Researcher")
    .WithPlugin<WebSearchPlugin>()
    .WithPlugin<DocumentAnalysisPlugin>()
    .Build();

// Foundation handles:
// - Research memory
// - API retry logic
// - Provider switching
// - Usage monitoring
```

**Same foundation. Different logic. Zero infrastructure work.**

---

## What's Always On

### 🧠 Memory Systems
- **Dynamic Memory** - Agent-controlled working memory
- **Static Memory** - Read-only knowledge base (RAG without vector DB)
- **Plan Mode** - Goal → Steps → Execution tracking
- **Auto-eviction** - Smart memory management

### 🛡️ Error Handling & Resilience
- **Provider-aware retry** - Knows which errors are retryable
- **Retry-After headers** - Respects rate limits automatically
- **Exponential backoff** - Smart retry timing
- **Circuit breakers** - Prevents cascade failures
- **Timeouts** - Configurable, sensible defaults

### 🔌 11 LLM Providers (All Ready)
OpenAI • Anthropic • Azure OpenAI • Azure AI Inference • Google AI • Mistral • Ollama • HuggingFace • AWS Bedrock • OnnxRuntime • OpenRouter

Switch providers in config. No code changes.

### 📊 Observability
- **Token counting** - Real-time usage tracking
- **Cost tracking** - Know what you're spending
- **OpenTelemetry** - Production-ready tracing
- **Structured logging** - Debug with confidence

### 🔐 Permissions
- **Function-level permissions** - Control what agents can do
- **Human-in-the-loop** - Require approval for sensitive actions
- **Persistent storage** - Remember permission grants

### 🎯 Plugin System
- **Source-generated** - Native AOT compatible
- **Scoped functions** - 87.5% token reduction
- **Conditional loading** - Load only what's needed
- **Permission-aware** - Security built-in

### 🔍 Web Search (3 Providers)
Tavily • Brave • Bing - Pick one, all configured

### 🌐 MCP Integration
Built-in Model Context Protocol client with manifest loading

---

## Core Principles

### 1. Everything On By Default

```json
// Minimal config - everything works
{
  "name": "MyAgent"
}

// Want to turn something off? Only then configure.
{
  "name": "MyAgent",
  "memory": { "enabled": false }
}
```

### 2. Configuration-First

JSON or code. Your choice. Same result.

```csharp
// Code-first
var agent = new AgentBuilder()
    .WithName("MyAgent")
    .Build();

// Config-first
var agent = AgentBuilder.FromConfig("agent.json");
```

### 3. Native AOT Compatible

100% trim-safe, AOT-ready. No reflection, no surprises.

### 4. Event-Driven

AG-UI Protocol built-in. Stream everything.

```csharp
await agent.RunStreamingAsync(
    "Your task",
    onEvent: (evt) => Console.WriteLine(evt.Type)
);
```

---

## Microsoft Compatible

| Component | HPD-Agent |
|-----------|-----------|
| `AIAgent` abstraction | ✅ Implements |
| `AgentThread` | ✅ Extends with ConversationThread |
| `RunAsync()` / `RunStreamingAsync()` | ✅ Full implementation |
| `WorkflowBuilder` | ✅ Drop-in compatible |
| `GroupChatWorkflowBuilder` | ✅ Works seamlessly |
| A2A Protocol | ✅ Compatible |

**Works in Microsoft workflows. Uses Microsoft abstractions. Adds the foundation.**

---

## The Philosophy

### Other Frameworks

**Microsoft Agent Framework:**
> "Here are primitives. Build your features."

**Semantic Kernel:**
> "Here's a kitchen sink. Enable what you need."

**LangChain:**
> "Here's everything. Configure all the things."

### HPD-Agent

> **"The foundation is ready. Build your agent."**

We built the infrastructure. You build the behavior.

---

## Comparison

| Feature | Microsoft | Semantic Kernel | HPD-Agent |
|---------|-----------|----------------|-----------|
| **Philosophy** | Primitives | Kitchen sink | Complete foundation |
| **Setup Required** | High | High | Zero |
| **Memory** | Build it | Configure it | ✓ Working |
| **Error Handling** | Build it | Configure it | ✓ Working |
| **Providers** | 2 | Many (configure) | 11 (ready) |
| **Permissions** | Build it | Build it | ✓ Working |
| **Web Search** | Build it | Plugin system | ✓ Working |
| **Token Tracking** | Build it | Add middleware | ✓ Working |
| **Native AOT** | Partial | No | ✅ Complete |
| **Agent-ready** | Primitives | Some assembly required | ✓ Ready to use |

---

## Configuration Philosophy

### Opt-Out, Not Opt-In

```json
// Traditional frameworks (opt-in)
{
  "enableMemory": true,
  "enableRetries": true,
  "enableCircuitBreaker": true,
  "enableTokenTracking": true
}

// HPD-Agent (opt-out)
{
  // Nothing needed. Everything on.

  // Only configure to disable:
  "memory": { "enabled": false }
}
```

**Default = Production-ready**

---

## Real-World Ready

### Before: Other Frameworks

```csharp
// Setup memory
var memory = new MemoryBuilder()
    .WithStorage(...)
    .WithEviction(...)
    .Build();

// Setup error handling
var retryPolicy = Policy
    .Handle<HttpException>()
    .WaitAndRetryAsync(...);

// Setup circuit breaker
var circuitBreaker = Policy
    .Handle<Exception>()
    .CircuitBreakerAsync(...);

// Setup token tracking
var tokenTracker = new TokenTracker(...);

// Finally build agent
var agent = new AgentBuilder()
    .WithMemory(memory)
    .WithRetryPolicy(retryPolicy)
    .WithCircuitBreaker(circuitBreaker)
    .WithTokenTracking(tokenTracker)
    .Build();
```

### After: HPD-Agent

```csharp
// Everything already works
var agent = new AgentBuilder()
    .WithName("MyAgent")
    .Build();
```

---

## Documentation

- **[Getting Started](docs/getting-started.md)** - Build your first agent in 5 minutes
- **[Configuration Reference](docs/configuration-reference.md)** - Turn things off if needed
- **[Plugin Development](docs/plugins.md)** - Add custom functionality
- **[Examples](examples/)** - Coding, support, research agents
- **[Full Overview](OVERVIEW.md)** - Deep dive into features

---

## FAQ

**Do I need to configure anything?**
No. Everything works out of the box.

**What if I want to customize something?**
Check the config guide. Everything is configurable. Nothing is required.

**Is this compatible with Microsoft Agent Framework?**
Yes, 100%. We implement `AIAgent` and work in Microsoft workflows.

**Can I use this with Microsoft's WorkflowBuilder?**
Absolutely. HPD-Agent is a drop-in replacement for ChatClientAgent.

**Why not just use Microsoft Agent Framework?**
Microsoft gives you primitives. We give you a complete foundation. Choose based on whether you want to build infrastructure or build agents.

**Is this open source?**
No, HPD-Agent is closed source with a proprietary license.

**What about performance?**
Native AOT compatible, zero reflection, production-tested. Fast by default.

---

## The Story

We built HPD-Agent on Microsoft.Extensions.AI before Microsoft released Agent Framework.

When Agent Framework launched with clean abstractions (`AIAgent`, `WorkflowBuilder`, A2A Protocol), we had a choice:

1. Fight Microsoft's direction
2. Embrace their architecture

We chose option 2. Kept Microsoft's abstractions. Added our foundation.

**Result:** Microsoft's blueprint + our batteries.

You get:
- ✅ Microsoft's clean architecture
- ✅ Microsoft's workflow system
- ✅ Microsoft's A2A protocol
- ✅ Production features that just work

**Read more:** [OVERVIEW.md](OVERVIEW.md#the-story-why-hpd-agent-exists)

---

## Support

- **Documentation**: [docs/](docs/)
- **Examples**: [examples/](examples/)
- **Issues**: [GitHub Issues](https://github.com/yourorg/hpd-agent/issues)
- **Email**: [support@hpd-agent.com](mailto:support@hpd-agent.com)

---

## License

Proprietary. See [LICENSE.md](LICENSE.md) for details.

---

<div align="center">

# Build Any Agent. Foundation Included.

**Coding • Support • Research • Analysis • Whatever You Need**

Everything works. Zero setup. Just build.

[Get Started](docs/getting-started.md) · [See Examples](examples/) · [Full Overview](OVERVIEW.md)

</div>

---

<div align="center">

### The iOS of Agent Frameworks

*We make the features. You use them.*

**Install → Build → Deploy**

</div>

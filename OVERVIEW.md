# HPD-Agent

**Production-Ready Agent Framework for .NET**

*Microsoft Agent Framework, Batteries Included* 🔋

---

## What is HPD-Agent?

HPD-Agent is a production-ready implementation of Microsoft's Agent Framework specification. While Microsoft provides clean abstractions (`AIAgent`, `AgentThread`) and multi-agent orchestration (`WorkflowBuilder`), HPD-Agent adds the features production applications actually need: **memory systems, advanced conversation management, error handling, permissions, and observability**.

### The Three-Layer Stack

```
┌─────────────────────────────────────────┐
│   Your Application                      │
└─────────────────┬───────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│   Microsoft.Agents.AI.Workflows  ✅     │  ← Multi-agent orchestration
│   (Keep - works with both)              │
└─────────────────┬───────────────────────┘
                  ↓
┌──────────────────┬──────────────────────┐
│ ChatClientAgent  │  HPD-Agent 🔋        │  ← Choose your implementation
│ (Basic)          │  (Production-ready)  │
└──────────────────┴──────────────────────┘
                  ↓
┌─────────────────────────────────────────┐
│   Microsoft.Agents.AI.Abstractions  ✅  │  ← Foundation (same for both)
│   AIAgent, AgentThread                  │
└─────────────────────────────────────────┘
```

**Both implement the same `AIAgent` spec. HPD-Agent just comes with batteries.**

---

## Quick Start

### Installation

```bash
dotnet add package HPD-Agent
dotnet add package HPD-Agent.Providers.OpenAI
```

### Basic Agent

```csharp
using HPD_Agent;

var agent = new AgentBuilder()
    .WithName("Assistant")
    .WithInstructions("You are a helpful AI assistant.")
    .WithProvider("openai", "gpt-4o", apiKey)
    .Build();

var response = await agent.RunAsync("What is 2+2?");
Console.WriteLine(response);
```

### With Memory & Permissions

```csharp
var agent = new AgentBuilder()
    .WithName("DataAnalyst")
    .WithInstructions("You are a data analysis expert with long-term memory.")
    .WithProvider("openai", "gpt-4o", apiKey)

    // Dynamic memory - agent can remember facts across conversations
    .WithDynamicMemory(opts => {
        opts.MaxTokens = 4000;
        opts.EnableAutoEviction = true;
    })

    // Static knowledge base
    .WithStaticMemory(opts => {
        opts.AddDocument("./docs/sql-best-practices.md");
        opts.AddDocument("./docs/data-schema.md");
    })

    // Human-in-the-loop for dangerous operations
    .WithPermissionFilter<ConsolePermissionFilter>()

    // Plugin with filesystem access
    .WithPlugin<FileSystemPlugin>()

    .Build();
```

### In Microsoft Workflows

```csharp
using Microsoft.Agents.AI.Workflows;

// Create HPD agents with full features
var researcher = new AgentBuilder()
    .WithName("Researcher")
    .WithProvider("openai", "gpt-4o", apiKey)
    .WithWebSearch("tavily", tavilyApiKey)
    .Build();

var coder = new AgentBuilder()
    .WithName("Coder")
    .WithProvider("anthropic", "claude-3-5-sonnet-20241022", apiKey)
    .WithPlugin<FileSystemPlugin>()
    .Build();

// Use in Microsoft's WorkflowBuilder
var workflow = new WorkflowBuilder(researcher)
    .AddEdge(researcher, coder)
    .Build();

await workflow.RunAsync("Research the latest .NET features and create a sample project");
```

**Microsoft's orchestration + HPD's batteries = Production-ready agents**

---

## Why HPD-Agent?

### Microsoft ChatClientAgent vs HPD-Agent

| Feature | ChatClientAgent | HPD-Agent |
|---------|----------------|-----------|
| **Implements AIAgent** | ✅ | ✅ |
| **Works in Workflows** | ✅ | ✅ |
| **Memory Systems** | ❌ | ✅ 3 types (Dynamic, Static, Planning) |
| **Error Handling** | ⚠️ Exceptions | ✅ Provider-aware retry + circuit breakers |
| **Token Counting** | ❌ | ✅ Built-in with cost tracking |
| **Message Reduction** | ⚠️ Basic truncation | ✅ LLM-based summarization |
| **Document Handling** | ❌ | ✅ PDF, DOCX, images, URLs |
| **Permissions** | ❌ | ✅ Function-level + human-in-the-loop |
| **Plugin System** | ⚠️ Basic tools | ✅ Scoped, conditional, permissioned |
| **Planning** | ❌ | ✅ Goal → Steps → Execution tracking |
| **Web Search** | ❌ | ✅ Tavily, Brave, Bing |
| **MCP Support** | ❌ | ✅ Full Model Context Protocol |
| **Event System** | ⚠️ Basic | ✅ AG-UI protocol implementation |
| **Native AOT** | ⚠️ Partial | ✅ Zero reflection, source-generated |
| **Provider Support** | ⚠️ OpenAI, Azure | ✅ 11 providers + extensible |
| **Time to Production** | 6-12 months | Hours |

---

## Core Features

### 1. Memory Systems (3-Tier Architecture)

#### Dynamic Memory - Editable Working Memory

Dynamic Memory enables agents to create, update, and recall facts during conversations. Think of it as the agent's working memory - information it learns during interactions that needs to persist across turns. The agent automatically manages its own memories using plugin functions, and can search through them when needed. Memories are automatically evicted when approaching token limits to prevent context overflow.

```csharp
.WithDynamicMemory(opts => {
    opts.MaxTokens = 4000;
    opts.EnableAutoEviction = true;  // Auto-remove old memories at 85% capacity
    opts.StorageDirectory = "./memory";
})
```

**Agent functions:** `create_memory()`, `update_memory()`, `delete_memory()`, `search_memories()`

#### Static Memory - Read-Only Knowledge Base (RAG)

Static Memory provides the agent with read-only domain expertise loaded from documents. This is ideal for giving agents specialized knowledge like API documentation, coding standards, or domain-specific information. Documents are extracted (PDF, DOCX, Markdown, web pages) and injected into the conversation context up to a token limit. This implements the RAG (Retrieval-Augmented Generation) pattern without requiring a vector database.

```csharp
.WithStaticMemory(opts => {
    opts.AddDocument("./docs/python-best-practices.md");
    opts.AddDocument("./docs/api-reference.pdf");
    opts.AddDocument("https://fastapi.tiangolo.com/");
    opts.MaxTokens = 8000;
})
```

#### Plan Mode - Execution Planning

Plan Mode enables agents to break down complex tasks into hierarchical plans with trackable steps. The agent creates a goal, defines steps to achieve it, tracks status (Pending, InProgress, Completed, Blocked), and adds context notes as it learns. The current plan is automatically injected into every prompt so the agent maintains focus on its objectives. Perfect for multi-step workflows and complex reasoning tasks.

```csharp
.WithPlanMode(opts => {
    opts.EnablePersistence = true;
    opts.StorageDirectory = "./plans";
})
```

**Agent functions:** `create_plan()`, `add_step()`, `update_step()`, `complete_plan()`

#### History Reduction - Conversation Compression

History Reduction manages long conversations that exceed context windows. Instead of truncating messages and losing context, you can use LLM-based summarization to compress older messages while preserving meaning. The reduction system is cache-aware, tracking tokens from actual API responses (not just estimates). You can use a separate, cheaper model for summarization to optimize costs.

```csharp
// Simple message counting
.WithMessageCountingReduction(targetMessageCount: 20)

// Or LLM-based summarization
.WithSummarizingReduction(targetMessageCount: 20)
    .WithSummarizerProvider("openai", "gpt-4o-mini")  // Use cheaper model for summaries
```

---

### 2. Provider Ecosystem (11 Providers)

HPD-Agent supports 11 LLM providers out of the box with automatic discovery - just reference the provider package and it's registered. Each provider implementation includes provider-specific error handling, retry logic, and configuration validation. Providers are resolved via a string key system for maximum flexibility and Native AOT compatibility.

**Supported providers:**
- **OpenAI** (GPT-4, GPT-4o, o1, o3-mini)
- **Anthropic** (Claude 3.5 Sonnet, Opus, Haiku) with prompt caching
- **Azure OpenAI** (deployment-based)
- **Azure AI Inference** (Model-as-a-Service)
- **Google AI** (Gemini Pro/Flash)
- **Mistral AI** (Large/Medium/Small)
- **Ollama** (local models)
- **HuggingFace** (Inference API)
- **AWS Bedrock** (Claude, Titan)
- **OnnxRuntime** (local ONNX models)
- **OpenRouter** (200+ models via single API)

```csharp
// OpenAI
.WithProvider("openai", "gpt-4o", apiKey)

// Anthropic with prompt caching
.WithProvider("anthropic", "claude-3-5-sonnet-20241022", apiKey)

// Ollama (local)
.WithProvider("ollama", "llama3.2", endpoint: "http://localhost:11434")

// OpenRouter (access 200+ models)
.WithProvider("openrouter", "anthropic/claude-3.5-sonnet", apiKey)
```

**Automatic provider discovery** - reference the provider package and it's auto-registered.

---

### 3. Advanced Error Handling & Resilience

#### Provider-Aware Error Handling

Production LLM applications need intelligent error handling that goes beyond catching exceptions. HPD-Agent categorizes errors by type (transient, rate limit, auth, etc.) and applies provider-specific retry strategies. It respects Retry-After headers from OpenAI and Anthropic, uses exponential backoff with jitter to avoid thundering herd problems, and allows per-category retry limits. Each provider has custom error parsing to normalize different error formats into consistent categories.

```csharp
builder.Config.ErrorHandling = new ErrorHandlingConfig {
    MaxRetries = 3,
    RetryDelay = TimeSpan.FromSeconds(1),
    BackoffMultiplier = 2.0,
    UseProviderRetryDelays = true,  // Respect Retry-After headers
    MaxRetryDelay = TimeSpan.FromSeconds(30)
};
```

**Features:**
- **Error categorization**: Transient, RateLimitRetryable, ClientError, AuthError, ContextWindow, ServerError
- **Retry-After header respect** (OpenAI, Anthropic, Azure)
- **Exponential backoff with jitter** (2x multiplier, ±10%)
- **Per-category retry limits**
- **Provider-specific error parsing**

#### Safety Controls

Prevent runaway agents with circuit breakers and timeouts. These controls protect against infinite loops, excessive API costs, and hung processes. The agentic loop tracks how many times the agent has called functions consecutively and can terminate when thresholds are exceeded. Parallel execution limits prevent resource exhaustion when agents make many concurrent tool calls.

```csharp
builder.Config.AgenticLoop = new AgenticLoopConfig {
    MaxTurnDuration = TimeSpan.FromMinutes(5),
    MaxConsecutiveFunctionCalls = 5,  // Circuit breaker
    MaxParallelFunctions = 3,
    TerminateOnUnknownCalls = false
};
```

---

### 4. Filter System (6 Filter Types)

The filter system provides hooks at every stage of agent execution, enabling fine-grained control over agent behavior. Unlike Microsoft's limited `AIContextProvider`, HPD-Agent's filter system covers six distinct lifecycle stages with separate filter types for each. Filters can be scoped globally, per-plugin, or per-function. This architecture enables memory injection, observability, permissions, cost tracking, and custom processing without modifying core agent logic.

#### Prompt Filters - Modify before LLM

Prompt filters intercept messages before they're sent to the LLM. This is where memory systems inject context - dynamic memories, static knowledge, and current plans are all added via prompt filters. You can also use prompt filters for custom preprocessing, context enrichment, or prompt engineering.

```csharp
.WithPromptFilter<DynamicMemoryFilter>()   // Inject memories
.WithPromptFilter<StaticMemoryFilter>()    // Inject knowledge
.WithPromptFilter<AgentPlanFilter>()       // Inject current plan
```

#### Function Invocation Filters - Wrap tool calls

Function invocation filters wrap around tool execution, providing pre/post hooks for every function call. This enables observability (tracing, metrics), logging, performance monitoring, and custom function orchestration. Filters can be scoped to specific plugins or functions for granular control.

```csharp
.WithFilter<ObservabilityAiFunctionFilter>()  // OpenTelemetry traces/metrics
.WithFilter<LoggingAiFunctionFilter>()        // Comprehensive logging
```

#### Permission Filters - Human-in-the-loop

Permission filters implement human-in-the-loop patterns by intercepting function calls that require approval. When a function marked with `[RequiresPermission]` is called, the permission filter presents the request to the user (console, web UI, etc.) and waits for approval. Permission decisions can be stored (AlwaysAllow, AlwaysDeny) at conversation, project, or global scope.

```csharp
.WithPermissionFilter<ConsolePermissionFilter>()  // Console approval
.WithPermissionFilter<AGUIPermissionFilter>()     // Web UI approval
```

#### Message Turn Filters - Post-turn processing

Message turn filters process completed agentic turns, enabling cost tracking, audit logging, analytics, and custom post-processing. These filters receive the complete turn context including messages, token usage, and execution time.

```csharp
.WithMessageTurnFilter<CostTrackingFilter>()
.WithMessageTurnFilter<AuditLogFilter>()
```

---

### 5. Plugin System (Source-Generated)

HPD-Agent's plugin system is built on Roslyn source generators - registration code is generated at compile-time, not runtime. This ensures Native AOT compatibility and validates plugin definitions during build. The system supports conditional functions (type-safe expressions for dynamic availability), plugin scoping (hierarchical organization to reduce token usage), and permission requirements. All plugin metadata is extracted and validated at compile-time.

#### Basic Plugin

Define functions with attributes and the source generator handles registration automatically. Parameters and return types are automatically converted to JSON schemas for the LLM.

```csharp
public class WeatherPlugin
{
    [AIFunction]
    [AIDescription("Get current weather for a city")]
    public async Task<string> GetWeather(
        [AIDescription("City name")] string city)
    {
        // Implementation
    }
}

// Register
.WithPlugin<WeatherPlugin>()
```

#### Conditional Functions (Type-Safe)

Conditional functions appear/disappear based on runtime context using type-safe expressions validated at compile-time. The expression DSL supports boolean logic and is checked against the context type - typos or invalid properties cause build errors, not runtime failures.

```csharp
[ConditionalFunction<WebSearchContext>(
    condition: "HasTavilyProvider || HasBraveProvider",
    description: "Search the web using {context.DefaultProvider}"
)]
public async Task<string> WebSearch(string query) { }
```

#### Plugin Scoping (87.5% Token Reduction)

Plugin scoping organizes functions hierarchically - the LLM sees container functions initially, and child functions only after expansion. This dramatically reduces token usage in the initial tool list. Post-expansion instructions provide ephemeral guidance shown only after the container is expanded, enabling context-specific instructions without polluting the global prompt.

```csharp
[PluginScope(
    description: "File system operations",
    postExpansionInstructions: "IMPORTANT: Always use absolute paths."
)]
public class FileSystemPlugin
{
    [AIFunction] public Task ReadFile(string path) { }
    [AIFunction] public Task WriteFile(string path, string content) { }
    [AIFunction] public Task DeleteFile(string path) { }
}
```

**Before scoping:**
- 8 plugins × 5 functions = 40 tools sent to LLM

**After scoping:**
- 8 container functions sent initially
- Functions only visible after container expansion
- **87.5% token reduction** in initial tool list

#### Permissions

Mark functions that require user approval. When the agent attempts to call these functions, permission filters intercept and request approval before execution.

```csharp
[AIFunction]
[RequiresPermission]  // User approval required
public async Task DeleteFile(string path) { }
```

---

### 6. Skills System (Cross-Plugin Composition)

Skills compose functions from multiple plugins into semantic capabilities. Unlike plugins which are defined in code, skills are defined in agent configuration and reference existing plugins/functions. This creates an M:N relationship - one skill can reference many plugins, and one plugin's functions can be used by many skills. Skills support scoping modes for token optimization and can load post-expansion instructions from Markdown files.

```csharp
.AddSkill("FileManagement", skill => skill
    .WithFunctionReferences(
        "FileSystemPlugin.ReadFile",
        "FileSystemPlugin.WriteFile",
        "TextEditorPlugin.FormatCode"
    )
    .WithPostExpansionInstructions("./skills/file-management-guide.md")
)

.AddSkill("WebResearch", skill => skill
    .WithPluginReferences("WebSearchPlugin", "BrowserPlugin")
)

// Hide ALL plugins, show ONLY skills
.EnableSkillsOnlyMode()
```

---

### 7. Web Search (Multi-Provider)

Web search integration provides agents with real-time internet access through multiple search providers. Each provider has unique capabilities - Tavily offers AI-generated answers with citations, Brave focuses on privacy, and Bing provides enterprise features. Configuration presets (ForResearchMode, ForNewsMode, ForPrivacyFocusedSearch) optimize search settings for common use cases.

```csharp
// Tavily (AI-powered answers)
.WithTavilyWebSearch(apiKey, opts => opts
    .ForResearchMode()  // Advanced search + AI answers + raw content
)

// Brave (privacy-focused)
.WithBraveWebSearch(apiKey, opts => opts
    .ForPrivacyFocusedSearch()
)

// Bing (enterprise)
.WithBingWebSearch(apiKey)
```

**Agent functions:** `WebSearch()`, `NewsSearch()`, `AnswerSearch()`, `VideoSearch()`, `ShoppingSearch()`

---

### 8. MCP (Model Context Protocol) Support

Model Context Protocol (MCP) is an open standard for connecting AI agents to external tools and data sources. HPD-Agent includes full MCP client implementation - it launches MCP server processes, discovers available tools, and routes function calls via stdio communication. Tool scoping groups MCP tools by server to reduce token usage, and per-server instructions can be provided after expansion.

```csharp
.WithMCP("./mcp-manifest.json", opts => {
    opts.Timeout = TimeSpan.FromSeconds(30);
    opts.EnableToolScoping = true;  // Group tools by server
})
```

**Integrates with MCP servers for:**
- File system access
- GitHub operations
- Database queries
- Custom integrations

---

### 9. AG-UI Protocol Implementation

AG-UI is an open protocol (by CopilotKit) that standardizes how agents communicate with frontend applications. HPD-Agent implements the AG-UI specification, streaming typed JSON events (text deltas, tool calls, reasoning steps, permission requests, state changes) over Server-Sent Events (SSE) or WebSocket. This enables real-time UI synchronization with zero custom protocol code. Events are source-generated for Native AOT compatibility. The bidirectional event system allows filters to request responses (like permission approval) and wait for user input.

**Learn more about AG-UI:** [docs.ag-ui.com](https://docs.ag-ui.com)

```csharp
await foreach (var update in agent.RunStreamingAsync("Hello"))
{
    switch (update.Event.Type)
    {
        case "TEXT_MESSAGE_CHUNK":
            Console.Write(update.Event.Content);
            break;
        case "TOOL_CALL_START":
            Console.WriteLine($"\n[Calling {update.Event.ToolName}]");
            break;
        case "FUNCTION_PERMISSION_REQUEST":
            // Show approval dialog in UI
            break;
    }
}
```

**25+ event types:**
- Text events (deltas, complete messages)
- Reasoning events (extended thinking)
- Tool events (calls, arguments, results)
- Permission events (approval requests)
- State events (snapshots, deltas)

**Frontend Tool System:**

Frontend tools are functions that execute in the client browser, not on the server. The agent requests the action via an event, and the UI executes it (e.g., showing a notification, opening a dialog, updating state).

```csharp
[FrontendTool]  // Executed by JavaScript, not C#
public class UIActionTool
{
    [AIFunction]
    public void ShowNotification(string message, string type) { }
}
```

---

### 10. Permissions & Safety

Production agents need guardrails to prevent dangerous operations and runaway costs. HPD-Agent implements a comprehensive permission system with function-level controls, continuation permissions (to prevent infinite loops), and persistent user preferences. Permission filters intercept dangerous operations and request approval through any UI (console, web, mobile). User decisions can be remembered at conversation, project, or global scope.

#### Function-Level Permissions

Mark individual functions as requiring approval. When the agent attempts to call these functions, permission filters pause execution and request user consent before proceeding.

```csharp
[RequiresPermission]
public async Task DeleteDatabase(string name) { }
```

#### Continuation Permissions

Prevent agents from consuming excessive tokens and costs by limiting how many function-calling iterations they can perform. After reaching the limit, the agent must request permission to continue. This protects against infinite loops and excessive API usage.

```csharp
.WithMaxFunctionCallTurns(10)  // Max iterations before asking user
.WithContinuationExtensionAmount(3)  // Extra turns if user approves
```

#### Permission Storage

User preferences can be stored at different scopes - just this conversation, all conversations in a project, or globally across all agents. This prevents repeatedly asking for permission for the same operations.

```csharp
public enum PermissionScope
{
    Conversation,  // Just this conversation
    Project,       // All conversations in project
    Global         // All agents, all conversations
}
```

**User sees:**
- Function name and arguments
- Iteration count (e.g., "5/10 iterations")
- Elapsed time
- List of completed functions
- Options: Approve Once, Always Allow, Always Deny, Deny Once

---

### 11. Human-in-the-Loop Clarification

When orchestrating multiple agents, sub-agents often need information they don't have - but stopping the entire agentic turn to ask would break the workflow. HPD-Agent's clarification system allows sub-agents to request information from users **mid-turn** without breaking execution flow. When a sub-agent needs clarification, the orchestrator can call `AskUserForClarification()`, wait for the user's response, and continue processing - all within the same agentic turn.

#### How It Works

The clarification function uses an event-based architecture where requests bubble up to the root agent's event handlers. The orchestrator emits a clarification request event, waits for a response event (with configurable timeout), and returns the user's answer to continue execution.

```csharp
var orchestrator = new AgentBuilder()
    .WithName("Orchestrator")
    .WithProvider("openai", "gpt-4o", apiKey)
    .Build();

var codingAgent = new AgentBuilder()
    .WithName("CodingAgent")
    .WithProvider("anthropic", "claude-3-5-sonnet-20241022", apiKey)
    .WithPlugin<FileSystemPlugin>()
    .Build();

// Register sub-agent and clarification function on PARENT
orchestrator.AddFunction(codingAgent.AsAIFunction());
orchestrator.AddFunction(ClarificationFunction.Create(timeout: TimeSpan.FromMinutes(10)));
```

#### Execution Flow

```
1. Orchestrator calls codingAgent("Build authentication")
2. CodingAgent returns: "I need to know which framework to use"
3. Orchestrator doesn't have this information
4. Orchestrator calls AskUserForClarification("Which framework should I use?")
5. User responds: "Use Express.js"
6. Orchestrator continues in SAME turn: codingAgent("Build Express.js authentication")
7. Final response delivered to user
```

#### Configuration

```csharp
// Default 5-minute timeout
ClarificationFunction.Create()

// Custom timeout
ClarificationFunction.Create(timeout: TimeSpan.FromMinutes(10))

// With custom function options
ClarificationFunction.Create(
    options: new AIFunctionFactoryOptions { Name = "RequestInfo" },
    timeout: TimeSpan.FromMinutes(15)
)
```

**Key benefits:**
- Maintains agentic turn continuity
- Event-based architecture with bubble-up to root
- Configurable timeout (default 5 minutes)
- Graceful timeout and cancellation handling
- Correlation via unique request IDs

---

### 12. Conversation Management

HPD-Agent extends Microsoft's `AgentThread` abstraction with `ConversationThread`, adding token counting, cost tracking, and rich metadata. Message stores are pluggable - use in-memory for testing, JSON files for persistence, or implement custom stores backed by databases or Redis. Threads are fully serializable for cross-session continuity.

#### ConversationThread (extends Microsoft's AgentThread)

ConversationThread adds production features to Microsoft's base `AgentThread` abstraction. Track tokens and costs across the conversation, attach custom metadata (user IDs, project IDs, tags), and serialize/deserialize threads for persistence.

```csharp
var thread = agent.GetNewThread();

// Metadata
thread.Metadata["userId"] = "123";
thread.Metadata["projectId"] = "abc";

// Token counting
var tokens = thread.TotalTokens;
var cost = thread.EstimatedCost;

// Serialization
var json = thread.Serialize();
var restored = agent.DeserializeThread(json);
```

#### ConversationMessageStore

Message stores handle conversation history persistence. The abstraction allows swapping storage backends without code changes. In-memory is great for development, JSON files for simple persistence, and custom implementations for enterprise scenarios (SQL, MongoDB, Redis).

```csharp
// In-memory
new InMemoryConversationMessageStore()

// JSON file-based
new JsonConversationMessageStore("./conversations")

// Custom (implement IConversationMessageStore)
public class DatabaseMessageStore : ConversationMessageStore { }
```

---

### 13. Observability & Telemetry

Production agents need visibility into operations, performance, and costs. HPD-Agent integrates with OpenTelemetry for distributed tracing and metrics, Microsoft.Extensions.Logging for structured logging, and IDistributedCache for response caching. These integrations follow standard .NET patterns and work with existing monitoring infrastructure.

#### OpenTelemetry Integration

One-line integration with OpenTelemetry generates comprehensive traces and metrics for agent operations. Traces include complete context (model, tokens, arguments, results) making debugging and performance analysis straightforward. Metrics track success rates, error rates, and latencies for both LLM calls and tool executions.

```csharp
.WithOpenTelemetry(sourceName: "MyAgent")
```

**Generates:**
- **Agent turn traces** (duration, status, token usage)
- **LLM call traces** (model, tokens, latency, errors)
- **Tool call traces** (function name, arguments, results)
- **Tool call metrics** (duration, success rate, error rate)

#### Logging

Structured logging integration with Microsoft.Extensions.Logging. Logs include key-value pairs for filtering and querying. Control verbosity with separate flags for chat messages and function calls.

```csharp
.WithLogging(loggerFactory,
    includeChats: true,
    includeFunctions: true
)
```

#### Caching

Reduce costs and improve performance by caching identical LLM requests. Uses standard IDistributedCache abstraction, supporting Redis, SQL Server, or in-memory caching. Particularly effective for repeated queries or deterministic operations.

```csharp
.WithCaching(distributedCache)  // Redis, SQL Server, etc.
```

---

### 14. Document Handling

Users often need agents to process documents - contracts, reports, code files, images. HPD-Agent extracts text from multiple formats and injects it into the conversation context. For vision-capable models, images are sent directly without extraction. Documents can be local files or URLs. The full-text injection strategy balances simplicity (no vector database required) with effectiveness for most use cases.

```csharp
var message = new ChatMessage(ChatRole.User, "Analyze this contract")
{
    Documents = new[]
    {
        new DocumentReference { Path = "./contract.pdf" },
        new DocumentReference { Path = "./terms.docx" },
        new DocumentReference { Url = "https://example.com/agreement" }
    }
};

await agent.RunAsync(message);
```

**Supported formats:** `.txt`, `.md`, `.pdf`, `.docx`, images (vision models), URLs

---

### 15. Native AOT Compatibility

Native AOT (Ahead-of-Time compilation) produces self-contained executables with instant startup and minimal memory footprint - critical for serverless and edge deployments. HPD-Agent is designed for Native AOT from day one, not retrofitted. All serialization, plugin registration, and event handling uses compile-time code generation via Roslyn source generators. No reflection. No runtime IL generation. No JIT compilation required.

**Zero reflection. Zero runtime code generation.**

```csharp
// Source-generated plugin registration
public class MyPluginRegistration  // ← Generated by Roslyn analyzer
{
    public static IEnumerable<AIFunction> ToAIFunctions(MyPlugin plugin)
    {
        // Generated at compile-time
    }
}
```

**Features:**
- Source-generated JSON serialization (all AgentConfig, events, messages)
- Compile-time plugin registration (no runtime discovery)
- Compile-time conditional function validation (type-safe expressions)
- AOT-safe error handlers (regex, not reflection)
- FFI-ready for Python, Rust, JavaScript bindings

---

## Advanced Features

### Parallel Function Execution

```csharp
.WithMaxParallelFunctions(5)  // Execute up to 5 tools concurrently
```

### Provider Fallback Chains

```csharp
var primaryAgent = new AgentBuilder()
    .WithProvider("anthropic", "claude-3-5-sonnet-20241022")
    .Build();

var fallbackAgent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .Build();

// Implement fallback logic in error handler
```

### Cost Tracking

```csharp
var response = await agent.RunAsync("Write a story");

Console.WriteLine($"Input tokens: {response.Usage?.InputTokens}");
Console.WriteLine($"Output tokens: {response.Usage?.OutputTokens}");
Console.WriteLine($"Estimated cost: ${response.EstimatedCost:F4}");
```

### Configuration as JSON

```csharp
// Save agent configuration
var config = builder.Config;
var json = JsonSerializer.Serialize(config, HPDJsonContext.Default.AgentConfig);
File.WriteAllText("agent-config.json", json);

// Load from configuration
var agent = AgentBuilder.FromJsonFile("agent-config.json").Build();
```

---

## Migration Guides

### From ChatClientAgent

**Before:**
```csharp
using Microsoft.Agents.AI;

var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "Assistant",
        Instructions = "Be helpful"
    }
);
```

**After:**
```csharp
using HPD_Agent;

var agent = new AgentBuilder()
    .WithName("Assistant")
    .WithInstructions("Be helpful")
    .WithProvider("openai", "gpt-4o", apiKey)
    .Build();
```

**What you gain:**
- ✅ Memory systems (Dynamic, Static, Planning)
- ✅ Advanced error handling with retries
- ✅ Token counting and cost tracking
- ✅ Permissions and human-in-the-loop
- ✅ Plugin scoping (87.5% token reduction)
- ✅ Web search integration
- ✅ MCP support
- ✅ 25+ streaming events

### From Semantic Kernel

HPD-Agent brings back Semantic Kernel features that didn't make it into Agent Framework - enhanced and built on clean foundations:

**Memory Management:**
- SK: `SemanticTextMemory` with vector search
- AF: ❌ Not included
- HPD: ✅ `DynamicMemory` + `StaticMemory` + `PlanMode` (3 memory types)

**Plugins:**
- SK: `KernelPlugin` with function calling
- AF: Basic `AIFunction` support
- HPD: ✅ Enhanced with scoping, permissions, conditional functions, skills

**Planning:**
- SK: `StepwisePlanner`, `SequentialPlanner`
- AF: ❌ Not included
- HPD: ✅ `PlanMode` with goal → steps → execution tracking

**Filters:**
- SK: `IPromptRenderFilter`, `IFunctionFilter`, `IAutoFunctionFilter`
- AF: `AIContextProvider` (limited)
- HPD: ✅ 6 filter types with granular control

**Document Handling:**
- SK: Basic text loading
- AF: ❌ Not included
- HPD: ✅ PDF, DOCX, images, URLs with full-text extraction

---

## Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────┐
│              Your Application                           │
└────────────────┬────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────┐
│    Microsoft.Agents.AI.Workflows (Keep)                │
│    Multi-agent orchestration                            │
└────────────────┬────────────────────────────────────────┘
                 │
                 ▼
┌──────────────────────────┬──────────────────────────────┐
│  ChatClientAgent         │  HPD-Agent 🔋                │
│  (Microsoft's impl)      │  (Production impl)           │
│                          │                              │
│  Features:               │  Features:                   │
│  └─ Clean, unified API   │  ├─ Same clean API           │
│                          │  ├─ 3 memory systems         │
│                          │  ├─ 6 filter types           │
│                          │  ├─ Error handling           │
│                          │  ├─ Permissions              │
│                          │  ├─ 11 providers             │
│                          │  └─ Native AOT               │
└──────────────────────────┴──────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────┐
│    Microsoft.Agents.AI.Abstractions (Keep)             │
│    AIAgent, AgentThread, RunAsync(), etc.              │
└─────────────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────┐
│         Microsoft.Extensions.AI                         │
│         IChatClient, AIFunction, etc.                   │
└─────────────────────────────────────────────────────────┘
```

### Filter Pipeline Architecture

```
User Input → Prompt Filters → LLM → Function Invocation Filters → Tool Execution
                ↓                           ↓
         (Inject memories)         (Permissions check)
                ↓                           ↓
         (Inject knowledge)         (Observability)
                ↓                           ↓
         (Inject plan)                 (Logging)
                                            ↓
                                     Message Turn Filters
                                            ↓
                                     (Cost tracking)
                                            ↓
                                      Final Response
```

---

## Microsoft Compatibility

| Microsoft Component | HPD-Agent Support |
|--------------------|-------------------|
| `AIAgent` abstraction | ✅ Implements |
| `AgentThread` | ✅ `ConversationThread` extends it |
| `RunAsync()` | ✅ Full implementation |
| `RunStreamingAsync()` | ✅ Enhanced with 25+ events |
| `WorkflowBuilder` | ✅ Drop-in compatible |
| `GroupChatWorkflowBuilder` | ✅ Works seamlessly |
| `GetNewThread()` | ✅ Supported |
| Thread serialization | ✅ Supported |
| Service discovery | ✅ Supported |
| `AIContextProvider` | ✅ Compatible + filter system |
| A2A Protocol | ✅ Compatible |

**You're not abandoning Microsoft's framework. You're using a better implementation of it.**

---

## FAQ

### Is HPD-Agent compatible with Microsoft Agent Framework?

**Yes, 100%.** HPD-Agent implements the exact same `AIAgent` specification that `ChatClientAgent` implements. Both work seamlessly in Microsoft's workflows.

### Can I use Microsoft's WorkflowBuilder?

**Absolutely!** HPD-Agent agents are drop-in replacements for ChatClientAgent in workflows. Use Microsoft's orchestration with HPD's batteries.

### Why not just use ChatClientAgent?

`ChatClientAgent` is intentionally minimal - it provides clean abstractions and lets you build features yourself.

HPD-Agent is intentionally batteries-included - it provides those features so you can focus on your application, not infrastructure.

Choose based on whether you want to build or buy.

### Why not contribute these features to Microsoft?

Microsoft's design philosophy is to provide minimal, unopinionated abstractions and let the ecosystem build on top. That's the right approach for a framework.

HPD-Agent is one implementation of that vision - opinionated, batteries-included, production-focused. The ecosystem supports both approaches.

### What about Semantic Kernel?

Semantic Kernel is in maintenance mode as Microsoft focuses on Agent Framework. HPD-Agent brings back SK's best features (memories, planning, plugins) built on Agent Framework's clean foundation - no legacy baggage.

### Is this open source?

HPD-Agent is **closed source** with a commercial license model. We provide production support, regular updates, and enterprise features.

### What providers are supported?

11 providers out-of-the-box: OpenAI, Anthropic, Azure OpenAI, Azure AI Inference, Google AI, Mistral, Ollama, HuggingFace, Bedrock, OnnxRuntime, OpenRouter.

Adding a new provider takes ~100 lines of code via the `IProviderFeatures` interface.

---

## Examples

### Research Agent with Memory

```csharp
var researcher = new AgentBuilder()
    .WithName("Researcher")
    .WithInstructions("You research topics and remember key findings.")
    .WithProvider("openai", "gpt-4o", apiKey)

    .WithDynamicMemory(opts => opts.MaxTokens = 4000)
    .WithStaticMemory(opts => {
        opts.AddDocument("./research-guidelines.md");
    })

    .WithTavilyWebSearch(tavilyApiKey, opts => opts.ForResearchMode())

    .WithOpenTelemetry()
    .Build();

await researcher.RunAsync("Research the latest advancements in quantum computing");
```

### File Management Agent with Permissions

```csharp
var fileAgent = new AgentBuilder()
    .WithName("FileManager")
    .WithInstructions("You help users manage files safely.")
    .WithProvider("anthropic", "claude-3-5-sonnet-20241022", apiKey)

    .WithPlugin<FileSystemPlugin>()
    .WithPermissionFilter<ConsolePermissionFilter>()

    .WithMaxFunctionCallTurns(10)

    .Build();

await fileAgent.RunAsync("Delete all .tmp files in the current directory");
// User sees: "FileManager wants to call DeleteFile('./temp.tmp'). Allow? [Y/n/Always/Never]"
```

### Multi-Agent Workflow

```csharp
var planner = new AgentBuilder()
    .WithName("Planner")
    .WithProvider("openai", "gpt-4o")
    .WithPlanMode(opts => opts.EnablePersistence = true)
    .Build();

var coder = new AgentBuilder()
    .WithName("Coder")
    .WithProvider("anthropic", "claude-3-5-sonnet-20241022")
    .WithPlugin<FileSystemPlugin>()
    .Build();

var tester = new AgentBuilder()
    .WithName("Tester")
    .WithProvider("openai", "gpt-4o")
    .WithPlugin<FileSystemPlugin>()
    .Build();

var workflow = new WorkflowBuilder(planner)
    .AddEdge(planner, coder)
    .AddEdge(coder, tester)
    .Build();

await workflow.RunAsync("Build a REST API for a todo list with tests");
```

---

## Documentation

- **[Getting Started Guide](docs/getting-started.md)**
- **[Agent Developer Guide](docs/Agent-Developer-Documentation.md)**
- **[Configuration Reference](docs/configuration-reference.md)**
- **[Provider Guide](docs/providers.md)**
- **[Plugin Development](docs/plugins.md)**
- **[Skills System](docs/skills.md)**
- **[Migration Guides](docs/migration/)**
- **[API Reference](docs/api/)**

---

## The Story: Why HPD-Agent Exists

### The Problem: Legacy Baggage

We started with **Semantic Kernel** - Microsoft's pioneering AI orchestration framework. It was groundbreaking, but suffered from a common problem: **early exploration decisions became permanent constraints**.

Semantic Kernel's team learned valuable lessons through real-world usage. This led them to create **Microsoft.Extensions.AI** - a cleaner foundation with better patterns. But here's the catch-22: Semantic Kernel had already reached v1.0, which meant maintaining backwards compatibility with APIs they knew weren't optimal.

**The result?** An over-abstracted framework trying to retrofit new foundations while carrying legacy decisions. Innovation constrained by compatibility.

### The Vision: Start Fresh, Start Right

In 2024, we saw an opportunity: build on the **new** foundation (`Microsoft.Extensions.AI`) without the baggage. Design an agent framework with fundamental architectural requirements from day one:

- **Native AOT Compatible** - Cloud-native from the start, not retrofitted
- **Event-Driven** - Real-time UI integration as a first-class citizen
- **Dual-Conscious** - Both console and web UI experiences considered
- **Highly Serializable** - Configuration-first architecture
- **Resilient** - Production-grade error handling and recovery
- **Protocol Agnostic** - Yet adaptive to provider specifics
- **Safe** - Security and permissions built-in, not bolted-on

### The Philosophy: Higher Place Design

Our motto: **"Make the simple things simple, and the complex things possible."**

No over-abstraction. No legacy compromises. Just clean patterns that work for both quick prototypes and enterprise deployments.

We spent 6 months building this vision.

### The Convergence

Then Microsoft released the **Agent Framework** - a clean specification for what agents should be, built on the same modern foundation we chose.

**Perfect timing.** They provided:
- Excellent abstractions (`AIAgent`, `AgentThread`)
- Powerful orchestration (Workflows)
- A minimal reference implementation (`ChatClientAgent`)

We had:
- A batteries-included implementation
- 6 months of production-focused design
- All the features enterprises actually need

**The synergy was obvious:** Keep Microsoft's architecture, replace their basic implementation with our production-ready one.

### The Result

HPD-Agent isn't a competitor to Microsoft Agent Framework - it's the **production implementation** they didn't build. We implement their spec, work with their workflows, and prove that their architecture works at scale.

**Microsoft provides the blueprint. We provide the building.** 🔋

---

## Support

- **Documentation**: [docs.hpd-agent.com](https://docs.hpd-agent.com)
- **Email**: [support@hpd-agent.com](mailto:support@hpd-agent.com)
- **Issues**: [GitHub Issues](https://github.com/yourorg/hpd-agent/issues)

---

## License

Proprietary. See [LICENSE.md](LICENSE.md) for details.

---

<div align="center">

**HPD-Agent: Microsoft Agent Framework, Batteries Included** 🔋

*Same abstractions · Same workflows · Better implementation*

[Get Started](docs/getting-started.md) · [Documentation](docs/) · [Examples](examples/)

</div>

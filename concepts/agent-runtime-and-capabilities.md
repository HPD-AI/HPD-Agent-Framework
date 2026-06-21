# Agent Runtime And Capabilities

This page is the map of the HPD Agent runtime. Use it when you know the first agent path and want to understand where the larger pieces fit.

The short version:

```text
AgentBuilder configures the runtime.
Agent runs message turns.
Providers supply clients.
Tools and middleware shape what can happen during the turn.
Events describe what happened.
Sessions and threads decide where state lives.
Hosting exposes the same runtime over a process boundary.
```

## Runtime Loop

A normal message turn follows this shape:

```text
user input
  -> message-turn middleware
  -> thread history and run context
  -> model iteration
      -> provider chat client
      -> optional tool/function calls
      -> function middleware and permissions
      -> tool results
      -> more iterations if needed
  -> assistant result
  -> events, thread projection, persistence, eval/telemetry hooks
```

`RunAsync(...)` performs the turn and returns an `AgentTurnResult`. During the same turn, the agent can emit live events for text, tool calls, permissions, retries, errors, middleware, nested agents, workflows, audio, evaluation, and diagnostics.

## Direct Runs And Started Runtimes

Most in-process code can call `RunAsync(...)` directly. In that shape, the call executes the input turn immediately and returns the completed `AgentTurnResult`.

`StartAsync(...)` is for the long-lived runtime shape. It does not run a prompt by itself. It creates the runtime input loop, runtime event coordinator, runtime struct-event hub, input queue, cancellation lifetime, runtime capabilities, and background-task registry. It then runs the start middleware hooks and begins a single-reader loop that accepts `AgentInputEvent` values.

After an agent is started, `RunAsync(input)` queues the input into that runtime loop and returns `AgentTurnResult.Empty`. The runtime loop processes queued inputs, publishes completion events for thread runs, keeps the runtime alive after interrupted turns, and gives hosted clients a live target for events, responses, client tools, permissions, and interruptions.

Use direct `RunAsync(...)` when your app owns the call and only needs the result. Use a started runtime when the agent must be addressable while it is running: ASP.NET Core hosting, SSE/WebSocket clients, bot adapters, TUI runtimes, externally executed client tools, middleware response routing, or runtime-owned background work.

The start/stop middleware hooks belong to this started-runtime shape:

- `BeforeStartAsync` and `AfterStartedAsync` wrap runtime startup.
- `BeforeStopAsync` and `AfterStoppedAsync` wrap runtime shutdown.
- Message-turn, iteration, function, and thread hooks still run inside the turns processed by the runtime loop.

## Capability Map

| Capability | Use It For | Start Here |
| --- | --- | --- |
| Providers and clients | Choosing model, audio, embedding, hosted-file, realtime, and other clients | [Providers, Clients, And Secrets](providers-clients-and-secrets.md) |
| Tools | Letting the model call C# functions | [Tools, Functions, And Harnesses](tools-functions-and-harnesses.md) |
| Tool harnesses | Grouping related generated functions and capability metadata | [Author A Tool Harness](../guides/tools/author-a-tool-harness.md) |
| Collapsed tools | Reducing model-facing tool overload with expandable containers | [Collapsing And Containers](../guides/tools/collapsing-and-containers.md) |
| Client tools | Letting a UI, editor, app, or remote client execute a tool | [Externally Executed Client Tools](../guides/tools/externally-executed-client-tools.md) |
| Events | Rendering live output, timelines, traces, approvals, and diagnostics | [Event Streams And Hierarchies](event-streams-and-hierarchies.md) |
| Sessions and threads | Durable conversation state, forks, replay, and thread-specific state | [Sessions, Threads, And Events](sessions-threads-and-events.md) |
| Middleware | Cross-cutting runtime behavior around turns, iterations, tools, and threads | [Middleware Lifecycle](middleware-lifecycle.md) |
| Permissions | Interactive gates for tool/function execution and app-specific policy | [Permissions Middleware](../guides/middleware/permissions.md) |
| Content | Uploading bytes, preserving thread references, and resolving provider-facing content | [Content Upload And Resolution](../guides/content/content-upload-and-resolution.md) |
| Subagents | Exposing specialist agents as tools with their own runtime policy | [Subagents](../guides/agents/subagents.md) |
| Workflows | Explicit graph orchestration across agents and functions | [Multi-Agent Overview](../guides/multi-agent/overview.md) |
| Hosting | HTTP/SSE/WebSocket/runtime endpoints for external clients | [ASP.NET Core Hosting](../guides/hosting/aspnet-core.md) |
| Observability | Logging, OpenTelemetry, and usage tracking at agent and client layers | [Logging And Telemetry](../guides/observability/logging-and-telemetry.md) |
| Evaluations | Batch, live, judge, safety, red-team, and report workflows | [Evaluations Overview](../guides/evaluations/overview.md) |
| Sandboxing | Local process isolation and execution boundaries for command-capable tools | [Sandboxing Overview](../guides/sandboxing/overview.md) |

## Run Inputs And Outputs

The simplest input is a string:

```csharp
var result = await agent.RunAsync("Summarize this in one sentence.");
Console.WriteLine(result.Text);
```

Use richer input events when the run needs multiple messages, binary content, explicit session/thread routing, or per-run configuration.

`AgentTurnResult.Text` is the final concatenated assistant text. Do not treat it as the whole runtime record. Tool calls, permission prompts, retries, errors, reasoning, custom progress, audio artifacts, workflow node activity, and evaluation scores are event-stream concerns.

If the app needs a UI, trace, timeline, permission queue, or replay view, subscribe to events or use hosted streaming. See [Render An Event Stream](../guides/sessions-and-streaming/render-an-event-stream.md).

## Extension Points

HPD has several extension points. Choose the one closest to the behavior you need:

| Need | Extension Point |
| --- | --- |
| Add or swap the model/audio/realtime backend | Provider or explicit client registration |
| Add one model-callable operation | `[AIFunction]` tool |
| Add a group of related operations | Tool harness |
| Hide and reveal a large tool surface | Collapsed harness |
| Add retrieval, policy, retry, formatting, telemetry, or content handling around the turn | Middleware |
| Add memory or dynamic context before the model call | Middleware plus app-owned storage or content storage |
| Store private runtime policy or progress | Middleware state |
| Ask the user or host for a decision during a run | Bidirectional events |
| Delegate to a specialist from the parent model | Subagent capability |
| Make the process deterministic and graph-shaped | Workflow |
| Serve clients outside the process | ASP.NET Core hosting |

## Providers And Structured Output

Provider capabilities are not identical. Some providers expose response-format or JSON-schema options through provider config, and ONNX Runtime has HPD-owned structured tool calling that turns local model output into normal HPD function calls.

Treat structured output as a provider capability unless a higher-level HPD API is documented for your scenario. Check the provider page before promising typed JSON behavior in application code.

Start with:

- [ONNX Structured Tool Calling](../guides/providers/onnx-structured-tool-calling.md)
- [Provider Setup Overview](../guides/providers/overview.md)
- [Provider Families](../reference/provider-families.md)

## State Model

HPD separates state into scopes:

| Scope | What Belongs There |
| --- | --- |
| Run | Per-call options, runtime middleware, temporary behavior |
| Thread | Conversation path, thread metadata, thread middleware state, thread event projection |
| Session | Session metadata, shared middleware state, thread tree, user/workspace grouping |
| Agent | Agent configuration, tools, clients, stores, middleware, hosted agent definition |
| Content | Uploaded or generated bytes and references, often scoped to a thread |

This is why thread-aware features such as compaction, audio projection, content upload, subagents, and permissions all need clear `sessionId + threadId` routing.

## Trust Boundaries

Treat these as untrusted unless your application has verified them:

- user input
- model output
- function arguments selected by the model
- tool results from external systems
- retrieved documents and web content
- uploaded files
- custom events from clients
- stored session or thread data loaded from a backend

System instructions, permission policy, storage authorization, tenant checks, network controls, and sandbox configuration belong to the host application. HPD gives you hooks, stores, events, permissions, and sandboxing surfaces; your app still owns the product policy.

Use:

- [Permissions Middleware](../guides/middleware/permissions.md) for interactive tool gates
- [Sandboxing Overview](../guides/sandboxing/overview.md) for process-execution boundaries
- [Content Upload And Resolution](../guides/content/content-upload-and-resolution.md) for user bytes and provider-facing content
- [Logging And Telemetry](../guides/observability/logging-and-telemetry.md) for sensitive-data choices
- [LLM Judges And Safety](../guides/evaluations/llm-judges-and-safety.md) and [Red Team](../guides/evaluations/red-team.md) for review signals

## What Not To Assume

Do not assume:

- every provider supports the same content, tool, response-format, or streaming features
- `AgentTurnResult.Text` includes everything important that happened
- permissions sandbox a tool body or authorize external services
- local file stores are production storage
- live events and durable thread events are the same JSON shape
- middleware `OnErrorAsync` catches every provider or turn failure
- a hosted client can respond to an old permission request after the run has moved on

The runtime is powerful because these concerns are explicit. Keep the boundary visible in app code and docs.

## Related Pages

- [Agent Builder And Agent](agent-builder-and-agent.md)
- [Getting Started](../getting-started/index.md)
- [Events Overview](../guides/events/overview.md)
- [Middleware Overview](../guides/middleware/overview.md)
- [Custom Middleware](../guides/middleware/custom-middleware.md)
- [Multi-Agent Overview](../guides/multi-agent/overview.md)
- [Hosted Streaming API](../guides/hosting/hosted-streaming-api.md)

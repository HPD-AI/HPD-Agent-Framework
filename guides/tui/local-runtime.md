# Local TUI Runtime

`InMemoryAgentTuiRuntime` adapts a local `Agent` to the TUI runtime interface. The terminal app and the agent run in the same process.

Use this mode when you want the shortest path from `AgentBuilder` to an interactive terminal.

## Build The Agent

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;
using HPD.Agent.TUI;
using HPD.Agent.TUI.Runtime;

var agentId = "local-tui-agent";
var sessionId = "local-session";

var agent = await new AgentBuilder()
    .WithAgentId(agentId)
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are concise and helpful.")
    .BuildAsync();
```

The important part is that the runtime receives a built `Agent`. Provider setup, tools, middleware, stores, and instructions are still configured through the normal agent builder path.

## Run The TUI

```csharp
var scope = new AgentTuiRuntimeScope(agentId, sessionId, "main");
await using var runtime = new InMemoryAgentTuiRuntime(agent, scope);
await using var app = HpdAgentTuiApp.Create(
    runtime,
    scope,
    tui => tui
        .AddAgentTuiDefaults()
        .AddEventHandler("myapp.text", new TextMessageStreamHandler()));

await app.RunAsync();
```

`HpdAgentTuiApp.RunAsync()` ensures the scope, loads existing thread events if the runtime can provide them, starts observing new events, and then runs the terminal prompt loop.

## Scope Defaults

If you do not pass a default scope to `InMemoryAgentTuiRuntime`, it uses:

```text
agentId: the wrapped agent's AgentId
sessionId: local-session
threadId: main
```

Passing the scope explicitly is clearer in docs and production code because it makes the session and thread visible at the call site.

## Session Store Behavior

If the wrapped agent has a session store and the requested session does not exist, `InMemoryAgentTuiRuntime.EnsureScopeAsync(...)` creates the session through the agent before the TUI starts.

The runtime also exposes session, thread, and agent-store operations when the wrapped agent has the corresponding stores configured. Without those stores, list/search/create operations may be unavailable or return empty results.

## Thread Projection And Compaction

The local runtime uses the same thread event and projection model as other HPD runtime surfaces. After hard durable thread-history compaction, render the projected thread history as canonical.

Current local TUI thread-fork APIs mirror metadata-only fork requests. Do not document a TUI-level fork-compaction toggle unless the runtime API adds one; configure compaction through the wrapped agent and middleware pipeline.

## Agent Switching

The local runtime reports `CanSwitchAgents == false`. It wraps one built in-process agent. A local TUI can inspect configured agent-store APIs, but it does not switch the running runtime agent the way the hosted runtime can.

## Event Rendering

The TUI shell receives events; it does not automatically know how your app wants every event family displayed. Add handlers for the transcript behavior you need:

```csharp
tui => tui
    .AddAgentTuiDefaults()
    .AddEventHandler("myapp.text", new TextMessageStreamHandler())
```

The `TextMessageStreamHandler` shown here is application code, not a guaranteed built-in default.

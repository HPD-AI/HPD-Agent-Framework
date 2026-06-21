# TUI Overview

`HPD-Agent.TUI` is a reusable terminal shell for an HPD agent runtime. It does not define what every agent event means in your product. It gives you the shell, prompt loop, runtime boundary, transcript surface, command registry, and interaction hooks; your application decides how domain events are rendered and how user-facing policies should behave.

The same shell can run against an in-process agent or against an ASP.NET Core hosted HPD Agent API. In both cases the TUI works from an `agentId`, `sessionId`, and `threadId`.

## Core Pieces

`HpdAgentTuiApp` owns the terminal application lifecycle. It ensures a scope, hydrates existing thread events, starts runtime observation, accepts prompt input, and dispatches events to registered handlers.

`IHpdAgentTuiRuntime` is the boundary between the shell and an agent runtime. It supports scope creation, thread event hydration, live observation, input submission, middleware responses, and active-run lookup.

`AgentTuiRuntimeScope` identifies the current `AgentId`, `SessionId`, and `ThreadId`.

`InMemoryAgentTuiRuntime` wraps a local `Agent` in-process.

`HostedAgentTuiRuntime` talks to the hosted HPD Agent API with relative HTTP paths.

`HpdAgentTuiBuilder` composes shell defaults, event handlers, pages, widgets, slash commands, shortcuts, autocomplete providers, run-config composers, and interaction handlers.

## Minimal Shape

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

`AddAgentTuiDefaults()` installs default shell mechanics such as header, footer, prompt, layout, help, clear command, and slash-command autocomplete. It is not a complete product transcript renderer. Add event handlers for the event families your application wants to show.

## Runtime Choice

Use the local runtime when your terminal app owns the `Agent` instance directly. This is the smallest development loop and is useful for internal tools, prototypes, and agent diagnostics.

Use the hosted runtime when the terminal is a client of an ASP.NET Core HPD Agent API. This keeps session, thread, agent definition, active run, and middleware response behavior on the server.

## Event And Interaction Ownership

TUI handlers receive `AgentEvent` instances and a shell context. A text handler might update assistant and user transcript rows. A tool handler might show tool-call lifecycle rows. A product-specific handler might render custom cards.

Built-in interaction handlers exist for common request types such as permissions, continuations, and clarifications, but the application still owns how those requests should be exposed, approved, denied, or constrained for its users.

## Related Pages

Start with [Local Runtime](local-runtime.md) when the TUI is embedded in the same process as an agent.

Use [Hosted Runtime](hosted-runtime.md) when the TUI connects to a mapped HPD Agent API.

Use [Composition](composition.md) when you need commands, pages, widgets, shortcuts, autocomplete, model/run configuration, or custom event handling.

Use [Coding TUI](../harnesses/coding-tui.md) when the terminal needs renderers for coding harness exploration, command execution, and file mutation events.

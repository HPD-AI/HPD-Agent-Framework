# Hosted TUI Runtime

`HostedAgentTuiRuntime` connects the TUI shell to an ASP.NET Core HPD Agent API. The terminal process becomes a client; the hosted app owns agent definitions, sessions, threads, active runs, and the bidirectional response route.

## Map The Hosted API

```csharp
using HPD.Agent;
using HPD.Agent.AspNetCore;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRouting();
builder.Services.AddHPDAgent("tui-agent", config =>
{
    config.DefaultAgent = new AgentConfig
    {
        Name = "Hosted TUI Agent",
        SystemInstructions = "You are concise and helpful.",
        Clients = new AgentClientConfig
        {
            Chat = new ClientProviderConfig
            {
                ProviderKey = "openai",
                ModelName = "gpt-5-mini"
            }
        }
    };
});

var app = builder.Build();
app.MapGroup("/hpd").MapHPDAgentApi("tui-agent");
await app.RunAsync();
```

The hosted runtime does not call an in-process `Agent`. It sends HTTP requests to the mapped HPD Agent API.

The host project must reference and register the provider package used by `ProviderKey = "openai"` following the provider docs. Keep provider registration in the hosted app; the terminal client connects to the mapped API and does not create the model client itself.

## Connect The TUI

```csharp
using HPD.Agent.TUI;
using HPD.Agent.TUI.Runtime;

var scope = new AgentTuiRuntimeScope("tui-agent", "local-session", "main");
await using var runtime = new HostedAgentTuiRuntime(new HostedAgentTuiRuntimeOptions
{
    BaseAddress = new Uri("http://127.0.0.1:5057/hpd/"),
    DefaultScope = scope
});

await using var tui = HpdAgentTuiApp.Create(
    runtime,
    scope,
    builder => builder.AddAgentTuiDefaults());

await tui.RunAsync();
```

## Route-Base Warning

`HostedAgentTuiRuntime` uses relative paths such as `sessions`, `agents`, and `agents/{agentId}/sessions/{sessionId}/threads/{threadId}/inputs`.

Set `HostedAgentTuiRuntimeOptions.BaseAddress` to the HPD Agent API route root, not necessarily the web host root.

If your server maps the API at root:

```csharp
app.MapHPDAgentApi("tui-agent");
```

use:

```csharp
BaseAddress = new Uri("http://127.0.0.1:5057/")
```

If your server maps the API under a group:

```csharp
app.MapGroup("/hpd").MapHPDAgentApi("tui-agent");
```

use:

```csharp
BaseAddress = new Uri("http://127.0.0.1:5057/hpd/")
```

Do not point the hosted TUI at `http://127.0.0.1:5057/` when the HPD API is actually under `/hpd`.

## Runtime Calls

The hosted runtime uses the hosted API for:

- listing, loading, creating, updating, and deleting stored agent definitions
- listing, searching, loading, creating, renaming, and deleting sessions
- listing, creating, forking, renaming, and deleting threads
- loading thread events
- observing live thread events with SSE
- submitting `AgentInputEvent` instances
- checking the active thread run
- sending middleware responses for permissions, continuations, clarifications, and client tools

Hosted TUI middleware responses go through hosted response endpoints. Bot adapters do not use this same hosted response route model for platform button callbacks.

## Thread Projection And Compaction

The hosted TUI observes the server's session, thread, and compaction behavior. It can fork threads through the hosted API, but the current hosted fork request does not expose a per-fork compaction intent. Fork compaction is controlled by the server-side agent and middleware configuration unless the hosted app adds its own route.

After hard durable thread-history compaction, the projected thread history is canonical. Render the thread as loaded from the hosted API or thread event projection, and treat compaction events as audit/debug metadata.

## Scope Defaults

If `DefaultScope` is not supplied, `HostedAgentTuiRuntime` defaults to:

```text
agentId: default
sessionId: local-session
threadId: main
```

For most hosted apps, pass the scope explicitly so the TUI starts on the intended agent, session, and thread.

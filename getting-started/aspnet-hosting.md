# ASP.NET Hosting

ASP.NET Core hosting turns an agent runtime into HTTP endpoints for sessions, threads, content, streaming, middleware responses, and stored agent definitions.

Use hosting when a web app, TypeScript client, TUI, or another process needs to talk to HPD Agent over a boundary.

## Create A Web App

```bash
dotnet new web -n HpdAgentHost
cd HpdAgentHost
dotnet add package HPD-Agent.AspNetCore --version 0.5.5
dotnet add package HPD-Agent.Providers.OpenAI --version 0.5.5
```

Set an OpenAI API key:

```bash
export OPENAI_API_KEY="..."
```

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.AspNetCore;
using HPD.Agent.Hosting.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRouting();

builder.Services.AddHPDAgent("getting-started-agent", config =>
{
    var dataRoot = Path.Combine(Directory.GetCurrentDirectory(), ".hpd-hosting");

    config.SessionStorePath = Path.Combine(dataRoot, "sessions");
    config.AgentStore = new JsonAgentStore(Path.Combine(dataRoot, "agents"));
    config.PersistAfterTurn = true;

    config.DefaultAgent = new AgentConfig
    {
        Name = "Getting Started Agent",
        SystemInstructions = "You are a hosted HPD Agent. Be concise and helpful.",
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

app.MapGet("/", () => "HPD Agent is hosted.");

app.MapGroup("/hpd").MapHPDAgentApi("getting-started-agent", options =>
{
    options.MapEvals = false;
});

app.Run();
```

Run it:

```bash
dotnet run
```

Verify the host is listening:

```bash
curl http://localhost:5000/
```

Expected:

```text
HPD Agent is hosted.
```

Before exposing hosted routes outside local development, decide how your app will handle authentication, authorization, CORS, storage durability, rate limits, and streaming client lifecycle.

## What Happens

`AddHPDAgent(...)` registers a named hosted runtime.

`SessionStorePath` creates a `JsonSessionStore` for hosted sessions and threads.

`AgentStore` stores reusable hosted agent definitions. This sample uses `JsonAgentStore`.

`DefaultAgent` is the fallback agent definition used when a client talks to an agent id that does not already exist in the agent store.

`MapHPDAgentApi(...)` maps the built-in HPD Agent endpoints under `/hpd`.

The hosting layer owns persistence and passes the configured stores to runtimes as requests arrive. Configure stores through hosting options rather than creating separate stores inside `ConfigureAgent`.

When a hosted input request arrives, hosting resolves the route `agentId`, starts the thread runtime if needed, and queues the input into that runtime. That active runtime is what live events, middleware responses, client tools, permissions, interruptions, SSE, and WebSocket streams attach to. For the direct-run versus started-runtime model, see [Agent Runtime And Capabilities](../concepts/agent-runtime-and-capabilities.md).

## Next

At this point, the first path is complete: build an agent, stream it, add a tool, keep session history, save state, and host the runtime.

Choose a next track:

| Track | Start Here |
| --- | --- |
| Build a web client | [Hosted Streaming API](../guides/hosting/hosted-streaming-api.md) and [TypeScript Client Events](../guides/events/typescript-client.md) |
| Add real tool surfaces | [Author A Tool Harness](../guides/tools/author-a-tool-harness.md), [MCP Tools](../guides/tools/mcp-tools.md), and [OpenAPI Tools](../guides/tools/openapi-tools.md) |
| Add safety and control | [Middleware Overview](../guides/middleware/overview.md) and [Permissions Middleware](../guides/middleware/permissions.md) |
| Add orchestration | [Subagents](../guides/agents/subagents.md) and [Multi-Agent Overview](../guides/multi-agent/overview.md) |
| Ship the runtime | [ASP.NET Core Hosting](../guides/hosting/aspnet-core.md), [Stored Agent Definitions](../guides/hosting/stored-agent-definitions.md), and [Logging And Telemetry](../guides/observability/logging-and-telemetry.md) |

For a short orchestration tutorial, continue to [Build A Multi-Agent Workflow](agent-workflow.md).

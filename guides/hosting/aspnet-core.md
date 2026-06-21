# ASP.NET Core Hosting

HPD Agent hosting exposes sessions, threads, stored agent definitions, input submission, live events, WebSocket streaming, content, and middleware response endpoints through ASP.NET Core minimal APIs.

The core hosting model is:

```text
AddHPDAgent(...) registers a hosting bundle
MapHPDAgentApi(...) maps that bundle to routes
agentId + sessionId + threadId selects a runtime scope inside the bundle
```

The hosting registration name is not the route `agentId`.

## Minimal Setup

```csharp
using HPD.Agent;
using HPD.Agent.AspNetCore;
using HPD.Agent.Providers.OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHPDAgent(options =>
{
    options.ConfigureAgent = agent =>
        agent.WithOpenAI(model: "gpt-5-mini")
             .WithInstructions("You are a concise helpful assistant.");
});

var app = builder.Build();

app.MapHPDAgentApi();

app.Run();
```

This maps the default hosting bundle. Requests can then use route `agentId` values to select stored definitions or fallback build paths inside that bundle.

## Route Prefix

Use endpoint options to place all hosted routes under a prefix:

```csharp
app.MapHPDAgentApi(options =>
{
    options.RoutePrefix = "/api/hpd";
});
```

With this prefix, a sessions request becomes `POST /api/hpd/sessions`.

## Named Hosting Bundles

Use named registrations when one ASP.NET Core app hosts multiple independent HPD Agent bundles:

```csharp
builder.Services.AddHPDAgent("support", options =>
{
    options.ConfigureAgent = agent =>
        agent.WithOpenAI(model: "gpt-5-mini")
             .WithInstructions("You help with support requests.");
});

builder.Services.AddHPDAgent("research", options =>
{
    options.ConfigureAgent = agent =>
        agent.WithOpenAI(model: "gpt-5-mini")
             .WithInstructions("You help with research tasks.");
});

app.MapHPDAgentApi("support", options => options.RoutePrefix = "/support-agent");
app.MapHPDAgentApi("research", options => options.RoutePrefix = "/research-agent");
```

The hosting name selects the server-side bundle. The URL prefix is whatever you configure. The route `agentId` still refers to a requested agent definition or runtime agent inside that bundle.

## Hosting Bundle Contents

Each named bundle owns its own:

- session manager
- agent manager
- agent store
- session store
- hosting service facade

If `SessionStorePath` is configured, hosting uses a JSON session store. Otherwise, the default session store is in-memory. The default agent store is also in-memory.

Created-resource `Location` headers are currently prefixless even when the API is mapped under a route prefix. Content uploads use a hosting content store scoped to the thread. The default hosting service creates an in-memory content store, so do not treat uploaded content as durable unless a durable content store is configured.

## Agent Build Resolution

When a hosted runtime needs an agent, the manager resolves it in this order:

1. `IAgentFactory` from dependency injection, if registered.
2. Stored agent definition for the route `agentId`.
3. `HPDAgentConfig.DefaultAgent`.
4. `HPDAgentConfig.DefaultAgentPath`.
5. An empty `AgentBuilder`.

After selecting the source, hosting enriches the builder with the service provider, route agent id, agent store, session store, and configured hosting options.

Configure stores through hosting options. Avoid configuring a separate session store inside `ConfigureAgent`, because hosted runtime state should use the hosting bundle's store.

## Runtime Scope

Runtime routes include:

```text
/agents/{agentId}/sessions/{sessionId}/threads/{threadId}/...
```

That scope is used by HTTP input, interrupt, SSE, WebSocket, thread runs, and the bidirectional response route. TUI hosted runtimes and bot adapters should map their UI or platform identity into the same runtime scope before sending input or responses.

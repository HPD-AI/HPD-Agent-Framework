# Save Sessions And State

Persistence lets sessions, threads, agent definitions, and content survive process restarts.

Use persistence when you are building anything beyond a throwaway console sample.

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var dataRoot = Path.Combine(Directory.GetCurrentDirectory(), ".hpd-data");

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise helpful assistant.")
    .WithSessionStore(Path.Combine(dataRoot, "sessions"))
    .WithAgentStore(Path.Combine(dataRoot, "agents"))
    .WithContentStore(new LocalFileContentStore(Path.Combine(dataRoot, "content")))
    .BuildAsync();

var (sessionId, threadId) = await agent.CreateSessionAsync("getting-started-persistence");

var result = await agent.RunAsync(
    "Remember that my workspace name is Northstar.",
    sessionId,
    threadId);

Console.WriteLine(result.Text);
Console.WriteLine($"Saved session: {sessionId}");
```

Run it:

```bash
dotnet run
```

Run it again and inspect the `.hpd-data` folder. The stores contain the durable state created by the run.

```bash
find .hpd-data -maxdepth 3 -type f
```

## You Succeeded If

You should see files under `.hpd-data/sessions`, and possibly `.hpd-data/agents` or `.hpd-data/content` depending on what the run created.

## What Happens

`WithSessionStore(path)` creates a `JsonSessionStore`.

The session store saves sessions, threads, messages, thread metadata, and session-scoped middleware state. By default, explicitly configuring a session store enables persistence after each turn.

`WithAgentStore(path)` creates a `JsonAgentStore`.

The agent store saves reusable agent definitions. This matters for hosted agents, stored subagents, and systems where agent definitions can be edited or selected at runtime.

`WithContentStore(...)` configures framework-managed content storage for uploads, artifacts, and internal content references.

`LocalFileContentStore` stores content bytes on disk. The default content store is in-memory, so configure a durable content store when thread history or artifacts should survive a restart.

## Local Store Or Hosted Store

Use the JSON session and agent stores for local development, samples, tests, and small tools.

For production hosting, place the same store responsibilities behind the storage layer you want to operate:

- session and thread metadata
- thread message projection and thread event data
- session-scoped and thread-scoped middleware state
- agent definitions, if users can create or select agents at runtime
- content bytes, hosted-file references, and metadata needed to resolve attachments

Treat local file stores as a development default unless your deployment model is designed around that filesystem. Production apps should make explicit choices for retention, backup, tenant boundaries, authorization, encryption, and content lifecycle.

## Next

Next: expose the runtime over HTTP in [ASP.NET Hosting](aspnet-hosting.md).

Optional: add behavior around the turn in [Middleware](middleware.md).

Go deeper: see [Sessions, Threads, And Events](../concepts/sessions-threads-and-events.md) and [Content Upload And Resolution](../guides/content/content-upload-and-resolution.md).

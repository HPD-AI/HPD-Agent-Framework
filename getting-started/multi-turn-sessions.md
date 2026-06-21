# Multi-Turn Sessions

A session gives the agent durable conversation state. Use a session when the next turn should remember earlier turns.

Continue in the same `HpdAgentQuickstart` folder from [Hello Agent](hello-agent.md). This page adds `CreateSessionAsync(...)` and passes the returned session and thread to each run.

## Add Program.cs

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise helpful assistant.")
    .BuildAsync();

var (sessionId, threadId) = await agent.CreateSessionAsync("getting-started-chat");

var first = await agent.RunAsync(
    "My project is called Atlas. Remember that.",
    sessionId,
    threadId);

Console.WriteLine(first.Text);

var second = await agent.RunAsync(
    "What is my project called?",
    sessionId,
    threadId);

Console.WriteLine(second.Text);
```

Run it:

```bash
dotnet run
```

## You Succeeded If

The second answer should identify the project name as `Atlas`.

## What Happens

`CreateSessionAsync(...)` creates a session and its default thread.

The first `RunAsync(...)` writes a user turn and assistant turn into that thread.

The second `RunAsync(...)` runs against the same `sessionId` and `threadId`, so the model receives the previous thread history.

Use this shape for chat apps, command loops, hosted assistants, and any flow where the user expects context to carry forward.

## What Persists

With a durable session store, HPD persists the conversation container, not just the final assistant text.

The thread stores the projected transcript for one conversation path. That is the history the model sees on the next turn for the same `sessionId` and `threadId`.

The session stores metadata shared across threads, plus session-scoped middleware state such as remembered permission choices or user/session preferences.

The thread also stores thread-scoped middleware state. Use thread scope for state that should fork with the conversation path, such as compaction metadata, plan progress, or memory pointers derived from that thread.

Without a configured session store, sessions are process-local. Add persistence before relying on session ids across restarts.

## Common Mistakes

If the agent forgets the first turn, make sure both `RunAsync(...)` calls use the same `sessionId` and `threadId`.

If you only pass a session id sometimes, the conversation can split across different history paths. Keep session and thread handling consistent.

## Sessions And Threads

A session is the conversation container.

A thread is one path through that conversation.

Most apps start with one session and one main thread. Threads become useful when you want alternatives, drafts, retries, subagent work, or user-visible forks.

## Next

Next: turn this into an interactive assistant in [Tiny Console Chat Loop](chat-loop.md).

Optional: fork the conversation in [Threads](threads.md).

Go deeper: see [Sessions, Threads, And Events](../concepts/sessions-threads-and-events.md).

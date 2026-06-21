# What Is An Agent?

An agent is a program that uses a model to decide what to say or do next. A normal function follows code you wrote ahead of time. An agent can read a request, call tools when it needs outside information or actions, use the results, and then continue until it has a final answer.

The smallest HPD agent loop looks like this:

```text
user message
  -> call the model
  -> optionally call tools
  -> optionally call the model again with tool results
  -> final assistant response
```

HPD Agent gives that loop a .NET shape:

```text
AgentBuilder configures the agent
Agent runs turns
tools expose C# methods
sessions and threads hold history
events show what is happening while the turn runs
middleware adds behavior around turns and tool calls
```

## When To Use An Agent

Use an agent when the useful part is language understanding, generation, planning, or model-directed tool use.

Use ordinary C# when the task is deterministic. If code can decide exactly what to do every time, keep it as code. If the model needs to interpret the request, choose tools, write text, or adapt to context, use an agent.

## First 30 Minutes

The first path builds one local agent, streams output, gives it one tool, keeps conversation history, and then turns it into a tiny console chat loop.

Read these next:

1. [Hello Agent](hello-agent.md)
2. [Streaming Events](streaming-events.md)
3. [Add A Tool](add-a-tool.md)
4. [Multi-Turn Sessions](multi-turn-sessions.md)
5. [Tiny Console Chat Loop](chat-loop.md)


# Getting Started

This path takes you from the basic agent loop to a local chat assistant, saved conversation state, and a hosted runtime.

HPD Agent gives you a small builder surface for the first run, then lets you add the runtime pieces that real apps need: tools, sessions, threads, events, middleware, persistence, content, hosting, and workflows.

## Primary Path

Read these in order:

| Step | Page | What You Build |
| --- | --- | --- |
| 0 | [What Is An Agent?](what-is-an-agent.md) | Understand the loop before writing code. |
| 1 | [Hello Agent](hello-agent.md) | Create an agent, call a model, and print the response. |
| 2 | [Streaming Events](streaming-events.md) | Print assistant text as it arrives. |
| 3 | [Add A Tool](add-a-tool.md) | Let the model call one C# function. |
| 4 | [Multi-Turn Sessions](multi-turn-sessions.md) | Keep conversation history across turns. |
| 5 | [Tiny Console Chat Loop](chat-loop.md) | Build a usable local assistant loop. |
| 6 | [Save Sessions And State](persistence.md) | Save sessions, threads, content, and agent definitions. |
| 7 | [ASP.NET Hosting](aspnet-hosting.md) | Expose the runtime over HTTP. |

## Optional Early Detours

These are first-class HPD surfaces, but they do not need to interrupt the shortest path:

| Page | Use It When |
| --- | --- |
| [Tool Harnesses](tool-harness.md) | You want to register a group of related C# functions. |
| [Threads](threads.md) | You want one session to fork into alternate conversation paths. |
| [Middleware](middleware.md) | You want behavior around turns or tool calls, such as retrieval, policy, or usage tracking. |
| [Build A Multi-Agent Workflow](agent-workflow.md) | You want explicit stages, routing, or handoffs after you understand one agent. |

## When To Use What

| Need | Use |
| --- | --- |
| A deterministic operation | A normal C# function. |
| Open-ended language, tool use, or planning | An agent. |
| A specialist called by another agent during a turn | A subagent. |
| Explicit stages, routing, or handoffs | A workflow. |
| External clients, web apps, TUIs, or process boundaries | ASP.NET hosting. |

If ordinary code can do the job predictably, start with ordinary code. Use an agent when language understanding, generation, or model-directed tool use is the useful part.

## Before You Start

You need:

- a supported .NET SDK
- one model provider
- an API key or a local model runtime

The first page uses OpenAI. [Hello Agent](hello-agent.md) also links to a local Ollama option.

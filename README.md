# HPD Agent

HPD Agent is a .NET framework for building production agent applications.

It gives you a small first-run API, then keeps the runtime surfaces explicit as your app grows: tools, sessions, threads, events, middleware, providers, hosted APIs, audio, bots, workflows, and evaluations.

```csharp
var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise product assistant.")
    .WithTool(new WeatherTools())
    .BuildAsync();

var result = await agent.RunAsync("What should I pack for Seattle?");
Console.WriteLine(result.Text);
```

## Why HPD Agent

Most agent demos start clean and become hard to operate once you need real application behavior. HPD Agent is built around the runtime pieces that show up in production:

- tools and tool harnesses for model-callable C# capabilities
- sessions and threads for durable conversation state
- event streams for live UI, tracing, tool activity, permissions, audio, and workflows
- middleware for retrieval, policy, state, usage tracking, and custom turn behavior
- provider packages for OpenAI, Azure OpenAI, Anthropic, Google AI, Bedrock, Mistral, Hugging Face, Ollama, ONNX Runtime, and audio providers
- hosting APIs for web, desktop, TUI, bot, and TypeScript clients
- multi-agent workflows and subagents when one agent is no longer enough

The goal is not to hide the system from you. The goal is to make the system composable.

## What You Can Build

Use HPD Agent for:

- local console agents
- hosted agent backends
- agent workspaces and TUIs
- tool-using assistants
- coding and filesystem harnesses
- Slack, Discord, Telegram, WhatsApp, and Teams bots
- speech-to-text and text-to-speech agents
- multi-agent workflows
- evaluation and red-team pipelines

## First Path

Start with the docs:

1. [What Is An Agent?](getting-started/what-is-an-agent.md)
2. [Hello Agent](getting-started/hello-agent.md)
3. [Streaming Events](getting-started/streaming-events.md)
4. [Add A Tool](getting-started/add-a-tool.md)
5. [Multi-Turn Sessions](getting-started/multi-turn-sessions.md)
6. [Tiny Console Chat Loop](getting-started/chat-loop.md)
7. [Save Sessions And State](getting-started/persistence.md)
8. [ASP.NET Hosting](getting-started/aspnet-hosting.md)

The full docs site starts at [index.md](index.md).

# Hello Agent

This page builds one agent, sends one message, and prints the final response.

## Prerequisites

You need a supported .NET SDK and one model provider. This page uses OpenAI with `OPENAI_API_KEY`.

The provider reads the API key from the environment. Use environment variables or your normal secret manager for keys. Do not hard-code provider secrets in source files.

## Create A Console App

```bash
dotnet new console -n HpdAgentQuickstart
cd HpdAgentQuickstart
dotnet add package HPD-Agent.Framework --version 0.5.5
dotnet add package HPD-Agent.Providers.OpenAI --version 0.5.5
```

Set an OpenAI API key in the environment:

```bash
export OPENAI_API_KEY="..."
```

Use environment variables or your normal secret manager for keys. Do not hard-code provider secrets in source files.

## Add Program.cs

Replace `Program.cs` with:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise helpful assistant.")
    .BuildAsync();

var result = await agent.RunAsync("Say hello in one sentence.");
Console.WriteLine(result.Text);
```

Run it:

```bash
dotnet run
```

## You Succeeded If

You should see one short assistant sentence printed in the console, similar to:

```text
Hello! I'm ready to help you build with HPD Agent.
```

## What Happens

`new AgentBuilder()` starts the agent configuration.

`.WithOpenAI(model: "gpt-5-mini")` configures the default chat provider and model. The OpenAI provider resolves the API key from `OPENAI_API_KEY`.

`.WithInstructions(...)` sets the agent's behavior guidance.

`BuildAsync()` creates the runnable `Agent`.

`RunAsync(...)` sends one user message through the configured model.

`result.Text` contains the final assistant text for the turn.

## Stream The Same Run

If you want live output, subscribe to text deltas before calling `RunAsync(...)`:

```csharp
using var output = agent.Subscribe<TextDeltaEvent>(evt => Console.Write(evt.Text));

_ = await agent.RunAsync("Say hello in one sentence.");
```

That is the smallest streaming path. For typed event families, timelines, tool progress, and hosted clients, continue to [Streaming Events](streaming-events.md).

## Local Alternative With Ollama

Use Ollama when you want a local, no-cloud first run. This is an optional provider swap; the rest of the getting-started path uses the same `AgentBuilder` shape.

```bash
ollama pull llama3.2
ollama serve
dotnet add package HPD-Agent.Framework --version 0.5.5
dotnet add package HPD-Agent.Providers.Ollama --version 0.5.5
```

Use this `Program.cs`:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.Ollama;

var agent = await new AgentBuilder()
    .WithOllama(model: "llama3.2")
    .WithInstructions("You are a concise helpful assistant.")
    .BuildAsync();

var result = await agent.RunAsync("Say hello in one sentence.");
Console.WriteLine(result.Text);
```

Ollama does not require an API key. The provider uses an explicit endpoint if supplied, then `OLLAMA_ENDPOINT`, then `OLLAMA_HOST`, then `http://localhost:11434`.

## Troubleshooting

If the provider is not registered, install the matching provider package, add its namespace, and call the matching fluent method such as `.WithOpenAI(...)` or `.WithOllama(...)`.

If the run says no chat model is configured, make sure the builder includes a provider call with a model.

If OpenAI configuration is invalid or a required OpenAI secret is missing, set `OPENAI_API_KEY` in the same shell where you run `dotnet run`.

If the Ollama run fails at runtime, confirm the server is running and the model has been pulled.

## Next

Next: print the same run live in [Streaming Events](streaming-events.md).

Then: register one local function in [Add A Tool](add-a-tool.md).

Go deeper: for provider models and credentials, see [Providers, Clients, And Secrets](../concepts/providers-clients-and-secrets.md).

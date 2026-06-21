# Agent Builder And Agent

`AgentBuilder` is the setup object. It gathers the agent's provider, model, instructions, tools, middleware, stores, and other configuration.

`Agent` is the runtime object returned by `BuildAsync()`. It accepts user input through `RunAsync(...)`, calls the configured chat client, coordinates tools and middleware, emits events, and returns an `AgentTurnResult`.

For the larger runtime map, including events, sessions, threads, tools, middleware, hosting, observability, evaluations, and trust boundaries, see [Agent Runtime And Capabilities](agent-runtime-and-capabilities.md).

## The First-Reader Flow

The basic flow is:

1. Create a new `AgentBuilder`.
2. Add a provider, such as OpenAI or Ollama.
3. Add instructions.
4. Await `BuildAsync()`.
5. Await `RunAsync(...)`.
6. Read `AgentTurnResult.Text` for the final assistant text.

Build-time configuration and run-time execution are separate. `BuildAsync()` creates a configured agent. `RunAsync(...)` performs a message turn.

## Provider

A chat provider supplies the model client used for the run. In the first quickstart, `.WithOpenAI(model: "gpt-5-mini")` configures the default chat model. The local alternative uses `.WithOllama(model: "llama3.2")`.

Provider packages also handle provider registration and secret resolution. If a provider is missing or not registered, the run can fail with a provider-registration error. See [Providers, Clients, And Secrets](providers-clients-and-secrets.md) and [Provider Keys And Env Vars](../reference/provider-keys-and-env-vars.md).

## Instructions

Instructions are the agent-level guidance sent with the run. For example, a first agent might be told to be concise and helpful. Keep starter instructions short so the behavior is easy to observe.

## Run Result

`RunAsync(...)` returns an `AgentTurnResult`. For first-reader examples, the important property is `Text`, which contains the final concatenated assistant text.

When streaming events are subscribed, event handlers can print text while the run is still happening. The final `AgentTurnResult.Text` is still available after the run completes. See [Streaming Events](../getting-started/streaming-events.md) and [Sessions, Threads, And Events](sessions-threads-and-events.md).

## Tools And Middleware

Tools let the model call C# methods. Add one local tool after the hello-agent path is working. See [Add a Tool](../getting-started/add-a-tool.md) and [Tools, Functions, And Harnesses](tools-functions-and-harnesses.md).

Middleware belongs after the first run and first tool. It can participate in lifecycle hooks, wrapping, permissions, state, retries, and formatting. See [Middleware Lifecycle](middleware-lifecycle.md).

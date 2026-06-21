# Providers, Clients, And Secrets

Providers connect an HPD agent to model clients. A provider package registers a provider key, such as `openai`, `anthropic`, or `ollama`. The agent configuration chooses a client family, usually `Clients.Chat`, and points that family at a provider key plus a model name.

The same resolution model applies across providers:

1. A fluent `AgentBuilder` helper writes a `ClientProviderConfig` into `AgentConfig.Clients.Chat`.
2. `BuildAsync()` validates the provider configuration, creates the secret resolver chain, and asks the provider to create the client.
3. The provider resolves required values such as API keys and endpoints through `ISecretResolver`.
4. The built `Agent` uses the resolved chat client when you call `RunAsync(...)`.

For most applications, prefer fluent setup:

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

Configuration-based setup uses the same model, but writes the client selection directly:

```json
{
  "Clients": {
    "Chat": {
      "ProviderKey": "openai",
      "ModelName": "gpt-4o"
    }
  }
}
```

Do not use older top-level provider JSON shapes for new docs or new apps. Current configuration belongs under `Clients`, with `Clients.Chat` for the primary chat model.

## Client Families

`AgentConfig.Clients` has separate slots for different model client families:

- `Chat`
- `TextToSpeech`
- `SpeechToText`
- `Realtime`
- `ImageGeneration`
- `Embeddings`
- `HostedFiles`
- `VoiceActivityDetection`
- `EndOfTurnDetection`

Provider setup pages focus first on `Clients.Chat`. Some provider packages expose additional families. See [Provider Families](../reference/provider-families.md) for the broader client-family model.

The family slot matters as much as the provider key. `openai` can mean chat, text-to-speech, speech-to-text, realtime, image generation, embeddings, or hosted files depending on the slot and referenced provider packages. `elevenlabs` can mean speech-to-text or text-to-speech, but not chat. A provider key is not a promise that every family exists.

Provider selection does not replace the HPD runtime. The same `Agent` runtime still owns `RunAsync(...)`, tools, middleware, sessions, threads, events, permissions, compaction, hosting, and stored definitions.

## Secret Resolution

`BuildAsync()` creates the default secret resolver chain before provider clients are created:

1. environment variables
2. resolvers added with `AddSecretResolver(...)`
3. configuration, when an `IConfiguration` is available

`WithSecretResolver(...)` replaces that chain. Explicit values passed in fluent setup or placed directly in `ClientProviderConfig` win before resolver lookup.

Environment variables are the usual deployment path. Configuration lookup also supports provider-scoped keys such as:

```text
openai:ApiKey
Providers:openai:ApiKey
```

Configuration capitalization is mechanical. Prefer the exact provider key in docs and examples unless a provider page lists a specific alias.

## Setup Caveats

Provider creation can resolve secrets from environment variables or configuration. These are the practical setup boundaries:

| Setup path | Behavior |
| --- | --- |
| OpenAI, Azure OpenAI, Anthropic, Google AI, Hugging Face, Mistral | Env-backed fluent setup works when the documented environment variable is present. OpenAI and Anthropic also support env-backed `Clients.Chat` JSON setup. Missing credentials fail at `BuildAsync()` before live provider calls. |
| Bedrock | Region env such as `AWS_REGION` / `AWS_DEFAULT_REGION` is accepted; credentials normally flow through the AWS SDK default credential chain. |
| Ollama | `OLLAMA_ENDPOINT` / `OLLAMA_HOST` apply to fluent `.WithOllama(...)`; JSON/config setup should include `Endpoint` when not using localhost. |

Keep a credential preflight in application code when you need a friendlier error message before `BuildAsync()`.

## Related Guides

- [Provider Setup Overview](../guides/providers/overview.md)
- [Provider Families](../reference/provider-families.md)
- [Provider Keys And Environment Variables](../reference/provider-keys-and-env-vars.md)
- [Audio Overview](../guides/audio/overview.md)

# Provider Setup Overview

Provider setup has one shape:

1. Install the provider package.
2. Configure `AgentBuilder` with the provider's fluent helper.
3. Put secrets in environment variables or an explicit secret resolver.
4. Call `BuildAsync()`.
5. Run the agent with `RunAsync(...)`.

## Provider Capability Snapshot

Provider keys do not all mean the same thing. Some keys only create chat clients; some keys create multiple client families; some features such as hosted files or native realtime require a specific family slot.

| Provider key | Chat | STT | TTS | Realtime | Images | Embeddings | Hosted files | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `openai` | yes | yes | yes | yes | yes | yes | yes | Chat and non-audio families come from `HPD-Agent.Providers.OpenAI`; audio families come from the OpenAI audio provider package. |
| `azure-openai` | yes | no | no | no | yes | yes | yes | Traditional Azure OpenAI resource path; `ModelName` is a deployment name. |
| `azure-ai` | yes | no | no | no | no | no | no | Azure AI Projects / Foundry chat path; endpoint/auth requirements differ from `azure-openai`. |
| `anthropic` | yes | no | no | no | no | no | no | Chat provider package. |
| `google-ai` | yes | no | no | no | no | no | no | Chat provider package. |
| `ollama` | yes | no | no | no | no | no | no | Local or server-backed chat; no API key required. |
| `huggingface` | yes | no | no | no | no | no | no | Chat provider package. |
| `mistral` | yes | no | no | no | no | no | no | Chat provider package. |
| `bedrock` | yes | no | no | no | no | no | no | Uses AWS SDK credential behavior. |
| `onnx-runtime` | yes | no | no | no | no | no | no | Local ONNX Runtime GenAI chat provider. |
| `elevenlabs` | no | yes | yes | no | no | no | no | Realtime Scribe is exposed through the speech-to-text family, not `Clients.Realtime`. |

Use this table to choose the provider family slot. Then open the provider-specific page for package names, environment variables, and typed options.

Fluent setup should be the first choice in application code:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.Anthropic;

var agent = await new AgentBuilder()
    .WithAnthropic(model: "claude-3-5-sonnet-latest")
    .BuildAsync();

var result = await agent.RunAsync("Summarize provider setup in one sentence.");
Console.WriteLine(result.Text);
```

JSON or configuration setup is useful when agent definitions are stored outside code. Use `Clients.Chat`:

```json
{
  "Clients": {
    "Chat": {
      "ProviderKey": "anthropic",
      "ModelName": "claude-3-5-sonnet-latest"
    }
  }
}
```

Do not use old top-level `"Provider"` JSON examples. They are not the current primary configuration shape.

Provider-specific knobs live on the same family config through `ProviderOptionsJson`. For example, audio providers use it for voice, output format, speech speed, transcript options, and realtime provider ids. In C# code, use the provider's typed config with `SetProviderConfig(...)` when available; in stored JSON, use `ProviderOptionsJson`.

## Fluent Helpers And Family Config

Most application code should use provider builder extensions because they keep setup short and strongly typed:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.Audio.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAITextToSpeech(model: "tts-1", voice: "nova")
    .BuildAsync();
```

Those helpers do not create a different system. They populate the same `Clients.*` family config that JSON uses:

| Setup style | Best for | Tradeoff |
| --- | --- | --- |
| Fluent builder extension | Normal C# apps, samples, tests, local setup. | Requires referencing the provider package in code. |
| `Clients.*` JSON/config | Stored agent definitions, FFI, generated configs, control planes. | More verbose; provider options are encoded in `ProviderOptionsJson`. |
| Manual `ClientProviderConfig` + `SetProviderConfig(...)` | Advanced C# composition or dynamic family wiring. | More ceremony, but exact control over the family slot. |

For audio, provider setup and runtime behavior are separate. `.WithOpenAITextToSpeech(...)` chooses a TTS provider; `WithAudioRuntimeAttachment(...)` or `WithAudio()` decides when assistant text is synthesized, where artifacts go, and whether playback/projection is enabled.

## Provider Keys

Use these current chat provider keys:

| Provider | Key | Primary setup page |
| --- | --- | --- |
| OpenAI | `openai` | [OpenAI And Azure OpenAI](openai-and-azure-openai.md) |
| Azure OpenAI | `azure-openai` | [OpenAI And Azure OpenAI](openai-and-azure-openai.md) |
| Anthropic | `anthropic` | [Anthropic](anthropic.md) |
| Google AI | `google-ai` | [Google AI](google-ai.md) |
| Ollama | `ollama` | [Ollama](ollama.md) |
| Hugging Face | `huggingface` | [Hugging Face](huggingface.md) |
| Mistral | `mistral` | [Mistral](mistral.md) |
| Amazon Bedrock | `bedrock` | [Amazon Bedrock](bedrock.md) |
| ONNX Runtime | `onnx-runtime` | [ONNX Runtime](onnx-runtime.md) |
| OpenAI audio | `openai` | [OpenAI Audio](openai-audio.md) |
| ElevenLabs audio | `elevenlabs` | [ElevenLabs Audio](elevenlabs-audio.md) |

Azure AI Foundry, OpenRouter, and Azure AI Inference legacy are outside the primary setup path. Azure AI Foundry needs endpoint/auth validation before it gets a beginner setup page. OpenRouter currently has a provider implementation but no package-level fluent `AgentBuilder` helper. Azure AI Inference is obsolete and should be treated as legacy-only.

## Agent Definition Style

HPD separates provider selection from agent definition.

Use `AgentBuilder` when the app owns the agent definition in code. Use stored agent definitions when a hosted app needs to create, update, list, or select agent configs at runtime. Use an `IAgentFactory` override when construction depends on application services or policy that should not be stored as JSON.

This is different from frameworks where each provider produces a different agent subclass. In HPD, provider packages create family-specific clients; the `Agent` runtime still owns sessions, threads, middleware, events, tools, and hosting behavior.

## Setup Caveats

Providers validate required fields such as model, API key, endpoint, region, and option ranges. Secret resolution can use environment variables or configuration during build/client creation.

| Setup path | Behavior |
| --- | --- |
| OpenAI, Azure OpenAI, Anthropic, Google AI, Hugging Face, Mistral | Env-backed fluent setup works when the documented environment variable is present. OpenAI and Anthropic also support env-backed `Clients.Chat` JSON setup. Missing credentials fail at `BuildAsync()` before live provider calls. |
| Bedrock | Region env is accepted; credentials normally flow through the AWS SDK default credential chain. |
| Ollama | Endpoint env aliases apply to fluent `.WithOllama(...)`; JSON/config setup should include `Endpoint` when not using localhost. |
| ONNX Runtime | Local model paths are required for live inference. Compatible ONNX Runtime GenAI instruct models can opt into [structured tool calling](onnx-structured-tool-calling.md). |

See [Providers, Clients, And Secrets](../../concepts/providers-clients-and-secrets.md), [Provider Families](../../reference/provider-families.md), and [Provider Keys And Environment Variables](../../reference/provider-keys-and-env-vars.md).

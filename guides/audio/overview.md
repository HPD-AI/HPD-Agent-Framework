# Audio Overview

HPD Agent audio support has three different paths. Pick the path first, then configure providers.

| Path | Use it when | Main docs |
| --- | --- | --- |
| Finite speech-to-text input | A user uploads or sends an audio file and the model should receive transcript text. | [Speech To Text Input](speech-to-text-input.md) |
| Assistant text-to-speech output | The model produces text and the app also wants an audio artifact or playback flow. | [Text To Speech Output](text-to-speech-output.md) |
| Native realtime audio | The model/provider owns realtime audio and transcript streaming during the turn. | [Realtime Audio](realtime-audio.md) |

Input and output are separate choices. A user can send text, audio, or both; the assistant can return text, audio, or both. The runtime path decides how those shapes are bridged.

| User sends | Assistant returns | What to expect |
| --- | --- | --- |
| Text | Text | Normal chat turn. Audio runtime does not run finite input handling. |
| Text | Audio | The model produces text, then TTS or realtime provider audio produces spoken output. |
| Text | Text + audio | Assistant text remains primary; audio is a realtime stream or synthesized artifact. |
| Audio | Text | Finite STT can inject transcript text before the normal model turn, or realtime can produce transcript text during the turn. |
| Audio | Audio | Use realtime audio for provider-owned voice loops, or finite STT input plus TTS output for pipeline-style voice UX. |
| Audio | Text + audio | Common assistant voice UX: durable text/transcript plus spoken response. |
| Text + audio | Text, audio, or both | Typed text is preserved. Audio can become transcript context, realtime media, or reference content depending on input mode. |

The important distinction is that audio input, model input, assistant output, and durability are different axes. Audio does not always mean realtime, and realtime does not always mean audio-only.

Audio runtime behavior is attached with `WithAudio()` or `WithAudioRuntimeAttachment(...)`.

```csharp
using HPD.Agent;
using HPD.Agent.Audio;
```

```csharp
var agent = await new AgentBuilder()
    .WithChatClient(chatClient)
    .WithAudio()
    .BuildAsync();
```

Use `WithAudio()` for the default attachment. Use `WithAudioRuntimeAttachment(...)` when the app needs explicit speech-to-text, text-to-speech, artifact, thread projection, or playback behavior.

## Provider Families

Audio providers are resolved by client family, not by the chat provider alone.

| Family | Config slot | Use it for |
| --- | --- | --- |
| Speech to text | `Clients.SpeechToText` | Audio input transcription, including finite and provider-streaming STT paths. |
| Text to speech | `Clients.TextToSpeech` | Assistant text synthesis. |
| Realtime | `Clients.Realtime` | Native realtime model turns. |

Source-confirmed provider families include:

| Provider key | Families |
| --- | --- |
| `openai` | speech to text, text to speech, realtime |
| `elevenlabs` | speech to text, text to speech |

A chat provider package alone does not imply audio support. Audio provider packages register their own family slots.

## What Gets Stored

The audio runtime treats text as the durable default:

- Finite input audio can be transcribed and the transcript can be injected into the model input.
- Thread history stores derived transcript text by default, not raw input audio.
- Assistant TTS keeps assistant text as the primary output, then adds synthesized audio artifacts when configured.
- Assistant audio artifacts are stored through `IContentStore` when content-store capture is enabled.

This matters for privacy and replay. Store raw audio only when your app has an explicit retention policy for it.

Audio uses the same generic content pipeline as other binary inputs: user bytes can be uploaded into thread-scoped content references, then resolved into provider-facing content before the model call. Audio detection happens before upload so transcripts and input metadata can still refer back to the original media. See [Content Upload And Resolution](../content/content-upload-and-resolution.md).

## Boundaries

HPD Agent audio does not include microphone capture, browser device handling, or WebRTC UI plumbing. Those belong in the host application. The TypeScript client and hosted text streaming APIs also do not currently expose the full audio submission and realtime configuration surface.

## Where To Go Next

- [Audio Runtime Attachment](runtime-attachment.md)
- [Speech To Text Input](speech-to-text-input.md)
- [Text To Speech Output](text-to-speech-output.md)
- [Realtime Audio](realtime-audio.md)
- [Audio Events And Traces](audio-events-and-traces.md)
- [OpenAI Audio Provider](../providers/openai-audio.md)
- [ElevenLabs Audio Provider](../providers/elevenlabs-audio.md)

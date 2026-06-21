# Text To Speech Output

Text-to-speech output turns assistant text into audio after the model turn.

The important rule is that text stays primary. The assistant text is still available for thread history, accessibility, logs, and fallback. TTS adds synthesized audio when the runtime can resolve a provider and store or play the result.

## Configure Text To Speech

Configure the TTS provider family in app configuration:

```json
{
  "Clients": {
    "TextToSpeech": {
      "ProviderKey": "openai",
      "ModelName": "tts-1"
    }
  }
}
```

Then attach output synthesis behavior:

```csharp
using HPD.Agent;
using HPD.Agent.Audio;
```

```csharp
var agent = await new AgentBuilder()
    .WithChatClient(chatClient)
    .WithContentStore(new InMemoryContentStore())
    .WithAudioRuntimeAttachment(audio =>
    {
        audio.AssistantOutputSynthesisMode = AssistantOutputSynthesisMode.FinalText;
        audio.AssistantOutputProviderKey = "openai";
        audio.AssistantOutputModelId = "tts-1";
        audio.AssistantOutputVoiceId = "nova";
        audio.AssistantOutputFormat = "mp3";
    })
    .BuildAsync();
```

You can also provide an `ITextToSpeechClient` directly through the runtime attachment options when your app owns client construction.

OpenAI and ElevenLabs have source-confirmed TTS provider implementations. See [OpenAI Audio](../providers/openai-audio.md) and [ElevenLabs Audio](../providers/elevenlabs-audio.md).

## Final Text Flow

In the final-text TTS path, the runtime:

1. waits for the model to produce assistant text,
2. records the assistant text as the primary result,
3. resolves a TTS client from explicit runtime options or the `Clients.TextToSpeech` family,
4. synthesizes the final assistant text,
5. writes an assistant-audio artifact when artifact capture is enabled,
6. emits assistant-audio output events for synthesis, artifact, playback, completion, or failure.

If no TTS client can be resolved, the turn completes text-only. If synthesis fails, the runtime keeps the assistant text and reports a text-only fallback instead of pretending audio was produced.

## Artifacts

Content-store artifact capture is the default assistant-output artifact policy. Configure an `IContentStore` when you want synthesized audio artifacts:

```csharp
var agent = await new AgentBuilder()
    .WithChatClient(chatClient)
    .WithContentStore(contentStore)
    .WithAudio()
    .BuildAsync();
```

Captured assistant audio artifacts are stored with metadata such as output flow id, response id, provider, model, and voice. If content-store artifact capture is requested but no content store is configured, the runtime falls back to text-only with a missing-content-store reason.

## Playback Truth

Playback is separate from synthesis. A synthesized audio file is not the same thing as audio the user actually heard.

The runtime tracks playback conservatively:

- queued audio does not mean played audio,
- playback progress records boundaries,
- playback completion is the signal that audio was heard through the playback path,
- playback failure keeps the assistant text and synthesized artifact truth separate from heard-audio truth.

## Related Reading

- [Audio Runtime Attachment](runtime-attachment.md)
- [Audio Events And Traces](audio-events-and-traces.md)
- [OpenAI Audio Provider](../providers/openai-audio.md)
- [ElevenLabs Audio Provider](../providers/elevenlabs-audio.md)

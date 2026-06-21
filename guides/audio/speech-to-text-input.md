# Speech To Text Input

Speech-to-text input is for finite audio content: a WAV, MP3, FLAC, WebM, M4A, OGG, or PCM payload that should become text before the model reasons over it.

This is different from native realtime audio. Finite STT uses the HPD audio runtime to transcribe input and inject transcript text. Native realtime audio sends compatible audio to a realtime model transport. See [Realtime Audio](realtime-audio.md).

Some STT providers also support streaming transcription through `ISpeechToTextClient.GetStreamingTextAsync(...)`. That is still the speech-to-text side of the system: the provider streams transcript updates, then the agent or app decides what to do with the text. It is not the same as a realtime model transport where the model owns the whole conversation turn.

## Send Audio Content

Use `AudioContent` helpers for common formats:

```csharp
using HPD.Agent;
using HPD.Agent.Audio;
using Microsoft.Extensions.AI;
```

```csharp
var audio = AudioContent.Wav(await File.ReadAllBytesAsync("question.wav"));
audio.Name = "question.wav";

await agent.RunAsync(new UserMessagesInputEvent([
    new ChatMessage(ChatRole.User, [audio])
])
{
    SessionId = "session-1",
    ThreadId = "main"
});
```

You can also use `AudioContent.FromFileAsync(...)` or convert from existing `DataContent`.

## Send Text And Audio Together

Mixed input is additive. If the user sends typed text and an audio attachment in the same message, the typed text is preserved and the committed transcript is added as extra model context.

```csharp
var audio = AudioContent.Wav(await File.ReadAllBytesAsync("question.wav"));
audio.Name = "question.wav";

await agent.RunAsync(new UserMessagesInputEvent([
    new ChatMessage(ChatRole.User,
    [
        new TextContent("Answer briefly and focus on the question in the recording."),
        audio
    ])
])
{
    SessionId = "session-1",
    ThreadId = "main"
});
```

In finite STT mode, the runtime detects the audio content, resolves or uploads media as needed, and appends committed transcript text to the user message. It does not replace the user's typed text.

## Configure Speech To Text

Attach audio runtime behavior to the agent:

```csharp
var agent = await new AgentBuilder()
    .WithChatClient(chatClient)
    .WithAudioRuntimeAttachment(audio =>
    {
        audio.InputMode = AudioInputMode.BatchSpeechToText;
    })
    .BuildAsync();
```

Configure the STT provider family in app configuration:

```json
{
  "Clients": {
    "SpeechToText": {
      "ProviderKey": "openai",
      "ModelName": "whisper-1"
    }
  }
}
```

When using provider registry wiring directly, register the provider family and attach it to the runtime:

```csharp
using HPD.Agent.Providers.Audio.Meai;
```

```csharp
builder.WithAudioRuntimeAttachment(audio =>
{
    audio.UseSpeechToTextProvider(
        providerRegistry,
        new ClientProviderConfig
        {
            ProviderKey = "openai",
            ModelName = "whisper-1",
            ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        });
});
```

## What The Runtime Does

For finite audio input, the runtime can:

- resolve the input audio stream,
- call the configured STT provider,
- mark the input as transcribed,
- inject the transcript text into the user message sent to the model,
- project committed transcript text into thread history when thread projection is enabled.

The original audio content is not the durable default. Treat transcript text as the normal thread-history representation unless your product explicitly stores raw media.

## Input Modes

Use [Audio Runtime Attachment](runtime-attachment.md) for the full mode table. The most common STT mode is `BatchSpeechToText`.

`ReferenceOnly` is useful when audio should be available as a reference but not transcribed. `Reject` is useful when an agent or workflow should not accept audio input.

## Realtime Conversion

Some encoded formats can be converted for native realtime input:

```csharp
var realtimePcm = AudioContent.Wav(wavBytes).ToRealtimeInputAudio();
```

Realtime model transport accepts decoded realtime-compatible audio such as PCM, PCMU, or PCMA. Do not assume every uploaded audio format can be sent directly to realtime.

## Related Reading

- [Audio Runtime Attachment](runtime-attachment.md)
- [Realtime Audio](realtime-audio.md)
- [OpenAI Audio Provider](../providers/openai-audio.md)
- [Audio Events And Traces](audio-events-and-traces.md)

# Audio Events And Traces

Audio workflows are event-heavy because they cross model output, provider calls, artifacts, playback, transcript projection, and thread history.

Use typed events for application behavior. Use traces and struct samples for local diagnostics.

## Event Families

| Family | Examples | Use it for |
| --- | --- | --- |
| User transcript events | transcript delta, completed, failed | Show or persist realtime input transcripts. |
| Assistant audio output events | output started, text pushed, segment, chunk, artifact, playback, completed, failed | Track final-text TTS and playback flow. |
| Tool/function events | tool start, args, result, end, custom tool progress | Understand realtime tool execution and audio-adjacent tool work. |
| Struct audio samples | playout, queue depth, underrun, diagnostic samples | Local low-overhead audio pipeline diagnostics. |

Assistant audio event type names are listed in the [Events Reference](../../reference/events.md). Prefer typed handling over hand-written JSON field assumptions.

## Live Events Vs Durable History

Not every audio event should become thread history.

| Data | Usual destination |
| --- | --- |
| Transcript text | Thread history when committed. |
| Assistant text | Thread history as the primary assistant result. |
| Assistant audio artifact | `IContentStore` when artifact capture is enabled. |
| Playback progress | Live event stream or trace. |
| Queue depth and underrun samples | Local struct-event observers or diagnostics. |
| Raw input audio | Explicit app storage only. |

This split keeps replay useful without quietly turning every run into a raw-media archive.

## Mixed Input Correlation

When a user message contains both text and audio, audio input metadata records the original content index for each detected media item. Use that index to correlate UI attachments, uploaded content, transcripts, and runtime metadata without guessing from display order.

Committed transcript metadata records the transcript text, provider key, route decision, topology, and response ownership when available. Assistant audio output events are separate: they describe synthesized or provider-owned assistant audio, not the user's input transcript.

## Assistant Audio Output Flow

Final-text TTS emits events around the assistant audio output flow:

1. the assistant text is available,
2. synthesis starts,
3. segments or chunks may be produced,
4. an artifact may be stored,
5. playback may queue, progress, complete, or fail,
6. the output flow completes or falls back to text-only.

Playback truth is intentionally conservative. A queued artifact is not proof that the user heard the audio.

## Struct Samples

Audio struct samples are process-local diagnostics. They are useful when code in the same process needs low-overhead observations such as playback queue depth or underruns.

They are not the same thing as normal `AgentEvent` streaming. If you need a client, host, or persisted report to see something, emit or bridge a regular agent event or artifact.

## Testing

For event-driven audio tests:

- subscribe before `RunAsync`,
- capture typed events into a list,
- assert event type, session id, thread id, response id, output flow id, and ordering,
- test text-only fallback paths as well as successful synthesis,
- test playback truth separately from synthesis truth.

## Related Reading

- [Text To Speech Output](text-to-speech-output.md)
- [Speech To Text Input](speech-to-text-input.md)
- [Realtime Audio](realtime-audio.md)
- [Live Vs Durable Events](../events/live-vs-durable-events.md)

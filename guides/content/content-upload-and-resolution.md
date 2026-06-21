# Content Upload And Resolution

Binary content has two separate middleware steps:

1. upload user-provided bytes into a durable or provider-native reference,
2. resolve HPD's internal reference into content the current provider can read.

This split lets thread history keep stable references while each model turn still receives provider-facing content.

## Upload User Content

`ContentUploadMiddleware` runs before the message turn. It scans the user message for `DataContent` with bytes, including typed content such as images, audio, documents, and video.

```csharp
using HPD.Agent;
using Microsoft.Extensions.AI;

var agent = await new AgentBuilder()
    .WithChatClient(chatClient)
    .WithContentStore(new InMemoryContentStore())
    .BuildAsync();

var image = new DataContent(await File.ReadAllBytesAsync("diagram.png"), "image/png")
{
    Name = "diagram.png"
};

await agent.RunAsync(new UserMessagesInputEvent([
    new ChatMessage(ChatRole.User,
    [
        new TextContent("Describe this diagram."),
        image
    ])
])
{
    SessionId = "session-1",
    ThreadId = "main"
});
```

The upload path depends on the run and provider configuration:

| Upload path | Resulting content |
| --- | --- |
| Hosted file client | `HostedFileContent` |
| Framework content store | `UriContent(hpd-content://{contentId})` |
| No configured path or failed required path | Original `DataContent` remains |

`AgentRunConfig.UploadStrategy` controls which path is required or preferred:

| Strategy | Behavior |
| --- | --- |
| `Auto` | Prefer hosted files when available; fall back to the content store when possible. |
| `Hosted` | Require a hosted file client. |
| `Local` | Require an `IContentStore`. |

Hosted upload can come from a provider-created hosted-file client, `AgentRunConfig.OverrideHostedFileClient`, or the agent's resolved client set. Local upload requires `WithContentStore(...)`.

## Resolve Internal References

`ContentReferenceResolverMiddleware` runs before the model iteration. It looks for internal HPD content references:

```text
hpd-content://{contentId}
```

Providers should not receive that internal URI directly. The resolver turns it into the best provider-facing shape available for the current run:

| Resolution path | Provider sees |
| --- | --- |
| Temporary read URI from the content store | `UriContent(directUri, mediaType)` |
| Hosted file upload from the content stream | `HostedFileContent` |
| Buffered fallback | `DataContent` |

If resolution fails, the original internal reference is preserved and a failure event is emitted.

## Why There Are Two Steps

Upload and resolution answer different questions:

| Step | Question |
| --- | --- |
| Upload | Where should these user bytes live after the initial message? |
| Resolution | What content shape can this provider consume right now? |

This is especially useful for sessions and threads. A thread can persist `hpd-content://...` references, then later resolve them as direct URLs, hosted files, or buffered bytes depending on the provider and runtime environment.

## Thread Scope

Local content-store upload and reference resolution are scoped to the active thread:

```text
sessionId + threadId
```

Normal `RunAsync(..., sessionId: ...)` execution supplies that context through the active thread. When you construct `UserMessagesInputEvent` directly, include both `SessionId` and `ThreadId` so upload and resolution use the same durable scope.

Sibling threads do not automatically resolve each other's local content references. Forking and replay should preserve the thread path that owns the content reference.

## Events

Upload and resolution emit observability events:

| Event family | Meaning |
| --- | --- |
| `ContentUploadedEvent` | User bytes were stored in `IContentStore`. |
| `HostedFileUploadedEvent` | User bytes were uploaded to a provider hosted-file client. |
| `ContentUploadFailedEvent` | Local upload failed or a required local path was unavailable. |
| `HostedFileUploadFailedEvent` | Hosted upload failed or a required hosted path was unavailable. |
| `ContentReferenceResolvedEvent` | An internal HPD reference was resolved for provider use. |
| `ContentReferenceResolutionFailedEvent` | An internal HPD reference could not be resolved. |

Use these events for UI state, diagnostics, and tests. Do not infer upload or resolution success only from the final content type.

## Audio Interaction

Audio input participates in the same content flow, but audio runtime detection happens before content upload. That lets the audio runtime preserve media identity, content index, name, media type, size, and transcript metadata even if later middleware turns the original `AudioContent` into a `UriContent` or hosted file reference.

For the audio-specific model behavior, see [Speech To Text Input](../audio/speech-to-text-input.md) and [Audio Runtime Attachment](../audio/runtime-attachment.md).

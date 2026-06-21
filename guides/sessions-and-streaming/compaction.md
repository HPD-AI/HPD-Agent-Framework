# Compaction

Compaction reduces conversation history before a model call and can optionally compact the durable thread projection.

Use this mental model:

```text
strategy changes what the next model sees
retention changes what future thread projection keeps
fork compaction rewrites the new target thread before it is committed
```

There are three related surfaces:

- model-visible compaction: middleware reduces the non-system messages used for the next model turn
- durable thread-history compaction: hard retention removes messages from the projected thread history and may insert replacement messages
- fork compaction: a newly forked target thread is reduced before its initial thread history is saved

## Enable Compaction

Compaction is opt-in. Set `Enabled = true` when configuring compaction:

```csharp
builder.WithCompaction(config =>
{
    config.Enabled = true;
    config.Strategy = new MessageCountingCompactionOptions
    {
        TargetMessageCount = 50
    };
    config.Trigger = new CountCompactionTriggerOptions
    {
        TargetCount = 20,
        Threshold = 5
    };
    config.Retention = new PreserveThreadHistoryOptions();
});
```

This configuration performs soft compaction when the count trigger fires. The next model call sees reduced history, but durable thread history remains intact.

Per-run controls can force or skip compaction. `SkipCompaction` wins over `TriggerCompaction`.

## Strategies

The strategy decides what remains visible to the next model turn.

`MessageCountingCompactionOptions` keeps recent messages. Its default target is 50 messages.

`SummarizingCompactionOptions` summarizes older history and keeps recent messages. The default shape keeps 20 recent messages, resummarizes after 5 new messages, uses a single summary, and uses handoff-style summaries. A summarizing strategy can use a separate summarizer provider through `ClientProviderConfig? SummarizerProvider`; otherwise it can use the main chat client.

System messages are separated before reduction and added back before the model call.

Choose the smallest strategy that solves the pressure you are seeing:

| Pressure | Use |
| --- | --- |
| The chat is simply getting long | `MessageCountingCompactionOptions` with preserve retention. |
| Older turns matter, but exact wording does not | `SummarizingCompactionOptions` with preserve retention. |
| The thread projection itself must stay small | A strategy plus `CompactThreadHistoryOptions` or `DeleteCompactedMessagesOptions`. |
| Tool calls/results are involved | Add boundary options that keep tool-call groups intact. |
| A new fork should start lighter than its source thread | Fork compaction through `ThreadForkOptions.CompactionIntent`. |
| One request needs full context for debugging or a critical decision | `AgentRunConfig.SkipCompaction = true`. |
| One request should compact before continuing | `AgentRunConfig.TriggerCompaction = true`. |

## Triggers

Triggers decide when compaction runs:

| Trigger | Behavior |
| --- | --- |
| `CountCompactionTriggerOptions` | Runs after message or message-turn count exceeds `TargetCount + Threshold`. |
| `TokenBudgetCompactionTriggerOptions` | Runs when the last observed input token count exceeds `TargetTokenBudget + TokenBudgetThreshold`. |
| `ContextWindowCompactionTriggerOptions` | Runs when the last observed input token count crosses a configured percentage of the context window. |
| `CompositeCompactionTriggerOptions` | Runs when any child trigger in `AnyOf` runs. |

Token and context-window triggers use usage observed from prior turns. They are not preflight token counters for the current turn.

## Retention

Retention decides what happens to durable thread history after model-visible compaction.

| Retention | Durable thread projection |
| --- | --- |
| `PreserveThreadHistoryOptions` | Preserves thread history. This is the default and safest mode. |
| `CompactThreadHistoryOptions` | Removes durable compacted messages and inserts replacement messages where the removed range began. |
| `DeleteCompactedMessagesOptions` | Removes durable compacted messages without replacement messages. |

Preserve retention is soft compaction. It changes what the model sees but does not remove projected thread messages.

Compact and delete retention are hard retention modes. They can change future `LoadThreadAsync(...)` projection and can make old message ids unavailable as fork points.

## Boundary Policies

Boundary policies control which durable messages are removed under hard retention:

| Boundary | Durable removal scope |
| --- | --- |
| `ExactCompactedMessagesBoundaryOptions` | Removes exactly the durable compacted message ids selected by compaction, excluding retained and system messages. |
| `IncludePreviousMessagesBoundaryOptions` | Includes previous non-system messages before the compacted range. |
| `IncludeMessageTurnBoundaryOptions` | Expands to messages in the same message turn. |
| `IncludeToolCallGroupBoundaryOptions` | Expands to matching tool-call and tool-result messages. |
| `CompositeCompactionBoundaryOptions` | Applies multiple boundary policies. |

Message-turn boundaries depend on projected message metadata such as `hpd.messageTurnId`.

## Events And State

Compaction uses three different records:

| Record | Meaning |
| --- | --- |
| `CompactionEvent` | Live middleware observability for skipped or performed compaction. |
| `CompactionStateData` | Thread-scoped persistent middleware state with last compaction, trigger counts, and usage observations. |
| `ThreadHistoryCompactedEvent` | Durable thread event that changes thread projection under hard retention. |

`CompactionEvent` is useful for diagnostics and live UI. It is not the durable projection instruction.

`ThreadHistoryCompactedEvent` is appended when hard retention is applied to a thread with a store. Projection applies it by removing `DurableCompactedMessageIds` and inserting any `ReplacementMessages`.

## Fork And Compact

A normal fork copies source messages through the fork point, copies thread-scoped middleware state, shares session-scoped state, and prepares target thread metadata in memory.

Fork compaction runs in the pre-commit fork middleware hook. If enabled, `CompactionMiddleware` reduces the target thread messages before the target thread's initial event document is saved.

The source thread is unchanged.

Fork compaction does not append a standalone `ThreadHistoryCompactedEvent`. The target thread starts with the already-compacted initial history.

Direct in-process callers can override the fork compaction choice with `ThreadForkOptions`:

```csharp
await agent.ForkThreadAsync(
    sessionId,
    sourceThreadId,
    newThreadId,
    fromMessageId,
    new ThreadForkOptions
    {
        CompactionIntent = ThreadForkCompactionIntent.Enabled,
        Metadata = new Dictionary<string, object>
        {
            ["name"] = "Compacted exploration"
        }
    },
    cancellationToken);
```

ASP.NET Core hosted fork requests do not currently include a per-request compaction intent. In hosted apps, fork compaction is controlled by the configured server-side agent and middleware pipeline unless the application exposes its own higher-level route.

## UI Guidance

For transcript views, render the projected thread messages as canonical.

After hard thread-history compaction:

- replacement messages appear where the compacted range used to be
- delete retention closes the gap without replacement messages
- the compaction event can be shown in an audit or debug lane
- compacted-away message ids may no longer be valid fork points

Do not render deleted compacted messages as ordinary transcript rows unless your application has a separate archival source.

Example projection:

```text
Before hard thread-history compaction:
  user-1, assistant-1, user-2, assistant-2

After compact retention with a replacement message:
  summary-1, user-2, assistant-2

After delete retention without replacement:
  user-2, assistant-2
```

## Related Pages

- [Sessions, Threads, And Events](../../concepts/sessions-threads-and-events.md)
- [Thread History And Forking](thread-history-and-forking.md)
- [Render An Event Stream](render-an-event-stream.md)
- [Live Vs Durable Events](../events/live-vs-durable-events.md)
- [Middleware State Persistence](../middleware/state-persistence.md)
- [Hosted Streaming API](../hosting/hosted-streaming-api.md)
- [Hosted Endpoints](../../reference/hosted-endpoints.md)
- [Hosted TUI Runtime](../tui/hosted-runtime.md)
- [Subagents](../agents/subagents.md)

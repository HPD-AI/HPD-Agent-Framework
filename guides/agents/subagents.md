# Subagents

A subagent is a real HPD agent exposed to a parent agent as a tool. The parent model sees one generated function with a `query` argument. When the model calls it, HPD builds or loads the child agent, routes the child run into the selected session and thread, streams child events through the parent event path, and returns the child answer as the tool result.

Use subagents when the parent should delegate to a specialist during normal tool calling. Use multi-agent workflows when the whole task should follow an explicit graph of roles and routes.

## Smallest Setup

Create a tool harness method marked with `[SubAgent]` and return a `SubAgent` definition:

```csharp
using HPD.Agent;

public sealed class SpecialistTools
{
    [SubAgent]
    public SubAgent Summarizer() =>
        SubAgent.FromConfig(
            "summarizer",
            "Summarizes a request before the parent answers.",
            new AgentConfig
            {
                Name = "Summarizer",
                SystemInstructions = "Summarize the user's request in three concise bullets.",
            });
}
```

Register the harness like any other tool harness:

```csharp
var agent = await new AgentBuilder()
    .WithChatClient(chatClient)
    .WithToolHarness<SpecialistTools>()
    .BuildAsync();
```

`SubAgent.FromConfig(...)` defaults to `ParentSessionForkedThread()`. That means each subagent call uses the parent session, forks from the current parent thread, and writes the child conversation to a hidden child thread.

`AgentBuilder` provides an in-memory session store by default. Use `WithSessionStore(...)` when subagent threads should survive process restarts or be visible to another host.

## Tool Shape

The generated tool takes one model-facing argument:

```json
{
  "query": "Summarize this request."
}
```

The subagent name becomes the tool name. The description becomes the tool description, so write it as guidance for the parent model. Subagent tools require permission by default.

The tool result is the child agent's text response. HPD also records subagent metadata on the tool result, including `subAgentStatus`, `subAgentSessionId`, `subAgentThreadId`, `subAgentName`, and `subAgentRunId`.

## Choose The Source

A subagent has two source modes:

| Source | Use When | What Happens |
| --- | --- | --- |
| `SubAgent.FromConfig(...)` | The specialist is defined next to the parent harness. | HPD builds a child agent from the inline `AgentConfig`. If the child config does not specify its own chat provider, it can inherit the parent chat client. |
| `SubAgent.FromAgentId(...)` | The specialist is stored, managed, or reused outside this harness. | HPD loads the stored agent definition through the parent agent store, then runs it with the selected execution policy. |

Inline config is best for local specialists that belong to one product surface. Stored agents are better when specialists are user-created, versioned, administered, or shared across parent agents.

## Add Child Tools

Pass tool harness types after the execution policy:

```csharp
[SubAgent]
public SubAgent Researcher() =>
    SubAgent.FromConfig(
        "researcher",
        "Researches a question using the approved search tools.",
        new AgentConfig
        {
            Name = "Researcher",
            SystemInstructions = "Research the request and return cited notes.",
        },
        SubAgentExecutionPolicies.ParentSessionForkedThread(),
        typeof(SearchTools),
        typeof(CitationTools));
```

Those harnesses are registered on the child agent, not exposed directly to the parent. The parent sees only the `researcher` tool.

## Choose The Execution Policy

The execution policy decides where the child conversation lives. This is the main subagent DX choice.

| Policy | Session | Thread | Use When |
| --- | --- | --- | --- |
| `ParentSessionForkedThread()` | Parent session | New thread forked from current parent thread | Default. The child needs the parent's current context but should write its own durable history separately. |
| `ParentSessionFreshThread()` | Parent session | New empty thread | The child should be related to the same user/session but should not inherit parent thread messages. |
| `ParentThread()` | Parent session | Current parent thread | The child should write into the same thread as the parent. Use deliberately because histories interleave. |
| `NewSession()` | New session | New empty thread | The child should be isolated from the parent session. |
| `SharedSessionFreshThread("session-id")` | Named shared session | New empty thread | A specialist should accumulate or organize work under a stable shared session. |
| `SharedSessionExistingThread("session-id", "thread-id")` | Named shared session | Existing thread | A specialist should keep using a known thread. |
| `ExistingThread("thread-id")` | Parent session | Existing thread | The parent session already has a thread that the child should use. |

Most apps should start with the default. It gives the child enough context to be useful, keeps parent and child histories separate, and still lets the UI render them as one hierarchy.

## Thread Metadata

Subagent-created threads are hidden by default and include metadata that links them back to the parent:

- `kind = subagent`
- `subAgentName`
- `subAgentSourceKind`
- `parentSessionId`
- `parentThreadId`
- `parentToolCallId`
- `subAgentRunId`
- `sessionPolicy`
- `threadPolicy`
- `visibility = hidden`
- `createdBy = subagent`

You can add custom thread metadata when creating the subagent:

```csharp
SubAgent.FromConfig(
    "reviewer",
    "Reviews the draft.",
    reviewerConfig,
    SubAgentExecutionPolicies.ParentSessionForkedThread(),
    metadata: new Dictionary<string, object>
    {
        ["team"] = "writing",
        ["priority"] = "normal"
    });
```

Use metadata for UI grouping, retention policy, audit labels, or application-specific routing. Do not depend on thread names for identity; use the route metadata and result metadata.

## Events And Hierarchy

Subagent runs are event-native. The parent run emits normal tool call events for the subagent tool, and the child agent emits its own agent events with child `AgentMetadata`.

That lets a host render:

- the parent model deciding to call `researcher`
- `ToolCallStart`, `ToolCallArgs`, `ToolCallEnd`, and `ToolCallResult` for the subagent tool
- child text, tool calls, permissions, custom events, and turn events
- the parent model continuing after the child result

When a hosted event route is scoped to a parent session and thread, HPD can include child subagent thread events when the child thread metadata points back to the routed parent thread.

For rendering patterns, see [Event Streams And Hierarchies](../../concepts/event-streams-and-hierarchies.md), [Workflow Events](../multi-agent/workflow-events.md), and [Render An Event Stream](../sessions-and-streaming/render-an-event-stream.md).

## Thread Compaction

Only forked-thread subagents can set thread compaction at fork time. This lets the subagent control how much parent thread history is copied into the child thread when the child thread is created:

```csharp
SubAgentExecutionPolicies.ParentSessionForkedThread(
    SubAgentThreadCompaction.PreferCache)
```

Use:

- `Inherit` to use the agent's normal `CompactOnFork` behavior
- `Enabled` to compact the child thread before it is committed, even if normal fork compaction is off
- `Disabled` to keep the child thread un-compacted, even if normal fork compaction is on
- `PreferCache` to reuse a matching copied thread compaction cache when possible, and otherwise run normal fork compaction

The compaction runs against the fork target before the subagent starts. In practice, that means a `ParentSessionForkedThread()` subagent can inherit the parent's recent context without copying every parent message into the child thread.

`PreferCache` is useful when many subagents fork from the same parent context. HPD only uses the cache when the cached original message ids match the fork target and the cached model-visible messages are present on that thread. If the cache is missing or stale, HPD falls back to the configured compaction strategy.

This controls the fork operation. It does not define the compaction strategy. The configured compaction middleware still decides how history is reduced. See [Compaction](../sessions-and-streaming/compaction.md).

## Permissions

Subagent tools require permission by default because they can run another agent, call child tools, and write durable history. Treat permission prompts as a parent-level decision: the parent is asking to delegate work to the named specialist.

If you build a custom permission middleware, you can approve at whatever level your product needs: the subagent tool call, the generated query, the child's own tools, or a higher-level command signature stored in middleware state.

See [Permissions](../middleware/permissions.md) and [Bidirectional Events](../events/bidirectional-events.md).

## Subagents Vs Workflows

Use subagents when:

- the parent model should decide when to delegate
- each specialist is naturally a tool
- the parent should receive a single tool result and continue
- thread policy is more important than graph topology

Use multi-agent workflows when:

- the app should control the graph
- routing, fan-out, review gates, or fixed stages matter
- node outputs need explicit keys
- you want to run the workflow directly from app code or expose the whole graph through `[MultiAgent]`

See [Multi-Agent Overview](../multi-agent/overview.md) and [Multi-Agent Capabilities](../tools/multi-agent-capabilities.md).

## Boundaries

`[SubAgent]` is the normal authoring path. The source generator is the production path and reflection fallback exists for development and troubleshooting.

Generated and reflection-created subagent wrappers both use the parent function execution context for chat-client inheritance, session-store access, event coordination, and hierarchical metadata. Treat low-level runtime helpers as implementation details unless you are extending HPD Agent itself.

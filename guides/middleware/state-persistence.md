# Middleware State Persistence

Middleware state is private state owned by middleware and carried through the agent loop. Use it for policy data the middleware needs to make the next decision: counters, permission choices, compaction metadata, plan progress, continuation limits, error tracking, and circuit breaker state.

State is transient by default. It lives for the current run unless the state record opts in with `[MiddlewareState(Persistent = true)]`.

## Quick Start

Declare a sealed record, choose the persistence scope, then update it from a middleware hook.

```csharp
using HPD.Agent;
using HPD.Agent.Middleware;

[MiddlewareState(Persistent = true, Scope = StateScope.Thread)]
public sealed record TurnCounterState
{
    public int Count { get; init; }
}

public sealed class TurnCounterMiddleware : IAgentMiddleware
{
    public Task BeforeMessageTurnAsync(
        BeforeMessageTurnContext context,
        CancellationToken cancellationToken)
    {
        context.UpdateMiddlewareState<TurnCounterState>(state => state with
        {
            Count = state.Count + 1
        });

        return Task.CompletedTask;
    }
}
```

This is the normal shape for one middleware-owned state type:

- `[MiddlewareState]` registers the state type with the framework.
- `Persistent = true` makes the state eligible to be loaded at the next run.
- `Scope = StateScope.Thread` stores it on the current thread, so forks start with a copy and then diverge.
- `UpdateMiddlewareState<TState>(...)` reads the current value inside the update lambda and writes a new immutable value immediately.

## Attribute Settings

`[MiddlewareState]` can be applied to a record type. The source comments recommend sealed records with JSON-serializable members.

| Setting | Default | Use it for |
| --- | --- | --- |
| `Version` | `1` | Schema versioning for breaking state-shape changes. |
| `Persistent` | `false` | Opting in to save/load across runs. |
| `Scope` | `StateScope.Thread` | Choosing whether persistent state belongs to a thread or the whole session. |

Only set `Persistent = true` when the next run should see the value. Most safety and per-run bookkeeping should remain transient.

## Persistent Or Transient

Transient state is the default:

```csharp
using HPD.Agent;

[MiddlewareState]
public sealed record AttemptWindowState
{
    public int ConsecutiveFailures { get; init; }
}
```

Use transient state for data that should reset for each run: retry windows, batch decisions, temporary tool gating, per-run metrics, and circuit breaker counters. Persisting these can make a later run inherit stale safety state.

Persistent state is for data the user or conversation should remember:

```csharp
using HPD.Agent;

[MiddlewareState(Persistent = true, Scope = StateScope.Session)]
public sealed record PermissionMemoryState
{
    public string[] AlwaysAllowedFunctions { get; init; } = [];
    public string[] AlwaysDeniedFunctions { get; init; } = [];
}
```

Built-in examples follow the same split:

- `PermissionPersistentStateData` is persistent and session-scoped, so remembered permission choices apply across threads in the same session.
- `CompactionStateData` is persistent and thread-scoped, so compaction metadata follows the conversation path.
- `PlanModePersistentStateData` is persistent and thread-scoped because `Scope` is omitted and thread is the default.
- `BatchPermissionStateData`, continuation permission state, error tracking state, total error threshold state, and circuit breaker state are transient unless their attribute says otherwise.

`CompactionStateData` records thread-scoped compaction metadata and trigger observations. It is not the same as `ThreadHistoryCompactedEvent`, which changes durable thread projection under hard retention. See [Compaction](../sessions-and-streaming/compaction.md).

## Session Or Thread

Use `StateScope.Session` for persistent state that belongs to the whole session and should be shared by every thread:

```csharp
[MiddlewareState(Persistent = true, Scope = StateScope.Session)]
public sealed record UserPreferenceState
{
    public string? PreferredToolMode { get; init; }
}
```

Session-scoped state is loaded from `Session.MiddlewareState` and saved back to the session. When a thread is forked, the new thread reads the same session-scoped state as the source thread.

Use `StateScope.Thread` for persistent state derived from the conversation path:

```csharp
[MiddlewareState(Persistent = true, Scope = StateScope.Thread)]
public sealed record ThreadProgressState
{
    public int CompletedSteps { get; init; }
}
```

Thread-scoped state is loaded from `Thread.MiddlewareState` and saved back to the thread. When a thread is forked, the thread middleware state dictionary is copied to the new thread before `BeforeThreadForkCommitAsync` runs; after the fork, each thread saves its own copy and can diverge.

If `Scope` is omitted, the state is thread-scoped.

## Update Model

For one state type, prefer `UpdateMiddlewareState<TState>(...)`:

```csharp
context.UpdateMiddlewareState<TurnCounterState>(state => state with
{
    Count = state.Count + 1
});
```

The helper:

- Uses the state's fully qualified type name as the storage key.
- Creates `new TState()` when the state is missing.
- Throws if the transform returns `null`.
- Delegates to `UpdateState(...)`, so the framework's generation guard and immediate visibility still apply.

Use `GetMiddlewareState<TState>()` for simple point-in-time reads:

```csharp
var count = context.GetMiddlewareState<TurnCounterState>()?.Count ?? 0;
```

For decisions that read several fields together, use `Analyze(...)` so the read happens under the state lock:

```csharp
var (count, iteration) = context.Analyze(state =>
{
    var counter = state.MiddlewareState.GetState<TurnCounterState>(
        typeof(TurnCounterState).FullName!);

    return (counter?.Count ?? 0, state.Iteration);
});
```

Use `UpdateState(...)` when one operation must update core loop state or multiple middleware state records together:

```csharp
context.UpdateState(state =>
{
    var key = typeof(TurnCounterState).FullName!;
    var current = state.MiddlewareState.GetState<TurnCounterState>(key)
        ?? new TurnCounterState();

    return state with
    {
        MiddlewareState = state.MiddlewareState.SetState(
            key,
            current with { Count = current.Count + 1 }),
        TerminationReason = "Counter updated"
    };
});
```

Updates are visible to later hooks immediately. There is no "pending update" phase and no rollback after a middleware writes new state.

## Avoid Stale Reads

Do not read state, `await`, and then write a value derived from the old read:

```csharp
var count = context.GetMiddlewareState<TurnCounterState>()?.Count ?? 0;
await SomeOtherWorkAsync(cancellationToken);

context.UpdateMiddlewareState<TurnCounterState>(state => state with
{
    Count = count + 1
});
```

The captured `count` can be stale. Put the read inside the update lambda:

```csharp
await SomeOtherWorkAsync(cancellationToken);

context.UpdateMiddlewareState<TurnCounterState>(state => state with
{
    Count = state.Count + 1
});
```

The framework protects state updates with a lock and generation counter. That catches some stale or nested mutation bugs, but middleware authors should still structure updates so each transform derives from the state it receives.

## Save And Load Timing

On a fresh run with a session and thread, the agent loads persistent middleware state from both places:

- Session-scoped persistent state comes from `Session.MiddlewareState`.
- Thread-scoped persistent state comes from `Thread.MiddlewareState`.
- The two containers are merged into the initial loop state for the turn.

At the end of the message turn, after `AfterMessageTurnAsync`, the agent saves persistent state back by scope:

- `StateScope.Session` state is saved to the session.
- `StateScope.Thread` state is saved to the thread.
- Thread middleware state may also be appended to the thread event store when one is configured.

Persistence errors are caught and ignored by the agent loop. Do not use middleware state as the only durable record for business-critical side effects; store those in an application database or service you control.

## Fork Behavior

Forks preserve the right kind of continuity for each scope:

| Scope | On fork | After fork |
| --- | --- | --- |
| `StateScope.Session` | Not copied to the thread; both threads read the same session state. | Changes are shared across threads in the session. |
| `StateScope.Thread` | Copied from the source thread to the new thread. | Each thread saves independently and diverges. |
| Transient state | Recreated for the fork middleware context from persistent state only. | Not saved between runs unless marked persistent. |

If your state answers "what has this user/session decided?", use session scope. If it answers "what has happened along this conversation path?", use thread scope.

## Common Errors

**Forgetting `using HPD.Agent;`**

`[MiddlewareState]` and `StateScope` are in `HPD.Agent`.

**Forgetting `using HPD.Agent.Middleware;`**

`IAgentMiddleware`, hook context types, `UpdateMiddlewareState<TState>(...)`, and `GetMiddlewareState<TState>()` are in `HPD.Agent.Middleware`.

**Using a primary-constructor record with `UpdateMiddlewareState<TState>(...)`**

The helper requires `where TState : class, new()`. Use a record with a parameterless constructor shape, as shown in the examples, or use `UpdateState(...)` and construct the value yourself.

**Expecting transient state to survive the next run**

`[MiddlewareState]` without `Persistent = true` resets between runs.

**Expecting session-scoped state to fork independently**

Session-scoped state is shared by all threads in the session. Use thread scope for state that should split after a fork.

**Persisting secrets or large payloads**

Middleware state is serialized into session or thread state. Store secret values, file bodies, embeddings, large caches, and external resource payloads elsewhere; persist only small IDs, preferences, counters, or metadata needed to resume middleware behavior.

## Validation Notes

This page is source/test-checked against the middleware state attribute, state container, update helpers, agent turn load/save path, fork path, and middleware persistence tests. The snippets are intended to be runnable candidates with the shown imports, but a clean external consumer project compile has not been run.

Known source-doc mismatch to avoid repeating: some comments still describe plan-mode state as session-persistent or refer to session load/save, but the actual attribute is `[MiddlewareState(Persistent = true)]`, so it is thread-scoped by default.

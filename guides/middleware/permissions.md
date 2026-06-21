# Permissions Middleware

Permission middleware gates tool/function execution through bidirectional events. It is a runtime policy: tools declare or receive a permission requirement, middleware asks the UI or host for a decision, and the function either runs or is blocked.

Permissions are tied to events. Any runtime that wants interactive permissions must route permission request events to a user interface and return permission response events to the same run.

In rendered event views, permissions usually sit under the tool/function call that requested them. Generated subagent and multi-agent tools require permission by default, so a parent permission prompt can appear before child subagent or workflow events. See [Bidirectional Events](../events/bidirectional-events.md) and [Event Streams And Hierarchies](../../concepts/event-streams-and-hierarchies.md).

## Built-In Vs Custom Permission Policy

HPD Agent separates permission metadata from permission policy.

`[RequiresPermission]`, `RequirePermissionFor(...)`, `DisablePermissionFor(...)`, and generated capability metadata say that a capability should be considered by permission middleware. They do not define every possible permission model.

The built-in `PermissionMiddleware` implements `IAgentPermissionMiddleware` and provides a simple function-level policy:

- the protected subject is the function name
- the prompt is a `PermissionRequestEvent`
- the response is a `PermissionResponseEvent`
- remembered choices are `Ask`, `AlwaysAllow`, or `AlwaysDeny`
- persistent grants are stored in middleware state by function name

If your app needs command-level, path-level, network-level, tenant-level, risk-based, or workspace-scoped grants, write custom middleware. Custom permission middleware can still use `[RequiresPermission]` metadata, or it can define its own metadata, custom permission events, and custom middleware state.

Use the built-in middleware for ordinary function gating. Use custom permission middleware when the user is approving a more specific subject than "this function may run."

## Function Permission Flow

`PermissionMiddleware` uses these phases:

1. `BeforeIterationAsync` resets transient batch permission state.
2. `BeforeParallelBatchAsync` checks a parallel tool batch sequentially and records approvals or denials.
3. `BeforeFunctionAsync` checks whether a function requires permission.
4. If no stored choice applies, it emits `PermissionRequestEvent` and waits for `PermissionResponseEvent`.
5. Approval allows execution. Denial blocks execution and supplies the denial result.

Persistent choices are middleware state, not external storage. `AlwaysAllow` and `AlwaysDeny` are stored in session-scoped `PermissionPersistentStateData`, so they apply across threads in the same session. `Ask` is request-scoped and does not remember the decision.

Permission prompts are sequential even for a parallel tool batch. The batch hook asks for each gated function, records approvals and denials in transient batch state, and the later per-function hook reuses that state instead of prompting again.

## Register Permission Middleware

Use the builder helper for normal applications:

```csharp
using HPD.Agent;
using HPD.Agent.Permissions;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("Ask before running tools that require permission.")
    .WithPermissions()
    .RequirePermissionFor("run_command")
    .WithTool<ShellTools>("run_command")
    .BuildAsync();
```

`WithPermissions()` constructs `PermissionMiddleware` with the builder's agent config and permission override registry. That is what makes `RequirePermissionFor(...)`, `DisablePermissionFor(...)`, and `ClearPermissionOverride(...)` effective.

Use direct `PermissionMiddleware` registration only when manually composing middleware and not relying on builder permission overrides:

```csharp
using HPD.Agent;
using HPD.Agent.Permissions;

var agent = await new AgentBuilder()
    .WithMiddleware(new PermissionMiddleware())
    .WithTool<ShellTools>("run_command")
    .BuildAsync();
```

With direct registration, attribute-based requirements still work, but builder override helpers are not wired into that middleware instance.

Mark a tool function as requiring permission:

```csharp
using HPD.Agent;
using Microsoft.Extensions.AI;

public sealed class ShellTools
{
    [AIFunction(Name = "run_command")]
    [AIDescription("Runs a shell command.")]
    [RequiresPermission]
    public string RunCommand(
        [AIDescription("The command to run.")] string command)
    {
        return $"Would run: {command}";
    }
}
```

You can also require permission from the builder when the tool comes from a third-party harness or generated surface that you do not own:

```csharp
var agent = await new AgentBuilder()
    .WithPermissions()
    .RequirePermissionFor("DeleteAllData")
    .BuildAsync();
```

Use `DisablePermissionFor("ReadFile")` sparingly and only when the function is safe in your runtime. It overrides the attribute-based decision for that function name.

## Handle Events

Permission UI is event-driven. A local app, hosted API client, TUI, or bot adapter must show the request and send a matching response.

The sample below uses the direct in-process `Agent` API. Hosted API clients use the hosted response route shown after the sample.

```csharp
using HPD.Agent;
using HPD.Agent.Permissions;

using var permissionRequests = agent.Subscribe<PermissionRequestEvent>(request =>
{
    Console.WriteLine($"Allow {request.FunctionName} from {request.SourceName}?");

    // In a real UI, collect the user's choice before sending the response.
    _ = agent.TryRespondAsync(new PermissionResponseEvent(
        PermissionId: request.PermissionId,
        SourceName: request.SourceName,
        Approved: true,
        Reason: "Approved from sample handler",
        Choice: PermissionChoice.Ask));
});

var result = await agent.RunAsync("Run the command pwd.");
Console.WriteLine(result.Text);
```

`PermissionChoice.Ask` approves or denies only the current request. `PermissionChoice.AlwaysAllow` persists an approval for the function in session-scoped middleware state. `PermissionChoice.AlwaysDeny` persists a denial.

Preserve the `PermissionId` and `SourceName` from the request. They are how the response is matched to the active waiter. Do not synthesize a new id in the UI.

Denials are normal tool results, not process failures. The middleware sets `BlockExecution` and supplies a denial message as the function result. The model may continue the turn using that result.

## Hosted API

Hosted runtimes expose one response route for standardized response events. A hosted UI should read `PermissionRequestEvent` from the event stream, render the request, and post the corresponding `PermissionResponseEvent` envelope through the hosted response path.

The response route is:

```http
POST /agents/{agentId}/sessions/{sid}/threads/{bid}/responses
```

Hosted responses are routed to the active runtime for the exact `agentId + sessionId + threadId`. If that thread runtime is not active, the service returns a `ThreadRuntimeNotActive` conflict. If no matching request is waiting, the response is rejected as a conflict. Clients should post responses while the request is live and preserve the request id from the event.

Do not treat permissions as a separate hidden channel. They are part of the same event vocabulary as other agent runtime interactions.

## TUI And Bots

The TUI can render permission prompts in its local or hosted runtime, but it still depends on the same request/response event routing. Local TUI code responds through the in-process runtime; hosted TUI code answers through the hosted thread runtime.

Bot integrations must preserve the permission request identity, map the platform user action back to a `PermissionResponseEvent`, and respect platform webhook/auth constraints. Keep platform-specific permission UX in the bot adapter page for that platform.

## Continuation Permission

`ContinuationPermissionMiddleware` is separate from function permission and is not auto-registered. It asks whether an agent may continue after an iteration limit.

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithMiddleware(new ContinuationPermissionMiddleware(maxIterations: 15))
    .BuildAsync();
```

It emits `ContinuationRequestEvent`, waits for `ContinuationResponseEvent`, and can skip the next model call, override the response, and terminate the run when continuation is denied.

Hosted continuation responses use the same bidirectional response route:

```http
POST /agents/{agentId}/sessions/{sid}/threads/{bid}/responses
```

## What Not To Overclaim

Permissions are a tool/function gate. They do not sandbox the function body, authorize external services, or guarantee that a trusted function is harmless. Put OS, network, tenant, and platform authorization checks in the tool implementation or host environment.

Stored `AlwaysAllow` and `AlwaysDeny` choices are session-scoped. They are not global user preferences, and they are not thread-local.

`[RequiresPermission]` is metadata, not a universal security boundary. It is consumed by permission middleware. A custom `IAgentPermissionMiddleware` can interpret it differently or ignore it entirely.

The snippets above are source-checked example candidates. They have not been clean-compiled in a separate consumer project.

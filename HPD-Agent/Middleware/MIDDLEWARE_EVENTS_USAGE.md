# Middleware Event System Usage Guide

This guide shows how to use the new bidirectional Middleware event system.

## Table of Contents

- [Overview](#overview)
- [Event Interfaces](#event-interfaces) - `IMiddlewareEvent` and `IPermissionEvent`
- [Quick Start](#quick-start) - Get up and running in 5 minutes
- [Handling Events at Different Levels](#handling-events-at-different-levels) - Infrastructure, Domain, Specific
- [When to Create Custom Permission Middlewares](#when-to-create-custom-permission-Middlewares) - **Start here if you need advanced permissions**
- [Custom Event Types](#custom-event-types) - Create your own Middleware and permission events
- [Key Features](#key-features) - Real-time streaming, protocol agnostic, thread-safe
- [Best Practices](#best-practices)
- [Examples](#examples)

## Overview

The Middleware event system allows Middlewares to:
- **Emit one-way events** (progress, errors, custom data)
- **Request/wait for responses** (permissions, approvals, user input)
- **Work with any protocol** (AGUI, Console, Web, etc.)

All events flow through a **shared channel** and are **streamed in real-time** to handlers.

### Quick Decision Guide

**Use the default permission Middleware if:**
-  You need simple approve/deny decisions
-  You're building a console app or simple UI
-  You don't need to modify function arguments
-  Binary permissions are enough

**Create a custom permission Middleware if:**
- 🔧 You need richer decision states (approved with changes, deferred, requires preview)
- 🔧 You need to modify function arguments before execution
- 🔧 You have multi-stage approval workflows
- 🔧 You need enterprise metadata (cost, risk, compliance)
- 🔧 You want risk-based or cost-based auto-decisions

See [When to Create Custom Permission Middlewares](#when-to-create-custom-permission-Middlewares) for details.

## Event Interfaces

The system provides two marker interfaces for categorizing Middleware events:

### `IBidirectionalEvent`
Marker interface for all events supporting bidirectional communication. Allows applications to handle events uniformly for monitoring, logging, and UI routing.

```csharp
public interface IBidirectionalEvent
{
    string SourceName { get; }
}
```

### `IPermissionEvent : IBidirectionalEvent`
Marker interface for permission-related events. A specialized subset of bidirectional events that require user interaction and approval workflows.

```csharp
public interface IPermissionEvent : IBidirectionalEvent
{
    string PermissionId { get; }
}
```

**Event Hierarchy:**
```
AgentEvent (base)
    ↓
IBidirectionalEvent (all bidirectional events)
│   - SourceName: string
│
├── MiddlewareProgressEvent
├── MiddlewareErrorEvent
├── Custom events (user-defined, implement IBidirectionalEvent)
│
└── IPermissionEvent (permission-specific)
    │   - SourceName: string (inherited)
    │   - PermissionId: string
    │
    ├── PermissionRequestEvent
    ├── PermissionResponseEvent
    ├── PermissionApprovedEvent
    ├── PermissionDeniedEvent
    ├── ContinuationRequestEvent
    └── ContinuationResponseEvent
```

---

## Quick Start

### 1. Create a Simple Progress Middleware

```csharp
public class MyProgressMiddleware : IAIFunctionMiddleware
{
    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        // Emit start event
        context.Emit(new MiddlewareProgressEvent(
            "MyProgressMiddleware",
            $"Starting {context.ToolCallRequest.FunctionName}",
            PercentComplete: 0));

        await next(context);

        // Emit completion event
        context.Emit(new MiddlewareProgressEvent(
            "MyProgressMiddleware",
            "Done!",
            PercentComplete: 100));
    }
}
```

### 2. Add Middleware to Agent

```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4", apiKey)
     .WithTool<FileSystemPlugin>()
    .WithMiddleware(new MyProgressMiddleware())  // Add your Middleware!
    .Build();
```

### 3. Handle Events

```csharp
await foreach (var evt in agent.RunStreamingAsync(thread, options))
{
    // Option A: Handle specific event types
    switch (evt)
    {
        case MiddlewareProgressEvent progress:
            Console.WriteLine($"[{progress.SourceName}] {progress.Message}");
            break;

        case TextDeltaEvent text:
            Console.Write(text.Text);
            break;
    }

    // Option B: Handle all Middleware events uniformly
    if (evt is IBidirectionalEvent bidirEvt)
    {
        _MiddlewareMonitor.Track(bidirEvt.SourceName);
    }

    // Option C: Handle permission events uniformly
    if (evt is IPermissionEvent permEvt)
    {
        await _permissionHandler.HandleAsync(permEvt);
    }
}
```

That's it! Events automatically flow from Middlewares to handlers.

## Handling Events at Different Levels

The event system supports three levels of handling:

### Level 1: Infrastructure - All Middleware Events (`IMiddlewareEvent`)

Handle all Middleware events uniformly for monitoring, logging, or UI routing:

```csharp
await foreach (var evt in agent.RunStreamingAsync(...))
{
    if (evt is IMiddlewareEvent MiddlewareEvt)
    {
        // Works for ALL Middleware events (progress, errors, permissions, custom)
        await _MiddlewareMonitor.TrackAsync(MiddlewareEvt.MiddlewareName);
        await _MiddlewareLogger.LogAsync($"[{MiddlewareEvt.MiddlewareName}] {evt.GetType().Name}");
    }
}
```

### Level 2: Domain - Permission Events (`IPermissionEvent`)

Handle all permission events for approval workflows:

```csharp
await foreach (var evt in agent.RunStreamingAsync(...))
{
    if (evt is IPermissionEvent permEvt)
    {
        // Works for ALL permission events (requests, responses, approvals, denials)
        await _auditLog.RecordAsync(permEvt.PermissionId, permEvt.MiddlewareName, evt);
        await _permissionPipeline.ProcessAsync(permEvt);
    }
}
```

### Level 3: Specific - Individual Event Types

Handle specific event types for exact behavior:

```csharp
await foreach (var evt in agent.RunStreamingAsync(...))
{
    switch (evt)
    {
        case PermissionRequestEvent req:
            // Specific handling for permission requests
            await PromptUserAsync(req);
            break;

        case MiddlewareProgressEvent progress:
            // Specific handling for progress
            UpdateProgressBar(progress.PercentComplete);
            break;
    }
}
```

### Combining All Three Levels

```csharp
await foreach (var evt in agent.RunStreamingAsync(...))
{
    // Level 1: Infrastructure (all Middlewares)
    if (evt is IMiddlewareEvent MiddlewareEvt)
    {
        await _MiddlewareMonitor.TrackAsync(MiddlewareEvt.MiddlewareName);
    }

    // Level 2: Domain (permissions)
    if (evt is IPermissionEvent permEvt)
    {
        await _auditLog.RecordAsync(permEvt);
    }

    // Level 3: Specific (individual events)
    switch (evt)
    {
        case PermissionRequestEvent req:
            await PromptUserAsync(req);
            break;
        case TextDeltaEvent text:
            Console.Write(text.Text);
            break;
    }
}
```

---

## Event Types

### One-Way Events (No Response Needed)

#### Progress Events
```csharp
context.Emit(new MiddlewareProgressEvent(
    "MyMiddleware",
    "Processing...",
    PercentComplete: 50));
```

#### Error Events
```csharp
context.Emit(new MiddlewareErrorEvent(
    "MyMiddleware",
    "Something went wrong",
    exception));
```

#### Custom Events
```csharp
// Define your own event type
public record MyCustomEvent(
    string SourceName,
    string CustomData,
    int Count
) : AgentEvent, IBidirectionalEvent;

// Emit it
context.Emit(new MyCustomEvent(
    "MyMiddleware",
    "value",
    42));
```

### Bidirectional Events (Request/Response)

#### Permission Requests
```csharp
var permissionId = Guid.NewGuid().ToString();

// 1. Emit request
context.Emit(new PermissionRequestEvent(
    permissionId,
    sourceName: "MyPermissionMiddleware",
    functionName: "DeleteFile",
    description: "Delete important file",
    callId: "...",
    arguments: context.ToolCallRequest.Arguments));

// 2. Wait for response (blocks Middleware, but events still flow!)
var response = await context.WaitForResponseAsync<PermissionResponseEvent>(
    permissionId,
    timeout: TimeSpan.FromMinutes(5));

// 3. Handle response
if (response.Approved)
{
    await next(context);  // Continue
}
else
{
    context.IsTerminated = true;  // Stop
}
```

---

## Complete Example: Permission Middleware

### Middleware Code

```csharp
public class SimplePermissionMiddleware : IAIFunctionMiddleware
{
    private readonly string _MiddlewareName;

    public SimplePermissionMiddleware(string MiddlewareName = "SimplePermissionMiddleware")
    {
        _MiddlewareName = MiddlewareName;
    }

    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        var permissionId = Guid.NewGuid().ToString();

        // Emit request
        context.Emit(new PermissionRequestEvent(
            permissionId,
            _MiddlewareName,
            context.ToolCallRequest.FunctionName,
            "Permission required",
            callId: "...",
            arguments: context.ToolCallRequest.Arguments));

        // Wait for user response
        try
        {
            var response = await context.WaitForResponseAsync<PermissionResponseEvent>(
                permissionId);

            if (response.Approved)
            {
                context.Emit(new PermissionApprovedEvent(permissionId, _MiddlewareName));
                await next(context);
            }
            else
            {
                context.Emit(new PermissionDeniedEvent(permissionId, _MiddlewareName, "User denied"));
                context.Result = "Permission denied";
                context.IsTerminated = true;
            }
        }
        catch (TimeoutException)
        {
            context.Result = "Permission request timed out";
            context.IsTerminated = true;
        }
    }
}
```

### Handler Code (Console)

```csharp
public async Task RunWithPermissionsAsync(Agent agent)
{
    await foreach (var evt in agent.RunStreamingAsync(thread, options))
    {
        switch (evt)
        {
            case PermissionRequestEvent permReq:
                // Prompt user in background thread
                _ = Task.Run(async () =>
                {
                    Console.WriteLine($"\nPermission required: {permReq.FunctionName}");
                    Console.Write("Allow? (y/n): ");
                    var input = Console.ReadLine();
                    var approved = input?.ToLower() == "y";

                    // Send response back to waiting Middleware
                    agent.SendMiddlewareResponse(permReq.PermissionId,
                        new PermissionResponseEvent(
                            permReq.PermissionId,
                            permReq.MiddlewareName,
                            approved,
                            approved ? null : "User denied",
                            PermissionChoice.Ask));
                });
                break;

            case TextDeltaEvent text:
                Console.Write(text.Text);
                break;
        }
    }
}
```

---

## Custom Event Types

You can create your own event types and implement the marker interfaces for automatic categorization:

### Custom Middleware Events

```csharp
// 1. Define custom event that implements IMiddlewareEvent
public record DatabaseQueryStartEvent(
    string SourceName,
    string QueryId,
    string Query,
    TimeSpan EstimatedDuration) : AgentEvent, IMiddlewareEvent;

// 2. Emit in Middleware
public class DatabaseMiddleware : IAIFunctionMiddleware
{
    private readonly string _MiddlewareName = "DatabaseMiddleware";

    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        var queryId = Guid.NewGuid().ToString();

        context.Emit(new DatabaseQueryStartEvent(
            _MiddlewareName,
            queryId,
            query: "SELECT * FROM users",
            EstimatedDuration: TimeSpan.FromSeconds(2)));

        await next(context);
    }
}

// 3. Handle in event loop
await foreach (var evt in agent.RunStreamingAsync(...))
{
    // Option A: Handle generically as a Middleware event
    if (evt is IMiddlewareEvent MiddlewareEvt)
    {
        _MiddlewareMonitor.Track(MiddlewareEvt.MiddlewareName);  // Works automatically!
    }

    // Option B: Handle specifically
    switch (evt)
    {
        case DatabaseQueryStartEvent dbEvt:
            Console.WriteLine($"[{dbEvt.MiddlewareName}] Query starting: {dbEvt.Query}");
            break;
    }
}
```

### Custom Permission Events

```csharp
// Define rich custom permission event
public record EnterprisePermissionRequestEvent(
    string PermissionId,
    string SourceName,
    string FunctionName,
    IDictionary<string, object?>? Arguments,

    // Custom enterprise fields
    decimal EstimatedCost,
    SecurityLevel SecurityLevel,
    string[] RequiredApprovers
) : AgentEvent, IPermissionEvent;  // ← Implements IPermissionEvent

public record EnterprisePermissionResponseEvent(
    string PermissionId,
    string SourceName,
    bool Approved,

    // Custom enterprise fields
    Guid WorkflowInstanceId,
    string[] ApproverChain
) : AgentEvent, IPermissionEvent;

// Use in custom Middleware
public class EnterprisePermissionMiddleware : IPermissionMiddleware
{
    private readonly string _MiddlewareName = "EnterprisePermissionMiddleware";

    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        var permissionId = Guid.NewGuid().ToString();

        // Emit rich custom event
        context.Emit(new EnterprisePermissionRequestEvent(
            permissionId,
            _MiddlewareName,
            context.ToolCallRequest.FunctionName,
            context.ToolCallRequest.Arguments,
            EstimatedCost: CalculateCost(context),
            SecurityLevel: DetermineSecurityLevel(context),
            RequiredApprovers: new[] { "manager@company.com" }));

        var response = await context.WaitForResponseAsync<EnterprisePermissionResponseEvent>(
            permissionId);

        if (response.Approved)
            await next(context);
        else
            context.IsTerminated = true;
    }
}

// Handle in application
await foreach (var evt in agent.RunStreamingAsync(...))
{
    // Infrastructure: ALL permission events (built-in AND custom)
    if (evt is IPermissionEvent permEvt)
    {
        await _auditLog.RecordAsync(permEvt.PermissionId, permEvt.MiddlewareName, evt);
    }

    // Specific: Handle your custom event
    switch (evt)
    {
        case EnterprisePermissionRequestEvent enterpriseReq:
            // Show rich UI with cost, security level, approvers
            await ShowEnterprisePermissionUI(enterpriseReq);
            break;
    }
}
```

---

## When to Create Custom Permission Middlewares

### Default Permission Middleware: Good for Most Use Cases

The built-in `PermissionMiddleware` is perfect for 90% of applications:

```csharp
var agent = new AgentBuilder()
    .WithPermissions()  // Uses default PermissionMiddleware
    .Build();

// Simple binary decisions: Allow or Deny
case PermissionRequestEvent req:
    var approved = Console.ReadLine()?.ToLower() == "y";
    agent.SendMiddlewareResponse(req.PermissionId,
        new PermissionResponseEvent(
            req.PermissionId,
            req.MiddlewareName,
            approved));
```

**Use default when you need:**
-  Simple approve/deny decisions
-  Basic permission storage (always allow, always deny, ask)
-  Console or simple UI prompts
-  Low complexity permission logic

### Custom Permission Middlewares: For Advanced Scenarios

Create a custom permission Middleware when you need:

#### **1. Richer Decision States**

The default has binary `Approved` (true/false). You might need:

```csharp
public enum RichDecisionType
{
    Approved,              // Yes, execute as-is
    ApprovedWithChanges,   // Yes, but modify function arguments
    Denied,                // No, don't execute
    Deferred,              // Send to approval workflow, ask later
    RequiresPreview,       // Show preview first, then ask again
    PartiallyApproved      // Approve some operations, deny others
}
```

#### **2. Parameter Modification**

User sees: `DeleteFile("/important/data.txt")`
User wants: "Yes, but move to trash instead of permanent delete"

```csharp
// Custom response event with modified arguments
new RichPermissionResponseEvent(
    permissionId,
    MiddlewareName,
    Decision: RichDecisionType.ApprovedWithChanges,
    ModifiedArguments: new Dictionary<string, object?>
    {
        ["filePath"] = "/trash/data.txt",
        ["permanent"] = false  // Changed from true
    });

// Middleware applies changes before execution
if (response.Decision == RichDecisionType.ApprovedWithChanges)
{
    foreach (var (key, value) in response.ModifiedArguments ?? new Dictionary<string, object?>())
    {
        context.ToolCallRequest.Arguments[key] = value;
    }
    await next(context);
}
```

#### **3. Multi-Stage Approval Workflows**

```csharp
// Stage 1: Request permission
context.Emit(new WorkflowPermissionRequestEvent(
    permissionId,
    MiddlewareName,
    functionName,
    RequiredApprovers: new[] { "manager@company.com" },
    WorkflowType: WorkflowType.ManagerApproval));

// Stage 2: Deferred to workflow
var response = await context.WaitForResponseAsync<WorkflowPermissionResponseEvent>(...);
if (response.Decision == WorkflowDecision.Deferred)
{
    // Workflow engine processes approval
    // User gets notification later when approved
    context.Result = $"Sent to approval workflow {response.WorkflowId}";
    context.IsTerminated = true;
}
```

#### **4. Rich Metadata**

Attach enterprise data to permission events:

```csharp
public record EnterprisePermissionRequestEvent(
    string PermissionId,
    string SourceName,
    string FunctionName,
    IDictionary<string, object?>? Arguments,

    // Enterprise-specific metadata
    decimal EstimatedCost,
    RiskLevel RiskLevel,
    string[] RequiredApprovers,
    ComplianceRequirement[] ComplianceFlags,
    string DepartmentId,
    string ProjectId
) : AgentEvent, IPermissionEvent;
```

#### **5. Cost-Based or Risk-Based Auto-Decisions**

```csharp
public class RiskBasedPermissionMiddleware : IPermissionMiddleware
{
    public async Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next)
    {
        var riskScore = CalculateRisk(
            context.ToolCallRequest.FunctionName,
            context.ToolCallRequest.Arguments);

        if (riskScore < 30)
        {
            // Low risk - auto-approve
            await next(context);
        }
        else if (riskScore < 70)
        {
            // Medium risk - ask user
            var approved = await AskUserAsync(context);
            if (approved) await next(context);
        }
        else
        {
            // High risk - auto-deny
            context.Result = "Operation blocked: too risky";
            context.IsTerminated = true;
        }
    }
}
```

### Benefits of `IPermissionEvent` for Custom Middlewares

When you create custom permission events that implement `IPermissionEvent`, you **automatically benefit** from application infrastructure:

```csharp
// Application handles ALL permission events uniformly
await foreach (var evt in agent.RunStreamingAsync(...))
{
    // Works for built-in AND custom permission events!
    if (evt is IPermissionEvent permEvt)
    {
        // Audit logging
        await _auditLog.RecordAsync(permEvt.PermissionId, permEvt.MiddlewareName, evt);

        // Compliance validation
        await _complianceChecker.ValidateAsync(permEvt);

        // Metrics tracking
        await _metrics.TrackPermissionAsync(permEvt.MiddlewareName);
    }

    // Then handle your specific custom event
    switch (evt)
    {
        case EnterprisePermissionRequestEvent enterpriseReq:
            await ShowEnterpriseUI(enterpriseReq);
            break;
    }
}
```

**No need to duplicate infrastructure for each custom Middleware!**

### Complete Custom Permission Middleware Example

See the [Custom Permission Events](#custom-permission-events) section for a full working example.

---

## Key Features

###  Real-Time Streaming
Events are visible to handlers **WHILE** Middlewares are executing (not after):

```
Timeline:
T0: Middleware emits permission request → Shared channel
T1: Background drainer reads → Event queue
T2: Main loop yields → Handler receives event
T3: Middleware still blocked waiting ← HANDLER CAN RESPOND!
T4: Handler sends response
T5: Middleware receives response and unblocks
```

###  Zero Dependencies
Middlewares don't need dependency injection:

```csharp
// No constructor parameters needed!
public class MyMiddleware : IAIFunctionMiddleware
{
    public async Task InvokeAsync(AiFunctionContext context, ...)
    {
        context.Emit(event);  // Just works!
    }
}
```

###  Protocol Agnostic
One Middleware works with **all** protocols:

```csharp
// Same Middleware used by:
// - AGUI (web UI)
// - Console app
// - Discord bot
// - Web API
// - Any future protocol!
```

###  Thread-Safe
Multiple Middlewares can emit events concurrently:

```csharp
// Middleware pipeline:
Middleware1.Emit(Event1)
  → calls Middleware2
     → Middleware2.Emit(Event2)  // Concurrent!

// Both events flow through shared channel safely
```

---

## Best Practices

### 1. Always Emit Observability Events
```csharp
context.Emit(new MiddlewareProgressEvent("MyMiddleware", "Starting"));
await next(context);
context.Emit(new MiddlewareProgressEvent("MyMiddleware", "Done"));
```

### 2. Handle Timeouts
```csharp
try
{
    var response = await context.WaitForResponseAsync<T>(id, TimeSpan.FromMinutes(5));
}
catch (TimeoutException)
{
    context.Result = "Timeout";
    context.IsTerminated = true;
}
```

### 3. Use Unique Request IDs
```csharp
var requestId = Guid.NewGuid().ToString();  // Always unique!
```

### 4. Emit Result Events
```csharp
if (response.Approved)
{
    context.Emit(new PermissionApprovedEvent(permissionId));
}
else
{
    context.Emit(new PermissionDeniedEvent(permissionId, reason));
}
```

---

## Performance

- **Memory**: ~16 bytes per function call (vs ~400 bytes with Task.Run)
- **CPU**: ~50ns per event (negligible vs 500ms-2s LLM calls)
- **Concurrency**: Background drainer handles all events efficiently

---

## Migration Guide

### Old Way (Custom Interfaces)
```csharp
public class OldMiddleware
{
    private readonly IPermissionEventEmitter _emitter;

    public OldMiddleware(IPermissionEventEmitter emitter)  // Requires DI!
    {
        _emitter = emitter;
    }
}
```

### New Way (Standardized)
```csharp
public class NewMiddleware : IAIFunctionMiddleware
{
    // No dependencies!

    public async Task InvokeAsync(AiFunctionContext context, ...)
    {
        context.Emit(new PermissionRequestEvent(...));
        var response = await context.WaitForResponseAsync<T>(...);
    }
}
```

---

## Examples

See `ExampleMiddlewares.cs` for:
- `ProgressLoggingMiddleware` - Simple one-way events
- `CostTrackingMiddleware` - Custom event types
- `SimplePermissionMiddleware` - Bidirectional request/response

Happy Middlewareing! 🎉
ß
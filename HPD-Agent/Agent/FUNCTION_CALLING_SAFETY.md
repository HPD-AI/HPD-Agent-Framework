# Function Calling Safety Architecture

## Overview

HPD-Agent implements a **multi-layered safety system** for function calling, inspired by production implementations from Microsoft.Extensions.AI, Google's Gemini CLI, and lessons learned from building robust agentic systems. This document explains all the mechanisms, their purposes, and the nuances considered in their design.

---

## Table of Contents

1. [Safety Mechanisms Overview](#safety-mechanisms-overview)
2. [Parallel Function Calling Architecture](#parallel-function-calling-architecture)
3. [Circuit Breaker (Function Signature Tracking)](#circuit-breaker-function-signature-tracking)
4. [Turn-Level Error Tracking](#turn-level-error-tracking)
5. [Function-Level Retry Logic](#function-level-retry-logic)
6. [Turn Timeout](#turn-timeout)
7. [How They Work Together](#how-they-work-together)
8. [Design Decisions & Nuances](#design-decisions--nuances)
9. [Configuration Reference](#configuration-reference)

---

## Safety Mechanisms Overview

| Mechanism | Purpose | Scope | Inspiration |
|-----------|---------|-------|-------------|
| **Circuit Breaker** | Prevents infinite loops from same function+args | Per-signature across iterations | Gemini CLI |
| **Error Tracking** | Stops after N consecutive errors | Across iterations | M.E.AI |
| **Retry Logic** | Handles transient failures | Per function execution | M.E.AI |
| **Function Timeout** | Prevents hanging functions | Per function execution | Industry standard |
| **Turn Timeout** | Prevents infinite agentic loops | Per turn | HPD-Agent design |

**Key Insight**: Each mechanism operates at a different level and solves different problems. They complement, not duplicate, each other.

---

## Parallel Function Calling Architecture

### Two-Phase Execution

Inspired by Gemini CLI's `CoreToolScheduler`, we use a **two-phase** approach for parallel function calling:

```csharp
// PHASE 1: Permission checking (SEQUENTIAL to prevent race conditions)
foreach (var toolRequest in toolRequests)
{
    var approved = await CheckPermissionAsync(toolRequest, ...);
    if (approved) approvedTools.Add(toolRequest);
    else deniedTools.Add(toolRequest);
}

// PHASE 2: Execution (PARALLEL for approved tools)
var executionTasks = approvedTools.Select(async toolRequest => {
    return await ProcessFunctionCallAsync(...);
}).ToArray();

var results = await Task.WhenAll(executionTasks);
```

### Why This Matters

**Permission filters** need sequential execution because:
- They may interact with shared state (e.g., approval UI)
- Prompting user for approval on 3 tools at once would be confusing
- Deduplication logic prevents asking twice for the same tool

**Function execution** benefits from parallel execution because:
- Independent I/O operations (file reads, API calls) can overlap
- Reduces total turn latency significantly
- Safe because each function operates on isolated context

### Fallback to Sequential

Single tool calls use sequential execution to avoid parallelization overhead:

```csharp
if (toolRequests.Count <= 1)
{
    return await ExecuteSequentiallyAsync(...);
}
```

**Related Files**:
- `Agent.cs` (lines 1781-1942): ToolScheduler implementation
- `Agent.cs` (lines 1232-1262): FunctionCallProcessor permission checking

---

## Circuit Breaker (Function Signature Tracking)

### Problem Statement

Agents can get stuck calling the **same function with the same arguments** repeatedly:

```
Iteration 1: ReadFile("/missing.txt") → Error: File not found
Iteration 2: ReadFile("/missing.txt") → Error: File not found
Iteration 3: ReadFile("/missing.txt") → Error: File not found
...
∞
```

This is different from:
```
Iteration 1: ReadFile("/file1.txt") → Success
Iteration 2: ReadFile("/file2.txt") → Success
Iteration 3: ReadFile("/file3.txt") → Success
✓ Different arguments = forward progress
```

### Implementation (Gemini CLI-Inspired)

**Function Signature Generation** (Agent.cs:1074-1088):
```csharp
private static string GetFunctionSignature(FunctionCallContent toolCall)
{
    // Serialize arguments to JSON
    var argsJson = JsonSerializer.Serialize(
        toolCall.Arguments ?? new Dictionary<string, object?>(),
        AGUIJsonContext.Default.DictionaryStringObject);

    // Combine name + args
    var combined = $"{toolCall.Name}:{argsJson}";

    // SHA256 hash for efficient comparison
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
    return Convert.ToHexString(hashBytes);
}
```

**Tracking Logic** (Agent.cs:599-635):
```csharp
string? lastFunctionSignature = null;
int consecutiveSameSignatureCount = 0;

// Check each tool request (handles parallel calls)
foreach (var toolRequest in toolRequests)
{
    var currentSignature = GetFunctionSignature(toolRequest);

    if (currentSignature == lastFunctionSignature)
    {
        consecutiveSameSignatureCount++;
    }
    else
    {
        lastFunctionSignature = currentSignature;
        consecutiveSameSignatureCount = 1;
    }

    if (consecutiveSameSignatureCount >= maxConsecutive)
    {
        // Circuit breaker triggered!
        break;
    }
}
```

### Parallel Handling Nuance

**Question**: What does "consecutive" mean when parallel calling is enabled?

**Example**:
```
Turn 1: [ReadFile("a.txt"), WriteFile("b.txt")]
Turn 2: [ReadFile("a.txt"), WriteFile("c.txt")]
Turn 3: [ReadFile("a.txt"), WriteFile("d.txt")]
```

**Our Approach**: Process each function in the batch sequentially through the circuit breaker check:
- Turn 1: ReadFile("a.txt") = signature A (count = 1), WriteFile("b.txt") = signature B (count = 1)
- Turn 2: ReadFile("a.txt") = signature A (count = 2), WriteFile("c.txt") = signature C (count = 1)
- Turn 3: ReadFile("a.txt") = signature A (count = 3), ...

This means **within a turn**, we check each function individually against the last seen signature.

**Alternative Considered**: Count entire batch as one "call" → Rejected because it doesn't detect spamming a single function within parallel batches.

### Why Not Just Track Function Name?

**Bad Scenario 1**: Different arguments = legitimate use
```
ReadFile("file1.txt") → Success
ReadFile("file2.txt") → Success
ReadFile("file3.txt") → Success
...
ReadFile("file50.txt") → Success
✓ This is fine! Batch processing 50 files.
```

If we only tracked name, this would trigger after 5 calls. **Wrong!**

**Bad Scenario 2**: Same arguments = stuck loop
```
ReadFile("missing.txt") → Error
ReadFile("missing.txt") → Error
✗ This is broken! Agent isn't learning.
```

**Solution**: Track name + arguments hash → Only breaks on exact repetition.

### Research Validation

**Microsoft.Extensions.AI** (FunctionInvokingChatClient.cs):
- ❌ Does NOT have circuit breaker for function spam
- ✅ Only has `MaximumIterationsPerRequest = 40` (total iteration limit)

**Gemini CLI** (loopDetectionService.ts:113-191):
- ✅ Tracks function signature via SHA256 hash
- ✅ Threshold: `TOOL_CALL_LOOP_THRESHOLD = 5`
- ✅ Exactly matches our implementation!

**Google's production agent uses this exact approach.**

---

## Turn-Level Error Tracking

### Problem Statement

Agents can get stuck in error loops where **different functions keep failing**:

```
Iteration 1: GetWeather() → Error: API unavailable
Iteration 2: GetNews() → Error: API unavailable
Iteration 3: GetStocks() → Error: API unavailable
...
```

Even though functions are different, the turn is clearly not making progress.

### Implementation

**Tracking** (Agent.cs:637-662 + AgentRunContext in AiFunctionOrchestrationContext.cs:180-204):

```csharp
// In AgentRunContext
private int _consecutiveErrorCount = 0;

public void RecordError() => _consecutiveErrorCount++;
public void RecordSuccess() => _consecutiveErrorCount = 0;
public bool HasExceededErrorLimit(int maxErrors) => _consecutiveErrorCount >= maxErrors;
```

**Enforcement** (Agent.cs:637-662):
```csharp
if (hasErrors)
{
    agentRunContext.RecordError();

    var maxConsecutiveErrors = Config?.ErrorHandling?.MaxRetries ?? 3;
    if (agentRunContext.HasExceededErrorLimit(maxConsecutiveErrors))
    {
        yield return CreateTextMessageContent(
            $"⚠️ Maximum consecutive errors ({maxConsecutiveErrors}) exceeded.");

        agentRunContext.IsTerminated = true;
        break;
    }
}
else
{
    agentRunContext.RecordSuccess();  // Reset on success!
}
```

### Key Design Decision: Reset on Success

**Why**: One successful iteration proves the agent isn't completely stuck.

**Example**:
```
Iteration 1: ReadFile() → Error (count = 1)
Iteration 2: WriteFile() → Success (count = 0 ← RESET)
Iteration 3: DeleteFile() → Error (count = 1)
Iteration 4: ReadFile() → Error (count = 2)
```

This allows the agent to recover from temporary issues while still stopping infinite error loops.

### Comparison to Circuit Breaker

| Mechanism | Tracks | Resets on |
|-----------|--------|-----------|
| **Circuit Breaker** | Exact function signature | Different signature |
| **Error Tracking** | Any error | Any success |

**Example where both trigger**:
```
ReadFile("missing.txt") → Error
ReadFile("missing.txt") → Error
ReadFile("missing.txt") → Error
```
- Circuit breaker: Detects same function+args (count = 3)
- Error tracking: Detects consecutive errors (count = 3)
- **Circuit breaker triggers first** (more specific)

**Example where only error tracking triggers**:
```
ReadFile("file1.txt") → Error
WriteFile("file2.txt") → Error
DeleteFile("file3.txt") → Error
```
- Circuit breaker: Different signatures, no trigger
- Error tracking: 3 consecutive errors → **STOP**

**They solve different problems!**

---

## Function-Level Retry Logic

### Problem Statement

**Transient failures** should be retried, not immediately reported as errors:

- Network timeout (temporary)
- Rate limit (429 - wait and retry)
- Service unavailable (503 - temporary)
- File locked (retry after delay)

**Permanent failures** should fail immediately:
- File not found (404)
- Permission denied (403)
- Invalid arguments (400)

### Implementation

**Retry with Exponential Backoff** (Agent.cs:1354-1399):

```csharp
private async Task ExecuteWithRetryAsync(AiFunctionContext context, CancellationToken ct)
{
    var maxRetries = _errorHandlingConfig?.MaxRetries ?? 3;
    var retryDelay = _errorHandlingConfig?.RetryDelay ?? TimeSpan.FromSeconds(1);
    var functionTimeout = _errorHandlingConfig?.SingleFunctionTimeout;

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try
        {
            // Create timeout for THIS attempt
            using var functionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (functionTimeout.HasValue)
            {
                functionCts.CancelAfter(functionTimeout.Value);
            }

            var result = await context.Function!.InvokeAsync(args, functionCts.Token);
            return; // ✅ Success - exit retry loop
        }
        catch (OperationCanceledException) when (functionTimeout.HasValue && !ct.IsCancellationRequested)
        {
            // Function timeout (not user cancellation)
            context.Result = $"Function timed out after {functionTimeout.Value.TotalSeconds}s";
            return;
        }
        catch (Exception ex)
        {
            if (attempt >= maxRetries)
            {
                context.Result = $"Error after {maxRetries + 1} attempts: {ex.Message}";
                return;
            }

            // Exponential backoff: 1s, 2s, 3s, ...
            var delay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * (attempt + 1));
            await Task.Delay(delay, ct);
        }
    }
}
```

### Exponential Backoff Strategy

**Why not fixed delay?**
- Fixed: `1s → 1s → 1s` (might not be enough time for recovery)
- Exponential: `1s → 2s → 3s` (gives system time to recover)

**Formula**: `delay = baseDelay × (attempt + 1)`

**Example with `RetryDelay = 1s`, `MaxRetries = 3`**:
```
Attempt 0: Execute
  ↓ Fail
  ↓ Wait 1s
Attempt 1: Execute
  ↓ Fail
  ↓ Wait 2s
Attempt 2: Execute
  ↓ Fail
  ↓ Wait 3s
Attempt 3: Execute
  ↓ Fail → Give up
```

### Function Timeout vs Retry

**Question**: Why have both?

**Answer**: Different purposes!

**Function Timeout** (30s default):
- Prevents **individual execution** from hanging
- Applies to **each retry attempt**
- Example: HTTP request hangs for 30s → timeout → retry

**Retry Logic** (3 attempts default):
- Gives functions **multiple chances**
- Example: API returns 503 → wait 1s → retry → success

**Combined Example**:
```
Attempt 1: API call → hangs for 30s → timeout → retry
  ↓ Wait 1s
Attempt 2: API call → 503 Service Unavailable → retry
  ↓ Wait 2s
Attempt 3: API call → 200 OK → ✅ Success!
```

### Relationship to Turn-Level Error Tracking

**Important**: Retry logic happens **before** error tracking sees the result.

**Scenario 1: Retry succeeds**
```
Function execution:
  Attempt 1 → Fail (internal)
  Attempt 2 → Success ✅

Turn-level error tracking:
  Sees: Success
  Action: RecordSuccess(), reset error count
```

**Scenario 2: All retries fail**
```
Function execution:
  Attempt 1 → Fail (internal)
  Attempt 2 → Fail (internal)
  Attempt 3 → Fail (internal)
  Result: Error after 4 attempts ❌

Turn-level error tracking:
  Sees: Error
  Action: RecordError(), increment count
```

**Benefit**: Transient failures don't pollute error tracking!

---

## Turn Timeout

### Problem Statement

The entire agentic loop could run indefinitely if:
- Circuit breaker doesn't trigger (different functions each time)
- Error tracking doesn't trigger (some successes mixed in)
- Agent keeps making "progress" but never completes

**Example**:
```
Iteration 1-10: Various file operations (all succeed)
Iteration 11-20: Various API calls (all succeed)
Iteration 21-30: Various calculations (all succeed)
...
Iteration 500: Still going...
```

### Implementation

**Linked Cancellation Token** (Agent.cs:369-376):

```csharp
// Create linked cancellation token for turn timeout
using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
if (Config?.AgenticLoop?.MaxTurnDuration is { } turnTimeout)
{
    turnCts.CancelAfter(turnTimeout);
}
var effectiveCancellationToken = turnCts.Token;

// Use effectiveCancellationToken for all operations in this turn
```

**Propagation**:
```csharp
await _messageProcessor.PrepareMessagesAsync(..., effectiveCancellationToken);
await _agentTurn.RunAsync(..., effectiveCancellationToken);
await _toolScheduler.ExecuteToolsAsync(..., effectiveCancellationToken);
```

### Why Linked Cancellation Token?

**Problem**: User might provide their own `CancellationToken` (e.g., HTTP request timeout).

**Solution**: Link both tokens so **either** one can cancel:
- User cancels → Entire operation stops
- Turn timeout expires → Only this turn stops

```csharp
var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
//                                                            ↑
//                                                    User's token
turnCts.CancelAfter(TimeSpan.FromMinutes(5));  // Our timeout
```

### Default: 5 Minutes

**Why 5 minutes?**
- Most legitimate agentic tasks complete in < 1 minute
- Complex tasks (large file processing) might need 2-3 minutes
- 5 minutes is generous buffer
- Prevents runaway loops from consuming resources indefinitely

**Configurable** via `AgenticLoopConfig.MaxTurnDuration`.

---

## How They Work Together

### Execution Flow with All Safety Mechanisms

```
┌─────────────────────────────────────────────┐
│         Turn Starts (t=0)                   │
│  ┌─────────────────────────────────────┐   │
│  │ Turn Timeout: 5 minutes             │   │
│  └─────────────────────────────────────┘   │
└─────────────────────────────────────────────┘
              ↓
┌─────────────────────────────────────────────┐
│         Iteration 1                         │
├─────────────────────────────────────────────┤
│  LLM Call → Tool Requests: [ReadFile(...)] │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Circuit Breaker Check              │    │
│  │ ReadFile("data.txt") → sig: ABC123 │    │
│  │ Last sig: null                     │    │
│  │ Count: 1                           │    │
│  │ Status: ✓ Pass                     │    │
│  └────────────────────────────────────┘    │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Function Execution                 │    │
│  │                                    │    │
│  │ Attempt 1: ┌──────────────────┐   │    │
│  │            │ Timeout: 30s     │   │    │
│  │            │ Status: Success  │   │    │
│  │            └──────────────────┘   │    │
│  │ Result: ✓ File contents           │    │
│  └────────────────────────────────────┘    │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Error Tracking                     │    │
│  │ Result: Success                    │    │
│  │ Action: RecordSuccess()            │    │
│  │ Error count: 0                     │    │
│  └────────────────────────────────────┘    │
└─────────────────────────────────────────────┘
              ↓
┌─────────────────────────────────────────────┐
│         Iteration 2                         │
├─────────────────────────────────────────────┤
│  LLM Call → Tool Requests: [ReadFile(...)] │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Circuit Breaker Check              │    │
│  │ ReadFile("data.txt") → sig: ABC123 │    │
│  │ Last sig: ABC123                   │    │
│  │ Count: 2 ⚠️                        │    │
│  │ Status: ✓ Pass (threshold: 5)     │    │
│  └────────────────────────────────────┘    │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Function Execution                 │    │
│  │                                    │    │
│  │ Attempt 1: ┌──────────────────┐   │    │
│  │            │ Timeout: 30s     │   │    │
│  │            │ Status: Timeout  │   │    │
│  │            └──────────────────┘   │    │
│  │ Wait: 1s                           │    │
│  │ Attempt 2: ┌──────────────────┐   │    │
│  │            │ Timeout: 30s     │   │    │
│  │            │ Status: Success  │   │    │
│  │            └──────────────────┘   │    │
│  │ Result: ✓ File contents           │    │
│  └────────────────────────────────────┘    │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Error Tracking                     │    │
│  │ Result: Success (retry succeeded!) │    │
│  │ Action: RecordSuccess()            │    │
│  │ Error count: 0                     │    │
│  └────────────────────────────────────┘    │
└─────────────────────────────────────────────┘
              ↓
           ... (more iterations)
              ↓
┌─────────────────────────────────────────────┐
│         Iteration 5                         │
├─────────────────────────────────────────────┤
│  LLM Call → Tool Requests: [ReadFile(...)] │
│                                             │
│  ┌────────────────────────────────────┐    │
│  │ Circuit Breaker Check              │    │
│  │ ReadFile("data.txt") → sig: ABC123 │    │
│  │ Last sig: ABC123                   │    │
│  │ Count: 5 ❌                        │    │
│  │ Status: ✗ CIRCUIT BREAKER!         │    │
│  └────────────────────────────────────┘    │
│                                             │
│  Termination: Agent stuck in loop          │
│  Message: "Circuit breaker triggered..."   │
└─────────────────────────────────────────────┘
```

### Safety Net Layering

**Level 1: Function-Level** (Fastest)
- ⏱️ Function Timeout (30s): Catches hanging I/O
- 🔄 Retry Logic (3 attempts): Handles transient failures

**Level 2: Turn-Level** (Per Iteration)
- 🛑 Circuit Breaker (5 calls): Catches signature loops
- ❌ Error Tracking (3 errors): Catches error spirals

**Level 3: Turn-Level** (Entire Agentic Loop)
- ⏰ Turn Timeout (5 min): Ultimate safety net

**Philosophy**: Defense in depth. Multiple independent safety mechanisms that complement each other.

---

## Design Decisions & Nuances

### 1. Why SHA256 for Function Signature?

**Alternatives Considered**:

**Option A: String concatenation**
```csharp
var signature = $"{toolCall.Name}:{JsonSerializer.Serialize(toolCall.Arguments)}";
```
- ✅ Simple
- ❌ Long strings (1KB+ for complex args)
- ❌ Expensive string comparisons
- ❌ Memory inefficient

**Option B: SHA256 Hash** (Chosen)
```csharp
var hash = SHA256.Hash($"{name}:{argsJson}");
```
- ✅ Fixed 64-character hex string
- ✅ Fast comparison (string equality)
- ✅ Memory efficient
- ✅ Collision probability: ~0% for practical use
- ✅ **Matches Gemini CLI implementation**

**Decision**: Use SHA256 like Google does.

### 2. Why Reset Error Count on Success?

**Alternative**: Never reset, track total error count.

**Problem**:
```
Iteration 1: Success
Iteration 2: Error (count = 1)
Iteration 3: Success
Iteration 4: Error (count = 2)
...
Iteration 50: Error (count = 25)
❌ Stops after 25 errors spread over 50 successful iterations
```

This would stop agents that are **mostly succeeding** but occasionally failing.

**Our Approach**: Track **consecutive** errors.
```
Iteration 1: Success
Iteration 2: Error (count = 1)
Iteration 3: Success (count = 0 ← RESET)
Iteration 4: Error (count = 1)
...
✓ Never stops because errors aren't consecutive
```

**Rationale**: Consecutive errors indicate the agent is stuck. Sporadic errors are normal.

### 3. Why Check Each Function in Parallel Batch?

**Alternative**: Check only the first function `toolRequests[0]`.

**Problem**:
```csharp
// Turn 1
toolRequests = [ReadFile("a.txt"), SpamFunction("x")]

// Turn 2
toolRequests = [ReadFile("b.txt"), SpamFunction("x")]

// Turn 3
toolRequests = [ReadFile("c.txt"), SpamFunction("x")]
```

If we only check `[0]`, we'd see:
- Turn 1: ReadFile("a.txt") → count = 1
- Turn 2: ReadFile("b.txt") → different sig → count = 1
- Turn 3: ReadFile("c.txt") → different sig → count = 1

**SpamFunction spamming would never be detected!**

**Our Approach**: Check **all** functions in the batch.
```csharp
foreach (var toolRequest in toolRequests)
{
    var sig = GetFunctionSignature(toolRequest);
    // Check against last signature
}
```

Now we detect:
- Turn 1: ReadFile("a.txt") → count = 1, SpamFunction("x") → count = 1
- Turn 2: ReadFile("b.txt") → count = 1, SpamFunction("x") → count = 2
- Turn 3: ReadFile("c.txt") → count = 1, SpamFunction("x") → count = 3
- ...
- Turn 5: Circuit breaker on SpamFunction! ✅

### 4. Why Separate Retry Config from Error Tracking Config?

Both live in `ErrorHandlingConfig`, but serve different purposes:

```csharp
public class ErrorHandlingConfig
{
    // Turn-level error tracking
    public int MaxRetries { get; set; } = 3;  // Consecutive errors

    // Function-level retry logic
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan? SingleFunctionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

**Naming Overload**: `MaxRetries` is used for **both**:
- Turn-level: "Stop after 3 consecutive errors"
- Function-level: "Retry each function up to 3 times"

**Why Same Name?**
- Semantic consistency: Both represent "how many times to try before giving up"
- Configuration simplicity: One value controls both policies
- Historical: Microsoft.Extensions.AI uses `MaximumConsecutiveErrorsPerRequest` for the same dual purpose

**Could We Split Them?**
```csharp
public int MaxConsecutiveErrors { get; set; } = 3;  // Turn-level
public int MaxFunctionRetries { get; set; } = 3;    // Function-level
```

Yes, but we chose simplicity. Most users want the same threshold for both.

### 5. Why Linked Cancellation Token Instead of Timeout Wrapper?

**Alternative**: Wrap execution in timeout task:
```csharp
var task = RunAgenticLoopCore(...);
var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
await Task.WhenAny(task, timeoutTask);
```

**Problems**:
- Task continues running after timeout (resource leak)
- Doesn't propagate cancellation to child operations
- Can't distinguish user cancellation from timeout

**Our Approach**: Linked cancellation token
```csharp
using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
turnCts.CancelAfter(timeout);
```

**Benefits**:
- ✅ Propagates to all child operations
- ✅ Properly disposes resources
- ✅ Can check `cancellationToken.IsCancellationRequested` to distinguish user cancel vs timeout
- ✅ Standard .NET pattern

### 6. Why Default 5 Consecutive Calls for Circuit Breaker?

**Research**:
- Gemini CLI: `TOOL_CALL_LOOP_THRESHOLD = 5`
- Microsoft.Extensions.AI: No circuit breaker (only total iteration limit of 40)

**Rationale**:
- **Too Low (2-3)**: False positives. Legitimate retry patterns like "try primary API → try fallback API → success" would trigger.
- **Too High (10+)**: Agent wastes time/resources on 10 identical failed calls before stopping.
- **5**: Sweet spot. Enough to allow legitimate retries, but catches infinite loops quickly.

**Example Legitimate Use Case**:
```
Attempt 1: GetWeather("SF", provider="primary") → 503
Attempt 2: GetWeather("SF", provider="fallback") → Different signature, count = 1
Attempt 3: GetWeather("SF", provider="fallback") → 503, count = 2
Attempt 4: GetWeather("SF", provider="fallback") → 200 ✓
```

With threshold 5, this succeeds. With threshold 2, this would wrongly trigger.

### 7. Why AOT-Friendly JSON Serialization?

**Original Implementation**:
```csharp
JsonSerializer.Serialize(args, new JsonSerializerOptions { ... });
```

**Build Warning**:
```
warning IL2026: JSON serialization might require types that cannot be statically analyzed
warning IL3050: Might need runtime code generation for AOT
```

**Fixed Version**:
```csharp
JsonSerializer.Serialize(args, AGUIJsonContext.Default.DictionaryStringObject);
```

**Why?**
- HPD-Agent targets AOT (Ahead-of-Time compilation) for performance
- Source generators create serialization code at compile time
- No runtime reflection needed
- Smaller binary, faster startup, better trimming

**Trade-off**: Requires pre-registered types in `AGUIJsonContext`, but we already have `DictionaryStringObject` registered for tool arguments.

---

## Configuration Reference

### AgenticLoopConfig

**Location**: `AgentConfig.AgenticLoop`

```csharp
public class AgenticLoopConfig
{
    /// <summary>
    /// Maximum duration for a single turn before timeout.
    /// Default: 5 minutes
    /// </summary>
    public TimeSpan? MaxTurnDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of consecutive calls to the same function with same arguments
    /// before circuit breaker triggers.
    /// Default: 5
    /// Inspired by: Gemini CLI (TOOL_CALL_LOOP_THRESHOLD)
    /// </summary>
    public int? MaxConsecutiveFunctionCalls { get; set; } = 5;
}
```

**Example**:
```csharp
var config = new AgentConfig
{
    AgenticLoop = new AgenticLoopConfig
    {
        MaxTurnDuration = TimeSpan.FromMinutes(10),  // Allow longer turns
        MaxConsecutiveFunctionCalls = 3          // More aggressive circuit breaker
    }
};
```

### ErrorHandlingConfig

**Location**: `AgentConfig.ErrorHandling`

```csharp
public class ErrorHandlingConfig
{
    /// <summary>
    /// Whether to normalize provider errors to standard format.
    /// Default: true
    /// </summary>
    public bool NormalizeErrors { get; set; } = true;

    /// <summary>
    /// Whether to include provider-specific details in error messages.
    /// Default: false
    /// </summary>
    public bool IncludeProviderDetails { get; set; } = false;

    /// <summary>
    /// Maximum number of consecutive errors before stopping.
    /// Also used as max retry attempts for individual function calls.
    /// Default: 3
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Timeout for individual function execution attempts.
    /// Default: 30 seconds
    /// </summary>
    public TimeSpan? SingleFunctionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Base delay between retry attempts. Uses exponential backoff.
    /// Default: 1 second
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
```

**Example**:
```csharp
var config = new AgentConfig
{
    ErrorHandling = new ErrorHandlingConfig
    {
        MaxRetries = 5,                                      // More retries
        SingleFunctionTimeout = TimeSpan.FromMinutes(1),     // Longer timeout
        RetryDelay = TimeSpan.FromMilliseconds(500)          // Faster retries
    }
};
```

### Recommended Configurations

**Development** (Fail Fast):
```csharp
new AgentConfig
{
    AgenticLoop = new()
    {
        MaxTurnDuration = TimeSpan.FromMinutes(2),
        MaxConsecutiveFunctionCalls = 3
    },
    ErrorHandling = new()
    {
        MaxRetries = 2,
        SingleFunctionTimeout = TimeSpan.FromSeconds(10)
    }
}
```

**Production** (Resilient):
```csharp
new AgentConfig
{
    AgenticLoop = new()
    {
        MaxTurnDuration = TimeSpan.FromMinutes(10),
        MaxConsecutiveFunctionCalls = 5
    },
    ErrorHandling = new()
    {
        MaxRetries = 3,
        SingleFunctionTimeout = TimeSpan.FromSeconds(60),
        RetryDelay = TimeSpan.FromSeconds(2)
    }
}
```

**Long-Running Tasks** (Patient):
```csharp
new AgentConfig
{
    AgenticLoop = new()
    {
        MaxTurnDuration = TimeSpan.FromMinutes(30),
        MaxConsecutiveFunctionCalls = 10
    },
    ErrorHandling = new()
    {
        MaxRetries = 5,
        SingleFunctionTimeout = TimeSpan.FromMinutes(5),
        RetryDelay = TimeSpan.FromSeconds(3)
    }
}
```

---

## Related Documentation

- [CONSECUTIVE_ERROR_TRACKING.md](./CONSECUTIVE_ERROR_TRACKING.md) - Deep dive into error tracking mechanism
- [ASYNC_LOCAL_CONTEXT.md](../Conversation/ASYNC_LOCAL_CONTEXT.md) - How context flows across async boundaries
- Agent.cs (lines 369-662) - Turn timeout and circuit breaker implementation
- Agent.cs (lines 1354-1399) - Retry logic implementation
- Agent.cs (lines 1074-1088) - Function signature generation
- ToolScheduler (lines 1781-1942) - Parallel function execution

---

## References

**Microsoft.Extensions.AI**:
- [FunctionInvokingChatClient.cs](../../Reference/extensions/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/FunctionInvokingChatClient.cs)
- `MaximumIterationsPerRequest = 40`
- `MaximumConsecutiveErrorsPerRequest = 3`

**Gemini CLI**:
- [loopDetectionService.ts](../../Reference/gemini-cli/packages/core/src/services/loopDetectionService.ts)
- `TOOL_CALL_LOOP_THRESHOLD = 5`
- Function signature hashing with SHA256

**Agent Framework**:
- [_validation.py](../../Reference/agent-framework/python/packages/core/agent_framework/_workflows/_validation.py)
- Workflow cycle detection (design-time, not runtime)

---

*Last Updated: 2025-01-04*
*Implementation: HPD-Agent v0.x*

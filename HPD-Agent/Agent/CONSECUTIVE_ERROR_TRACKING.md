# Consecutive Error Tracking

## Overview

The agent now tracks consecutive errors across iterations to prevent infinite error loops, similar to Microsoft.Extensions.AI's `FunctionInvokingChatClient`.

## How It Works

### Tracking Mechanism

The `AgentRunContext` maintains a `ConsecutiveErrorCount` that:
- **Increments** when a tool execution results in an error
- **Resets to 0** when an iteration completes successfully
- **Triggers termination** when exceeding the configured limit

### Error Detection

An error is detected when a `FunctionResultContent` has:
- A non-null `Exception` property, OR
- A `Result` string starting with "Error:"

### Configuration

The maximum consecutive errors is controlled by:
```csharp
Config.ErrorHandling.MaxRetries // Default: 3
```

## Example Scenarios

### Scenario 1: Infinite Loop Prevention

```
Iteration 1: ReadFile("missing.txt")     → Error: File not found (consecutiveErrors = 1)
Iteration 2: ReadFile("missing.txt")     → Error: File not found (consecutiveErrors = 2)
Iteration 3: ReadFile("missing.txt")     → Error: File not found (consecutiveErrors = 3)
Iteration 4: ReadFile("missing.txt")     → Error: File not found (consecutiveErrors = 4)
           ⚠️ TERMINATION: Exceeded maximum consecutive errors (3)
```

### Scenario 2: Recovery After Success

```
Iteration 1: ReadFile("missing.txt")     → Error: File not found (consecutiveErrors = 1)
Iteration 2: WriteFile("missing.txt")    → Success! (consecutiveErrors = 0 ← RESET)
Iteration 3: ReadFile("missing.txt")     → Success! (consecutiveErrors = 0)
Iteration 4: DeleteFile("wrong.txt")     → Error: File not found (consecutiveErrors = 1)
Iteration 5: DeleteFile("missing.txt")   → Success! (consecutiveErrors = 0 ← RESET)
```

The AI recovers from transient errors and continues execution.

### Scenario 3: Mixed Parallel Execution

```
Iteration 1: Parallel execution of 3 tools
  - Tool A → Success
  - Tool B → Error
  - Tool C → Success

Result: At least one success → consecutiveErrors = 0 (RESET)
```

If **any** tool in a parallel batch succeeds, the error count resets.

## API Reference

### AgentRunContext Methods

```csharp
// Track success (resets error count)
public void RecordSuccess()

// Track error (increments error count)
public void RecordError()

// Check if error limit exceeded
public bool HasExceededErrorLimit(int maxConsecutiveErrors)

// Current consecutive error count
public int ConsecutiveErrorCount { get; set; }
```

### Error Handling Config

```csharp
public class ErrorHandlingConfig
{
    /// <summary>
    /// Maximum number of consecutive errors before terminating execution.
    /// Default: 3
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}
```

## Termination Behavior

When the error limit is exceeded:

1. **Event Emitted**: A warning message is sent to the stream
   ```
   ⚠️ Maximum consecutive errors (3) exceeded. Stopping execution to prevent infinite error loop.
   ```

2. **Context Updated**:
   ```csharp
   agentRunContext.IsTerminated = true
   agentRunContext.TerminationReason = "Exceeded maximum consecutive errors (3)"
   ```

3. **Loop Exits**: The agentic loop breaks and returns control to the caller

## Comparison to Microsoft.Extensions.AI

| Feature | Microsoft.Extensions.AI | HPD-Agent |
|---------|------------------------|-----------|
| **Error Tracking** | `consecutiveErrorCount` local variable | `AgentRunContext.ConsecutiveErrorCount` property |
| **Configuration** | `MaximumConsecutiveErrorsPerRequest = 3` | `ErrorHandling.MaxRetries = 3` |
| **Error Detection** | Checks for `Exception` on `FunctionInvocationResult` | Checks `FunctionResultContent.Exception` OR `Result.StartsWith("Error:")` |
| **Reset Logic** | Resets when no errors in iteration | Same |
| **Termination** | Throws `AggregateException` | Sets `IsTerminated` flag + emits warning |

## Benefits

1. **Prevents Runaway Loops**: Stops the agent from repeatedly trying failed operations
2. **Allows Recovery**: Resets on success, enabling the AI to recover from transient errors
3. **Configurable**: Can adjust tolerance via `ErrorHandling.MaxRetries`
4. **Transparent**: Emits clear messages when termination occurs
5. **Stateful Tracking**: Maintained in `AgentRunContext` across the entire run

## Best Practices

1. **Set Appropriate Limits**: Default of 3 works for most cases, but adjust based on your use case
   - Higher limits (5-7) for flaky external APIs
   - Lower limits (1-2) for critical operations where you want fail-fast

2. **Return Clear Error Messages**: Tools should return error messages starting with "Error:" for proper detection

3. **Use Exception Handling**: Throw exceptions from tools when appropriate - they'll be tracked automatically

4. **Monitor Termination**: Check `AgentRunContext.IsTerminated` and `TerminationReason` in your application code

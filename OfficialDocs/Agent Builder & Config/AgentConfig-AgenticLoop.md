# AgentConfig - Agentic Loop Control

## Overview

Safety controls for preventing runaway agent execution and resource exhaustion.

## Properties

### MaxTurnDuration
Maximum time allowed for a single agent turn before timeout.

Default: `5 minutes`

Prevents stuck turns from blocking indefinitely.

### MaxParallelFunctions
Maximum number of functions to execute in parallel.

Default: `null` (unlimited)

Useful for:
- Limiting resource consumption
- Respecting external API rate limits
- Matching database connection pool sizes

### TerminateOnUnknownCalls
Whether to terminate the agentic loop when the LLM requests an unknown function.

Default: `false`

Options:
- `false` - Create error message and let LLM retry (normal scenarios)
- `true` - Terminate loop (multi-agent handoff scenarios)

**Use Case:** When you have multiple agents and want to hand off to a different agent that has the required function.

## Examples

[Coming soon...]

## Related Topics

- [Circuit Breaker Middleware](../Middleware/CircuitBreaker.md)
- [Loop Termination Patterns](../Patterns/LoopTermination.md)

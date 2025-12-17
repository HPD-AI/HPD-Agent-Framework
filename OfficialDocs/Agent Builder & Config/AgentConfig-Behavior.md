# AgentConfig - Agent Behavior

## Overview

Core agent behavior settings that control how the agent operates.

## Properties

### Name
Human-readable name for the agent.

Default: `"HPD-Agent"`

Used for logging, telemetry, and identification.

### SystemInstructions
The system prompt given to the LLM.

Default: `"You are a helpful assistant."`

This is the primary way to customize agent behavior at a high level.

### MaxAgenticIterations
Maximum number of agent turns (function-calling loops) before requiring user continuation permission.

Default: `10`

Each iteration allows the LLM to:
1. Analyze previous results
2. Decide whether to call more functions
3. Provide a final response

Example: User asks complex question → Agent takes 10 turns → Asks "Continue?" → User says yes → 3 more turns allowed

### ContinuationExtensionAmount
Additional turns allowed when the user chooses to continue past the limit.

Default: `3`

Works in conjunction with `MaxAgenticIterations` to provide graceful continuation.

## Examples

[Coming soon...]

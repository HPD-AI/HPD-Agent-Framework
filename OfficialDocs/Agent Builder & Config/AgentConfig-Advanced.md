# AgentConfig - Advanced Features

## Overview

Advanced configuration options for specialized use cases.

## Properties

### Validation
Provider validation behavior during agent building.

Default: Async validation disabled

[Detailed docs →](./AgentConfig-Validation.md)

Properties:
- `EnableAsyncValidation` - Network calls to validate API keys (2-5+ seconds)
- `TimeoutMs` - Timeout for validation operations
- `FailOnValidationError` - Whether build fails if validation fails

**Recommended:** 
- Development: `false` (fast iteration)
- Production/CI: `true` (catch issues early)

### Mcp
Model Context Protocol configuration.

[Detailed docs →](./AgentConfig-MCP.md)

Enables integration with MCP servers for additional tools and resources.

### BackgroundResponses
Long-running operation handling.

Default: Disabled

[Detailed docs →](./AgentConfig-BackgroundResponses.md)

Useful for serverless/API gateways with timeout limits. Return immediately with a token, then poll for results.

### Collapsing
Hierarchical function organization to reduce token usage.

Default: Enabled for Toolkits

[Detailed docs →](./AgentConfig-Collapsing.md)

Can reduce initial tool list size by up to 87.5% through hierarchical grouping.

### Messages
Customizable system messages for agents.

[Detailed docs →](./AgentConfig-Messages.md)

Enables:
- Internationalization
- Branding
- Context-specific messaging

Properties:
- `MaxIterationsReached` - Message when iteration limit hit
- `CircuitBreakerTriggered` - Message when circuit breaker opens
- `MaxConsecutiveErrors` - Message when error limit hit
- `PermissionDeniedDefault` - Message for permission denials

### Observability
Event sampling and observer circuit breaker configuration.

Default: Sampling disabled

[Detailed docs →](./AgentConfig-Observability.md)

Properties:
- `EnableSampling` - Enable event sampling for high-volume events
- `TextDeltaSamplingRate` - Sample N% of text delta events (0.0-1.0)
- `ReasoningDeltaSamplingRate` - Sample N% of reasoning events
- `MaxConcurrentObservers` - Max parallel observers per event
- `MaxConsecutiveFailures` - Observer circuit breaker threshold
- `SuccessesToResetCircuitBreaker` - Successes needed to close breaker

## Examples

[Coming soon...]

## Related Topics

- [Validation Strategies](./AgentConfig-Validation.md)
- [MCP Integration](../MCP/Integration.md)
- [Background Operations](../Async/BackgroundOperations.md)
- [Function Collapsing](../Tools/Collapsing.md)
- [Observability & Telemetry](../Observability/Overview.md)

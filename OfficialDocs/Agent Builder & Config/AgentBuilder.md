# AgentBuilder - Fluent API Reference

The `AgentBuilder` class provides a fluent, chainable API for configuring and constructing agents programmatically.

## Overview

`AgentBuilder` is the primary way to configure agents in code with full compile-time type safety. It supports three initialization patterns:

1. **From scratch** - `new AgentBuilder()`
2. **From AgentConfig object** - `new AgentBuilder(config)`
3. **From JSON file** - `new AgentBuilder("agent-config.json")`

## Builder Methods

[Detailed builder methods documentation coming soon...]

### Provider Configuration
- `WithProvider()`
- `WithOpenAI()`
- `WithAnthropic()`
- `WithOllama()`
- [More provider methods...]

### Tool Registration
- `WithTools<T>()`
- `WithServerConfiguredTools()`
- `WithToolSelection()`

### Middleware
- `WithMiddleware<T>()`
- `WithLogging()`
- `WithTelemetry()`
- `WithErrorHandling()`

### Session & Persistence
- `WithSessionStore()`
- `WithDurableExecution()`

### Advanced Configuration
- `WithServiceProvider()`
- `WithValidation()`
- `WithCaching()`
- [More methods...]

## Examples

[Detailed examples and use cases coming soon...]

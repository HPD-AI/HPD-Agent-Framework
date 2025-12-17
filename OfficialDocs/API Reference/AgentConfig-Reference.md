# AgentConfig API Reference

Complete reference for all `AgentConfig` properties and configuration sections.

## Quick Navigation

| Section | Purpose |
|---------|---------|
| [Provider](#provider-configuration) | AI provider settings |
| [Agent Behavior](#agent-behavior) | Core agent settings |
| [Error Handling](#error-handling) | Retry policies and error recovery |
| [Sessions](#sessions--persistence) | Durable execution and checkpointing |
| [Caching](#caching) | LLM response caching |
| [Agentic Loop](#agentic-loop-control) | Loop safety controls |
| [Tools](#tools) | Tool selection and registration |
| [Conversation](#conversation-management) | History management |
| [Documents](#document-handling) | File upload and extraction |
| [Advanced](#advanced-features) | Validation, MCP, observability |

## Provider Configuration

**Property:** `Provider`

**Type:** `ProviderConfig`

Configuration for which AI service to use and how to connect.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ProviderKey` | `string` | - | Provider identifier (e.g., "openai", "anthropic", "ollama") |
| `ModelName` | `string` | - | Specific model to use (e.g., "gpt-4o", "claude-3-5-sonnet") |
| `ApiKey` | `string?` | - | API authentication key |
| `Endpoint` | `string?` | - | Custom endpoint URL (for self-hosted or Azure) |
| `DefaultChatOptions` | `ChatOptions?` | - | Default temperature, max_tokens, etc. |
| `ProviderOptionsJson` | `string?` | - | Provider-specific config as JSON string (FFI-friendly) |
| `AdditionalProperties` | `Dictionary<string, object>?` | - | Provider-specific settings as key-value pairs (legacy) |

## Agent Behavior

**Properties:** Core agent settings

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string` | `"HPD-Agent"` | Agent name/identifier |
| `SystemInstructions` | `string` | `"You are a helpful assistant."` | System prompt for the LLM |
| `MaxAgenticIterations` | `int` | `10` | Max agent turns before requiring continuation |
| `ContinuationExtensionAmount` | `int` | `3` | Extra turns allowed when user continues |

## Error Handling

**Property:** `ErrorHandling`

**Type:** `ErrorHandlingConfig`

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `NormalizeErrors` | `bool` | `true` | Convert provider-specific errors to standard format |
| `IncludeProviderDetails` | `bool` | `false` | Include provider-specific error details |
| `IncludeDetailedErrorsInChat` | `bool` | `false` | ⚠️ Expose full exceptions to LLM (security risk) |
| `MaxRetries` | `int` | `3` | Max retry attempts for transient errors |
| `SingleFunctionTimeout` | `TimeSpan?` | `30s` | Max time per function execution |
| `RetryDelay` | `TimeSpan` | `1s` | Initial retry delay (exponential backoff) |
| `UseProviderRetryDelays` | `bool` | `true` | Respect provider Retry-After headers |
| `AutoRefreshTokensOn401` | `bool` | `true` | Auto-refresh tokens on 401 errors |
| `MaxRetryDelay` | `TimeSpan` | `30s` | Cap on retry delay (prevents excessive waiting) |
| `BackoffMultiplier` | `double` | `2.0` | Exponential backoff multiplier (e.g., 1s → 2s → 4s) |
| `MaxRetriesByCategory` | `Dictionary?` | - | Optional per-category retry limits |
| `CustomRetryStrategy` | `Func?` | - | Custom retry logic callback |

## Sessions & Persistence

**Property:** `SessionStore`, `SessionStoreOptions`

**Types:** `ISessionStore`, `SessionStoreOptions`

### SessionStore Property

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SessionStore` | `ISessionStore?` | - | Storage backend (InMemorySessionStore, JsonSessionStore, custom) |
| `SessionStoreOptions` | `SessionStoreOptions?` | - | Auto-save, checkpoint frequency, retention policy |

## Caching

**Property:** `Caching`

**Type:** `CachingConfig`

Reduces LLM API calls through response caching. Requires `IDistributedCache` registration.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Enable/disable caching (opt-in) |
| `CoalesceStreamingUpdates` | `bool` | `true` | Store final response vs all streaming updates |
| `CacheStatefulConversations` | `bool` | `false` | Cache multi-turn conversations (use with caution) |
| `CacheExpiration` | `TimeSpan?` | `30m` | Time-to-live before automatic eviction |

## Agentic Loop Control

**Property:** `AgenticLoop`

**Type:** `AgenticLoopConfig`

Safety controls for preventing runaway execution.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxTurnDuration` | `TimeSpan?` | `5m` | Max time per turn before timeout |
| `MaxParallelFunctions` | `int?` | `null` | Max parallel functions (null = unlimited) |
| `TerminateOnUnknownCalls` | `bool` | `false` | Terminate if LLM requests unknown function |

## Tools

**Property:** `ToolSelection`, `ServerConfiguredTools`

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ToolSelection` | `ToolSelectionConfig?` | - | Tool selection mode (Auto, None, RequireAny, RequireSpecific) |
| `ServerConfiguredTools` | `IList<AITool>?` | - | Pre-configured server-side tools |

## Conversation Management

**Property:** `HistoryReduction`, `PreserveReasoningInHistory`

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `HistoryReduction` | `HistoryReductionConfig?` | - | History compression (MessageCounting or Summarizing) |
| `PreserveReasoningInHistory` | `bool` | `false` | Keep reasoning tokens from o1/DeepSeek-R1 models |

## Document Handling

**Property:** `DocumentHandling`

**Type:** `DocumentHandlingConfig`

⚠️ **Legacy:** Use middleware extension instead.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DocumentTagFormat` | `string?` | - | Custom document tag format for message injection |
| `MaxFileSizeBytes` | `long` | `10MB` | Maximum file size to process |

## Advanced Features

**Properties:** Validation, MCP, background responses, collapsing, observability

### Validation

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Validation.EnableAsyncValidation` | `bool` | `false` | Network calls to validate API keys (Dev: false, Prod: true) |
| `Validation.TimeoutMs` | `int` | `3000` | Validation timeout in milliseconds |
| `Validation.FailOnValidationError` | `bool` | `false` | Fail build if validation fails |

### MCP (Model Context Protocol)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Mcp.ManifestPath` | `string` | - | Path to MCP server manifest |
| `Mcp.Options` | `object?` | - | MCP configuration options |

### Background Responses

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BackgroundResponses.DefaultAllow` | `bool` | `false` | Enable background responses |
| `BackgroundResponses.DefaultPollingInterval` | `TimeSpan` | `2s` | Polling interval for results |
| `BackgroundResponses.DefaultTimeout` | `TimeSpan?` | `null` | Max wait for background operation |
| `BackgroundResponses.AutoPollToCompletion` | `bool` | `false` | Auto-poll until done |
| `BackgroundResponses.MaxPollAttempts` | `int` | `1000` | Max polling attempts |

### Collapsing

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Collapsing.Enabled` | `bool` | `true` | Enable hierarchical function grouping |
| `Collapsing.CollapseClientTools` | `bool` | `false` | Group Client tools in container |
| `Collapsing.MaxFunctionNamesInDescription` | `int` | `10` | Max function names in auto-generated descriptions |
| `Collapsing.SkillInstructionMode` | `enum` | `Both` | Where to inject skill instructions |
| `Collapsing.MCPServerInstructions` | `Dictionary?` | - | Per-server post-expansion instructions |
| `Collapsing.ClientToolsInstructions` | `string?` | - | Post-expansion instructions for client tools |

### Observability

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Observability.EnableSampling` | `bool` | `false` | Enable event sampling |
| `Observability.TextDeltaSamplingRate` | `double` | `1.0` | Sample rate for text deltas (0.0-1.0) |
| `Observability.ReasoningDeltaSamplingRate` | `double` | `1.0` | Sample rate for reasoning events (0.0-1.0) |
| `Observability.MaxConcurrentObservers` | `int` | `10` | Max parallel observers per event |
| `Observability.MaxConsecutiveFailures` | `int` | `10` | Observer circuit breaker threshold |
| `Observability.SuccessesToResetCircuitBreaker` | `int` | `3` | Successes needed to close breaker |

### Messages

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Messages.MaxIterationsReached` | `string` | - | Message when iteration limit hit |
| `Messages.CircuitBreakerTriggered` | `string` | - | Message when circuit breaker opens |
| `Messages.MaxConsecutiveErrors` | `string` | - | Message when error limit hit |
| `Messages.PermissionDeniedDefault` | `string` | - | Message for permission denials |

## Usage Examples

See the [Getting Started guide](../Getting%20Started/01%20Customizing%20an%20Agent.md) for practical examples.

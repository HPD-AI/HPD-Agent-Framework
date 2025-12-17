# AgentConfig - Caching

## Overview

Distributed caching configuration for reducing LLM API calls and costs.

## Properties

### Enabled
Enable/disable response caching.

Default: `false` (opt-in for safety)

**Requires:** `IDistributedCache` registered via `WithServiceProvider()`

### CoalesceStreamingUpdates
Whether to coalesce streaming responses into final result before caching.

Default: `true` (space-efficient)

Options:
- `true` - Stores only final response (smaller cache, lower memory)
- `false` - Stores all streaming updates (higher fidelity, larger cache)

### CacheStatefulConversations
Whether to cache responses in multi-turn conversations (when `ConversationId` is set).

Default: `false` (prevents stale data)

**Warning:** Enable only if you understand cache invalidation implications.

### CacheExpiration
Time-to-live for cache entries before automatic eviction.

Default: `30 minutes`

Set to `null` for no expiration (use with caution).

## Examples

[Coming soon...]

## Related Topics

- [Caching Strategy Guide](../Caching/Strategy.md)
- [Cache Invalidation Patterns](../Caching/Invalidation.md)

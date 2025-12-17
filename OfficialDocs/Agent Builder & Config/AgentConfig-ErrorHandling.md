# AgentConfig - Error Handling

## Overview

Error handling configuration controls how the agent recovers from failures and retries operations.

## Properties

### NormalizeErrors
Whether to convert provider-specific errors into standard formats.

Default: `true`

### IncludeProviderDetails
Whether to include provider-specific error details in messages.

Default: `false`

### IncludeDetailedErrorsInChat
Whether to expose full exception messages to the LLM.

**Security Warning:** Can expose sensitive information (paths, connection strings, keys) to the LLM.

Default: `false` (recommended)

### MaxRetries
Maximum number of retry attempts for transient errors.

Default: `3`

### SingleFunctionTimeout
Maximum time allowed for a single function to execute.

Default: `30 seconds`

### RetryDelay
Initial delay before the first retry attempt.

Default: `1 second`

Increases exponentially based on `BackoffMultiplier`.

### UseProviderRetryDelays
Whether to respect provider-provided retry delays (from Retry-After headers).

Default: `true`

### AutoRefreshTokensOn401
Automatically attempt token refresh when receiving 401 auth errors.

Default: `true`

### MaxRetryDelay
Maximum cap on retry delays (prevents excessive waiting).

Default: `30 seconds`

### BackoffMultiplier
Exponential backoff multiplier for consecutive retries.

Default: `2.0` (doubles each retry)

Example: 1s → 2s → 4s → 8s → capped at 30s

### MaxRetriesByCategory
Optional per-error-category retry limits.

Example: Limit rate-limit errors to 5 retries, server errors to 3.

### CustomRetryStrategy
Implement custom retry logic via callback.

[More details coming soon...]

## Examples

[Coming soon...]

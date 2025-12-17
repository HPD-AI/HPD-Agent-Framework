# AgentConfig - Provider Configuration

## Overview

The `Provider` property configures which AI service to use and how to connect to it.

## Properties

### ProviderKey
The unique identifier for the AI provider (lowercase).

Examples: `"openai"`, `"anthropic"`, `"ollama"`, `"bedrock"`

### ModelName
The specific model to use with that provider.

Examples: `"gpt-4o"`, `"claude-3-5-sonnet"`, `"mistral-large"`

### ApiKey
API authentication key for the provider.

**Security Note:** Never commit API keys to version control. Use environment variables or secrets management.

### Endpoint
Custom endpoint URL (optional).

Useful for:
- Self-hosted models (Ollama)
- Azure OpenAI custom deployments
- Provider-specific endpoints

### DefaultChatOptions
Default `ChatOptions` settings applied to all requests.

Includes: temperature, max_tokens, top_p, etc.

### ProviderOptionsJson
Provider-specific configuration as a JSON string.

**Preferred for FFI/AOT scenarios** where cross-language compatibility matters.

[More details coming soon...]

### AdditionalProperties
Provider-specific settings as key-value pairs (legacy approach).

[More details coming soon...]

## Examples

[Coming soon...]

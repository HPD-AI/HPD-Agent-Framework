# AgentConfig - Configuration Reference

The `AgentConfig` class is a data-centric configuration object that holds all serializable settings for creating agents. It can be used as a C# object or loaded from JSON files.

## Overview

`AgentConfig` enables:
- Serialization and persistence of agent configuration
- Version control of configuration files separately from code
- Reusable configuration across multiple agent instances
- Both programmatic (C# class) and declarative (JSON file) approaches

## Configuration Sections

### Provider Configuration
- `Provider` - AI provider settings (model, API key, endpoint)
- [Detailed docs →](./AgentConfig-Provider.md)

### Agent Behavior
- `Name` - Agent name/identifier
- `SystemInstructions` - Agent system prompt
- `MaxAgenticIterations` - Max turns before requiring continuation
- `ContinuationExtensionAmount` - Additional turns when user continues
- [Detailed docs →](./AgentConfig-Behavior.md)

### Error Handling
- `ErrorHandling` - Retry policies, timeouts, error normalization
- [Detailed docs →](./AgentConfig-ErrorHandling.md)

### Session & Persistence
- `SessionStore` - Durable execution checkpoint storage
- `SessionStoreOptions` - Auto-save and retention policies
- [Detailed docs →](./AgentConfig-Sessions.md)

### Caching
- `Caching` - LLM response caching configuration
- [Detailed docs →](./AgentConfig-Caching.md)

### Agentic Loop Control
- `AgenticLoop` - Loop safety controls (timeouts, parallel limits)
- [Detailed docs →](./AgentConfig-AgenticLoop.md)

### Tool Configuration
- `ToolSelection` - Tool selection behavior (Auto, None, Required)
- `ServerConfiguredTools` - Pre-configured server-side tools
- [Detailed docs →](./AgentConfig-Tools.md)

### Conversation Management
- `HistoryReduction` - History compression and summarization
- `PreserveReasoningInHistory` - Keep reasoning tokens in history
- [Detailed docs →](./AgentConfig-Conversation.md)

### Document Handling
- `DocumentHandling` - File upload and text extraction settings
- [Detailed docs →](./AgentConfig-Documents.md)

### Advanced Features
- `Validation` - Provider validation behavior
- `Mcp` - Model Context Protocol configuration
- `BackgroundResponses` - Long-running operation handling
- `Collapsing` - Function hierarchies to reduce token usage
- `Messages` - Customizable system messages
- `Observability` - Event sampling and observer circuit breaker
- [Detailed docs →](./AgentConfig-Advanced.md)

## JSON File Examples

[Coming soon...]

## Building from Config

[Coming soon...]

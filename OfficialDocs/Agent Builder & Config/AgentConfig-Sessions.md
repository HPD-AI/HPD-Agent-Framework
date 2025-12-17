# AgentConfig - Sessions & Persistence

## Overview

Configure durable execution and session persistence for crash recovery and multi-turn conversations.

## Properties

### SessionStore
The storage backend for persisting agent state and conversation history.

Options:
- `InMemorySessionStore` - For development/testing (data lost on restart)
- `JsonSessionStore` - For production (persists to JSON files)
- Custom implementations via `ISessionStore`

[Detailed SessionStore docs →](./SessionStore.md)

### SessionStoreOptions
Configuration for session persistence behavior.

Properties:
- `AutoSave` - Automatically save checkpoints (enables one-line RunAsync)
- `CheckpointFrequency` - How often to save checkpoints
- `RetentionPolicy` - How long to keep session history

## Examples

[Coming soon...]

## Related Topics

- [Durable Execution & Checkpointing](../Sessions/Checkpointing.md)
- [Session Persistence Patterns](../Sessions/Persistence.md)

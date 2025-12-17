# AgentConfig - Tools

## Overview

Tool registration and selection behavior configuration.

## Properties

### ToolSelection
Controls how the LLM selects which tools to use.

[Detailed docs →](./AgentConfig-ToolSelection.md)

### ServerConfiguredTools
Tools registered on the provider's infrastructure that aren't sent in each request.

Default: `null`

**Use Cases:**
- OpenAI Assistants with pre-configured tools
- Azure AI Function Apps
- Anthropic account-level tools
- Testing scenarios where you want hidden tools

[More details →](./AgentConfig-ServerTools.md)

## Examples

[Coming soon...]

## Related Topics

- [Tool Selection Modes](./AgentConfig-ToolSelection.md)
- [Server-Configured Tools](./AgentConfig-ServerTools.md)
- [Tool Registration Guide](../Tools/Registration.md)

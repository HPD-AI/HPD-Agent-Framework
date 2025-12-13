# Tools Overview

Tools are functions that the agent can call to interact with the world. HPD-Agent supports multiple sources of tools, all unified under a common system with shared features like collapsing and instructions.

## Tool Sources

| Source | Description | Defined By |
|--------|-------------|------------|
| **C# Plugins** | Native plugins with full feature support | Developer (code) |
| **MCP Servers** | External tools via Model Context Protocol | MCP server configs |
| **Frontend Tools** | Tools provided by the client/UI | Frontend application |
| **OpenAPI** | Auto-generated from API specs | OpenAPI/Swagger files |

```
                    HPD-Agent
                        │
        ┌───────────────┼───────────────┐───────────────┐
        ▼               ▼               ▼               ▼
   C# Plugins      MCP Servers    Frontend Tools    OpenAPI
   [AIFunction]    filesystem      OpenFile         GET /users
   [Skill]         github          ShowDialog       POST /orders
   [SubAgent]      database        GetSelection     ...
```

---

## C# Plugins

The most powerful option. Define tools directly in C# with full access to:
- **AIFunctions** - Single operations
- **Skills** - Multi-function workflows with instructions
- **SubAgents** - Delegated child agents
- **Plugin Metadata** - Dynamic descriptions and conditional visibility
- **Collapsing** - Hierarchical organization with `[Collapse]`

```csharp
[Collapse("File operations")]
public class FilePlugin
{
    [AIFunction]
    [AIDescription("Read a file")]
    public string ReadFile(string path) { }

    [AIFunction]
    [ConditionalFunction("AllowWrite")]
    public void WriteFile(string path, string content) { }
}
```

→ See [02.1 C# Plugins Overview.md](02.1%20C%23%20Plugins%20Overview.md) for the full guide.

---

## MCP Servers

Connect external tool servers using the Model Context Protocol. MCP servers run as separate processes and expose tools over a standardized protocol.

```csharp
var agent = new AgentBuilder()
    .WithMCPServer("filesystem", new MCPServerConfig { ... })
    .WithMCPServer("github", new MCPServerConfig { ... })
    .Build();
```

MCP tools support:
- Collapsing (grouped by server)
- Custom instructions per server
- Automatic tool discovery

→ See [02.2 MCP Servers.md](02.2%20MCP%20Servers.md) for setup and configuration.

---

## Frontend Tools

Tools provided by the client application (IDE extension, web UI, etc.). These are injected at runtime and allow the agent to interact with the user's environment.

Common frontend tools:
- `OpenFile` - Open a file in the editor
- `ShowDialog` - Display a dialog to the user
- `GetSelection` - Get the user's current selection

```csharp
var config = new AgentConfig
{
    Collapsing = new CollapsingConfig
    {
        CollapseFrontendTools = true,
        FrontendToolsInstructions = "These tools interact with the user's IDE."
    }
};
```

→ See [02.3 Frontend Tools.md](02.3%20Frontend%20Tools.md) for integration details.

---

## OpenAPI (Coming Soon)

Auto-generate tools from OpenAPI/Swagger specifications. Point to an API spec and get tools for each endpoint.

```csharp
// Future API
var agent = new AgentBuilder()
    .WithOpenAPI("https://api.example.com/openapi.json")
    .Build();
```

---

## Shared Features

All tool sources share these capabilities:

### Collapsing

Group tools into expandable containers to reduce context clutter:

```csharp
var config = new AgentConfig
{
    Collapsing = new CollapsingConfig
    {
        Enabled = true,                    // C# plugins
        CollapseFrontendTools = true,      // Frontend tools
        // MCP servers are collapsed by server name automatically
    }
};
```

### Instructions

Provide guidance for tool usage:

```csharp
var config = new AgentConfig
{
    Collapsing = new CollapsingConfig
    {
        // Per-MCP-server instructions
        MCPServerInstructions = new Dictionary<string, string>
        {
            ["filesystem"] = "Always use absolute paths.",
            ["github"] = "Prefer GraphQL API for bulk operations."
        },

        // Frontend tools instructions
        FrontendToolsInstructions = "These tools interact with the user's IDE."
    }
};
```

For C# plugins, instructions are defined via `[Collapse]` attributes and skill instructions.

---

## Choosing a Tool Source

| Need | Best Choice |
|------|-------------|
| Full control, type safety, compile-time validation | C# Plugins |
| Use existing MCP-compatible servers | MCP Servers |
| Interact with user's environment (IDE, UI) | Frontend Tools |
| Integrate with REST APIs quickly | OpenAPI |

Most applications use a combination:
- **C# Plugins** for core business logic
- **MCP Servers** for standard capabilities (filesystem, git, etc.)
- **Frontend Tools** for UI interaction

---

## Next Steps

- [02.1 C# Plugins Overview.md](02.1%20C%23%20Plugins%20Overview.md) - Native plugin development
- [02.2 MCP Servers.md](02.2%20MCP%20Servers.md) - Model Context Protocol integration
- [02.3 Frontend Tools.md](02.3%20Frontend%20Tools.md) - Client-provided tools

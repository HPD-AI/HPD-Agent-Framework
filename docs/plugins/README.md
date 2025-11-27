# Plugin Documentation

This folder contains comprehensive documentation for the HPD-Agent plugin system.

## Documents

| Document | Audience | Description |
|----------|----------|-------------|
| [USER_GUIDE.md](./USER_GUIDE.md) | Developers | Getting started guide for creating and using plugins |
| [API_REFERENCE.md](./API_REFERENCE.md) | Developers | Complete API documentation for all plugin-related classes and attributes |
| [ARCHITECTURE.md](./ARCHITECTURE.md) | Contributors | Technical deep-dive into the plugin system architecture |
| [AIFUNCTIONS.md](./AIFUNCTIONS.md) | Developers | Detailed guide for creating AI Functions (placeholder) |

## Quick Start

```csharp
// 1. Create a plugin
public class MyPlugin
{
    [AIFunction]
    [AIDescription("Greets a user")]
    public string Greet(string name) => $"Hello, {name}!";
}

// 2. Register with your agent
var agent = new AgentBuilder()
    .WithProvider(myProvider)
    .WithPlugin<MyPlugin>()
    .Build();
```

## Related Documentation

- [Skills Guide](../skills/SKILLS_GUIDE.md) - Workflow containers
- [SubAgents Guide](../SubAgents/USER_GUIDE.md) - Specialized child agents
- [Plugin Collapsing](../SCOPING_SYSTEM.md) - Hierarchical organization with `[Collapse]`

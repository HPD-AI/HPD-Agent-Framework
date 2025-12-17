# Agent Builder & Config Quick Start

Quick reference for the three customization patterns. For detailed API documentation, see the [API Reference](../API%20Reference/README.md).

## The Three Patterns

### 1. Builder Pattern (Fluent API)
Programmatic configuration using chainable method calls.

```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSystemInstructions("You are helpful...")
    .WithTools<MyTools>()
    .Build();
```

**When to use:** Rapid prototyping, one-off agents, dynamic configuration

**Full details:** [Getting Started Guide](../Getting%20Started/01%20Customizing%20an%20Agent.md#builder-pattern-fluent-api)

---

### 2. Config Pattern (Data Model)
Define configuration as persistent data (C# class or JSON file).

```csharp
// C# Class
var config = new AgentConfig { ... };
var agent = await config.BuildAsync();

// JSON File
var agent = await AgentConfig.BuildFromFileAsync("agent-config.json");
```

**When to use:** Reusable configurations, version control, multi-environment setup

**Full details:** [Getting Started Guide](../Getting%20Started/01%20Customizing%20an%20Agent.md#config-pattern)

---

### 3. Builder + Config Pattern (Recommended)
Define configuration once as data, then layer builder methods for runtime customization.

```csharp
// From C# class
var agent1 = new AgentBuilder(config)
    .WithTools<CalculatorTool>()
    .Build();

var agent2 = new AgentBuilder(config)
    .WithTools<FileSystemTool>()
    .Build();

// From JSON file
var agent3 = new AgentBuilder("agent-config.json")
    .WithTools<DatabaseTool>()
    .Build();
```

**Benefits:**
- Configuration defined once (DRY principle)
- Reusable across multiple agents
- Type-safe tool registration
- Clean separation of concerns

**Full details:** [Getting Started Guide](../Getting%20Started/01%20Customizing%20an%20Agent.md#builder--config-pattern-recommended-pattern)

---

## Full Documentation

- **[Getting Started: Customizing an Agent](../Getting%20Started/01%20Customizing%20an%20Agent.md)** - Conceptual guide with pattern comparisons
- **[API Reference: AgentConfig](../API%20Reference/AgentConfig-Reference.md)** - Complete property reference
- **[API Reference: AgentBuilder](../API%20Reference/AgentBuilder-Reference.md)** - All builder methods (coming soon)

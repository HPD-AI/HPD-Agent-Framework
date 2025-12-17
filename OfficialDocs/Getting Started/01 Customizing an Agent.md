## Customizing an Agent
There are **three primary patterns** to customize an agent:

1. **Builder Pattern (Fluent API):** Configure agents programmatically using a fluent, chainable API in C# code.
2. **Config Pattern (Data Model):** Define agent configuration as data (e.g., JSON or C# objects), enabling serialization, persistence, and reuse.
3. **Builder + Config Pattern:** Define the agent configuration as data and then layer on the fluent `AgentBuilder` API.

Each method offers distinct advantages and trade-offs.

**For detailed API documentation, see:**
- [**AgentConfig Reference**](../API%20Reference/AgentConfig-Reference.md) - Complete property listing for all configuration sections
- [**AgentBuilder Reference**](../API%20Reference/AgentBuilder-Reference.md) - All builder methods and usage (coming soon)

### Builder Pattern (Fluent API)

```csharp
var agent = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSystemInstructions("You are helpful...")
    .WithTools<MyTools>()
    .WithTelemetry()
    .Build();
```

### Config Pattern

You can define agent configuration either as a C# class or as a JSON file. Here's a side-by-side comparison:

<div style="display: flex; gap: 20px;">
<div style="flex: 1;">

**C# Config Class:**

```csharp
var config = new AgentConfig
{
    Name = "MyAgent",
    SystemInstructions = "You are helpful...",
    Provider = new ProviderConfig
    {
        ProviderKey = "openai",
        ModelName = "gpt-4o",
        ApiKey = "sk-..."
    },
    MaxAgenticIterations = 10
};

// Build directly from config
var agent = await config.BuildAsync();

// Or use the sync version if needed
var agent = config.Build();



```

</div>
<div style="flex: 1;">

**JSON Config File (agent-config.json):**

```json
{
    "Name": "MyAgent",
    "SystemInstructions": "You are helpful...",
    "Provider": {
        "ProviderKey": "openai",
        "ModelName": "gpt-4o",
        "ApiKey": "sk-..."
    },
    "MaxAgenticIterations": 10
}
```
```csharp
// Load, deserialize, and build in one call
var agent = await AgentConfig.BuildFromFileAsync("agent-config.json");

// Sync version if needed
var agent = AgentConfig.BuildFromFile("agent-config.json");
```

</div>
</div>

### Builder + Config Pattern (Recommended Pattern)

This pattern combines the best of both worlds: define your agent configuration as data (for persistence and reuse), then layer on the fluent API for runtime customization.

<div style="display: flex; gap: 20px;">
<div style="flex: 1;">

**C# Config Class + Builder:**

```csharp
// Start with a config object
var config = new AgentConfig
{
    Name = "MyAgent",
    SystemInstructions = "You are helpful.",
    Provider = new ProviderConfig
    {
        ProviderKey = "openai",
        ModelName = "gpt-4o",
        ApiKey = "sk-..."
    },
    MaxAgenticIterations = 10
};

// Layer builder methods on top for runtime customization
var agent = new AgentBuilder(config)
    .WithServiceProvider(services)
    .WithLogging()
    .WithTelemetry()
    .WithTools<MyTools>()
    .WithMiddleware<MyCustomMiddleware>()
    .Build();
```

</div>
<div style="flex: 1;">

**JSON Config File + Builder:**

```json
{
    "Name": "MyAgent",
    "SystemInstructions": "You are helpful.",
    "Provider": {
        "ProviderKey": "openai",
        "ModelName": "gpt-4o",
        "ApiKey": "sk-..."
    },
    "MaxAgenticIterations": 10
}
```

**C# Usage:**
```csharp
// Load config from JSON
var agent = new AgentBuilder("agent-config.json")
    .WithServiceProvider(services)
    .WithLogging()
    .WithTelemetry()
    .WithTools<MyTools>()
    .WithMiddleware<MyCustomMiddleware>()
    .Build();
```

</div>
</div>

### Why is the Builder + Config Pattern recommended for production?

The Builder + Config Pattern combines the strengths of both approaches while avoiding their weaknesses:

**Against Pure Builder Pattern - Builder Methods Get Messy:**

The core issue: with many configurations, the builder gets very long and repetitive.

1. **Too many chained calls** - Imagine 20+ `.With()` calls:
```csharp
// Pure Builder Pattern - becomes a wall of code!
var agent1 = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")
    .WithSystemInstructions("You are helpful Math assistant")
    .WithServiceProvider(services)
    .WithLogging()
    .WithTelemetry()
    .WithCaching(TimeSpan.FromHours(1))
    .WithMaxFunctionCallTurns(10)
    .WithContinuationExtensionAmount(3)
    .WithDurableExecution("./checkpoints")
    .WithMiddleware<MyCustomMiddleware>()
    .WithMiddleware<ErrorHandlingMiddleware>()
    .WithMiddleware<HistoryReductionMiddleware>()
    .WithTools<Tool1>()
    .WithTools<Tool2>()
    .WithTools<Tool3>()
    // ... 20+ more configuration calls
    .Build();
```

2. **Configuration duplication across multiple agents** - You have to repeat this everywhere:
```csharp
// If you have 10 agents with common config, all this is duplicated 10 times!
var agent2 = new AgentBuilder()
    .WithProvider("openai", "gpt-4o")  // Repeated!
    .WithSystemInstructions("You are helpful finance assistant")  // Repeated!
    .WithServiceProvider(services)  // Repeated!
    .WithLogging()  // Repeated!
    .WithTelemetry()  // Repeated!
    .WithCaching(TimeSpan.FromHours(1))  // Repeated!
    .WithMaxFunctionCallTurns(10)  // Repeated!
    .WithContinuationExtensionAmount(3)  // Repeated!
    .WithDurableExecution("./checkpoints")  // Repeated!
    .WithMiddleware<MyCustomMiddleware>()
    .WithMiddleware<ErrorHandlingMiddleware>()  
    .WithMiddleware<HistoryReductionMiddleware>()  
    .WithTools<Tool2>()
    .Build();
```

**Against Pure Config Pattern:**
- Config files can't register native C# tools directly
- You lose compile-time type safety for tool registration
- Native tools need to be registered via code anyway
- You still need builder methods for these registrations

Example of limitations:
```csharp
// agent-config.json - can't specify C# tools!
{
    "Name": "MyAgent",
    "SystemInstructions": "You are helpful.",
    "Provider": { "ProviderKey": "openai", "ModelName": "gpt-4o" },
    // No way to register MyCalculatorTool here ❌
}

// You still need code:
var agent = await AgentConfig.BuildFromFileAsync("agent-config.json");
agent.WithTools<MyCalculatorTool>();  // Has to be separate! ❌
```

**With Builder + Config Pattern:**
- Configuration is defined once as data (DRY principle)
- Reusable across multiple agent instances
- Native C# tools registered via the fluent API with full type safety
- Clean separation: data configuration + runtime customization
- Easy to version control configuration separately from code
- Perfect for microservices and multi-agent systems

Example of clean solution:

<div style="display: flex; gap: 20px;">
<div style="flex: 1;">

**C# Config Class + Builder:**

```csharp
// Define config once as a C# class
var config = new AgentConfig
{
    Name = "MyAgent",
    SystemInstructions = "You are helpful.",
    Provider = new ProviderConfig
    {
        ProviderKey = "openai",
        ModelName = "gpt-4o",
        ApiKey = "sk-..."
    },
    MaxAgenticIterations = 10,
    Caching = new CachingConfig
    {
        Enabled = true,
        CacheExpiration = TimeSpan.FromMinutes(30)
    },
    ErrorHandling = new ErrorHandlingConfig
    {
        MaxRetries = 3,
        SingleFunctionTimeout = TimeSpan.FromSeconds(30)
    },
    AgenticLoop = new AgenticLoopConfig
    {
        MaxTurnDuration = TimeSpan.FromMinutes(5)
    }
};

// Reuse across multiple agents
var agent1 = new AgentBuilder(config)
    .WithServiceProvider(services)
    .WithTools<CalculatorTool>()
    .Build();

var agent2 = new AgentBuilder(config)
    .WithServiceProvider(services)
    .WithTools<FileSystemTool>()
    .Build();

var agent3 = new AgentBuilder(config)
    .WithServiceProvider(services)
    .WithTools<DatabaseTool>()
    .Build();
```

</div>
<div style="flex: 1;">

**JSON Config File + Builder:**

```json
{
    "Name": "MyAgent",
    "SystemInstructions": "You are helpful.",
    "Provider": {
        "ProviderKey": "openai",
        "ModelName": "gpt-4o",
        "ApiKey": "sk-..."
    },
    "MaxAgenticIterations": 10,
    "Caching": {
        "Enabled": true,
        "CacheExpiration": "00:30:00"
    },
    "ErrorHandling": {
        "MaxRetries": 3,
        "SingleFunctionTimeout": "00:00:30"
    },
    "AgenticLoop": {
        "MaxTurnDuration": "00:05:00"
    }
}
```

**C# Usage:**
```csharp
// Reuse across multiple agents
var agent1 = new AgentBuilder("agent-config.json")
    .WithServiceProvider(services)
    .WithTools<CalculatorTool>()
    .Build();

var agent2 = new AgentBuilder("agent-config.json")
    .WithServiceProvider(services)
    .WithTools<FileSystemTool>()
    .Build();

var agent3 = new AgentBuilder("agent-config.json")
    .WithServiceProvider(services)
    .WithTools<DatabaseTool>()
    .Build();
```

</div>
</div>

**Key insight:** All the tedious configuration (observability, caching, error handling, timeouts) is centralized once. Only tool-specific code stays in C#. No repetition, no middleware clutter—just load and customize tools.
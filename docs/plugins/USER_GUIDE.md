# HPD-Agent Plugin User Guide

**Version:** 1.0.0
**Last Updated:** 2025-11-27

---

## Table of Contents

1. [Introduction](#introduction)
2. [What is a Plugin?](#what-is-a-plugin)
3. [Quick Start](#quick-start)
4. [Plugin Capabilities](#plugin-capabilities)
5. [Registering Plugins](#registering-plugins)
6. [Plugin Scoping](#plugin-scoping)
7. [Mixed Capability Plugins](#mixed-capability-plugins)
8. [Best Practices](#best-practices)
9. [Common Patterns](#common-patterns)
10. [Troubleshooting](#troubleshooting)

---

## Introduction

Plugins are the fundamental building blocks for extending HPD-Agent. They are container classes that provide capabilities to your agent through three types of members:

| Capability | Attribute | Purpose |
|------------|-----------|---------|
| **AI Functions** | `[AIFunction]` | Atomic operations the agent can call |
| **Skills** | `[Skill]` | Workflow containers grouping related functions |
| **SubAgents** | `[SubAgent]` | Specialized child agents for complex tasks |

A single plugin can contain any combination of these capabilities.

---

## What is a Plugin?

A plugin is simply a C# class that contains one or more capabilities. There's no base class to inherit or interface to implement - you just add the appropriate attributes to your methods.

```csharp
public class MyFirstPlugin
{
    [AIFunction]
    [AIDescription("Adds two numbers together")]
    public int Add(int a, int b) => a + b;
}
```

That's it! This class is now a plugin with one AI function.

### Key Concepts

- **Plugins are containers** - They group related capabilities together
- **No inheritance required** - Just add attributes to your methods
- **Compile-time generated** - A source generator creates registration code automatically
- **Flexible composition** - Mix AI Functions, Skills, and SubAgents freely

---

## Quick Start

### Step 1: Create a Plugin Class

```csharp
using System.ComponentModel;

public class CalculatorPlugin
{
    [AIFunction]
    [AIDescription("Adds two numbers")]
    public double Add(
        [Description("First number")] double a,
        [Description("Second number")] double b) => a + b;

    [AIFunction]
    [AIDescription("Multiplies two numbers")]
    public double Multiply(
        [Description("First number")] double a,
        [Description("Second number")] double b) => a * b;
}
```

### Step 2: Register with Your Agent

```csharp
var agent = new AgentBuilder()
    .WithProvider(myProvider)
    .WithPlugin<CalculatorPlugin>()
    .Build();
```

### Step 3: Use It!

The agent can now call `Add` and `Multiply` as tools when responding to user queries.

---

## Plugin Capabilities

Plugins can contain three types of capabilities:

### AI Functions

Atomic operations that the agent can invoke. See [AI Functions Guide](./AIFUNCTIONS.md) for details.

```csharp
[AIFunction]
[AIDescription("Reads a file from disk")]
public async Task<string> ReadFile(string path)
{
    return await File.ReadAllTextAsync(path);
}
```

### Skills

Workflow containers that group related functions with instructions. See [Skills Guide](../skills/SKILLS_GUIDE.md) for details.

```csharp
[Skill]
public Skill FileDebugging() => SkillFactory.Create(
    "FileDebugging",
    "Debug file-related issues",
    "Use ReadFile to examine contents, then analyze for problems",
    "FileSystemPlugin.ReadFile",
    "FileSystemPlugin.GetFileInfo"
);
```

### SubAgents

Specialized child agents that can be invoked as tools. See [SubAgents Guide](../SubAgents/USER_GUIDE.md) for details.

```csharp
[SubAgent(Category = "Specialists")]
public SubAgent CodeReviewer()
{
    return SubAgentFactory.Create(
        "CodeReviewer",
        "Expert code reviewer for quality analysis",
        new AgentConfig
        {
            SystemInstructions = "You are an expert code reviewer...",
            Provider = new ProviderConfig { ModelName = "gpt-4" }
        });
}
```

---

## Registering Plugins

### Basic Registration

```csharp
var agent = new AgentBuilder()
    .WithPlugin<MyPlugin>()
    .Build();
```

### Multiple Plugins

```csharp
var agent = new AgentBuilder()
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<WebSearchPlugin>()
    .WithPlugin<CalculatorPlugin>()
    .Build();
```

### With Instance (for dependency injection)

```csharp
var myPlugin = new DatabasePlugin(connectionString);

var agent = new AgentBuilder()
    .WithPlugin(myPlugin)
    .Build();
```

### Shared Plugin Manager

For sharing plugins across multiple agents:

```csharp
var sharedPlugins = new PluginManager()
    .RegisterPlugin<CommonPlugin>()
    .RegisterPlugin<UtilityPlugin>();

var agent1 = new AgentBuilder()
    .WithPlugins(sharedPlugins)
    .WithPlugin<Agent1SpecificPlugin>()
    .Build();

var agent2 = new AgentBuilder()
    .WithPlugins(sharedPlugins)
    .WithPlugin<Agent2SpecificPlugin>()
    .Build();
```

---

## Collapsing Plugins

Use the `[Collapse]` attribute to organize plugins hierarchically. Collapse plugins appear as a single container until expanded, reducing token consumption.

### Basic Collapsing

```csharp
[Collapse("File system operations for reading, writing, and managing files")]
public class FileSystemPlugin
{
    [AIFunction]
    [AIDescription("Read file contents")]
    public string ReadFile(string path) { ... }

    [AIFunction]
    [AIDescription("Write content to file")]
    public void WriteFile(string path, string content) { ... }

    [AIFunction]
    [AIDescription("Delete a file")]
    public void DeleteFile(string path) { ... }
}
```

**Before expansion:** Agent sees one tool: `FileSystemPlugin - File system operations...`
**After expansion:** Agent sees all three functions: `ReadFile`, `WriteFile`, `DeleteFile`

### With Post-Expansion Instructions

```csharp
[Collapse(
    "Database operations for querying and modifying data",
    postExpansionInstructions: @"
        Always use transactions for multiple operations:
        1. Call BeginTransaction first
        2. Perform your operations
        3. Call CommitTransaction on success
        4. Call RollbackTransaction on failure
    "
)]
public class DatabasePlugin
{
    [AIFunction] public void BeginTransaction() { ... }
    [AIFunction] public void CommitTransaction() { ... }
    [AIFunction] public void RollbackTransaction() { ... }
    [AIFunction] public object Query(string sql) { ... }
}
```

---

## Mixed Capability Plugins

A single plugin can contain any combination of AI Functions, Skills, and SubAgents:

```csharp
[Collapse("Financial analysis tools and workflows")]
public class FinancialAnalysisPlugin
{
    // ═══════════════════════════════════════════════════════════
    // AI FUNCTIONS - Atomic operations
    // ═══════════════════════════════════════════════════════════

    [AIFunction]
    [AIDescription("Calculate current ratio from assets and liabilities")]
    public decimal CalculateCurrentRatio(decimal currentAssets, decimal currentLiabilities)
        => currentAssets / currentLiabilities;

    [AIFunction]
    [AIDescription("Calculate quick ratio (acid-test)")]
    public decimal CalculateQuickRatio(decimal quickAssets, decimal currentLiabilities)
        => quickAssets / currentLiabilities;

    // ═══════════════════════════════════════════════════════════
    // SKILLS - Workflow containers
    // ═══════════════════════════════════════════════════════════

    [Skill]
    public Skill LiquidityAnalysis() => SkillFactory.Create(
        "LiquidityAnalysis",
        "Complete liquidity assessment workflow",
        @"Perform a comprehensive liquidity analysis:
          1. Calculate current ratio
          2. Calculate quick ratio
          3. Compare against industry benchmarks
          4. Provide recommendations",
        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio"
    );

    // ═══════════════════════════════════════════════════════════
    // SUB-AGENTS - Specialized child agents
    // ═══════════════════════════════════════════════════════════

    [SubAgent]
    public SubAgent FinancialAdvisor()
    {
        return SubAgentFactory.Create(
            "FinancialAdvisor",
            "Expert financial advisor for investment guidance",
            new AgentConfig
            {
                SystemInstructions = "You are a certified financial advisor...",
                Provider = new ProviderConfig { ModelName = "gpt-4" }
            });
    }
}
```

---

## Best Practices

### 1. Group Related Functionality

Keep related capabilities in the same plugin:

```csharp
// ✅ Good - related functions together
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(string path) { ... }
    [AIFunction] public void WriteFile(string path, string content) { ... }
    [AIFunction] public void DeleteFile(string path) { ... }
}

// ❌ Bad - unrelated functions mixed
public class MiscPlugin
{
    [AIFunction] public string ReadFile(string path) { ... }
    [AIFunction] public decimal CalculateTax(decimal amount) { ... }
    [AIFunction] public void SendEmail(string to, string body) { ... }
}
```

### 2. Use Collapsing for Large Plugins

If a plugin has more than 3-5 functions, consider collapsing:

```csharp
// Without collapsing: 10 functions visible at all times (high token cost)
public class ComprehensivePlugin { /* 10 functions */ }

// With collapsing: 1 container visible, expand only when needed
[Collapse("Comprehensive toolkit for data processing")]
public class ComprehensivePlugin { /* 10 functions */ }
```

### 3. Provide Clear Descriptions

Help the agent understand when to use each capability:

```csharp
[AIFunction]
[AIDescription("Searches the web for current information. Use this for recent events, news, or facts that may have changed since training.")]
public async Task<string> WebSearch(
    [Description("Search query - be specific for better results")] string query)
{ ... }
```

### 4. Use Appropriate Capability Types

| Use Case | Recommended |
|----------|-------------|
| Single operation | AI Function |
| Multi-step workflow | Skill |
| Complex task requiring reasoning | SubAgent |
| Operations needing different models | SubAgent |

### 5. Keep Functions Focused

Each function should do one thing well:

```csharp
// ✅ Good - focused functions
[AIFunction] public string ReadFile(string path) { ... }
[AIFunction] public void WriteFile(string path, string content) { ... }

// ❌ Bad - function does too much
[AIFunction] public void ProcessFile(string path, string operation, string content) { ... }
```

---

## Common Patterns

### Pattern 1: Function-Focused Plugin

```csharp
public class MathPlugin
{
    [AIFunction] public double Add(double a, double b) => a + b;
    [AIFunction] public double Subtract(double a, double b) => a - b;
    [AIFunction] public double Multiply(double a, double b) => a * b;
    [AIFunction] public double Divide(double a, double b) => a / b;
}
```

### Pattern 2: Skill-Focused Plugin

```csharp
public class AnalysisSkills
{
    [Skill]
    public Skill QuickAnalysis() => SkillFactory.Create(...);

    [Skill]
    public Skill DetailedAnalysis() => SkillFactory.Create(...);

    [Skill]
    public Skill ComprehensiveReport() => SkillFactory.Create(...);
}
```

### Pattern 3: SubAgent-Focused Plugin

```csharp
public class ExpertAgents
{
    [SubAgent]
    public SubAgent TechnicalWriter() => SubAgentFactory.Create(...);

    [SubAgent]
    public SubAgent CodeReviewer() => SubAgentFactory.Create(...);

    [SubAgent]
    public SubAgent DataAnalyst() => SubAgentFactory.Create(...);
}
```

### Pattern 4: Mixed Capabilities

See [Mixed Capability Plugins](#mixed-capability-plugins) section above.

---

## Troubleshooting

### Plugin Not Found

**Error:** `Generated registration class {PluginName}Registration not found`

**Cause:** The source generator didn't process your plugin.

**Solution:**
1. Ensure your plugin class is `public`
2. Ensure methods have appropriate attributes (`[AIFunction]`, `[Skill]`, or `[SubAgent]`)
3. Clean and rebuild your project
4. Check that the HPD-Agent.SourceGenerator package is referenced

### Functions Not Visible

**Cause:** Plugin may be Collapse and not expanded.

**Solution:**
1. Check if plugin has `[Collapse]` attribute
2. If scoped, agent needs to expand the container first
3. Verify registration with `builder.WithPlugin<T>()`

### Skill References Not Resolving

**Cause:** Referenced plugin may not be registered.

**Solution:**
1. Skills auto-register referenced plugins, but verify the plugin exists
2. Check for typos in function reference strings
3. Use fully qualified names: `"PluginName.FunctionName"`

### SubAgent Not Invoked

**Cause:** SubAgent method must follow the correct pattern.

**Solution:**
1. Method must return `SubAgent`
2. Method must have `[SubAgent]` attribute
3. Use `SubAgentFactory.Create()` or `SubAgentFactory.CreateStateful()`

---

## Next Steps

- [AI Functions Guide](./AIFUNCTIONS.md) - Detailed guide on creating AI functions
- [Skills Guide](../skills/SKILLS_GUIDE.md) - Learn about workflow containers
- [SubAgents Guide](../SubAgents/USER_GUIDE.md) - Create specialized child agents
- [API Reference](./API_REFERENCE.md) - Complete API documentation
- [Architecture](./ARCHITECTURE.md) - Technical deep-dive for contributors

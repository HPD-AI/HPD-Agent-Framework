# HPD-Agent Official Documentation

Welcome to the official documentation for **HPD-Agent** - a powerful, production-ready framework for building AI agents with advanced plugin architecture, skills system, and intelligent scoping mechanisms.

## 📚 Documentation Structure

### Getting Started
1. **[Quick Start Guide](01-QuickStart.md)** - Get up and running in 5 minutes
2. **[Core Concepts](02-CoreConcepts.md)** - Understanding plugins, functions, and skills
3. **[Installation](03-Installation.md)** - Installation and setup

### Building Blocks
4. **[Plugins](04-Plugins.md)** - Creating reusable function collections
5. **[Functions](05-Functions.md)** - Defining AI-callable operations
6. **[Skills](06-Skills.md)** - Building guided workflows with context
7. **[Scoping](07-Scoping.md)** - Managing token efficiency with hierarchical organization

### Advanced Features
8. **[Dynamic Metadata](08-DynamicMetadata.md)** - Context-driven descriptions and conditional logic
9. **[Conditional Functions](09-ConditionalFunctions.md)** - Functions that appear/disappear based on configuration
10. **[Permission System](10-Permissions.md)** - User approval for sensitive operations
11. **[Skill Documents](11-SkillDocuments.md)** - SOPs and instruction documents for skills

### Agent Configuration
12. **[Agent Builder](12-AgentBuilder.md)** - Configuring and building agents
13. **[Observability](13-Observability.md)** - Telemetry, logging, and monitoring
14. **[Error Handling](14-ErrorHandling.md)** - Robust error handling and recovery

### Best Practices & Patterns
15. **[Design Patterns](15-DesignPatterns.md)** - Common patterns and best practices
16. **[Performance](16-Performance.md)** - Optimization and token management
17. **[Testing](17-Testing.md)** - Testing plugins, skills, and agents

### Reference
18. **[Attribute Reference](18-AttributeReference.md)** - Complete attribute documentation
19. **[API Reference](19-APIReference.md)** - Full API documentation
20. **[Migration Guide](20-MigrationGuide.md)** - Upgrading from other frameworks

## 🎯 What is HPD-Agent?

HPD-Agent is a framework for building AI agents that solves the "tool explosion" problem while providing **guided workflows at scale**.

### Key Features

✅ **Plugin System** - Organize functions into reusable, composable plugins
✅ **Skills Architecture** - Package workflows with guidance, instructions, and SOPs
✅ **Intelligent Scoping** - Hierarchical organization that saves tokens
✅ **Dynamic Metadata** - Functions adapt to runtime configuration
✅ **Conditional Logic** - Functions/parameters appear based on context
✅ **Permission System** - Human-in-the-loop for sensitive operations
✅ **AOT Compatible** - Source generation, no reflection
✅ **Type Safe** - Compile-time validation of contexts and references

### Core Philosophy

**Traditional Approach:**
```
System Prompt: "You are an analyst. Here's how to do 20 different workflows..."
[5000 tokens of instructions]

Agent sees: 150 functions at once
[30,000 tokens]

= 35,000 tokens before user asks a question
```

**HPD-Agent Approach:**
```
System Prompt: "You are an analyst."
[500 tokens]

Agent sees: 20 collapsed skills
[1,000 tokens]

Agent calls "QuickLiquidityAnalysis" →
  ✓ Gets inline instructions (300 tokens)
  ✓ Gets 3 relevant functions (600 tokens)
  ✓ Can read full SOP if needed (1,500 tokens)

= Guidance delivered just-in-time, at point of use
```

**Result:** Scale to hundreds of workflows without context explosion.

## 🚀 Quick Example

```csharp
// Define a plugin with functions
[Scope("Financial analysis operations")]
public class FinancialAnalysisPlugin
{
    [AIFunction]
    [AIDescription("Calculate current ratio for liquidity analysis")]
    public double CalculateCurrentRatio(double currentAssets, double currentLiabilities)
    {
        return currentAssets / currentLiabilities;
    }

    [AIFunction]
    [AIDescription("Calculate quick ratio (acid test)")]
    public double CalculateQuickRatio(
        double currentAssets,
        double inventory,
        double currentLiabilities)
    {
        return (currentAssets - inventory) / currentLiabilities;
    }
}

// Define a skill that guides the agent
public class FinancialAnalysisSkills
{
    [Skill(Category = "Liquidity Analysis", Priority = 10)]
    public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "QuickLiquidityAnalysis",
            description: "Analyze company's short-term liquidity position",
            instructions: @"
Use this skill to assess if a company can pay short-term obligations.

Steps:
1. Calculate Current Ratio (Current Assets / Current Liabilities)
2. Calculate Quick Ratio (Quick Assets / Current Liabilities)
3. Interpret results:
   - Current Ratio: >1.5 is generally healthy
   - Quick Ratio: >1.0 is conservative

See SOP documentation for detailed procedure.",

            options: new SkillOptions()
                .AddDocumentFromFile(
                    "./SOPs/QuickLiquidityAnalysis.md",
                    "Step-by-step procedure for liquidity analysis"),

            // References to functions (auto-registers plugin!)
            "FinancialAnalysisPlugin.CalculateCurrentRatio",
            "FinancialAnalysisPlugin.CalculateQuickRatio"
        );
    }
}

// Build the agent
var agent = new AgentBuilder()
    .WithInstructions("You are a financial analyst assistant.")
    .WithPlugin<FinancialAnalysisSkills>()  // Auto-registers FinancialAnalysisPlugin!
    .WithDocumentStore(documentStore)
    .WithOpenAI(apiKey, "gpt-4")
    .Build();

// Use the agent
await foreach (var response in agent.RunAsync("Analyze the liquidity of this company..."))
{
    Console.WriteLine(response.Content);
}
```

### What Happens:

1. **Agent sees:** `QuickLiquidityAnalysis` skill (collapsed)
2. **Agent calls:** `QuickLiquidityAnalysis`
3. **Agent receives:**
   - Inline instructions (how to use the skill)
   - Access to 2 functions (CalculateCurrentRatio, CalculateQuickRatio)
   - Link to full SOP document
4. **Agent executes:** Follows the workflow using the provided functions
5. **Agent returns:** Complete analysis with interpretation

## 🏗️ Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                         Agent                                │
│  - System Instructions                                       │
│  - Provider Configuration (OpenAI, Anthropic, etc.)         │
│  - Observability (Telemetry, Logging, Caching)             │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ uses
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    UnifiedScopingManager                     │
│  - Controls visibility of functions/skills                   │
│  - Manages token efficiency through scoping                  │
│  - Handles skill expansion and collapse                      │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ manages
                            ▼
┌──────────────────┬──────────────────┬──────────────────────┐
│     Plugins      │      Skills      │   Scoped Containers  │
├──────────────────┼──────────────────┼──────────────────────┤
│ - Functions      │ - Instructions   │ - Group functions    │
│ - Can have       │ - Documents      │ - Save tokens        │
│   skills inside  │ - Function refs  │ - Post-expansion     │
│ - Optional scope │ - Always scoped  │   instructions       │
└──────────────────┴──────────────────┴──────────────────────┘
                            │
                            │ powered by
                            ▼
┌─────────────────────────────────────────────────────────────┐
│              Source Generator (Build Time)                   │
│  - Analyzes [AIFunction] and [Skill] methods                │
│  - Generates registration code                               │
│  - Validates context references                              │
│  - Resolves skill dependencies                               │
│  - Produces AOT-compatible code                              │
└─────────────────────────────────────────────────────────────┘
```

## 🎓 Learning Path

### For Beginners
1. Start with [Quick Start Guide](01-QuickStart.md)
2. Read [Core Concepts](02-CoreConcepts.md) to understand the fundamentals
3. Follow [Plugins](04-Plugins.md) to create your first plugin
4. Try [Skills](06-Skills.md) to build your first workflow

### For Intermediate Users
1. Learn [Dynamic Metadata](08-DynamicMetadata.md) for adaptive functions
2. Master [Scoping](07-Scoping.md) for token efficiency
3. Explore [Conditional Functions](09-ConditionalFunctions.md) for smart adaptation
4. Read [Design Patterns](15-DesignPatterns.md) for best practices

### For Advanced Users
1. Deep dive into [Agent Builder](12-AgentBuilder.md) for full customization
2. Implement [Observability](13-Observability.md) for production monitoring
3. Study [Performance](16-Performance.md) optimization techniques
4. Review [API Reference](19-APIReference.md) for complete control

## 🤝 Getting Help

- **GitHub Issues:** [Report bugs or request features](https://github.com/yourusername/hpd-agent/issues)
- **Discussions:** [Ask questions and share ideas](https://github.com/yourusername/hpd-agent/discussions)
- **Examples:** See `samples/` directory for complete examples

## 📄 License

HPD-Agent is licensed under the MIT License. See LICENSE file for details.

---

**Ready to get started?** → [Quick Start Guide](01-QuickStart.md)

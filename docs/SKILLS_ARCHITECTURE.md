# HPD-Agent Skills Architecture

## Overview

Skills are a composable abstraction for complex multi-step AI workflows. Unlike simple functions, skills encapsulate:
- **Multiple AI function references** - The tools the skill will use
- **Step descriptions** - What each step does
- **Workflow state** - Parameters and intermediate results
- **Instructions** - Guidance for the AI

Skills enable the agent to perform sophisticated analyses while maintaining clean separation between skills and their underlying functions.

## Core Concepts

### What is a Skill?

A Skill is a **workflow container** that:
1. References multiple AI functions
2. Provides step-by-step instructions
3. Describes what each step accomplishes
4. Encapsulates related operations

**Example: QuickLiquidityAnalysis Skill**
```
Skill: QuickLiquidityAnalysis
├─ References: CalculateCurrentRatio, CalculateQuickRatio, CalculateWorkingCapital
├─ Step 1: "Calculate current ratio (current assets / current liabilities)"
├─ Step 2: "Calculate quick ratio (quick assets / current liabilities)"
├─ Step 3: "Calculate working capital (current assets - current liabilities)"
└─ Use When: "You need to assess a company's short-term liquidity"
```

### Skills vs Functions

| Aspect | Function | Skill |
|--------|----------|-------|
| **Complexity** | Single operation | Multi-step workflow |
| **Scope** | One action | Coordinated actions |
| **Container** | Returns single result | Encapsulates process |
| **Metadata** | ParentPlugin | ReferencedFunctions, ParentSkillContainer |
| **Visibility** | Depends on parent | Depends on skill class scope |
| **Example** | `CalculateCurrentRatio(a, b)` | `QuickLiquidityAnalysis(company)` |

## Architecture

### Component Hierarchy

```
Skill Class (Container)
├─ [Scope] attribute (optional, class-level scoping)
├─ Skill 1
│  ├─ ReferencedFunctions
│  │  ├─ PluginName.Function1
│  │  ├─ PluginName.Function2
│  │  └─ PluginName.Function3
│  ├─ Steps
│  │  ├─ "Step 1 description"
│  │  ├─ "Step 2 description"
│  │  └─ "Step 3 description"
│  └─ Usage Guidance
├─ Skill 2
│  ├─ ReferencedFunctions
│  └─ Steps
└─ Skill N
```

### Data Flow

```
User Request: "Analyze company liquidity"
    ↓
Agent selects: QuickLiquidityAnalysis skill
    ↓
UnifiedScopingManager checks:
├─ Is skill visible? (check parent scope)
├─ What functions does it reference?
└─ Are those functions available?
    ↓
Skill Expanded by Agent
    ↓
Agent executes steps:
1. Call CalculateCurrentRatio
2. Call CalculateQuickRatio
3. Call CalculateWorkingCapital
    ↓
Skill completes with results
```

## Skill Class Definition

### Basic Structure

```csharp
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis(
        string companyName,
        decimal currentAssets,
        decimal quickAssets,
        decimal currentLiabilities)
    {
        return new Skill
        {
            Name = "QuickLiquidityAnalysis",
            Description = "Analyzes a company's short-term liquidity",
            Instructions = new[]
            {
                "Step 1: Calculate current ratio (current assets / current liabilities)",
                "Step 2: Calculate quick ratio (quick assets / current liabilities)",
                "Step 3: Calculate working capital (current assets - current liabilities)",
                "Step 4: Interpret results"
            },
            ReferencedPlugins = new[] { "FinancialAnalysisPlugin" },
            ReferencedFunctions = new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio",
                "FinancialAnalysisPlugin.CalculateWorkingCapital"
            },
            UsageContext = "Use when you need to assess a company's ability to meet short-term obligations"
        };
    }

    [Skill]
    public Skill DetailedAnalysis(string balanceSheetData)
    {
        return new Skill
        {
            Name = "DetailedAnalysis",
            Description = "Performs comprehensive financial analysis",
            Instructions = new[] { ... },
            ReferencedPlugins = new[] { "FinancialAnalysisPlugin" },
            ReferencedFunctions = new[] { ... },
            UsageContext = "Use for in-depth financial analysis"
        };
    }
}
```

### With Scope (Grouped Skills)

```csharp
[Scope(
    "Advanced financial analysis workflows combining multiple analysis techniques",
    postExpansionInstructions: "These skills reference FinancialAnalysisPlugin functions. " +
                              "Expand this group to see individual skills.")]
public class AdvancedAnalysisSkills
{
    [Skill]
    public Skill AdvancedQuickAnalysis(...) { ... }
    
    [Skill]
    public Skill AdvancedDetailedAnalysis(...) { ... }
    
    // Without [Scope], skills are visible immediately
    // With [Scope], skills hidden until scope expanded
}
```

## Skill Object Definition

### Skill Class

```csharp
public class Skill
{
    /// <summary>
    /// Unique identifier for this skill
    /// Must match method name or be explicitly set
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Brief description shown when skill is visible
    /// Use to help agent understand what skill does
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Step-by-step instructions for executing this skill
    /// Each step guides the agent through the workflow
    /// </summary>
    public string[] Instructions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Names of plugins this skill uses
    /// Example: "FinancialAnalysisPlugin", "DataExtractionPlugin"
    /// </summary>
    public string[] ReferencedPlugins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Fully qualified function names this skill references
    /// Format: "PluginName.FunctionName"
    /// Example: "FinancialAnalysisPlugin.CalculateCurrentRatio"
    /// </summary>
    public string[] ReferencedFunctions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Guidance on when to use this skill
    /// Helps agent decide when to select this skill
    /// </summary>
    public string UsageContext { get; set; } = string.Empty;
}
```

### Property Details

| Property | Type | Purpose | Example |
|----------|------|---------|---------|
| `Name` | `string` | Unique skill identifier | `"QuickLiquidityAnalysis"` |
| `Description` | `string` | What the skill does (brief) | `"Analyzes short-term liquidity"` |
| `Instructions` | `string[]` | Step-by-step execution steps | `["Calculate ratio", "Interpret result"]` |
| `ReferencedPlugins` | `string[]` | Plugins this skill uses | `["FinancialAnalysisPlugin"]` |
| `ReferencedFunctions` | `string[]` | Functions this skill references | `["FinancialPlugin.CalcRatio"]` |
| `UsageContext` | `string` | When to use this skill | `"Use for quick liquidity check"` |

## Visibility Rules

### Skill Class Without [Scope]

```
Always Visible
├─ Skill 1 (visible)
├─ Skill 2 (visible)
└─ Skill 3 (visible)

All skills shown immediately to agent
```

### Skill Class With [Scope]

```
Before Expansion:
├─ SkillClassName (container)
│  ✅ Visible
└─ Individual skills (hidden)

After Expanding Scope:
├─ SkillClassName (container)
├─ Skill 1 (visible)
├─ Skill 2 (visible)
└─ Skill 3 (visible)

Scope container expanded → Individual skills appear
```

### Referenced Functions Visibility

```
When Skill NOT Expanded:
├─ CalculateCurrentRatio (hidden)
├─ CalculateQuickRatio (hidden)
└─ CalculateWorkingCapital (hidden)

Reason: Functions only available when skill is actually executed

When Skill Expanded (by agent):
├─ CalculateCurrentRatio (visible)
├─ CalculateQuickRatio (visible)
└─ CalculateWorkingCapital (visible)

Agent can now see what functions skill will use
```

## Skill Registration

### In AgentBuilder

```csharp
// Method 1: Register plugin that contains skills
builder.WithPlugin<FinancialAnalysisPlugin>();

// This automatically registers:
// - FinancialAnalysisPlugin (if has [Scope])
// - All AI functions in plugin
// - Any skills defined in associated skill classes
```

### Skill Discovery

Skills are discovered during plugin registration:

1. **Source Generator** detects `[Skill]` methods in skill classes
2. **Generates metadata** including referenced functions
3. **Creates Skill containers** with proper metadata
4. **Registers with agent** during plugin registration

## Patterns & Examples

### Pattern 1: Simple Skill Set

```csharp
public class BasicAnalysisSkills
{
    [Skill]
    public Skill SimpleAnalysis(string data)
    {
        return new Skill
        {
            Name = "SimpleAnalysis",
            Description = "Basic analysis of data",
            Instructions = new[]
            {
                "Step 1: Extract data",
                "Step 2: Calculate metrics",
                "Step 3: Return results"
            },
            ReferencedPlugins = new[] { "AnalysisPlugin" },
            ReferencedFunctions = new[]
            {
                "AnalysisPlugin.ExtractData",
                "AnalysisPlugin.Calculate"
            },
            UsageContext = "Use for routine analysis"
        };
    }
}

// Result: SimpleAnalysis skill immediately visible
```

### Pattern 2: Grouped Skills

```csharp
[Scope("Advanced analysis capabilities")]
public class AdvancedAnalysisSkills
{
    [Skill]
    public Skill AdvancedAnalysis(...) { ... }
    
    [Skill]
    public Skill PredictiveAnalysis(...) { ... }
}

// Result: Before expansion
//   ✅ AdvancedAnalysisSkills (scope container)
//   ❌ AdvancedAnalysis (hidden)
//   ❌ PredictiveAnalysis (hidden)

// Result: After expanding scope
//   ✅ AdvancedAnalysisSkills
//   ✅ AdvancedAnalysis (visible)
//   ✅ PredictiveAnalysis (visible)
```

### Pattern 3: Multi-Step Workflow

```csharp
[Skill]
public Skill ComprehensiveFinancialReview(string companyName, string financialData)
{
    return new Skill
    {
        Name = "ComprehensiveFinancialReview",
        Description = "Performs complete financial assessment",
        Instructions = new[]
        {
            "Step 1: Parse balance sheet",
            "Step 2: Calculate liquidity ratios",
            "Step 3: Analyze capital structure",
            "Step 4: Evaluate profitability",
            "Step 5: Generate recommendations"
        },
        ReferencedPlugins = new[] { "FinancialAnalysisPlugin", "ReportGeneratorPlugin" },
        ReferencedFunctions = new[]
        {
            "FinancialAnalysisPlugin.ParseBalanceSheet",
            "FinancialAnalysisPlugin.CalculateCurrentRatio",
            "FinancialAnalysisPlugin.CalculateDebtToEquityRatio",
            "FinancialAnalysisPlugin.CalculateProfitMargin",
            "ReportGeneratorPlugin.GenerateReport"
        },
        UsageContext = "Use when you need a complete financial evaluation"
    };
}

// This skill:
// - References 5 functions from 2 plugins
// - 5 step instructions guide execution
// - Clear usage context helps agent select it
```

### Pattern 4: Interdependent Skills

```csharp
[Scope("Financial analysis skills")]
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis(...)
    {
        // References: CalculateCurrentRatio, CalculateQuickRatio, CalculateWorkingCapital
        // Use: Quick assessment of liquidity
    }

    [Skill]
    public Skill SolvencyAnalysis(...)
    {
        // References: CalculateDebtToEquityRatio, CalculateInterestCoverage
        // Use: Assess long-term solvency
    }

    [Skill]
    public Skill ProfitabilityAnalysis(...)
    {
        // References: CalculateProfitMargin, CalculateROE, CalculateROA
        // Use: Evaluate profit-generating ability
    }
}

// Result: Three complementary skills grouped under one scope
// Agent can select any skill independently for specific analysis type
```

## Metadata Structure

### Skill Container Metadata

Generated by source generator for each `[Skill]` method:

```csharp
new Dictionary<string, object>
{
    ["IsContainer"] = true,           // Mark as container
    ["IsSkill"] = true,               // Mark as skill (not plugin)
    ["ReferencedFunctions"] = new[]   // Functions this skill uses
    {
        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio",
        "FinancialAnalysisPlugin.CalculateWorkingCapital"
    },
    ["ReferencedPlugins"] = new[]     // Plugins this skill uses
    {
        "FinancialAnalysisPlugin"
    },
    ["ParentSkillContainer"] = "FinancialAnalysisSkills"  // Parent scope (if has [Scope])
}
```

### Skill Class Scope Metadata

Generated when skill class has `[Scope]` attribute:

```csharp
new Dictionary<string, object>
{
    ["IsContainer"] = true,        // Mark as container
    ["IsScope"] = true,            // Mark as scope
    ["SkillNames"] = new[]         // Skills in this scope
    {
        "QuickLiquidityAnalysis",
        "SolvencyAnalysis",
        "ProfitabilityAnalysis"
    },
    ["SkillCount"] = 3             // Number of skills
}
```

## Source Generator Integration

### Detection Phase

```
1. Scan for classes with [Skill] methods
2. For each [Skill] method:
   ├─ Extract method name (becomes skill name)
   ├─ Parse method signature (parameters)
   ├─ Analyze method body (for referenced functions?)
   └─ Detect parent class [Scope] attribute
3. Generate registration code
```

### Code Generation

Generated code structure:
```csharp
private static AIFunction Create{SkillName}Container()
{
    return HPDAIFunctionFactory.Create(
        async (args, ct) => { /* execution */ },
        new HPDAIFunctionFactoryOptions
        {
            Name = "{SkillName}",
            Description = "{From attribute}",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["IsContainer"] = true,
                ["IsSkill"] = true,
                ["ReferencedFunctions"] = new[] { ... },
                ["ReferencedPlugins"] = new[] { ... },
                ["ParentSkillContainer"] = "{SkillClassName}"  // If scoped
            }
        });
}
```

## Execution Flow

### How Agent Uses Skills

```
1. Agent sees Skill Container
   "QuickLiquidityAnalysis - Analyzes company's short-term liquidity"

2. Agent selects skill (expands)

3. UnifiedScopingManager shows referenced functions
   ✅ CalculateCurrentRatio
   ✅ CalculateQuickRatio
   ✅ CalculateWorkingCapital

4. Agent executes steps:
   Step 1: Call CalculateCurrentRatio(assets, liabilities)
   Step 2: Call CalculateQuickRatio(quickAssets, liabilities)
   Step 3: Call CalculateWorkingCapital(assets, liabilities)

5. Agent synthesizes results and completes skill
```

## Best Practices

### 1. Clear Naming

```csharp
✅ GOOD
public Skill QuickLiquidityAnalysis(...) { ... }
public Skill DetailedSolvencyAnalysis(...) { ... }

❌ AVOID
public Skill Analysis(...) { ... }
public Skill Check(...) { ... }
```

### 2. Explicit ReferencedFunctions

```csharp
✅ GOOD
ReferencedFunctions = new[]
{
    "FinancialAnalysisPlugin.CalculateCurrentRatio",
    "FinancialAnalysisPlugin.CalculateQuickRatio",
    "FinancialAnalysisPlugin.CalculateWorkingCapital"
}

❌ AVOID
ReferencedFunctions = new[] { "FinancialAnalysisPlugin.*" }
```

### 3. Meaningful Instructions

```csharp
✅ GOOD
Instructions = new[]
{
    "Step 1: Calculate current ratio (current assets / current liabilities)",
    "Step 2: Calculate quick ratio (quick assets / current liabilities)",
    "Step 3: Interpret results based on industry benchmarks"
}

❌ AVOID
Instructions = new[]
{
    "Do step 1",
    "Do step 2"
}
```

### 4. Helpful Usage Context

```csharp
✅ GOOD
UsageContext = "Use when you need to quickly assess a company's ability to meet " +
               "short-term obligations. Particularly useful for creditor analysis."

❌ AVOID
UsageContext = "Do liquidity analysis"
```

### 5. Logical Skill Grouping

```csharp
✅ GOOD
[Scope("Liquidity analysis skills")]
public class LiquiditySkills { ... }

[Scope("Profitability analysis skills")]
public class ProfitabilitySkills { ... }

❌ AVOID
[Scope("Analysis")]
public class AllAnalysisSkills
{
    // Liquidity, solvency, profitability, efficiency all mixed together
}
```

## Testing Skills

### Test Skill Visibility

```csharp
[Fact]
public void SkillsWithoutScope_AreAlwaysVisible()
{
    // Arrange
    var tools = CreateTestTools(skillsHaveScope: false);
    var manager = new UnifiedScopingManager(tools);

    // Act
    var visible = manager.GetToolsForAgentTurn(tools, ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);

    // Assert
    visible.Should().Contain(t => t.Name == "QuickLiquidityAnalysis");
    visible.Should().Contain(t => t.Name == "SolvencyAnalysis");
}
```

### Test Skill Expansion

```csharp
[Fact]
public void ExpandingSkill_ShowsReferencedFunctions()
{
    // Arrange
    var tools = CreateTestTools();
    var manager = new UnifiedScopingManager(tools);

    // Act
    var visible = manager.GetToolsForAgentTurn(
        tools,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet.Create("QuickLiquidityAnalysis"));

    // Assert
    visible.Should().Contain(t => t.Name == "CalculateCurrentRatio");
    visible.Should().Contain(t => t.Name == "CalculateQuickRatio");
}
```

## Troubleshooting

### Skill Not Visible

**Symptoms:**
- Skill doesn't appear in agent's tool list
- Parent scope visible but individual skills hidden

**Causes:**
- Parent skill class has `[Scope]` and scope not expanded
- Skill references non-existent plugin
- Source generator didn't detect `[Skill]` attribute

**Solutions:**
```csharp
// 1. Check parent has [Scope]
[Scope("Description")]  // ← Required if you want scoped visibility
public class MySkills { ... }

// 2. Verify [Skill] attribute present
[Skill]  // ← Don't forget this
public Skill MySkill(...) { ... }

// 3. Rebuild to regenerate source
dotnet clean
dotnet build
```

### Referenced Functions Not Available

**Symptoms:**
- Skill visible but referenced functions don't appear when expanded
- "Function not found" errors

**Causes:**
- Function name in ReferencedFunctions doesn't match actual function
- Plugin not registered
- Function name case mismatch

**Solutions:**
```csharp
// Verify format: "PluginName.FunctionName"
ReferencedFunctions = new[]
{
    "FinancialAnalysisPlugin.CalculateCurrentRatio",  // ← Correct
    // NOT: "calculateCurrentRatio" (wrong case)
    // NOT: "FinancialAnalysisPlugin.CalcRatio" (wrong function name)
}

// Ensure plugin is registered
builder.WithPlugin<FinancialAnalysisPlugin>();
```

## Migration Guide

### From v0 Initial to v0 Current

**[Scope] on Skill Classes:**

```csharp
// Before: Implicit scoping
public class FinancialAnalysisSkills { ... }
// Skills always visible

// After: Optional explicit scoping
[Scope("Financial analysis workflows")]
public class FinancialAnalysisSkills { ... }
// Skills hidden until scope expanded
```

**No code changes required** - existing skills continue to work as before.

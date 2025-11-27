# HPD-Agent Plugin & Skills Integration Guide

## Quick Navigation

- **[Scoping System](./SCOPING_SYSTEM.md)** - How plugins and skills are hidden/shown
- **[Skills Architecture](./SKILLS_ARCHITECTURE.md)** - How skills encapsulate workflows

## The Complete Picture

### How It All Fits Together

```
User Request to Agent
    ↓
Agent needs tools
    ↓
ToolVisibilityManager.GetToolsForAgentTurn()
    ├─ 1. What plugins are scoped? [Scope] attribute
    ├─ 2. Which plugins are explicit? .WithPlugin<T>()
    ├─ 3. Which functions are referenced by skills?
    └─ 4. Return visible tools based on priorities
    ↓
Agent sees tool list
├─ Plugin containers (if scoped)
├─ Individual functions (if not scoped)
├─ Skill containers
├─ Referenced functions (if skill expanded)
└─ Non-scoped utilities
    ↓
Agent selects a tool
    ├─ If plugin container: Agent expands to see functions
    ├─ If skill container: Agent expands to see referenced functions
    └─ If function: Agent calls it directly
    ↓
Tool execution
    ├─ Call function with parameters
    ├─ Receive result
    └─ Continue workflow
```

## Key Relationships

### Plugin → Functions

```
Plugin (Container)
├─ Has AI functions
├─ Optional [Scope] attribute
├─ Optional explicit registration via .WithPlugin<T>()
└─ Functions hidden if scoped and not expanded

Example:
[Scope("Financial Analysis")]
public class FinancialAnalysisPlugin
{
    [AIFunction]
    public decimal CalculateCurrentRatio(...) { ... }
    
    [AIFunction]
    public decimal CalculateQuickRatio(...) { ... }
}
```

### Skill Class → Skills → Referenced Functions

```
Skill Class (Container)
├─ Optional [Scope] attribute for class-level grouping
├─ Skill 1
│  └─ References Functions from Plugins
├─ Skill 2
│  └─ References Functions from Plugins
└─ Skill N
   └─ References Functions from Plugins

Example:
[Scope("Financial Analysis Workflows")]
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis(...)
    {
        return new Skill
        {
            ReferencedFunctions = new[]
            {
                "FinancialAnalysisPlugin.CalculateCurrentRatio",
                "FinancialAnalysisPlugin.CalculateQuickRatio"
            }
        };
    }
}
```

## Registration Flow

### Step 1: Plugin Registration

```csharp
builder.WithPlugin<FinancialAnalysisPlugin>();
```

What happens:
1. Plugin added to PluginManager
2. Plugin name added to `_explicitlyRegisteredPlugins`
3. All AI functions extracted from plugin class
4. If plugin has `[Scope]`:
   - Container function created (hides functions)
5. Functions registered with scoping metadata

### Step 2: Skill Registration

When plugin is registered, source generator also:
1. Detects `[Skill]` methods in plugin class (or linked skill class)
2. Creates Skill container for each skill
3. Adds metadata about referenced functions
4. If skill class has `[Scope]`:
   - Scope container created (hides individual skills)

### Step 3: Scoping Setup

During Agent initialization:
```csharp
_scopingManager = new ToolVisibilityManager(
    initialTools,                        // All functions & skills
    config.ExplicitlyRegisteredPlugins, // Explicitly registered plugins
    logger);
```

ToolVisibilityManager:
1. Analyzes all tools
2. Detects containers and relationships
3. Tracks which plugins are explicit
4. Ready to filter visibility based on expansion state

## Visibility Decision Tree

### For Regular Functions

```
Function F in Plugin P?
├─ YES, P has [Scope]?
│  ├─ YES → HIDE (until P expanded)
│  └─ NO → Check next
├─ P explicitly registered?
│  ├─ YES → SHOW
│  └─ NO → Check next
├─ F referenced by any Skill S?
│  ├─ YES → HIDE (until S expanded)
│  └─ NO → Check next
├─ P is auto-registered via skills?
│  ├─ YES → HIDE (orphan)
│  └─ NO → SHOW
```

### For Skill Containers

```
Skill S in Class C?
├─ C has [Scope]?
│  ├─ YES → HIDE until C expanded
│  └─ NO → SHOW
```

### For Referenced Functions

```
When Skill S expanded?
├─ Show all functions in S.ReferencedFunctions
```

## Common Scenarios

### Scenario A: Simple Plugin (No Scoping)

```
Registration:
  builder.WithPlugin<SimplePlugin>();

Plugin Code:
  public class SimplePlugin
  {
      [AIFunction] public void DoWork() { ... }
      [AIFunction] public void Check() { ... }
  }

Result:
  ✅ DoWork (visible)
  ✅ Check (visible)
  
Why: No [Scope], so all functions immediately visible
```

### Scenario B: Organized Plugin (With Scoping)

```
Registration:
  builder.WithPlugin<FinancialPlugin>();

Plugin Code:
  [Scope("Financial calculations")]
  public class FinancialPlugin
  {
      [AIFunction] public decimal CalculateRatio() { ... }
      [AIFunction] public decimal CalculateMargin() { ... }
  }

Before Expansion:
  ✅ FinancialPlugin (container)
  ❌ CalculateRatio (hidden)
  ❌ CalculateMargin (hidden)

After Expanding Plugin:
  ✅ FinancialPlugin
  ✅ CalculateRatio (visible)
  ✅ CalculateMargin (visible)

Why: [Scope] creates container, hiding functions until expanded
```

### Scenario C: Organized Skills (No Scope)

```
Skill Class:
  public class AnalysisSkills
  {
      [Skill]
      public Skill QuickAnalysis() { ... }
      
      [Skill]
      public Skill DetailedAnalysis() { ... }
  }

Result:
  ✅ QuickAnalysis (visible)
  ✅ DetailedAnalysis (visible)

Why: No [Scope] on class, so skills immediately visible
```

### Scenario D: Organized Skills (With Scope)

```
Skill Class:
  [Scope("Analysis workflows")]
  public class AnalysisSkills
  {
      [Skill]
      public Skill QuickAnalysis() { ... }
      
      [Skill]
      public Skill DetailedAnalysis() { ... }
  }

Before Expansion:
  ✅ AnalysisSkills (scope container)
  ❌ QuickAnalysis (hidden)
  ❌ DetailedAnalysis (hidden)

After Expanding Scope:
  ✅ AnalysisSkills
  ✅ QuickAnalysis (visible)
  ✅ DetailedAnalysis (visible)

Why: [Scope] hides skills until scope expanded
```

### Scenario E: Skills + Functions Integration

```
Plugin:
  [Scope("Financial Analysis")]
  public class FinancialPlugin
  {
      [AIFunction]
      public decimal CalculateCurrentRatio(...) { ... }
      
      [AIFunction]
      public decimal CalculateQuickRatio(...) { ... }
  }

Skill:
  public class LiquiditySkills
  {
      [Skill]
      public Skill QuickLiquidityAnalysis(...)
      {
          return new Skill
          {
              ReferencedFunctions = new[]
              {
                  "FinancialPlugin.CalculateCurrentRatio",
                  "FinancialPlugin.CalculateQuickRatio"
              }
          };
      }
  }

Before Any Expansion:
  ✅ FinancialPlugin (container)
  ✅ QuickLiquidityAnalysis (skill)
  ❌ CalculateCurrentRatio (hidden, in scoped plugin)
  ❌ CalculateQuickRatio (hidden, in scoped plugin)

After Agent Expands Skill:
  ✅ FinancialPlugin (container, still Collapse)
  ✅ QuickLiquidityAnalysis (skill, still visible)
  ✅ CalculateCurrentRatio (now visible, skill references it)
  ✅ CalculateQuickRatio (now visible, skill references it)

After Agent Expands Plugin:
  ✅ FinancialPlugin (container, expanded)
  ✅ QuickLiquidityAnalysis (skill)
  ✅ CalculateCurrentRatio (visible, from plugin)
  ✅ CalculateQuickRatio (visible, from plugin)

Why: Skill expansion shows referenced functions,
     Plugin expansion shows all functions
```

## Development Workflow

### Adding a New Plugin

```
1. Create Plugin Class
   ├─ Add [Scope] if you want organized display
   ├─ Add [AIFunction] methods
   └─ Source generator creates container if needed

2. Register in AgentBuilder
   builder.WithPlugin<MyPlugin>();

3. Test Visibility
   ├─ Without expansion: Should see container or functions
   ├─ With expansion: Should see all functions
```

### Adding Skills to Plugin

```
1. Create Skill Class
   ├─ Add [Scope] if you want organized display
   ├─ Add [Skill] methods
   └─ Each skill returns Skill object

2. Define ReferencedFunctions
   └─ List all plugin functions this skill uses

3. Register
   └─ When plugin registered, skills auto-discovered

4. Test Visibility
   ├─ Skill should be visible
   ├─ Referenced functions hidden until skill expanded
```

### Testing Visibility Changes

```csharp
// In ToolVisibilityManagerTests.cs
[Fact]
public void MyNewScenario_Works()
{
    // Arrange
    var tools = CreateTestTools(
        pluginHasScope: true,
        skillsHaveScope: false,
        includePluginFunctions: true,
        includeSkills: true);
    
    var explicit = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "MyPlugin");
    
    var manager = new ToolVisibilityManager(tools, explicit);

    // Act
    var visible = manager.GetToolsForAgentTurn(
        tools.ToList(),
        ImmutableHashSet<string>.Empty,  // Expanded plugins
        ImmutableHashSet<string>.Empty); // Expanded skills

    // Assert
    visible.Should().Contain(t => t.Name == "ExpectedTool");
    visible.Should().NotContain(t => t.Name == "HiddenTool");
}
```

## Performance Tips

### 1. Minimize Container Nesting

```csharp
❌ AVOID - Deep nesting
[Scope("Outer")]
public class OuterSkills
{
    [Skill]
    public Skill OuterSkill()
    {
        return new Skill
        {
            ReferencedFunctions = new[]
            {
                "Plugin1.Func",
                "Plugin2.Func",
                "Plugin3.Func"
                // ... 100 more functions
            }
        };
    }
}

✅ GOOD - Logical grouping
[Scope("Financial Analysis")]
public class FinancialSkills { ... }

[Scope("Data Processing")]
public class DataSkills { ... }

[Scope("Reporting")]
public class ReportingSkills { ... }
```

### 2. Clear Referenced Functions

```csharp
❌ AVOID - Unclear references
ReferencedFunctions = new[] { "*" }

✅ GOOD - Explicit list
ReferencedFunctions = new[]
{
    "FinancialPlugin.CalculateCurrentRatio",
    "FinancialPlugin.CalculateQuickRatio"
}
```

### 3. Avoid Redundant Scoping

```csharp
❌ AVOID - Scoping at multiple levels
[Scope("Analysis")]
public class AnalysisSkills
{
    [Scope("Liquidity Analysis")]  // Extra scoping
    [Skill]
    public Skill QuickLiquidity() { ... }
}

✅ GOOD - Scope at class level only
[Scope("Analysis")]
public class AnalysisSkills
{
    [Skill]
    public Skill QuickLiquidity() { ... }
    
    [Skill]
    public Skill DetailedAnalysis() { ... }
}
```

## Debugging Tips

### Enable Debug Output

The scoping manager logs detailed info:

```csharp
[ToolVisibilityManager] 🔍 First Pass - Analyzing 22 tools
   📦 Scope Container: FinancialAnalysisSkills
   🔌 Plugin Container: FinancialAnalysisPlugin
   🎯 Skill Container: QuickLiquidityAnalysis
   ...
```

Look for emoji indicators:
- 📦 Scope container
- 🔌 Plugin container  
- 🎯 Skill container
- ❌ Hidden function

### Common Issues

| Issue | Debug Check |
|-------|-------------|
| Skill not visible | Check if parent [Scope] and not expanded |
| Functions not showing | Check if plugin [Scope] and not expanded |
| Referenced functions missing | Check ReferencedFunctions list accuracy |
| Orphan functions visible | Check if plugin should be scoped |

## Reference

- [Scoping System Details](./SCOPING_SYSTEM.md)
- [Skills Architecture Details](./SKILLS_ARCHITECTURE.md)
- Test Suite: `test/HPD-Agent.Tests/Scoping/ToolVisibilityManagerTests.cs`

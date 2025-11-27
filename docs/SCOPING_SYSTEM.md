# HPD-Agent Unified Scoping System

## Overview

The Unified Scoping System manages hierarchical visibility of AI functions to reduce token consumption and cognitive load. It handles two distinct display scenarios:

1. **Plugin Scoping** - Hide/show plugin functions based on container expansion
2. **Skill Scoping** - Hide/show skill containers and individual skills based on class-level and skill-level expansion

> **Important:** This is a display/UI scoping system. For permission/security scoping, see [Filter System](#filter-system-separate).

## Architecture

### Core Components

```
ToolVisibilityManager (Main Orchestrator)
├── First Pass Analysis
│   ├── Detect scope containers (IsScopeContainer)
│   ├── Detect plugin containers (IsPluginContainer)
│   ├── Detect skill containers (IsSkillContainer)
│   └── Track parent-child relationships
├── Categorization Logic
│   ├── Scoped containers
│   ├── Plugin containers
│   ├── Skill containers
│   ├── Non-scoped functions
│   ├── Expanded plugin functions
│   └── Expanded skill functions
└── GetToolsForAgentTurn() - Main visibility method
    ├── Inputs: allTools, expandedPlugins, expandedSkills
    └── Output: Visible tools for current agent turn
```

### Data Flow

```
Agent.GetToolsForAgentTurn()
    ↓
ToolVisibilityManager.GetToolsForAgentTurn()
    ↓
[First Pass: Analyze all tools]
    ├─ IsScopeContainer? → Track as skill scope
    ├─ IsPluginContainer? → Track as plugin with container
    ├─ IsSkillContainer? → Track skill references
    └─ Regular function? → Categorize based on parent
    ↓
[Second Pass: Categorize tools]
    ├─ Check PRIORITY 1: Plugin has [Scope]?
    ├─ Check PRIORITY 2: Plugin explicitly registered?
    ├─ Check PRIORITY 3: Function referenced by skill?
    ├─ Check PRIORITY 4: Orphan in auto-registered plugin?
    └─ Otherwise: Show
    ↓
[Return visible tools]
```

## The [Scope] Attribute

Universal attribute for marking containers. Replaces deprecated `[PluginScope]`.

### Usage

**Plugin Scoping:**
```csharp
[Scope(
    "Financial analysis functions for balance sheet and ratio calculations",
    postExpansionInstructions: "Available functions: CalculateCurrentRatio, CalculateQuickRatio, ...")]
public class FinancialAnalysisPlugin
{
    [AIFunction]
    public decimal CalculateCurrentRatio(decimal assets, decimal liabilities) { ... }
    
    [AIFunction]
    public decimal CalculateQuickRatio(decimal quickAssets, decimal liabilities) { ... }
}
```

**Skill Class Scoping:**
```csharp
[Scope(
    "Advanced financial analysis workflows combining multiple analysis techniques",
    postExpansionInstructions: "Use these skills for comprehensive financial analysis")]
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis(...) { ... }
    
    [Skill]
    public Skill CapitalStructureAnalysis(...) { ... }
}
```

### Parameters

| Parameter | Type | Purpose |
|-----------|------|---------|
| `description` | `string` | Shown to agent when container visible. Describes what functions/skills are inside. |
| `postExpansionInstructions` | `string?` | Instructions shown ONLY after expansion. Use for best practices, warnings, or workflow guidance. |

## Visibility Rules

### Priority Order (Most to Least Specific)

```
PRIORITY 1: Plugin has [Scope] attribute
├─ If YES → HIDE functions until plugin container expanded
├─ Applies EVEN IF plugin is explicitly registered
└─ Reason: Explicit attribute takes precedence over implicit registration

PRIORITY 2: Plugin explicitly registered via .WithPlugin<T>()
├─ If YES (and no [Scope]) → SHOW all functions immediately
├─ Reason: Explicit registration signals user wants all functions visible
└─ Example: builder.WithPlugin<FinancialAnalysisPlugin>()

PRIORITY 3: Function referenced by skill
├─ If YES → HIDE until skill is expanded
├─ Reason: Skill encapsulates these functions
└─ Check: ReferencedFunctions metadata on skill container

PRIORITY 4: Orphan function in auto-registered plugin
├─ If YES (plugin NOT explicitly registered) → HIDE
├─ Reason: Function not referenced by any skill
└─ Exception: If plugin has no [Scope], orphans are visible

PRIORITY 5: Otherwise → SHOW
├─ Non-scoped functions
├─ Orphans in explicitly registered plugins without [Scope]
└─ Global utility functions
```

### Skill Visibility Rules

When skill class has `[Scope]`:
```
If skill scope NOT expanded → Hide individual skills
If skill scope expanded → Show individual skills
If skill parent scope doesn't exist → Show as standalone
```

When skill class has NO `[Scope]`:
```
→ Show individual skills immediately
```

## Explicit vs Implicit Plugin Registration

### Explicit Registration
```csharp
builder.WithPlugin<FinancialAnalysisPlugin>()
```
- Tracked in `AgentConfig.ExplicitlyRegisteredPlugins`
- Passed to `ToolVisibilityManager` constructor
- Used only for visibility logic (no impact on function access)

### Implicit Registration
```csharp
// Plugin registered via skill that references it
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis(...)
    {
        // References FinancialAnalysisPlugin.CalculateCurrentRatio
        // Plugin auto-registered when skill is registered
    }
}
```
- Not explicitly registered
- Functions NOT in `ExplicitlyRegisteredPlugins`
- Subject to orphan hiding (functions not referenced by skills are hidden)

## Test Scenarios

### Scenario 1: Both Scoped & Both Explicit
```
Setup:
  - Plugin has [Scope]
  - Skills have [Scope]
  - Both explicitly registered

Expected:
  ✅ FinancialAnalysisPlugin (container)
  ✅ FinancialAnalysisSkills (container)
  ❌ Plugin functions (hidden until plugin expanded)
  ❌ Individual skills (hidden until skill scope expanded)
```

### Scenario 2: Plugin Not Scoped, Skills Scoped
```
Setup:
  - Plugin NO [Scope]
  - Skills have [Scope]
  - Both explicitly registered

Expected:
  ✅ CalculateCurrentRatio
  ✅ CalculateQuickRatio
  ✅ ... (all plugin functions)
  ✅ FinancialAnalysisSkills (container)
  ❌ Individual skills (hidden until expanded)
```

### Scenario 3: Plugin Scoped, Skills Not Scoped
```
Setup:
  - Plugin has [Scope]
  - Skills NO [Scope]
  - Both explicitly registered

Expected:
  ✅ FinancialAnalysisPlugin (container)
  ✅ QuickLiquidityAnalysis
  ✅ CapitalStructureAnalysis
  ✅ ... (all skills)
  ❌ Plugin functions (hidden until plugin expanded)
```

### Scenario 4: Only Skills Explicit, No Scope
```
Setup:
  - Plugin NOT explicitly registered (auto-registered by skills)
  - Skills have NO [Scope]

Expected:
  ✅ QuickLiquidityAnalysis
  ✅ CapitalStructureAnalysis
  ❌ OrphanFunction (not referenced by any skill)
  ❌ AnotherOrphanFunction
```

### Scenario 5: Only Skills Explicit, With Scope
```
Setup:
  - Plugin NOT explicitly registered
  - Skills have [Scope]

Expected:
  ✅ FinancialAnalysisSkills (container)
  ❌ Individual skills (hidden until expanded)
  ❌ Orphan functions (auto-registered plugin not scoped)
```

### Scenario 6: Plugin Scoped Only
```
Setup:
  - Plugin has [Scope], explicitly registered
  - No skills

Before Expansion:
  ✅ FinancialAnalysisPlugin (container)
  ❌ All functions (hidden)

After Expanding Plugin:
  ✅ FinancialAnalysisPlugin
  ✅ CalculateCurrentRatio
  ✅ CalculateQuickRatio
  ✅ ... (all functions)
```

## Expansion Behavior

### Expanding a Plugin Container

```csharp
// Before
tools = [
  FinancialAnalysisPlugin,  // Container
  ReadSkillDocument         // Non-scoped
]

// After agent expands FinancialAnalysisPlugin
expandedPlugins = { "FinancialAnalysisPlugin" }
tools = [
  FinancialAnalysisPlugin,       // Container (still visible)
  CalculateCurrentRatio,         // Now visible
  CalculateQuickRatio,           // Now visible
  CalculateDebtToEquityRatio,    // Now visible
  ... (all functions visible)
  ReadSkillDocument              // Non-scoped
]
```

### Expanding a Skill Class Scope

```csharp
// Before
tools = [
  FinancialAnalysisPlugin,  // Container (plugin scoped)
  ReadSkillDocument
]

// After agent expands FinancialAnalysisSkills
expandedSkills = { "FinancialAnalysisSkills" }
tools = [
  FinancialAnalysisPlugin,           // Still container
  QuickLiquidityAnalysis,            // Now visible
  CapitalStructureAnalysis,          // Now visible
  PeriodChangeAnalysis,              // Now visible
  ... (all individual skills visible)
  ReadSkillDocument
]
```

### Expanding an Individual Skill

```csharp
// Before
tools = [
  FinancialAnalysisPlugin,           // Container
  QuickLiquidityAnalysis,            // Skill container
  ReadSkillDocument
]

// After agent expands QuickLiquidityAnalysis skill
expandedSkills = { "QuickLiquidityAnalysis" }
tools = [
  FinancialAnalysisPlugin,           // Container
  QuickLiquidityAnalysis,            // Skill (still visible)
  CalculateCurrentRatio,             // Now visible (referenced by skill)
  CalculateQuickRatio,               // Now visible (referenced by skill)
  CalculateWorkingCapital,           // Now visible (referenced by skill)
  ReadSkillDocument
]
```

## Filter System (Separate)

> **Not to be confused with display scoping!**

The Filter System (`ScopedFilterSystem.cs`) controls **permissions and security**, not visibility.

```csharp
builder
  .SetGlobalScope()
  .AddFilter(globalValidation)
  
  .SetPluginScope("FinancialAnalysisPlugin")
  .AddFilter(financialAccessControl)
  
  .SetFunctionScope("DeleteAllData")
  .AddFilter(criticalActionFilter);
```

**Key Difference:**
- **Scoping System**: "Should this tool be VISIBLE?"
- **Filter System**: "Should this user be ALLOWED to call it?"

## Implementation Details

### ToolVisibilityManager Constructor

```csharp
public ToolVisibilityManager(
    IEnumerable<AIFunction> allFunctions,
    ImmutableHashSet<string> explicitlyRegisteredPlugins,
    ILogger<ToolVisibilityManager>? logger = null)
```

**Parameters:**
- `allFunctions` - All available AI functions (tools)
- `explicitlyRegisteredPlugins` - Plugins registered via `.WithPlugin<T>()`
- `logger` - Optional logging for debugging

### GetToolsForAgentTurn Method

```csharp
public List<AIFunction> GetToolsForAgentTurn(
    List<AIFunction> allTools,
    ImmutableHashSet<string> expandedPlugins,
    ImmutableHashSet<string> expandedSkills)
```

**Parameters:**
- `allTools` - All tools available this turn
- `expandedPlugins` - Plugins user has expanded (ask to see functions)
- `expandedSkills` - Skills user has expanded (ask to see referenced functions)

**Returns:**
- `List<AIFunction>` - Tools visible to agent this turn

### Function Metadata

**Container Metadata:**
```csharp
new Dictionary<string, object>
{
    ["IsContainer"] = true,
    ["PluginName"] = "FinancialAnalysisPlugin",
    ["FunctionNames"] = new[] { "CalculateCurrentRatio", "CalculateQuickRatio", ... },
    ["FunctionCount"] = 6
}
```

**Regular Function Metadata:**
```csharp
new Dictionary<string, object>
{
    ["ParentPlugin"] = "FinancialAnalysisPlugin"
}
```

**Skill Container Metadata:**
```csharp
new Dictionary<string, object>
{
    ["IsContainer"] = true,
    ["IsSkill"] = true,
    ["ReferencedFunctions"] = new[] { 
        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio",
        "FinancialAnalysisPlugin.CalculateWorkingCapital"
    },
    ["ReferencedPlugins"] = new[] { "FinancialAnalysisPlugin" },
    ["ParentSkillContainer"] = "FinancialAnalysisSkills"  // If parent has [Scope]
}
```

**Scope Container Metadata:**
```csharp
new Dictionary<string, object>
{
    ["IsContainer"] = true,
    ["IsScope"] = true,
    ["SkillNames"] = new[] { 
        "QuickLiquidityAnalysis", 
        "CapitalStructureAnalysis", 
        ...
    },
    ["SkillCount"] = 5
}
```

## Debugging

### Enable Debug Logging

The `ToolVisibilityManager` includes extensive `Console.WriteLine` debug output:

```csharp
[ToolVisibilityManager] 🔍 First Pass - Analyzing 22 tools
   📦 Scope Container: FinancialAnalysisSkills
   🔌 Plugin Container: FinancialAnalysisPlugin
   🎯 Skill Container: QuickLiquidityAnalysis
   ...
[ToolVisibilityManager] 🎯 Returning 7 tools:
   - FinancialAnalysisPlugin
   - QuickLiquidityAnalysis
   - CalculateCurrentRatio
   ...
```

### To Remove Debug Output

Search `ToolVisibilityManager.cs` for `Console.WriteLine` and remove lines (they're marked with emoji indicators for easy finding).

## Migration Guide

### From [PluginScope] to [Scope]

**Before (v0 Initial):**
```csharp
[PluginScope("Description")]
public class MyPlugin { ... }
```

**After (v0 Current):**
```csharp
[Scope("Description")]
public class MyPlugin { ... }
```

The `[PluginScope]` attribute has been removed. Update all plugins to use `[Scope]`.

## Common Patterns

### Pattern 1: Simple Plugin (All Functions Visible)

```csharp
// NO [Scope] attribute
public class UtilityPlugin
{
    [AIFunction]
    public string Helper1() { ... }
    
    [AIFunction]
    public string Helper2() { ... }
}

// Result: Both functions immediately visible
```

### Pattern 2: Scoped Plugin (Organized by Container)

```csharp
[Scope("Financial analysis functions")]
public class FinancialAnalysisPlugin
{
    [AIFunction]
    public decimal CalculateRatio(decimal a, decimal b) { ... }
}

// Result: Plugin container visible, functions hidden until expanded
```

### Pattern 3: Organized Skills (Individual Actions)

```csharp
public class AnalysisSkills
{
    [Skill]
    public Skill QuickAnalysis(...) { ... }
    
    [Skill]
    public Skill DetailedAnalysis(...) { ... }
}

// Result: Both skills visible immediately
```

### Pattern 4: Organized Skills (Grouped Under Scope)

```csharp
[Scope("Advanced financial analysis workflows")]
public class AdvancedAnalysisSkills
{
    [Skill]
    public Skill AdvancedQuickAnalysis(...) { ... }
    
    [Skill]
    public Skill AdvancedDetailedAnalysis(...) { ... }
}

// Result: Scope container visible, skills hidden until scope expanded
```

## Performance Considerations

- **First Pass**: O(n) where n = number of tools
- **Categorization**: O(n log n) due to HashSet operations
- **Memory**: O(n) for tracking relationships
- **Per-Call**: ~1-5ms for typical tool sets (20-50 tools)

Caching opportunities if `GetToolsForAgentTurn` called frequently with same tool set.

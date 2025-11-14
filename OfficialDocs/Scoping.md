# Scoping

## What is Scoping?

**Scoping** is a mechanism to collapse plugins and skills behind container functions, saving tokens by hiding details until the agent needs them.

Think of it like folders on your desktop - you see folder names until you open them.

## Why Scoping Matters

### Without Scoping
```
Agent sees:
- ReadFile
- WriteFile
- DeleteFile
- CopyFile
- MoveFile
- ListFiles
- CreateDirectory
- DeleteDirectory
... (20 file functions × 200 tokens = 4,000 tokens)
```

### With Scoping
```
Agent sees:
- FileSystemPlugin: File operations (1 function × 200 tokens = 200 tokens)

Agent calls FileSystemPlugin →
- ReadFile
- WriteFile
- DeleteFile
... (all 20 functions now visible)
```

**Token savings: 3,800 tokens!** And the agent only sees file functions when it needs them.

---

## Plugin Scoping (Optional)

Add the `[Scope]` attribute to a plugin class to make it collapsible.

### Basic Scoping

```csharp
[Scope("File system operations for reading, writing, and managing files")]
public class FileSystemPlugin
{
    [AIFunction]
    public string ReadFile(string path) { ... }

    [AIFunction]
    public void WriteFile(string path, string content) { ... }

    [AIFunction]
    public void DeleteFile(string path) { ... }

    // ... 17 more functions
}
```

**Agent sees initially:**
```
Available Functions:
- FileSystemPlugin: File system operations for reading, writing, and managing files
```

**Agent calls `FileSystemPlugin` →**
```
Response: "FileSystemPlugin expanded. Available functions: ReadFile, WriteFile, DeleteFile, ..."

Available Functions:
- ReadFile: Read text content from a file
- WriteFile: Write text content to a file
- DeleteFile: Delete a file from filesystem
... (all 20 functions)
```

---

### Scoping with Post-Expansion Instructions

You can provide additional guidance that only appears AFTER the agent expands the container:

```csharp
[Scope(
    description: "Database operations for reading and writing data",
    postExpansionInstructions: @"
TRANSACTION WORKFLOW:
1. Call BeginTransaction first
2. Execute Insert/Update/Delete operations
3. Call CommitTransaction to save changes
4. Call RollbackTransaction if errors occur

IMPORTANT: Always close transactions to prevent database locks!
Safety: Never delete without WHERE clause verification."
)]
public class DatabasePlugin
{
    [AIFunction]
    public void BeginTransaction() { ... }

    [AIFunction]
    public void CommitTransaction() { ... }

    [AIFunction]
    public void RollbackTransaction() { ... }

    [AIFunction]
    public void Insert(string table, object data) { ... }

    // ... more operations
}
```

**Agent calls `DatabasePlugin` →**
```
Response: "DatabasePlugin expanded. Available functions: BeginTransaction, CommitTransaction, ...

TRANSACTION WORKFLOW:
1. Call BeginTransaction first
2. Execute Insert/Update/Delete operations
3. Call CommitTransaction to save changes
4. Call RollbackTransaction if errors occur

IMPORTANT: Always close transactions to prevent database locks!
Safety: Never delete without WHERE clause verification."
```

**Benefits:**
- Instructions only consume tokens when container is expanded
- Perfect for safety warnings, best practices, workflow guidance
- Keeps system prompt clean

---

### When to Use Plugin Scoping

**Use `[Scope]` when:**
- Plugin has 10+ functions
- Functions are rarely used together with other plugins
- You want to organize functions hierarchically

**Skip `[Scope]` when:**
- Plugin has 2-5 functions that are frequently used
- Functions are core utilities (timestamps, GUIDs, etc.)
- You want functions always visible

---

## Skill Scoping (Always Scoped)

**Skills are ALWAYS scoped** - you don't need the `[Scope]` attribute. Scoping is built into the skill architecture.

```csharp
[Skill]
public Skill QuickLiquidityAnalysis()
{
    return SkillFactory.Create(
        name: "QuickLiquidityAnalysis",
        description: "Analyze short-term liquidity",
        instructions: "Steps: 1. Calculate Current Ratio...",
        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio"
    );
}
```

**Agent sees initially:**
```
Available Functions:
- QuickLiquidityAnalysis: Analyze short-term liquidity
```

**Agent calls `QuickLiquidityAnalysis` →**
```
Available Functions:
- CalculateCurrentRatio: Calculate current ratio
- CalculateQuickRatio: Calculate quick ratio
```

**The 2 referenced functions are now visible, but ONLY those 2.**

---

## Skill Class Scoping (Optional)

You can also add `[Scope]` to a class containing skills to create a **two-level hierarchy**:

```csharp
[Scope("Financial analysis workflows combining multiple analysis techniques")]
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis() { ... }

    [Skill]
    public Skill CapitalStructureAnalysis() { ... }

    [Skill]
    public Skill PeriodChangeAnalysis() { ... }
}
```

**Agent sees initially:**
```
Available Functions:
- FinancialAnalysisSkills: Financial analysis workflows combining multiple analysis techniques
```

**Agent calls `FinancialAnalysisSkills` →**
```
Response: "FinancialAnalysisSkills expanded. Available functions: QuickLiquidityAnalysis, CapitalStructureAnalysis, PeriodChangeAnalysis"

Available Functions:
- QuickLiquidityAnalysis: Analyze short-term liquidity
- CapitalStructureAnalysis: Analyze capital structure and leverage
- PeriodChangeAnalysis: Analyze period-over-period changes
```

**Agent calls `QuickLiquidityAnalysis` →**
```
Available Functions:
- CalculateCurrentRatio: Calculate current ratio
- CalculateQuickRatio: Calculate quick ratio
... (only functions referenced by this skill)
```

**Three-level hierarchy:**
1. Skill class scope (collapsed)
2. Skills (collapsed)
3. Functions (collapsed until skill expands them)

---

## Visibility Rules

The `UnifiedScopingManager` controls what the agent sees based on expansion state:

### Rule 1: Unscoped Functions (Always Visible)
```csharp
public class UtilsPlugin  // No [Scope] attribute
{
    [AIFunction]
    public string GetTimestamp() => DateTime.Now.ToString();
}
```
→ `GetTimestamp` is **always visible**

---

### Rule 2: Scoped Plugin Functions (Hidden Until Expanded)
```csharp
[Scope("Math operations")]
public class MathPlugin
{
    [AIFunction]
    public double Add(double a, double b) => a + b;
}
```
→ `Add` is **hidden** until agent calls `MathPlugin` container

---

### Rule 3: Functions Referenced by Skills (Hidden Until Skill Expands)
```csharp
public class FileSystemPlugin  // No [Scope] - normally always visible
{
    [AIFunction]
    public string ReadFile(string path) { ... }
}

public class DebugSkills
{
    [Skill]
    public Skill DebugFileIssue()
    {
        return SkillFactory.Create(
            "DebugFileIssue",
            "Debug file issues",
            "Instructions...",
            "FileSystemPlugin.ReadFile"  // Skill claims this function
        );
    }
}
```

→ `ReadFile` is **hidden** even though `FileSystemPlugin` has no `[Scope]`
→ `ReadFile` becomes **visible** when agent calls `DebugFileIssue`

**Skills "claim ownership" of functions for scoping purposes.**

---

### Rule 4: Skill Bypass for Scoped Plugins

If a function is in a scoped plugin AND referenced by an expanded skill, it becomes visible:

```csharp
[Scope("Financial calculations")]
public class FinancialAnalysisPlugin
{
    [AIFunction]
    public double CalculateCurrentRatio(...) { ... }

    [AIFunction]
    public double CalculateDebtRatio(...) { ... }
}

public class FinancialSkills
{
    [Skill]
    public Skill QuickLiquidity()
    {
        return SkillFactory.Create(
            "QuickLiquidity",
            "Quick liquidity check",
            "Instructions...",
            "FinancialAnalysisPlugin.CalculateCurrentRatio"
        );
    }
}
```

**Scenario:**
1. Initially: `FinancialAnalysisPlugin` collapsed, `QuickLiquidity` skill visible
2. Agent calls `QuickLiquidity` →
   - `CalculateCurrentRatio` becomes visible (skill bypass)
   - `CalculateDebtRatio` stays hidden (plugin still collapsed)
   - `FinancialAnalysisPlugin` container stays visible (not auto-expanded)

3. Agent can still call `FinancialAnalysisPlugin` container to see ALL 50 functions

**Skill bypass allows selective expansion without expanding entire plugin.**

---

## Ordering and Priority

Functions/skills appear in this order:

1. **Scope containers** (collapsed plugins/skill classes)
2. **Skill containers** (individual skills)
3. **Non-scoped functions** (always visible utilities)
4. **Expanded plugin functions** (from expanded scoped plugins)
5. **Expanded skill functions** (from expanded skills)

All sorted alphabetically within each category.

---

## Examples

### Example 1: Mixed Visibility

```csharp
// Unscoped utility (always visible)
public class CoreUtils
{
    [AIFunction]
    public string GetTimestamp() => DateTime.Now.ToString();
}

// Scoped plugin
[Scope("Advanced math")]
public class AdvancedMath
{
    [AIFunction]
    public double Derivative(...) { ... }

    [AIFunction]
    public double Integral(...) { ... }
}

// Skills
public class MathSkills
{
    [Skill]
    public Skill SolveEquation()
    {
        return SkillFactory.Create(
            "SolveEquation",
            "Solve mathematical equations",
            "Instructions...",
            "AdvancedMath.Derivative",
            "CoreUtils.GetTimestamp"
        );
    }
}
```

**Initial state:**
```
Available Functions:
- GetTimestamp (always visible - unscoped)
- AdvancedMath (collapsed - scoped plugin)
- SolveEquation (collapsed - skill)
```

**Agent calls `SolveEquation` →**
```
Available Functions:
- AdvancedMath (still collapsed)
- Derivative (expanded by skill - skill bypass!)
- GetTimestamp (now HIDDEN - claimed by skill)
```

**Agent calls `AdvancedMath` →**
```
Available Functions:
- GetTimestamp (back to visible)
- Derivative (still visible)
- Integral (now visible - plugin expanded)
```

---

### Example 2: Hierarchical Scoping

```csharp
[Scope("Financial analysis workflows")]
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidity() { ... }

    [Skill]
    public Skill CapitalStructure() { ... }

    [Skill]
    public Skill ComprehensiveDashboard()
    {
        return SkillFactory.Create(
            "ComprehensiveDashboard",
            "Complete financial health assessment",
            "Instructions...",
            "FinancialAnalysisSkills.QuickLiquidity",  // Reference other skill!
            "FinancialAnalysisSkills.CapitalStructure"
        );
    }
}
```

**Level 1:** `FinancialAnalysisSkills` (skill class scope)
**Level 2:** `QuickLiquidity`, `CapitalStructure`, `ComprehensiveDashboard` (skills)
**Level 3:** Individual functions referenced by each skill

**Skills can reference other skills for composition!**

---

## Best Practices

### ✅ DO: Scope large plugin surfaces
```csharp
[Scope("50+ database operations")]
public class DatabasePlugin { ... }  // 50 functions
```

### ✅ DO: Use post-expansion instructions for critical workflows
```csharp
[Scope("Database operations", postExpansionInstructions: "Always use transactions!")]
```

### ✅ DO: Group related skills under scoped class
```csharp
[Scope("Financial analysis workflows")]
public class FinancialAnalysisSkills { ... }
```

### ❌ DON'T: Scope tiny plugins
```csharp
[Scope("Utils")]
public class UtilsPlugin
{
    [AIFunction]
    public string GetTime() { ... }  // Only 1-2 functions - no benefit
}
```

### ❌ DON'T: Put critical always-needed functions in scoped plugins
```csharp
[Scope("Core operations")]
public class CorePlugin
{
    [AIFunction]
    public void RespondToUser(string message) { ... }  // Agent needs this immediately!
}
```

---

## Token Savings Calculator

**Example: Financial Analysis Agent**

### Without Scoping
```
Plugins:
- FinancialAnalysisPlugin (50 functions × 200 tokens) = 10,000 tokens
- DatabasePlugin (30 functions × 200 tokens) = 6,000 tokens
- ReportingPlugin (40 functions × 200 tokens) = 8,000 tokens

Total: 120 functions = 24,000 tokens (before user asks anything!)
```

### With Scoping + Skills
```
Initial state:
- 3 plugin containers (3 × 200 tokens) = 600 tokens
- 10 skill containers (10 × 100 tokens) = 1,000 tokens

Total: 1,600 tokens

When agent uses QuickLiquidityAnalysis skill:
- Skill instructions: 300 tokens
- 3 functions expanded: 600 tokens
- Document (if read): 1,500 tokens

Total for this workflow: 2,400 tokens
```

**Savings: 21,600 tokens!** And the agent gets exactly the guidance it needs for the current task.

---

## Container Lifecycle & History Management

### Turn-Scoped Expansion

Container expansions are **message-turn scoped** - they reset at the start of each new user message:

```csharp
User Turn 1: "Analyze this file"
→ Agent calls DebugFileIssue skill
→ ReadFile becomes visible
→ Agent calls ReadFile
→ Agent responds

User Turn 2: "Now check the database"
→ DebugFileIssue collapses (expansion cleared)
→ Agent must reactivate skill if needed
```

**Why this matters:**
- Fresh start for each user message
- Prevents stale function visibility
- Agent explicitly reactivates what it needs

---

### History Filtering

Container activation messages are **filtered from persistent chat history** to prevent pollution:

#### Within a Single Turn (Visible)
```
Agent iteration 1: Call FileSystemPlugin container
→ Result: "FileSystemPlugin expanded. Functions: ReadFile, WriteFile..."
→ Agent sees this result in NEXT iteration

Agent iteration 2: Call ReadFile (knows it's available from previous result)
→ Result: "File contents: console.log('hello');"
```

**The agent CAN see container activation within the same message turn.**

---

#### Across Message Turns (Filtered)

```
Turn 1:
  Agent: Calls FileSystemPlugin
  Agent: Calls ReadFile
  History stored: [User message, ReadFile result, Assistant response]
  ❌ FileSystemPlugin activation NOT stored

Turn 2:
  Agent sees: Clean history without stale "FileSystemPlugin expanded" message
  FileSystemPlugin is collapsed again (fresh state)
```

**Container activation messages are removed from persistent history.**

---

### Why Filter Container Activations?

**Problem without filtering:**
```
Turn 1: "FileSystemPlugin expanded. Functions: ReadFile, WriteFile..."
Turn 2: "DatabasePlugin expanded. Functions: Query, Insert..."
Turn 3: "MathPlugin expanded. Functions: Add, Multiply..."
Turn 10: History bloated with 9 stale activation messages (2,700 tokens wasted!)
```

**Solution with filtering:**
```
Turn 1: [User msg, ReadFile result, Response]
Turn 2: [User msg, Query result, Response]
Turn 3: [User msg, Add result, Response]
Turn 10: Clean history, only real tool results (600 tokens)
```

**Benefits:**
- ✅ No history pollution from stale activations
- ✅ Token efficiency across long conversations
- ✅ Agent must explicitly reactivate (clear intent)
- ✅ Consistent behavior: plugins and skills work identically

---

### What Gets Stored vs. Filtered

| Result Type | Stored in History? | Reason |
|-------------|-------------------|---------|
| **Container activation** (plugin/skill) | ❌ No | Temporary, turn-scoped |
| **Regular function call** (ReadFile, Add) | ✅ Yes | Actual work, persistent |
| **Text responses** | ✅ Yes | Conversation content |
| **Errors** | ✅ Yes | Important context |

---

### Implementation Detail

Two histories exist internally:

1. **`turnHistory`** (current turn only)
   - Contains ALL results including container activations
   - Visible to agent within current message turn
   - Allows agent to see what's available for subsequent iterations

2. **`currentMessages`** (persistent across turns)
   - Contains only non-container results
   - Container activations filtered out via `FilterContainerResults()`
   - Clean history passed to next message turn

**This dual-history architecture enables:**
- Within-turn visibility (agent knows what's available NOW)
- Cross-turn cleanliness (no pollution from previous activations)

---

## Summary

- **Plugin scoping is OPTIONAL** - use `[Scope]` on classes with 10+ functions
- **Skill scoping is ALWAYS ON** - skills are containers by nature
- **Skills claim functions** - referenced functions hide until skill expands
- **Skill bypass** - functions in scoped plugins can be accessed via skills without expanding entire plugin
- **Post-expansion instructions** - guidance delivered just-in-time
- **Container expansions are message-turn scoped** - reset at each new user message
- **Container activations are filtered from history** - prevents token pollution
- **Result** - Scale to hundreds of functions without token explosion

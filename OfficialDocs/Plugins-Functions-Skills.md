# Plugins, Functions, and Skills

## Overview

HPD-Agent organizes AI capabilities into three core building blocks:

- **Plugin** - A class that contains functions and/or skills
- **Function** - A single AI-callable operation (marked with `[AIFunction]`)
- **Skill** - A guided workflow that packages functions with instructions and documents (marked with `[Skill]`)

## Plugin

**A plugin is simply a class that contains `[AIFunction]` methods, `[Skill]` methods, or both.**

### Plugin with Functions Only

```csharp
public class MathPlugin
{
    [AIFunction]
    [AIDescription("Adds two numbers and returns the sum")]
    public decimal Add(decimal a, decimal b) => a + b;

    [AIFunction]
    [AIDescription("Multiplies two numbers and returns the product")]
    public decimal Multiply(decimal a, decimal b) => a * b;
}
```

**Registration:**
```csharp
var agent = new AgentBuilder()
    .WithPlugin<MathPlugin>()
    .Build();
```

**Agent sees:**
```
Available Functions:
- Add: Adds two numbers and returns the sum
- Multiply: Multiplies two numbers and returns the product
```

---

### Plugin with Skills Only

```csharp
public class FinancialAnalysisSkills
{
    [Skill]
    public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "QuickLiquidityAnalysis",
            description: "Analyze company's short-term liquidity position",
            instructions: "Follow these steps to assess liquidity...",
            "FinancialAnalysisPlugin.CalculateCurrentRatio",
            "FinancialAnalysisPlugin.CalculateQuickRatio"
        );
    }
}
```

**Registration:**
```csharp
var agent = new AgentBuilder()
    .WithPlugin<FinancialAnalysisSkills>()  // Auto-registers FinancialAnalysisPlugin!
    .Build();
```

---

### Plugin with Both Functions AND Skills (Hybrid)

```csharp
public class FileSystemPlugin
{
    // ============================================
    // FUNCTIONS (Low-level operations)
    // ============================================

    [AIFunction]
    [AIDescription("Read text content from a file")]
    public string ReadFile(string path)
    {
        return File.ReadAllText(path);
    }

    [AIFunction]
    [AIDescription("Write text content to a file")]
    public void WriteFile(string path, string content)
    {
        File.WriteAllText(path, content);
    }

    [AIFunction]
    [AIDescription("List all files in a directory")]
    public string[] ListFiles(string directory)
    {
        return Directory.GetFiles(directory);
    }

    // ============================================
    // SKILLS (High-level workflows)
    // ============================================

    [Skill]
    public Skill DebugFileIssue(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "DebugFileIssue",
            description: "Debug file reading/writing issues with systematic approach",
            instructions: @"
Systematic debugging workflow:
1. Use ListFiles to verify file exists in directory
2. Use ReadFile to check file permissions and content
3. Investigate error patterns and suggest fixes",
            options: new SkillOptions()
                .AddDocumentFromFile("./SOPs/FileDebugWorkflow.md", "Debugging procedure"),
            "FileSystemPlugin.ReadFile",
            "FileSystemPlugin.ListFiles"
        );
    }
}
```

**Benefits of Hybrid Pattern:**
- Single file contains both low-level operations and high-level workflows
- Natural organization - skills live next to the functions they use
- Easy discovery for developers

---

## Function

**A function is a method marked with `[AIFunction]` that the AI agent can call.**

### Basic Function

```csharp
[AIFunction]
[AIDescription("Calculate the area of a rectangle")]
public double CalculateRectangleArea(
    [AIDescription("Width of the rectangle")] double width,
    [AIDescription("Height of the rectangle")] double height)
{
    return width * height;
}
```

### Function Characteristics

- Marked with `[AIFunction]` attribute
- Single-purpose operation
- Always visible to agent (unless scoped or claimed by skill)
- No built-in guidance - just executes the operation

### Parameter Descriptions

Use `[AIDescription]` on parameters to help the agent understand what to pass:

```csharp
[AIFunction]
[AIDescription("Search for files matching a pattern")]
public string[] SearchFiles(
    [AIDescription("Directory to search in")] string directory,
    [AIDescription("Search pattern (e.g., '*.txt', 'report*.pdf')")] string pattern,
    [AIDescription("Whether to search subdirectories")] bool recursive = false)
{
    // Implementation
}
```

---

## Skill

**A skill is a method marked with `[Skill]` that returns a `Skill` object, packaging together:**
- **Functions** (references to functions the skill needs)
- **Instructions** (inline guidance shown when skill activates)
- **Documents** (optional SOP files for detailed procedures)

### Basic Skill

```csharp
[Skill]
public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
{
    return SkillFactory.Create(
        name: "QuickLiquidityAnalysis",
        description: "Analyze company's short-term liquidity position using ratios",
        instructions: @"
Use this skill to assess if a company can pay short-term obligations.

Steps:
1. Calculate Current Ratio (Current Assets / Current Liabilities)
2. Calculate Quick Ratio (Quick Assets / Current Liabilities)
3. Calculate Working Capital (Current Assets - Current Liabilities)

Interpretation:
- Current Ratio: >1.5 is generally healthy
- Quick Ratio: >1.0 is conservative
- Working Capital: Positive indicates liquidity cushion",

        // Function references
        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio",
        "FinancialAnalysisPlugin.CalculateWorkingCapital"
    );
}
```

### What Happens When Agent Calls the Skill

**Agent sees initially:**
```
Available Functions:
- QuickLiquidityAnalysis: Analyze company's short-term liquidity position using ratios
```

**Agent calls `QuickLiquidityAnalysis` →**

**Agent receives:**
```
QuickLiquidityAnalysis skill activated. Available functions: CalculateCurrentRatio, CalculateQuickRatio, CalculateWorkingCapital

Use this skill to assess if a company can pay short-term obligations.

Steps:
1. Calculate Current Ratio (Current Assets / Current Liabilities)
2. Calculate Quick Ratio (Quick Assets / Current Liabilities)
3. Calculate Working Capital (Current Assets - Current Liabilities)

Interpretation:
- Current Ratio: >1.5 is generally healthy
- Quick Ratio: >1.0 is conservative
- Working Capital: Positive indicates liquidity cushion
```

**Available Functions become:**
```
- CalculateCurrentRatio: Calculate current ratio
- CalculateQuickRatio: Calculate quick ratio
- CalculateWorkingCapital: Calculate working capital
```

---

### Skill with Documents

Skills can attach SOP (Standard Operating Procedure) documents for detailed guidance:

```csharp
[Skill(Category = "Liquidity Analysis", Priority = 10)]
public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
{
    return SkillFactory.Create(
        name: "QuickLiquidityAnalysis",
        description: "Analyze company's short-term liquidity position",
        instructions: "Follow these steps... See SOP documentation for detailed procedure.",

        options: new SkillOptions()
            .AddDocumentFromFile(
                "./Skills/SOPs/01-QuickLiquidityAnalysis-SOP.md",
                "Step-by-step procedure for analyzing liquidity ratios"),

        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio",
        "FinancialAnalysisPlugin.CalculateWorkingCapital"
    );
}
```

**When skill activates:**
```
QuickLiquidityAnalysis skill activated.

📚 Available Documents:
- quick-liquidity-analysis-sop: Step-by-step procedure for analyzing liquidity ratios

Use read_skill_document(documentId) to retrieve document content.

[Inline instructions here...]
```

**Agent can then call:**
```csharp
read_skill_document("quick-liquidity-analysis-sop")
```

**And receives:** Full SOP document with detailed procedures, interpretation tables, red flags, common pitfalls, etc.

---

### Skill Options

```csharp
// Add documents from files (uploaded at build time)
options: new SkillOptions()
    .AddDocumentFromFile(
        "./Skills/SOPs/liquidity-analysis.md",
        "Step-by-step procedure for liquidity analysis")
    .AddDocumentFromFile(
        "./Skills/SOPs/interpretation-guide.md",
        "How to interpret financial ratios")

// Reference existing documents from store
options: new SkillOptions()
    .AddDocument("global-financial-policies", "Company-wide policies")
```

---

### Skill Metadata

```csharp
[Skill(Category = "Liquidity Analysis", Priority = 10)]
public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
{
    // ...
}
```

**Properties:**
- `Category`: Group skills by category (e.g., "Liquidity Analysis", "Debugging")
- `Priority`: Higher priority = more prominent (used for future UI/ordering)

---

## Key Differences

| Aspect | Function | Skill |
|--------|----------|-------|
| **Purpose** | Single operation | Guided workflow |
| **Guidance** | None (just executes) | Inline instructions + optional documents |
| **Visibility** | Always visible (unless scoped/claimed) | Always scoped (container behavior) |
| **Composition** | Standalone | References functions from any plugin |
| **Use Case** | "Do X" | "Here's how to accomplish task X using functions Y and Z" |
| **Example** | `CalculateCurrentRatio` | `QuickLiquidityAnalysis` (uses 3 calculation functions) |

| Aspect | Plugin |
|--------|--------|
| **Purpose** | Container for functions and/or skills |
| **Can Contain** | Functions only, skills only, or both |
| **Scoping** | Optional (via `[Scope]` attribute) |
| **Registration** | Explicit: `WithPlugin<T>()` or auto via skill references |

---

## Function References in Skills

Skills reference functions using string format: `"PluginName.FunctionName"`

```csharp
[Skill]
public Skill MySkill()
{
    return SkillFactory.Create(
        "MySkill",
        "Description",
        "Instructions",
        "MathPlugin.Add",              // Reference function from MathPlugin
        "MathPlugin.Multiply",
        "FileSystemPlugin.ReadFile"    // Can reference multiple plugins
    );
}
```

**Auto-Registration:**
When you register a plugin containing skills, HPD-Agent automatically:
1. Analyzes skill references
2. Discovers referenced plugins (e.g., `MathPlugin`, `FileSystemPlugin`)
3. Auto-registers those plugins
4. Registers only the specific functions referenced by skills (selective registration)

**No manual plugin registration needed!**

---

## Complete Example

```csharp
// Plugin 1: Functions only
public class FinancialAnalysisPlugin
{
    [AIFunction]
    public double CalculateCurrentRatio(double assets, double liabilities)
        => assets / liabilities;

    [AIFunction]
    public double CalculateQuickRatio(double assets, double inventory, double liabilities)
        => (assets - inventory) / liabilities;

    [AIFunction]
    public double CalculateWorkingCapital(double assets, double liabilities)
        => assets - liabilities;
}

// Plugin 2: Skills only
public class FinancialAnalysisSkills
{
    [Skill(Category = "Liquidity Analysis")]
    public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "QuickLiquidityAnalysis",
            description: "Analyze short-term liquidity",
            instructions: @"
Steps:
1. Calculate Current Ratio
2. Calculate Quick Ratio
3. Calculate Working Capital
4. Interpret results",
            options: new SkillOptions()
                .AddDocumentFromFile("./SOPs/liquidity.md", "Full procedure"),
            "FinancialAnalysisPlugin.CalculateCurrentRatio",
            "FinancialAnalysisPlugin.CalculateQuickRatio",
            "FinancialAnalysisPlugin.CalculateWorkingCapital"
        );
    }
}

// Register
var agent = new AgentBuilder()
    .WithPlugin<FinancialAnalysisSkills>()  // Auto-registers FinancialAnalysisPlugin!
    .WithDocumentStore(documentStore)       // Required for skill documents
    .Build();
```

**Result:**
- Agent sees `QuickLiquidityAnalysis` skill (collapsed)
- When called → Gets instructions + 3 functions + document access
- Agent follows the workflow using the provided guidance

---

## Why This Architecture?

### Problem: Tool Explosion
```
Traditional: Agent sees 150 functions × 200 tokens = 30,000 tokens
```

### Solution: Skills + Scoping
```
HPD-Agent: Agent sees 20 skills × 50 tokens = 1,000 tokens

Agent calls skill →
  Instructions: 300 tokens
  Functions (3): 600 tokens
  Document (if needed): 1,500 tokens

= Guidance delivered just-in-time, only when needed
```

**You can scale to hundreds of workflows without blowing up the context window.**

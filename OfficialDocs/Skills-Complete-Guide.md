# Skills - Complete Guide

## What is a Skill?

A **skill** is a guided workflow that packages together:
- **Functions** (the tools needed to complete a task)
- **Instructions** (how to use those tools)
- **Documents** (optional detailed SOPs/procedures)

Think of a skill as giving the AI agent a "playbook" for accomplishing a specific task.

---

## Why Skills Exist

### The Problem: Tool Explosion Without Guidance

**Traditional approach:**
```
Agent sees:
- CalculateCurrentRatio
- CalculateQuickRatio
- CalculateWorkingCapital
- CalculateDebtToEquity
- CalculateROE
- CalculateROA
... (50 financial functions)

User: "Analyze this company's liquidity"

Agent: "Should I calculate current ratio? Quick ratio? Both? In what order?"
```

**The agent has tools but no guidance on how to use them together.**

---

### The Solution: Skills Package Tools with Guidance

```csharp
[Skill]
public Skill QuickLiquidityAnalysis()
{
    return SkillFactory.Create(
        name: "QuickLiquidityAnalysis",
        description: "Analyze company's short-term liquidity position",
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

        "FinancialAnalysisPlugin.CalculateCurrentRatio",
        "FinancialAnalysisPlugin.CalculateQuickRatio",
        "FinancialAnalysisPlugin.CalculateWorkingCapital"
    );
}
```

**Now the agent knows:**
- ✅ Which functions to use (only 3 out of 50)
- ✅ In what order to use them
- ✅ How to interpret the results

---

## The Architecture Stack: Scoping + Skills

**Skills don't exist in isolation—they're built on top of the Scoping architecture.**

```
┌─────────────────────────────────────────────────┐
│    SKILLS (Execution Guidance Layer)            │
│  - Packages functions with instructions         │
│  - Enables intelligent batching                 │
│  - 60% cost savings from smarter execution      │
│  - Agent knows WHEN to parallel                 │
└─────────────────────────────────────────────────┘
         ↑ BUILT ON TOP OF ↑
┌─────────────────────────────────────────────────┐
│    SCOPING (Visibility Architecture)            │
│  - Hierarchically collapses plugins/skills      │
│  - Hides functions until needed                 │
│  - 70% token savings from hierarchy             │
│  - Functions hidden by default, shown on demand │
└─────────────────────────────────────────────────┘
```

**How they work together:**

| Layer | Mechanism | Benefit |
|-------|-----------|---------|
| **Scoping** | Hierarchical visibility control | 70% fewer tokens (hide 120 functions, show 1,600 tokens initially) |
| **Skills** | Explicit execution guidance | 60% fewer LLM calls (intelligent batching) |
| **Combined** | Guided workflows with smart execution | ~85% total reduction in tokens + cost |

**Real impact:**

| Metric | Without Scoping + Skills | With Scoping + Skills | Savings |
|--------|---|---|---|
| Initial tokens visible | 24,000 | 1,600 | **93% hidden initially** |
| LLM calls for task | 15 | 4 | **73% fewer calls** |
| Round-trips | 15 | 4 | **73% faster** |
| Total cost | $0.45 | ~$0.09 | **80% cheaper** |

---

### How Scoping Enables Skills

1. **Scoping provides the architecture** - Skills are always scoped containers
2. **Skills provide the execution strategy** - Instructions guide intelligent batching
3. **Functions hidden until skill expands** - Only relevant functions visible when needed
4. **Batching happens with complete visibility** - Agent sees exactly what it needs for the current task

**Without Scoping:** Skill instructions compete with 120 other functions for context
**With Scoping:** Skill instructions have the agent's full attention (other functions are hidden)

---

## Anatomy of a Skill

### How Skills Use Scoping

Skills are **always scoped containers**. This means:

```
Without Scoping:
Agent sees all 50 functions from FinancialAnalysisPlugin
→ Takes up 10,000 tokens
→ Agent is distracted by functions it doesn't need

With Scoping (Skills):
Agent sees: QuickLiquidityAnalysis (skill name)
→ Takes up 100 tokens
→ Agent focuses on the task

Agent calls QuickLiquidityAnalysis:
→ Functions become visible (skill scope expands)
→ Only 3 functions shown (the ones the skill needs)
```

**Skills inherit Scoping to manage visibility** - They collapse complexity until the agent needs to expand it.

---

### Basic Skill Structure

```csharp
[Skill]  // ← Required attribute
public Skill MySkillName(SkillOptions? options = null)  // ← Standard signature
{
    return SkillFactory.Create(
        name: "MySkillName",           // Required: Skill name (shown to agent)
        description: "Brief summary",   // Required: What the skill does
        instructions: "How to use it",  // Required: Step-by-step guidance
        "PluginName.FunctionName1",    // Required: At least one function reference
        "PluginName.FunctionName2"
    );
}
```

---

### What Each Part Does

#### **1. Name** (Required)
```csharp
name: "QuickLiquidityAnalysis"
```

- Shown to agent in tool list
- Agent calls this name to activate the skill
- Should be descriptive and action-oriented

**Examples:**
- ✅ "QuickLiquidityAnalysis"
- ✅ "DebugFileIssue"
- ✅ "GenerateFinancialReport"
- ❌ "Liquidity" (too vague)
- ❌ "DoStuff" (not descriptive)

---

#### **2. Description** (Required)
```csharp
description: "Analyze company's short-term liquidity position using ratios"
```

- Shown BEFORE agent activates skill (in tool list)
- Agent uses this to decide whether to activate the skill
- Should explain WHEN to use this skill

**Good descriptions:**
```csharp
✅ "Analyze company's short-term liquidity position"
✅ "Debug file reading/writing issues with systematic approach"
✅ "Generate comprehensive financial report with charts and analysis"
```

**Bad descriptions:**
```csharp
❌ "Liquidity" (what about it?)
❌ "Do analysis" (what kind?)
❌ "Financial skill" (too generic)
```

---

#### **3. Instructions** (Required - MANDATORY)
```csharp
instructions: @"
Use this skill to assess if a company can pay short-term obligations.

Steps:
1. Calculate Current Ratio (Current Assets / Current Liabilities)
2. Calculate Quick Ratio (Quick Assets / Current Liabilities)
3. Calculate Working Capital (Current Assets - Current Liabilities)

Interpretation:
- Current Ratio: >1.5 is generally healthy
- Quick Ratio: >1.0 is conservative
- Working Capital: Positive indicates liquidity cushion"
```

**Why instructions are REQUIRED:**
- ✅ **Fallback mechanism** - Works when document store is not configured
- ✅ **Graceful degradation** - Skill still provides value without external dependencies
- ✅ **Always available** - Instructions are embedded in generated code (no I/O needed)
- ✅ **Quick reference** - Agent doesn't always need full SOP document

**Instructions are:**
- Shown AFTER agent activates skill (in function response)
- Tell agent HOW to use the functions
- MUST be self-contained (works without documents)
- Cannot be null/empty - will throw `ArgumentException` at runtime

**What to include:**
- ✅ When to use this skill
- ✅ Step-by-step procedure
- ✅ Order of operations
- ✅ How to interpret results
- ✅ Common patterns/workflows
- ✅ Red flags to watch for

**Instructions format:**
```csharp
// Option 1: Multi-line string literal
instructions: @"
Step 1: Do X
Step 2: Do Y
Step 3: Do Z
"

// Option 2: Concatenated strings
instructions:
    "Step 1: Do X\n" +
    "Step 2: Do Y\n" +
    "Step 3: Do Z"
```

---

#### **4. Function References** (Required - at least one)
```csharp
"FinancialAnalysisPlugin.CalculateCurrentRatio",
"FinancialAnalysisPlugin.CalculateQuickRatio",
"FinancialAnalysisPlugin.CalculateWorkingCapital"
```

**Format:** `"PluginName.FunctionName"`

**What happens:**
- Source generator validates these exist at compile-time
- Functions become visible when skill is activated
- Functions are hidden when skill is not active
- Plugin is auto-registered if not already registered

**Can reference functions from multiple plugins:**
```csharp
"MathPlugin.Add",
"MathPlugin.Multiply",
"FileSystemPlugin.ReadFile",
"DatabasePlugin.Query"
```

---

## Skill Options (Optional Enhancements)

### Basic Skill (No Options)
```csharp
return SkillFactory.Create(
    "MySkill",
    "Description",
    "Instructions",
    "Plugin.Function"
);
```

### Skill with Options
```csharp
return SkillFactory.Create(
    "MySkill",
    "Description",
    "Instructions",
    options: new SkillOptions()
        .AddDocumentFromFile("./sop.md", "Detailed procedure")
        .AddDocument("global-policy", "Company-wide policy"),
    "Plugin.Function"
);
```

---

### Document Options

#### **AddDocumentFromFile** - Upload Document at Build Time
```csharp
options: new SkillOptions()
    .AddDocumentFromFile(
        filePath: "./Skills/SOPs/01-QuickLiquidityAnalysis.md",
        description: "Step-by-step procedure for analyzing liquidity ratios"  // REQUIRED
    )
```

**When to use:**
- SOP documents stored in your codebase
- Documentation files you maintain
- Procedure guides specific to this skill

**Parameters:**
- `filePath` (string, required) - Path to document file
- `description` (string, **REQUIRED**) - What the document contains
- `documentId` (string, optional) - Custom ID (auto-derived from filename if not provided)

**Why description is REQUIRED:**
- ✅ Helps agent decide whether to read the document
- ✅ Provides context about document contents
- ✅ Makes document list self-documenting
- ❌ Will throw `ArgumentException` if null/empty

**What happens:**
- File is read at build time
- Content is uploaded to document store
- Document ID is auto-derived from filename (e.g., `01-QuickLiquidityAnalysis` → `01-quickliquidityanalysis`)
- Description is shown to agent in skill activation response

**Can specify custom document ID:**
```csharp
.AddDocumentFromFile(
    "./very-long-filename-here.md",
    "Description",
    documentId: "liquidity-sop"  // ← Custom ID
)
```

---

#### **AddDocument** - Reference Existing Document
```csharp
options: new SkillOptions()
    .AddDocument(
        documentId: "global-financial-policies",
        description: "Company-wide financial analysis policies"  // Optional override
    )
```

**When to use:**
- Document already exists in the store (uploaded elsewhere)
- Shared documents used by multiple skills
- Global policies/procedures

**Description override:**
```csharp
// Document in store has default description: "Global policies"
// This skill wants a more specific description:
.AddDocument(
    "global-policies",
    "Company financial analysis compliance requirements"  // ← Skill-specific description
)
```

---

#### **Chain Multiple Documents**
```csharp
options: new SkillOptions()
    .AddDocumentFromFile("./sop-1.md", "Part 1: Data collection")
    .AddDocumentFromFile("./sop-2.md", "Part 2: Analysis")
    .AddDocumentFromFile("./sop-3.md", "Part 3: Reporting")
    .AddDocument("global-policies", "Company-wide policies")
```

---

## What Happens When Agent Uses a Skill

### Overview: Scoping in Action

When an agent uses a skill, it goes through a series of Scoping expansions:

```
Level 1 (Collapse): Skill name shown
Level 2 (Expanded): Skill instructions + referenced functions shown
Level 3 (Expanded): Documents shown (if available)
```

Each expansion reveals only what's relevant for that layer.

---

### Step 1: Agent Sees Skill in Tool List (Scoping: Level 1)

**Before activation:**
```
Available Functions:
- QuickLiquidityAnalysis: Analyze company's short-term liquidity position
```

Agent reads description and decides if this skill fits the user's request.

---

### Step 2: Agent Calls the Skill

```javascript
// Agent makes function call
QuickLiquidityAnalysis()
```

---

### Step 3: Skill Activates - Agent Receives Response (Scoping: Level 2)

**With document store configured:**
```
QuickLiquidityAnalysis skill activated. Available functions: CalculateCurrentRatio, CalculateQuickRatio, CalculateWorkingCapital

📚 Available Documents:
- quick-liquidity-analysis-sop: Step-by-step procedure for analyzing liquidity ratios

Use read_skill_document(documentId) to retrieve document content.

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

**Without document store:**
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

---

### Step 4: Functions Become Available (Scoping Expansion)

**Now agent sees:**
```
Available Functions:
- CalculateCurrentRatio: Calculate current ratio
- CalculateQuickRatio: Calculate quick ratio
- CalculateWorkingCapital: Calculate working capital
- read_skill_document: Read skill document (only if document store configured)
```

**Only the 3 referenced functions are now visible!**

**This is Scoping in action:**
- ❌ Other 47 functions in FinancialAnalysisPlugin stayed hidden
- ✅ Only the 3 functions this skill needs are now shown
- ✅ Agent has complete clarity without distractions
- ✅ Enables effective batching (agent sees the full scope of what's needed)

---

### Step 5: Agent Follows Instructions

Agent reads the instructions and executes:
```javascript
// Following the steps from instructions
const currentRatio = CalculateCurrentRatio(250000, 140000);  // = 1.79
const quickRatio = CalculateQuickRatio(250000, 80000, 10000, 140000);  // = 1.14
const workingCapital = CalculateWorkingCapital(250000, 140000);  // = 110000

// Interprets results based on instructions
// Current Ratio: 1.79 > 1.5 ✅ Healthy
// Quick Ratio: 1.14 > 1.0 ✅ Conservative
// Working Capital: $110,000 ✅ Positive
```

---

### Step 6: Agent Can Read Documents (Optional, Scoping: Level 3)

If documents are available:
```javascript
const sop = read_skill_document("quick-liquidity-analysis-sop");
// Returns full 235-line SOP with detailed procedures
```

---

## What Happens If You Don't Provide...

### ❌ No Name → Compile Error
```csharp
return SkillFactory.Create(
    name: "",  // ❌ ArgumentException: "Skill name cannot be empty"
    description: "Description",
    instructions: "Instructions",
    "Plugin.Function"
);
```

**Result:** Build fails. Name is required.

---

### ❌ No Description → Compile Error
```csharp
return SkillFactory.Create(
    name: "MySkill",
    description: "",  // ❌ ArgumentException: "Skill description cannot be empty"
    instructions: "Instructions",
    "Plugin.Function"
);
```

**Result:** Build fails. Description is required.

---

### ❌ No Instructions → Runtime Error
```csharp
return SkillFactory.Create(
    name: "MySkill",
    description: "Description",
    instructions: "",  // ❌ ArgumentException at runtime
    "Plugin.Function"
);
```

**Result:** `ArgumentException: "Skill instructions cannot be empty. Instructions are required as they serve as the fallback when document store is not configured."`

**Why this is enforced:**
- Instructions are the **fallback mechanism** when document store is not available
- Ensures skills always provide value (graceful degradation)
- Forces developers to provide at least minimal guidance

**Required content:**
- ✅ At least a brief step-by-step workflow
- ✅ Self-contained (works without documents)
- ✅ Clear enough for agent to complete the task

---

### ✅ No Function References → Works for Composite Skills
```csharp
return SkillFactory.Create(
    name: "ComprehensiveAnalysis",
    description: "Complete financial analysis combining multiple sub-skills",
    instructions: "Use QuickLiquidityAnalysis, then CapitalStructureAnalysis, then...",
    // No function references - references other skills instead
);
```

**Use case:** Meta-skills that orchestrate other skills

---

### ✅ No Document Store → Falls Back to Inline Instructions
```csharp
// Agent code
var agent = new AgentBuilder()
    // ❌ No .WithDocumentStore() call
    .WithPlugin<FinancialSkills>()
    .Build();
```

**Result:**
- ✅ Skill works normally
- ✅ Agent receives inline instructions
- ❌ `read_skill_document` function not available
- ❌ Document references not shown

**Fallback behavior:**
```
QuickLiquidityAnalysis skill activated. Available functions: CalculateCurrentRatio, CalculateQuickRatio

Use this skill to assess if a company can pay short-term obligations.

Steps:
1. Calculate Current Ratio...
2. Calculate Quick Ratio...
3. Calculate Working Capital...
```

**Agent can still complete the task using inline instructions!**

---

### ✅ No Documents in Skill Options → Works Perfectly
```csharp
return SkillFactory.Create(
    name: "MySkill",
    description: "Description",
    instructions: "Complete self-contained instructions here",
    // No options parameter - no documents
    "Plugin.Function"
);
```

**Result:**
- ✅ Skill works normally
- ✅ Agent receives inline instructions
- ✅ No document section in activation response
- ✅ Perfect for skills that don't need detailed SOPs

---

## Document Store Setup

### Option 1: No Document Store (Simplest)
```csharp
var agent = new AgentBuilder()
    .WithPlugin<MySkills>()  // Skills work with inline instructions only
    .Build();
```

**When to use:**
- Quick prototyping
- Skills have comprehensive inline instructions
- Don't need detailed SOPs

---

### Option 2: In-Memory Store (Testing/Development)
```csharp
var store = new InMemoryInstructionStore(logger);

var agent = new AgentBuilder()
    .WithDocumentStore(store)
    .WithPlugin<MySkills>()
    .Build();
```

**When to use:**
- Unit tests
- Local development
- Temporary/disposable agents

**Characteristics:**
- ✅ Fast (no I/O)
- ✅ Zero setup
- ❌ Data lost on restart
- ❌ No persistence

---

### Option 3: FileSystem Store (Production)
```csharp
var store = new FileSystemInstructionStore(logger, "./skill-docs");

var agent = new AgentBuilder()
    .WithDocumentStore(store)
    .WithPlugin<MySkills>()
    .Build();
```

**When to use:**
- Production deployments
- Need persistence
- Single-server deployments

**Characteristics:**
- ✅ Persists to disk
- ✅ Human-readable files
- ✅ Easy to inspect/edit
- ✅ Version control friendly
- ❌ Not suitable for distributed systems

**File structure:**
```
./skill-docs/
  content/
    quick-liquidity-analysis-sop.txt
    capital-structure-analysis-sop.txt
  metadata/
    quick-liquidity-analysis-sop.json
    capital-structure-analysis-sop.json
```

---

### Option 4: Factory from Config (Recommended)
```csharp
// appsettings.json
{
  "DocumentStore": {
    "Type": "FileSystem",
    "FileSystem": {
      "BaseDirectory": "./skill-docs"
    }
  }
}

// Code
var store = InstructionDocumentStoreFactory.CreateFromConfig(config, loggerFactory);

var agent = new AgentBuilder()
    .WithDocumentStore(store)
    .WithPlugin<MySkills>()
    .Build();
```

**When to use:**
- Production applications
- Need to switch backends without code changes
- Different configs for dev/staging/production

**Switch to in-memory for testing:**
```json
{
  "DocumentStore": {
    "Type": "InMemory"
  }
}
```

---

## Sharing Document Store Across Multiple Agents

### ✅ Recommended: Shared Store Instance
```csharp
// Create ONE store instance
var sharedStore = new FileSystemInstructionStore(logger, "./skill-docs");

// Share across all agents
var financialAgent = new AgentBuilder()
    .WithDocumentStore(sharedStore)  // ← Same instance
    .WithPlugin<FinancialSkills>()
    .Build();

var legalAgent = new AgentBuilder()
    .WithDocumentStore(sharedStore)  // ← Same instance
    .WithPlugin<LegalSkills>()
    .Build();

var hrAgent = new AgentBuilder()
    .WithDocumentStore(sharedStore)  // ← Same instance
    .WithPlugin<HRSkills>()
    .Build();
```

**Benefits:**
- ✅ Documents uploaded once
- ✅ Shared cache (efficient)
- ✅ Consistent data across agents
- ✅ Less memory usage

---

## Skill Metadata (Optional)

```csharp
[Skill(Category = "Liquidity Analysis", Priority = 10)]
public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
{
    // ...
}
```

### Category
Groups related skills together (for future UI/organization).

**Examples:**
- "Liquidity Analysis"
- "Debugging"
- "Report Generation"
- "Data Processing"

---

### Priority
Higher priority = more prominent (for future ordering/UI).

**Examples:**
- `Priority = 1` - Critical/primary skills
- `Priority = 10` - Standard skills
- `Priority = 100` - Rarely used skills

**Current behavior:** No effect on runtime (reserved for future features)

---

## Complete Examples

### Example 1: Simple Skill (No Documents)
```csharp
[Skill]
public Skill CalculateROI()
{
    return SkillFactory.Create(
        name: "CalculateROI",
        description: "Calculate return on investment for a project",
        instructions: @"
Formula: ROI = (Gain - Cost) / Cost × 100

Steps:
1. Use Subtract to calculate (Gain - Cost)
2. Use Divide to divide by Cost
3. Use Multiply to multiply by 100

Example: Gain=$150k, Cost=$100k
ROI = (150k - 100k) / 100k × 100 = 50%",

        "MathPlugin.Subtract",
        "MathPlugin.Divide",
        "MathPlugin.Multiply"
    );
}
```

---

### Example 2: Skill with Document
```csharp
[Skill(Category = "Financial Analysis", Priority = 5)]
public Skill QuickLiquidityAnalysis(SkillOptions? options = null)
{
    return SkillFactory.Create(
        name: "QuickLiquidityAnalysis",
        description: "Analyze company's short-term liquidity position using current and quick ratios",
        instructions: @"
Use this skill to assess if a company can pay short-term obligations.

Steps:
1. Calculate Current Ratio (Current Assets / Current Liabilities)
2. Calculate Quick Ratio (Quick Assets / Current Liabilities)
3. Calculate Working Capital (Current Assets - Current Liabilities)

Interpretation:
- Current Ratio: >1.5 is generally healthy
- Quick Ratio: >1.0 is conservative
- Working Capital: Positive indicates liquidity cushion

See SOP documentation for detailed procedures, industry benchmarks, and common pitfalls.",

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

---

### Example 3: Skill with Multiple Documents
```csharp
[Skill(Category = "Debugging", Priority = 1)]
public Skill DebugFileIssue(SkillOptions? options = null)
{
    return SkillFactory.Create(
        name: "DebugFileIssue",
        description: "Debug file reading/writing issues with systematic troubleshooting",
        instructions: @"
Systematic debugging workflow:
1. Use ListFiles to verify file exists in directory
2. Use ReadFile to check if file is readable
3. Check file permissions and path validity
4. Investigate error patterns

See troubleshooting guide for common issues and solutions.",

        options: new SkillOptions()
            .AddDocumentFromFile(
                "./Debugging/FileSystemTroubleshooting.md",
                "Common file system issues and solutions")
            .AddDocumentFromFile(
                "./Debugging/PermissionsGuide.md",
                "Understanding and fixing permission errors")
            .AddDocument(
                "company-security-policies",
                "File access security requirements"),

        "FileSystemPlugin.ListFiles",
        "FileSystemPlugin.ReadFile",
        "FileSystemPlugin.GetFileInfo"
    );
}
```

---

### Example 4: Meta-Skill (Composing Other Skills)
```csharp
[Skill(Category = "Executive Summary", Priority = 1)]
public Skill ComprehensiveFinancialAnalysis(SkillOptions? options = null)
{
    return SkillFactory.Create(
        name: "ComprehensiveFinancialAnalysis",
        description: "Complete financial health assessment combining multiple analysis techniques",
        instructions: @"
Complete analysis workflow:

1. VALIDATE DATA
   - Use ValidateBalanceSheetEquation skill
   - Ensure Assets = Liabilities + Equity

2. LIQUIDITY ANALYSIS
   - Use QuickLiquidityAnalysis skill
   - Assess short-term payment ability

3. LEVERAGE ANALYSIS
   - Use CapitalStructureAnalysis skill
   - Evaluate debt/equity mix

4. TREND ANALYSIS
   - Use PeriodChangeAnalysis skill
   - Identify improving/declining metrics

5. GENERATE REPORT
   - Synthesize all findings
   - Provide overall risk rating

See comprehensive analysis framework for detailed methodology.",

        options: new SkillOptions()
            .AddDocumentFromFile(
                "./SOPs/ComprehensiveAnalysisFramework.md",
                "Complete framework for financial health assessment")
            .AddDocument(
                "global-financial-policies",
                "Company-wide analysis standards"),

        // This skill references OTHER skills
        "FinancialAnalysisSkills.ValidateBalanceSheetEquation",
        "FinancialAnalysisSkills.QuickLiquidityAnalysis",
        "FinancialAnalysisSkills.CapitalStructureAnalysis",
        "FinancialAnalysisSkills.PeriodChangeAnalysis"
    );
}
```

---

## Best Practices

### ✅ DO: Write Self-Contained Instructions
```csharp
instructions: @"
Quick workflow:
1. Calculate Current Ratio (Assets / Liabilities)
2. Calculate Quick Ratio (Quick Assets / Liabilities)
3. Interpret: >1.5 is healthy

For detailed procedures, see SOP."
```

**Why:** Works without document store (graceful degradation)

---

### ❌ DON'T: Make Instructions Useless Without Documents
```csharp
instructions: "See SOP for all instructions."  // ❌ Useless without document store
```

**Why this is bad:**
- Breaks graceful degradation
- Agent has no guidance if document store unavailable
- Defeats the purpose of required instructions

---

### ✅ DO: Provide Meaningful Document Descriptions
```csharp
.AddDocumentFromFile(
    "./SOPs/liquidity-analysis.md",
    "Step-by-step procedure with interpretation tables and industry benchmarks"  // ✅ Specific
)
```

---

### ❌ DON'T: Use Vague Document Descriptions
```csharp
.AddDocumentFromFile(
    "./SOPs/liquidity-analysis.md",
    "Document"  // ❌ Tells agent nothing
)
```

**Why description is required:**
- Helps agent decide if document is relevant
- Provides context without reading full document
- Makes document list self-documenting

---

### ✅ DO: Use Descriptive Names
```csharp
name: "QuickLiquidityAnalysis"  // ✅ Clear what it does
```

---

### ❌ DON'T: Use Generic Names
```csharp
name: "Analysis"  // ❌ Too vague
name: "DoFinancialStuff"  // ❌ Not professional
```

---

### ✅ DO: Include Interpretation Guidance
```csharp
instructions: @"
...
Interpretation:
- Current Ratio > 1.5: Healthy liquidity
- Current Ratio 1.0-1.5: Acceptable
- Current Ratio < 1.0: Concerning - investigate cash flow"
```

---

### ✅ DO: Reference Functions from Multiple Plugins
```csharp
return SkillFactory.Create(
    "DataPipeline",
    "Extract, transform, and load data",
    "Instructions...",
    "DatabasePlugin.Query",
    "MathPlugin.CalculateAverage",
    "FileSystemPlugin.WriteFile"  // ✅ Mix and match plugins
);
```

---

### ✅ DO: Chain Documents for Complex Workflows
```csharp
options: new SkillOptions()
    .AddDocumentFromFile("./part1-setup.md", "Part 1: Setup")
    .AddDocumentFromFile("./part2-execution.md", "Part 2: Execution")
    .AddDocumentFromFile("./part3-validation.md", "Part 3: Validation")
```

---

## Secondary Benefit: Skills Enable Intelligent Function Batching

### The Insight: The Capability Exists, But Behavior Is Unreliable

Modern LLMs (OpenAI, Claude, Anthropic) **already support parallel function calls**. Sometimes agents naturally batch calls when they infer no dependencies exist.

**The problem: It's inconsistent and unpredictable.**

Without explicit instruction:
- ✅ Sometimes they batch (when they correctly infer independence)
- ❌ Sometimes they don't (when unsure about dependencies)
- ❌ Sometimes they sequence unnecessarily (to be "safe")
- ❌ Sometimes they batch when they shouldn't (causing errors)

**Result: Unreliable behavior, wasted tokens, unpredictable costs, potential bugs.**

**Skills solve this by making batching RELIABLE and INTENTIONAL through explicit execution guidance.**

---

### The Problem: Unreliable Sequential Execution

Without skills, agents make unpredictable choices about batching:

```
User: "Analyze this company's liquidity"

Scenario A (Agent decides to batch):
Turn 1: CalculateCurrentRatio(), CalculateQuickRatio(), CalculateWorkingCapital() [PARALLEL]
Turn 2: Analysis
= 2 LLM calls

Scenario B (Agent decides to sequence):
Turn 1: CalculateCurrentRatio()
Turn 2: CalculateQuickRatio()
Turn 3: CalculateWorkingCapital()
Turn 4: Analysis
= 4 LLM calls

You can't predict which path the agent takes.
```

**Why unreliable?** The agent has no explicit guidance about what's actually independent. Each response depends on its reasoning at that moment, model temperature, context length, and other factors.

---

### The Solution: Skills Enforce Explicit Execution Strategies

Skills provide clear, unambiguous instructions that make batching deterministic:

```
User: "Analyze this company's liquidity"

EVERY TIME with Skills:
Turn 1:
Agent: "I'll use QuickLiquidityAnalysis skill"
Agent calls: QuickLiquidityAnalysis()
Response: "Instructions:
STEP 1 - PARALLEL: Calculate all ratios
1. Calculate Current Ratio
2. Calculate Quick Ratio
3. Calculate Working Capital"

Turn 2: Agent RELIABLY batches all 3 in parallel
- CalculateCurrentRatio(250000, 140000) → 1.79
- CalculateQuickRatio(250000, 80000, 10000, 140000) → 1.14
- CalculateWorkingCapital(250000, 140000) → 110000
[All in ONE turn]

Turn 3: Agent provides analysis

= 3 LLM calls (EVERY TIME)
= ALL calculations happen in parallel (GUARANTEED)
= PREDICTABLE cost and performance
```

**Why this is reliable:**
- ✅ Modern LLMs support parallel tool calls natively
- ✅ Skills make dependencies explicit (no guessing)
- ✅ Instructions provide unambiguous execution strategy
- ✅ Agent follows the roadmap, not inferring

---

### Real-World Cost Impact

**Scenario: Comprehensive Financial Analysis (15 function calls)**

| Metric | Without Skills | With Skills | Savings |
|--------|---|---|---|
| LLM Calls | 15 | 4 | **73% fewer** |
| Round-trips | 15 | 4 | **73% faster** |
| Tokens | ~15,000 | ~6,000 | **60% reduction** |
| GPT-4 Cost | $0.45 | $0.18 | **60% cheaper** |

---

### How to Write Instructions That Enable Batching

#### ✅ Explicit Parallel Batching

```csharp
instructions: @"
Efficient workflow (minimize API calls):

STEP 1 - PARALLEL: Calculate all ratios simultaneously
- CalculateCurrentRatio(assets, liabilities)
- CalculateQuickRatio(assets, inventory, liabilities)
- CalculateWorkingCapital(assets, liabilities)

STEP 2 - SEQUENTIAL: Interpret results
- Compare ratios to benchmarks
- Identify red flags
- Generate recommendation"
```

**Agent sees:**
- ✅ "PARALLEL" → Batch Step 1 into one turn
- ✅ "SEQUENTIAL" → Wait for Step 1 results before Step 2
- ✅ Minimizes round-trips automatically

---

#### ✅ Group Independent Operations

```csharp
instructions: @"
1. Data Collection (can be done in parallel):
   - ReadFile('balance-sheet.csv')
   - ReadFile('income-statement.csv')
   - ReadFile('cash-flow.csv')

2. Validation (sequential - depends on data):
   - ValidateBalanceSheet(data)
   - ValidateIncomeStatement(data)

3. Analysis (parallel - all independent):
   - AnalyzeLiquidity(data)
   - AnalyzeProfitability(data)
   - AnalyzeEfficiency(data)"
```

**Agent understands:**
- ✅ Section 1 = batch all three reads
- ✅ Section 2 = wait for Section 1 before validating
- ✅ Section 3 = batch all three analyses after validation

---

#### ❌ Avoid Implicit Sequencing

```csharp
// ❌ BAD - "Then" implies sequential
instructions: @"
1. Calculate Current Ratio
2. Then calculate Quick Ratio
3. Then calculate Working Capital
4. Then interpret"
```

**Better:**

```csharp
// ✅ GOOD - Implies parallel batching
instructions: @"
1. Calculate all ratios (Current, Quick, Working Capital)
2. Interpret results"
```

---

### Advanced: Conditional Batching

Instructions can describe conditional workflows that adapt based on intermediate results:

```csharp
instructions: @"
STEP 1: Quick Health Check
- CalculateCurrentRatio(assets, liabilities)

STEP 2: Conditional Deep Dive (based on Step 1 result)

IF Current Ratio < 1.0 (concerning):
  PARALLEL batch:
  - CalculateQuickRatio (detailed liquidity)
  - CalculateWorkingCapital (severity assessment)
  - CalculateCashFlowRatio (immediate risk)
  - READ DOCUMENT: 'distressed-company-analysis'

IF Current Ratio >= 1.5 (healthy):
  - Skip detailed analysis
  - Provide brief positive summary

STEP 3: Generate recommendation"
```

**Agent's adaptive execution:**

**Scenario A (Ratio = 0.8 - Concerning):**
```
Turn 1: CalculateCurrentRatio() → 0.8

Turn 2: PARALLEL batch (knows it's concerning)
- CalculateQuickRatio()
- CalculateWorkingCapital()
- CalculateCashFlowRatio()
- read_skill_document('distressed-company-analysis')

Turn 3: Generate detailed warning report
```

**Scenario B (Ratio = 1.8 - Healthy):**
```
Turn 1: CalculateCurrentRatio() → 1.8

Turn 2: Generate brief "healthy liquidity" summary
[Deep dive skipped]

= Only 2 turns total
```

**Cost difference:** Same workflow, but agent adapts execution path based on conditions = Only pays for analysis that matters.

---

## Summary

**Skills = Functions + Guidance + (Optional) Documents**
**Built on Scoping Architecture = Visibility Control + Hierarchical Expansion**

**Required (enforced at runtime):**
- ✅ **Name** - Cannot be null/empty
- ✅ **Description** - Cannot be null/empty
- ✅ **Instructions** - Cannot be null/empty (fallback mechanism)
- ✅ **At least one function reference** - Can be functions or other skills
- ✅ **Document descriptions** - Required when using `AddDocumentFromFile`

**Optional:**
- Documents (via `AddDocumentFromFile` or `AddDocument`)
- Document store (skills work without it - falls back to inline instructions)
- Category/Priority metadata
- Custom document IDs (auto-derived from filename if not provided)

**The Complete Picture:**

| Component | What It Does | Why It Matters |
|-----------|---|---|
| **Scoping** | Controls visibility hierarchically | Hides complexity, shows only what's needed |
| **Skills** | Packages functions with guidance | Agent knows what to do and how to do it efficiently |
| **Together** | Guided workflows with smart execution | Agent gets both clarity AND cost optimization |

**Key Benefits:**
- Agent knows which tools to use (Skills)
- Agent knows HOW to use them together (Skills)
- Only relevant functions are visible (Scoping)
- Agent can batch intelligently (Skills + Scoping together)
- Can access detailed SOPs if needed (Documents)
- Works without document store (graceful degradation)
- Scales to hundreds of workflows without token explosion

---

## Next Steps

**Learn the complete architecture in order:**

1. **[Scoping](Scoping.md)** ← READ THIS FIRST
   - Understand the hierarchical visibility control architecture
   - Learn how Skills use Scoping for function management
   - See how visibility expansion works

2. **Skills-Complete-Guide (this document)**
   - Understand how to package guidance with functions
   - Learn instruction patterns for intelligent batching
   - See how guidance enables cost optimization

3. **[Dynamic Metadata](Dynamic-Metadata.md)** (when ready)
   - Explore adaptive skills

4. **[Conditional Functions](Conditional-Functions-And-Parameters.md)** (when ready)
   - Explore smart adaptation

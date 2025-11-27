# Skills System

The Skills system enables semantic grouping of functions from multiple plugins, allowing M:N relationships where functions can belong to multiple skills.

## Overview

**Skills vs Plugins:**
- **Plugins**: Own functions (1:N ownership) - implementation units
- **Skills**: Reference functions (M:N membership) - semantic grouping units

**Key Features:**
- ✅ Functions can belong to multiple skills
- ✅ **Reference entire plugins or individual functions**
- ✅ **Auto-expand skills for "always visible" use case**
- ✅ Document-based instructions loaded at build time
- ✅ Ephemeral container pattern (like plugin containers)
- ✅ Automatic deduplication when multiple skills expanded
- ✅ AOT-safe (no reflection)
- ✅ Fully backward compatible with existing plugins

## Quick Start: Plugin References

Skills can reference entire plugins (common case) or individual functions (fine-grained control):

```csharp
// Reference entire plugin - simple and common
var coreSkill = new SkillDefinition
{
    Name = "CoreFileOperations",
    Description = "Essential file system operations",
    PluginReferences = new[] { "FileSystemPlugin" },  // All FileSystemPlugin functions
    AutoExpand = true  // Always visible (replaces "always visible" from plugin scoping)
};

// Reference specific functions - fine-grained control
var debugSkill = new SkillDefinition
{
    Name = "Debugging",
    Description = "Debugging and troubleshooting",
    FunctionReferences = new[] {
        "FileSystemPlugin.ReadFile",  // Specific function
        "DebugPlugin.GetStackTrace"
    }
};

// Mix both approaches
var hybridSkill = new SkillDefinition
{
    Name = "DataProcessing",
    Description = "Data processing and analysis",
    PluginReferences = new[] { "DataPlugin" },  // All DataPlugin functions
    FunctionReferences = new[] { "FileSystemPlugin.ReadFile" }  // Plus specific functions
};
```

**AutoExpand Feature:**
- `AutoExpand = true` makes skill functions always visible (no expansion needed)
- Replaces the "always visible" use case from plugin scoping
- Perfect for core utilities that should always be available

## When to Use Plugin Scoping vs Skills

Both plugin scoping and skills use the same container pattern, but serve different use cases:

### Plugin Scoping: Convenience for Simple Cases

**Best for:** When your plugin IS a cohesive semantic unit (1:1 mapping)

```csharp
[PluginScope("File system operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
    [AIFunction] public string DeleteFile(...) { }
}
```

**Benefits:**
- ✅ **Extremely convenient** - just one attribute
- ✅ **Built into plugin definition** - no separate configuration
- ✅ **Source generator handles everything** - zero boilerplate
- ✅ **Perfect for cohesive plugins** - all functions belong together semantically

### Skills: Flexibility for Complex Cases

**Best for:** Cross-plugin semantic groupings (M:N relationships)

```csharp
new SkillDefinition {
    Name = "Debugging",
    PluginReferences = new[] { "DebugPlugin" },      // All debug functions
    FunctionReferences = new[] {
        "FileSystemPlugin.ReadFile",                  // Borrow from FileSystem
        "NetworkPlugin.CheckConnection"               // Borrow from Network
    }
}
```

**Benefits:**
- ✅ **Maximum flexibility** - mix functions from anywhere
- ✅ **Reusable definitions** - define once, use across agents
- ✅ **Fine-grained control** - cherry-pick exactly what you need
- ✅ **Cross-cutting concerns** - semantic groupings spanning multiple plugins

### Decision Guide

| Your Situation | Recommended Approach |
|----------------|----------------------|
| All functions in plugin are semantically related | **Plugin Scoping** (simpler) |
| Need to group functions from multiple plugins | **Skills** (more flexible) |
| Want maximum convenience with minimal code | **Plugin Scoping** |
| Want reusable semantic definitions across agents | **Skills** |
| Plugin is tightly focused (e.g., FileSystemPlugin) | **Plugin Scoping** |
| Have cross-cutting concerns (e.g., "Debugging") | **Skills** |
| Just starting out / simple use case | **Plugin Scoping** |
| Complex agent with many semantic groupings | **Skills** |

### They Work Together!

You can use both plugin scoping AND skills in the same agent:

```csharp
// FileSystemPlugin uses plugin scoping (convenient)
[PluginScope("File system operations")]
public class FileSystemPlugin { ... }

// Agent configuration
var agent = AgentBuilder.Create()
    .WithPlugin<FileSystemPlugin>()    // Has [PluginScope], shows as container
    .WithPlugin<DebugPlugin>()          // No [PluginScope], functions always visible
    .WithSkill(new SkillDefinition {    // Custom skill borrowing functions
        Name = "Debugging",
        PluginReferences = new[] { "DebugPlugin" },
        FunctionReferences = new[] { "FileSystemPlugin.ReadFile" }
    })
    .Build();

// Result:
// - "FileSystemPlugin" container (from [PluginScope])
// - "Debugging" skill container (from SkillDefinition)
// - DebugPlugin functions always visible (no scoping)
```

## Architecture

Skills work **identically** to plugin containers:

1. **Container Expansion**: Skill containers appear in Tools list
2. **Two-Turn Pattern**: LLM invokes skill container → sees instructions + functions
3. **Ephemeral Instructions**: Container results Middlewareed from persistent history (message-turn scoped)
4. **Token Efficient**: Instructions shown once per expansion, not repeated

## Skill Reusability

Skills can be defined in multiple ways depending on your needs:

| Approach | Use Case | Reusable? |
|----------|----------|-----------|
| **Inline** | Agent-specific, one-time skills | ❌ No |
| **Static Definitions** | Shared skills across multiple agents | ✅ Yes |
| **Configuration** | Load from JSON/database | ✅ Yes |

### Key Insight: Skills vs Instances

- **SkillDefinition** = Blueprint (reusable, shareable)
- **SkillScopingManager** = Instance (one per agent, built from blueprint)
- Each agent gets its own skill scoping manager, but definitions can be shared

## Usage Examples

### Approach 1: Inline Skills (Agent-Specific)

Best for skills unique to a single agent:

```csharp
using HPD_Agent.Skills;

var agent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DebugPlugin>()
    .WithSkills(skills => {
        skills.DefineSkill(
            name: "Debugging",
            description: "Debugging and troubleshooting capabilities",
            functionRefs: new[] {
                "FileSystemPlugin.ReadFile",
                "DebugPlugin.GetStackTrace"
            },
            instructionDocuments: new[] { "debugging-protocol.md" });

        skills.DefineSkill(
            name: "FileManagement",
            description: "File operations",
            functionRefs: new[] {
                "FileSystemPlugin.ReadFile",
                "FileSystemPlugin.WriteFile"
            },
            instructionDocuments: new[] { "file-safety.md" });
    })
    .Build();
```

### Approach 2: Reusable Static Definitions (Recommended)

Best for skills shared across multiple agents:

```csharp
using HPD_Agent.Skills;

// Define reusable skills once (in a shared class/library)
public static class CommonSkills
{
    // Example 1: Reference entire plugin (simple, common case)
    public static readonly SkillDefinition FileManagement = new()
    {
        Name = "FileManagement",
        Description = "File and directory management operations",
        PluginReferences = new[] { "FileSystemPlugin" },  // All FileSystemPlugin functions
        PostExpansionInstructionDocuments = new[] { "file-safety-protocol.md" },
        InstructionDocumentBaseDirectory = "skills/documents/"
    };

    // Example 2: Mix plugin references and specific functions
    public static readonly SkillDefinition Debugging = new()
    {
        Name = "Debugging",
        Description = "Debugging and troubleshooting capabilities",
        PluginReferences = new[] { "DebugPlugin" },  // All DebugPlugin functions
        FunctionReferences = new[]
        {
            "FileSystemPlugin.ReadFile",      // Plus specific FileSystem functions
            "FileSystemPlugin.ListDirectory"
        },
        PostExpansionInstructions = "When debugging, always read error logs first.",
        PostExpansionInstructionDocuments = new[]
        {
            "debugging-protocol.md",
            "troubleshooting-checklist.md"
        },
        InstructionDocumentBaseDirectory = "skills/documents/"
    };

    // Example 3: Reference specific functions only (fine-grained control)
    public static readonly SkillDefinition DataAnalysis = new()
    {
        Name = "DataAnalysis",
        Description = "Data analysis and processing capabilities",
        FunctionReferences = new[]
        {
            "FileSystemPlugin.ReadFile",
            "DataPlugin.ParseCSV",
            "DataPlugin.GenerateChart",
            "MathPlugin.Statistics"
        },
        PostExpansionInstructions = "Always validate data before analysis.",
        InstructionDocumentBaseDirectory = "skills/documents/"
    };

    // Example 4: Auto-expanded skill (always visible)
    public static readonly SkillDefinition CoreUtilities = new()
    {
        Name = "CoreUtilities",
        Description = "Essential utilities always available",
        PluginReferences = new[] { "CorePlugin" },
        AutoExpand = true,  // Functions always visible, no expansion needed
        InstructionDocumentBaseDirectory = "skills/documents/"
    };
}

// Use .WithSkill() (singular) to add one skill at a time
var debugAgent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DebugPlugin>()
    .WithSkill(CommonSkills.Debugging)  // ✅ Reusable!
    .Build();

var dataAgent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DataPlugin>()
    .WithPlugin<MathPlugin>()
    .WithSkill(CommonSkills.DataAnalysis)  // ✅ Same definition, different agent!
    .Build();

// Agent with multiple reusable skills
var fullAgent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DebugPlugin>()
    .WithPlugin<DataPlugin>()
    .WithPlugin<MathPlugin>()
    .WithSkill(CommonSkills.Debugging)      // ✅
    .WithSkill(CommonSkills.FileManagement) // ✅
    .WithSkill(CommonSkills.DataAnalysis)   // ✅
    .Build();
```

### Approach 3: Mix Reusable and Custom Skills

Best for combining standard skills with agent-specific customizations:

```csharp
var agent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DebugPlugin>()

    // Add reusable skills
    .WithSkill(CommonSkills.Debugging)
    .WithSkill(CommonSkills.FileManagement)

    // Add agent-specific custom skill inline
    .WithSkills(skills => {
        skills.DefineSkill(
            name: "SpecializedDebugging",
            description: "Custom debugging protocol for this agent",
            functionRefs: new[] { "DebugPlugin.SpecialFunction" },
            instructions: "Agent-specific instructions that only apply here");
    })
    .Build();
```

### Approach 4: Configuration-Based Skills

Best for loading skills from JSON files or databases:

```csharp
// Load from JSON
var skillDefinitions = JsonSerializer.Deserialize<SkillDefinition[]>(json);

// Or create programmatically from database/config
var skills = new SkillDefinition[]
{
    new()
    {
        Name = "Debugging",
        Description = "Debugging and troubleshooting capabilities",
        FunctionReferences = new[]
        {
            "FileSystemPlugin.ReadFile",
            "FileSystemPlugin.ListDirectory",
            "DebugPlugin.GetStackTrace",
            "DebugPlugin.ListProcesses"
        },
        PostExpansionInstructions = "When debugging, always read error logs first.",
        PostExpansionInstructionDocuments = new[]
        {
            "debugging-protocol.md",
            "troubleshooting-checklist.md"
        },
        InstructionDocumentBaseDirectory = "skills/documents/"
    },

    new()
    {
        Name = "FileManagement",
        Description = "File and directory management operations",
        FunctionReferences = new[]
        {
            "FileSystemPlugin.ReadFile",      // Note: Same function in multiple skills!
            "FileSystemPlugin.WriteFile",
            "FileSystemPlugin.DeleteFile",
            "FileSystemPlugin.ListDirectory"
        },
        PostExpansionInstructionDocuments = new[]
        {
            "file-safety-protocol.md"
        },
        InstructionDocumentBaseDirectory = "skills/documents/"
    },

    new()
    {
        Name = "DataAnalysis",
        Description = "Data analysis and processing capabilities",
        FunctionReferences = new[]
        {
            "FileSystemPlugin.ReadFile",      // Same function again!
            "DataPlugin.ParseCSV",
            "DataPlugin.GenerateChart",
            "MathPlugin.Statistics"
        },
        PostExpansionInstructions = "Always validate data before analysis."
    }
};
```

### 2. Create Instruction Documents

Create markdown files with SOPs/protocols in `skills/documents/`:

**debugging-protocol.md:**
```markdown
# Debugging Protocol

## Step-by-Step Process:
1. Read error logs first using ReadFile
2. Analyze stack traces with GetStackTrace
3. Check running processes with ListProcesses
4. Verify file permissions
5. Test fixes incrementally

## Common Issues:
- Missing files: Use ListDirectory to verify paths
- Permission errors: Check file access rights
- ...
```

**file-safety-protocol.md:**
```markdown
# File Safety Protocol

## Before Any File Operation:
1. Verify file paths are valid
2. Check if file exists (use ReadFile first)
3. For destructive operations (delete, overwrite):
   - Confirm with user
   - Create backup if needed
4. Validate file permissions
```

### 3. Register Skills with Agent

```csharp
// Create all functions from plugins (existing code)
var pluginManager = new PluginManager();
pluginManager
    .RegisterPlugin<FileSystemPlugin>()
    .RegisterPlugin<DebugPlugin>()
    .RegisterPlugin<DataPlugin>()
    .RegisterPlugin<MathPlugin>();

var allFunctions = pluginManager.CreateAllFunctions();

// NEW: Create and build skill manager
var skillManager = new SkillManager();
skillManager
    .RegisterSkills(skills)
    .Build(allFunctions); // Validates function references and loads documents

// Get skill containers (add to ChatOptions.Tools)
var skillContainers = skillManager.GetSkillContainers();

// Create skill scoping manager
var skillScopingManager = skillManager.CreateScopingManager(allFunctions);

// Add all tools to ChatOptions
var chatOptions = new ChatOptions
{
    Tools = allFunctions  // All plugin functions
        .Concat(skillContainers) // Add skill containers
        .Cast<AITool>()
        .ToList()
};

// Create agent with skill scoping manager
var agent = new Agent(
    config,
    baseClient,
    chatOptions,
    PromptMiddlewares,
    ScopedFunctionMiddlewareManager,
    providerErrorHandler,
    providerRegistry,
    skillScopingManager: skillScopingManager, // NEW: Pass skill scoping manager
    PermissionMiddlewares,
    AIFunctionMiddlewares,
    MessageTurnMiddlewares
);
```

### 4. Enable Plugin Scoping (Required for Skills)

Skills require plugin scoping to be enabled in AgentConfig:

```csharp
var config = new AgentConfig
{
    Name = "MyAgent",
    PluginScoping = new PluginScopingConfig
    {
        Enabled = true  // REQUIRED for skills to work
    },
    // ... other config
};
```

## How It Works

### Scenario: LLM wants to debug a file issue

**Turn 1: Initial state**
```
Available Tools:
- Debugging (skill container)
- FileManagement (skill container)
- DataAnalysis (skill container)
- [Other non-plugin functions if any]
```

**Turn 2: LLM invokes "Debugging"**
```
Tool Call: Debugging
Tool Result: "Debugging expanded. Available functions: ReadFile, ListDirectory, GetStackTrace, ListProcesses

# Debugging Protocol
## Step-by-Step Process:
1. Read error logs first using ReadFile
...
[Full instructions from debugging-protocol.md]

# Troubleshooting Checklist
...
[Full instructions from troubleshooting-checklist.md]
"
```

**Turn 3: Functions available**
```
Available Tools:
- FileManagement (skill container - still Collapse)
- DataAnalysis (skill container - still Collapse)
- ReadFile (from Debugging expansion)
- ListDirectory (from Debugging expansion)
- GetStackTrace (from Debugging expansion)
- ListProcesses (from Debugging expansion)
```

**Turn 4: LLM uses ReadFile**
```
Tool Call: ReadFile("/var/log/error.log")
Tool Result: "[Error log contents]"
```

### Deduplication Example

If the LLM expands both "Debugging" AND "FileManagement":

**Turn 1:**
```
Tool Call 1: Debugging
Tool Call 2: FileManagement
```

**Turn 2: Both expanded**
```
Result 1: "Debugging expanded. Functions: ReadFile, ListDirectory, GetStackTrace, ListProcesses
[Debugging instructions...]"

Result 2: "FileManagement expanded. Functions: ReadFile, WriteFile, DeleteFile, ListDirectory
[File safety instructions...]"
```

**Turn 3: Deduplicated functions**
```
Available Tools:
- DataAnalysis (still Collapse)
- ReadFile (appears ONCE despite being in both skills)
- ListDirectory (appears ONCE despite being in both skills)
- GetStackTrace (from Debugging only)
- ListProcesses (from Debugging only)
- WriteFile (from FileManagement only)
- DeleteFile (from FileManagement only)
```

**Key Points:**
- ✅ Each function appears only once
- ✅ Both skill instructions were shown (ephemeral)
- ✅ LLM saw complete context for both skills

## Token Efficiency

### Container Results are Ephemeral

Container expansion messages are **Middlewareed from persistent conversation history**:

```csharp
// In Agent.cs - container results are filtered (line ~898)
var nonContainerResults = new List<AIContent>();
foreach (var content in toolResultMessage.Contents)
{
    if (content is FunctionResultContent result)
    {
        var isContainerResult = /* Check IsContainer or IsSkill metadata */;
        if (!isContainerResult)
        {
            nonContainerResults.Add(content); // Only non-container results persist
        }
    }
}
```

**What this means:**
- Instructions shown to LLM in current turn ✅
- Instructions NOT included in next turn ❌
- No token accumulation over conversation
- Perfect for rich SOPs (2000+ tokens)

### Token Cost Analysis

**Without Skills (naive approach):**
```
Every turn: Function descriptions with embedded instructions
- ReadFile: 50 tokens (description) + 2000 tokens (instructions) = 2050 tokens
- Total per turn: 2050 tokens × 4 functions = 8200 tokens
- Over 10 turns: 82,000 tokens wasted
```

**With Skills (ephemeral pattern):**
```
Turn 1: Skill container expansion
- Debugging container result: 2000 tokens (shown once, then filtered)
- ReadFile description: 50 tokens (simple, no instructions)
- Total: 2000 + (50 × 4) = 2200 tokens

Turn 2-10: No container expansions
- ReadFile description: 50 tokens
- Total per turn: 50 × 4 = 200 tokens
- Over 9 turns: 1800 tokens

Total: 2200 + 1800 = 4000 tokens (vs 82,000 tokens)
Savings: 95% token reduction
```

## Advanced Features

### Runtime vs Build-time Validation

**Build-time (recommended):**
```csharp
try
{
    skillManager.Build(allFunctions);
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Skill validation failed: {ex.Message}");
    // Example error:
    // "Skill 'Debugging' references unknown function 'InvalidFunction.DoSomething'.
    //  Available functions: FileSystemPlugin.ReadFile, DebugPlugin.GetStackTrace, ..."
}
```

**Benefits:**
- Fail-fast: Errors caught at startup
- Clear error messages with available function list
- Documents validated (existence, size, path security)

### Security Features

**Path Traversal Protection:**
```csharp
// SAFE: Path within allowed directory
PostExpansionInstructionDocuments = new[] { "debugging.md" }
// Resolved to: skills/documents/debugging.md ✅

// UNSAFE: Path traversal attempt
PostExpansionInstructionDocuments = new[] { "../../../secrets.txt" }
// Throws SecurityException ❌
```

**Document Size Limits:**
```csharp
// Default: 1MB max per document
// Prevents DoS attacks with huge files
```

**Custom Base Directory:**
```csharp
InstructionDocumentBaseDirectory = "my-custom-skills-path/"
// All documents resolved relative to this directory
```

### Skill Queries

```csharp
// Get all registered skills
var allSkills = skillManager.GetSkills();

// Get specific skill
var debuggingSkill = skillManager.GetSkillByName("Debugging");
if (debuggingSkill != null)
{
    Console.WriteLine($"Skill: {debuggingSkill.Name}");
    Console.WriteLine($"Functions: {string.Join(", ", debuggingSkill.FunctionReferences)}");
    Console.WriteLine($"Instructions: {debuggingSkill.ResolvedInstructions}");
}

// Check if skill scoping is enabled
if (agent.Config?.PluginScoping?.Enabled == true)
{
    Console.WriteLine("Skills are active");
}
```

## Best Practices

### 1. Skill Naming
- Use clear, semantic names: "Debugging", "FileManagement", "DataAnalysis"
- Avoid technical jargon: "FileOps" → "FileManagement"
- Keep names consistent with user mental models

### 2. Plugin References vs Function References

**When to use PluginReferences (recommended for most cases):**
```csharp
// ✅ PREFERRED: Reference entire plugin (simple, maintainable)
PluginReferences = new[] { "FileSystemPlugin", "DebugPlugin" }
// Benefits:
// - Simpler to write
// - Automatically includes new functions when plugin is updated
// - Clear semantic grouping
```

**When to use FunctionReferences (fine-grained control):**
```csharp
// ✅ USE WHEN: You need specific functions from multiple plugins
FunctionReferences = new[] {
    "FileSystemPlugin.ReadFile",   // Just one function from FileSystem
    "DebugPlugin.GetStackTrace",   // Just one function from Debug
    "DataPlugin.ParseCSV"          // Just one function from Data
}
// Benefits:
// - Precise control over what's included
// - Can cherry-pick functions from many plugins
```

**Mix both approaches when appropriate:**
```csharp
// ✅ HYBRID: All of DebugPlugin + specific functions from others
PluginReferences = new[] { "DebugPlugin" },  // All debug functions
FunctionReferences = new[] {
    "FileSystemPlugin.ReadFile",    // Plus specific file function
    "DataPlugin.ParseCSV"           // Plus specific data function
}
```

**Naming conventions:**
```csharp
✅ "FileSystemPlugin.ReadFile"  // Qualified (clear origin)
✅ "ReadFile"                   // Unqualified (works if unambiguous)
❌ "FileSystem.ReadFile"        // Wrong plugin name
```

### 3. Instruction Documents
- Keep each document focused (single concern)
- Use markdown for formatting (headings, lists, code blocks)
- Include examples and common patterns
- Limit to 1-2 pages per document (stay under 1MB)

### 4. Skill Granularity
**Good:**
```csharp
Skills: Debugging, FileManagement, DataAnalysis (3-5 functions each)
```

**Too Granular:**
```csharp
Skills: ReadFiles, WriteFiles, DeleteFiles (1 function each) ❌
```

**Too Broad:**
```csharp
Skills: AllOperations (20+ functions) ❌
```

### 5. Instruction Content
Focus on **when** and **how**, not **what**:
```markdown
✅ "When debugging, always read error logs first, then check stack traces..."
✅ "Before deleting files, confirm with user and create backup..."
❌ "ReadFile reads a file" (redundant with function description)
```

## Migration Guide

### Existing Plugin-Only Setup

**Before (Plugins only):**
```csharp
var pluginManager = new PluginManager();
pluginManager.RegisterPlugin<FileSystemPlugin>();
var functions = pluginManager.CreateAllFunctions();

var chatOptions = new ChatOptions { Tools = functions.Cast<AITool>().ToList() };
var agent = new Agent(config, client, chatOptions, ...);
```

**After (Plugins + Skills):**
```csharp
var pluginManager = new PluginManager();
pluginManager.RegisterPlugin<FileSystemPlugin>();
var functions = pluginManager.CreateAllFunctions();

// NEW: Add skills
var skillManager = new SkillManager();
skillManager.RegisterSkills(mySkills).Build(functions);
var skillContainers = skillManager.GetSkillContainers();
var skillScopingManager = skillManager.CreateScopingManager(functions);

var chatOptions = new ChatOptions
{
    Tools = functions.Concat(skillContainers).Cast<AITool>().ToList()
};

var agent = new Agent(
    config,
    client,
    chatOptions,
    ...,
    skillScopingManager: skillScopingManager // NEW parameter
);
```

**Backward Compatible:**
- Existing plugins work unchanged ✅
- Skills are optional ✅
- No breaking changes ✅

## Troubleshooting

### "Skill validation failed: unknown function"
**Cause:** Function reference doesn't match any registered function.

**Fix:**
```csharp
// Ensure plugin is registered before building skills
pluginManager.RegisterPlugin<FileSystemPlugin>(); // MUST come before skill build

// Use correct reference format
FunctionReferences = new[] { "FileSystemPlugin.ReadFile" } // ✅
FunctionReferences = new[] { "ReadFile" } // Also works if unambiguous
FunctionReferences = new[] { "FileSystem.ReadFile" } // ❌ Wrong plugin name
```

### "Document not found"
**Cause:** Document file doesn't exist or path is wrong.

**Fix:**
```csharp
// Check file exists
var fullPath = Path.Combine("skills/documents", "debugging.md");
Console.WriteLine($"Looking for: {fullPath}");
Console.WriteLine($"Exists: {File.Exists(fullPath)}");

// Verify InstructionDocumentBaseDirectory
InstructionDocumentBaseDirectory = "skills/documents/" // Trailing slash optional
```

### "Skills not working / functions not appearing"
**Cause:** Plugin scoping not enabled.

**Fix:**
```csharp
config.PluginScoping = new PluginScopingConfig { Enabled = true };
```

### "Document exceeds maximum size"
**Cause:** Instruction document > 1MB.

**Fix:**
- Split large documents into multiple smaller files
- Remove unnecessary content
- Each document should be focused and concise

## API Reference

See source code for complete API documentation:
- [SkillDefinition.cs](SkillDefinition.cs)
- [SkillManager.cs](SkillManager.cs)
- [SkillScopingManager.cs](SkillScopingManager.cs)

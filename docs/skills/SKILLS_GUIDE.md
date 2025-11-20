# HPD-Agent Skills Guide

**Version:** 2.0.0
**Last Updated:** 2025-10-26

---

## Table of Contents

1. [Introduction](#introduction)
2. [What Are Skills?](#what-are-skills)
3. [Creating Your First Skill](#creating-your-first-skill)
4. [Skill Components](#skill-components)
5. [Skill Scoping Modes](#skill-scoping-modes)
6. [Nested Skills](#nested-skills)
7. [Skills with Plugin Scope](#skills-with-plugin-scope)
8. [Advanced Patterns](#advanced-patterns)
9. [Best Practices](#best-practices)

---

## Introduction

Skills are type-safe, semantic groupings of AI functions that help organize your agent's capabilities. Unlike raw plugin functions, skills provide context and instructions to guide the agent in using related functions together to accomplish specific tasks.

### Key Benefits

- **Type Safety**: Compile-time validation of function references
- **Auto-Registration**: Referenced plugins are automatically registered
- **Semantic Grouping**: Functions organized by purpose, not just by plugin
- **Instructions**: Provide guidance on how to use functions together
- **IntelliSense Support**: Full IDE autocomplete and go-to-definition
- **Refactoring Safety**: Renaming functions updates skill references automatically

---

## What Are Skills?

A skill is a curated collection of AI functions combined with instructions on how to use them. Think of skills as "workflows" or "recipes" that tell the agent how to accomplish specific tasks.

### Skills vs Plugins

**Plugins** are collections of related functions grouped by technical domain:
```csharp
[PluginScope("File system operations")]
public class FileSystemPlugin
{
    [AIFunction] public async Task<string> ReadFile(string path) { }
    [AIFunction] public async Task<string> WriteFile(string path, string content) { }
    [AIFunction] public async Task<string> DeleteFile(string path) { }
}
```

**Skills** are collections of functions grouped by task or workflow:
```csharp
public static class DebuggingSkills
{
    public static Skill FileDebugging(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "FileDebugging",
            "Debug application issues by analyzing log files",
            @"Follow this workflow:
            1. Use ReadFile to examine error logs
            2. Use GetStackTrace to identify error locations
            3. Document findings with WriteFile",
            FileSystemPlugin.ReadFile,    // From FileSystemPlugin
            FileSystemPlugin.WriteFile,   // From FileSystemPlugin
            DebugPlugin.GetStackTrace     // From DebugPlugin
        );
    }
}
```

---

## Creating Your First Skill

### Step 1: Create a Skill Class

Create a public static class to hold your skills:

```csharp
using HPD_Agent.Skills;

public static class MySkills
{
    // Skills will go here
}
```

### Step 2: Define a Skill Method

Add a skill method that returns `Skill`:

```csharp
public static class MySkills
{
    public static Skill TextProcessing(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "TextProcessing",
            description: "Process and analyze text files",
            instructions: "Use ReadFile to load text, then analyze with text utilities",

            // Type-safe function references
            FileSystemPlugin.ReadFile,
            TextUtilsPlugin.CountWords,
            TextUtilsPlugin.FindPattern
        );
    }
}
```

### Step 3: Register the Skill Class

In your agent builder:

```csharp
var agent = new AgentBuilder()
    .WithPlugin<MySkills>()  // That's it! FileSystemPlugin and TextUtilsPlugin auto-registered
    .Build();
```

---

## Skill Components

### Name

The skill's identifier. Should match the method name for consistency.

```csharp
return SkillFactory.Create(
    name: "TextProcessing",  // ← Should match method name
    // ...
);

public static Skill TextProcessing(SkillOptions? options = null)  // ← Same name
```

### Description

Short description shown to the agent before the skill is activated. This helps the agent decide whether to use this skill.

```csharp
description: "Process and analyze text files",
```

**Guidelines:**
- Keep it concise (1-2 sentences)
- Describe WHAT the skill does, not HOW
- Focus on the outcome or capability

### Instructions

Detailed guidance shown to the agent AFTER the skill is activated. This tells the agent HOW to use the functions together.

```csharp
instructions: @"
Follow this workflow:
1. Use ReadFile to load the text file
2. Use CountWords to get word statistics
3. Use FindPattern to search for specific patterns
4. Combine results and present findings
"
```

**Guidelines:**
- Use numbered steps for workflows
- Reference function names explicitly
- Explain the expected order of operations
- Include any important constraints or warnings

### Function References

Type-safe references to AI functions:

```csharp
FileSystemPlugin.ReadFile,     // ✅ Compile-time safe
TextUtilsPlugin.CountWords,    // ✅ IntelliSense works
TextUtilsPlugin.FindPattern    // ✅ Refactoring support
```

**What you can reference:**
- Methods with `[AIFunction]` attribute
- Other skill methods (nested skills)

### Options

Optional configuration for the skill:

```csharp
options: new SkillOptions
{
    // Skills are always scoped by default,  // or Scoped
    AutoExpand = false,
    InstructionDocuments = new[] { "advanced_text_processing.md" },
    InstructionDocumentBaseDirectory = "skills/documents/"
}
```

---

## Skill Scoping Modes

Skills support two scoping modes that control function visibility:

### default scoped mode (Default)

Functions are **always visible**, skill just provides instructions when activated.

```csharp
public static Skill BasicFileOps(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "BasicFileOps",
        "Basic file operations",
        "Use ReadFile and WriteFile for safe file handling",
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile,
        options: new SkillOptions
        {
            // Skills are always scoped by default
        }
    );
}
```

**Behavior:**
- Turn 1: Agent sees `BasicFileOps` skill + `ReadFile` and `WriteFile` functions
- Agent calls `BasicFileOps` → receives instructions
- Turn 2: Agent can use `ReadFile` and `WriteFile` with context from instructions

**Use when:**
- Functions are safe to use anytime
- You just want to provide guidance
- Functions are general-purpose utilities

### Scoped Mode

Functions are **hidden until skill is activated**.

```csharp
public static Skill DangerousOperations(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "DangerousOperations",
        "Dangerous file operations - USE WITH CAUTION",
        "WARNING: These operations are destructive and cannot be undone!",
        FileSystemPlugin.DeleteFile,
        FileSystemPlugin.TruncateFile,
        DatabasePlugin.DropTable,
        options: new SkillOptions
        {
            // Skills are always scoped by default
        }
    );
}
```

**Behavior:**
- Turn 1: Agent sees only `DangerousOperations` skill (functions hidden)
- Agent must explicitly call `DangerousOperations` to unlock functions
- Turn 2: Agent sees `DeleteFile`, `TruncateFile`, `DropTable` with warning context

**Use when:**
- Functions are dangerous or destructive
- You want agent to consciously opt-in
- Functions require special context to use safely

### Auto-Expand

Skills can be automatically activated at conversation start:

```csharp
options: new SkillOptions
{
    AutoExpand = true  // Activate immediately
}
```

**Use cases:**
- Core skills that should always be available
- Setting default context for the agent
- Providing baseline instructions

---

## Nested Skills

Skills can reference other skills, creating hierarchical workflows:

```csharp
public static class FileWorkflows
{
    // Base skill
    public static Skill BasicFileOps(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "BasicFileOps",
            "Basic file operations",
            "Read and write files safely",
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile
        );
    }

    // Skill that builds on BasicFileOps
    public static Skill AdvancedFileProcessing(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "AdvancedFileProcessing",
            "Advanced file processing with validation",
            "Validate paths, then use file operations with error handling",

            // Reference another skill!
            FileWorkflows.BasicFileOps,  // ← Expands to ReadFile + WriteFile

            // Add more functions
            ValidationPlugin.ValidatePath,
            ValidationPlugin.CheckPermissions,
            ErrorHandlingPlugin.TryCatch
        );
    }
}
```

**How it works:**
1. Source generator detects `FileWorkflows.BasicFileOps` reference
2. Recursively resolves: `BasicFileOps` → `[ReadFile, WriteFile]`
3. Combines with direct references: `[ValidatePath, CheckPermissions, TryCatch]`
4. Final skill contains: `[ReadFile, WriteFile, ValidatePath, CheckPermissions, TryCatch]`

### Circular References

Circular skill references are handled gracefully:

```csharp
public static Skill SkillA(SkillOptions? options = null)
{
    return SkillFactory.Create("SkillA", "...", "...",
        MySkills.SkillB);  // References SkillB
}

public static Skill SkillB(SkillOptions? options = null)
{
    return SkillFactory.Create("SkillB", "...", "...",
        MySkills.SkillA);  // References SkillA - circular!
}
```

**Resolution:**
- Visited set prevents infinite loops
- Functions are deduplicated
- Both skills work correctly

---

## Skills with Plugin Scope

You can combine skills and regular functions in the same plugin class:

```csharp
[PluginScope("Git operations")]
public partial class GitPlugin
{
    // Regular AI functions
    [AIFunction]
    public async Task<string> GetDiff(string? path = null) { }

    [AIFunction]
    public async Task<string> GetBlame(string path) { }

    [AIFunction]
    public async Task<string> Commit(string message) { }

    // Skills using functions from this and other plugins
    public static Skill CodeReview(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "CodeReview",
            "Review code changes comprehensively",
            "Check diff first, then examine blame for context, use ReadFile for full file inspection",

            // Own functions
            GitPlugin.GetDiff,
            GitPlugin.GetBlame,

            // Other plugin functions
            FileSystemPlugin.ReadFile,
            LintPlugin.CheckStyle
        );
    }
}
```

**Benefits:**
- Related skills live with related functions
- Natural organizational structure
- Skills can use own plugin's functions + external functions

---

## Advanced Patterns

### Skill Containers

Skill classes can have `[PluginScope]` to create a container:

```csharp
[PluginScope("Debugging workflows and troubleshooting")]
public static class DebuggingSkills
{
    public static Skill FileDebugging(SkillOptions? options = null) { }
    public static Skill DatabaseDebugging(SkillOptions? options = null) { }
    public static Skill PerformanceDebugging(SkillOptions? options = null) { }
}
```

**Progressive disclosure:**
```
Turn 1: Agent sees "DebuggingSkills" container
Turn 2: Agent activates container → sees FileDebugging, DatabaseDebugging, PerformanceDebugging
Turn 3: Agent activates FileDebugging → sees ReadFile, WriteFile, GetStackTrace
```

### Instruction Documents

Load instructions from external files:

```csharp
public static Skill ComplexWorkflow(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "ComplexWorkflow",
        "Complex multi-step workflow",
        "See detailed instructions in documentation",
        FileSystemPlugin.ReadFile,
        options: new SkillOptions
        {
            InstructionDocuments = new[]
            {
                "complex_workflow_steps.md",
                "troubleshooting_guide.md"
            },
            InstructionDocumentBaseDirectory = "skills/documents/"
        }
    );
}
```

**Files loaded from:** `skills/documents/complex_workflow_steps.md`

### Conditional Skills

Skills can be conditionally available based on context:

```csharp
[PluginScope("Database operations")]
public partial class DatabasePlugin
{
    [AIFunction<DatabaseContext>]
    [ConditionalFunction("HasReadPermission")]
    public async Task<string> ExecuteQuery(string sql) { }

    // Skill only useful when database is available
    public static Skill DatabaseDebugging(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "DatabaseDebugging",
            "Debug database performance issues",
            "Check slow query log, analyze execution plans",
            DatabasePlugin.ExecuteQuery,    // Conditional function
            DatabasePlugin.GetQueryPlan,
            FileSystemPlugin.ReadFile
        );
    }
}
```

---

## Best Practices

### Naming

**DO:**
- Use descriptive, action-oriented names: `FileDebugging`, `CodeReview`, `DataAnalysis`
- Match method name and skill name: `public static Skill FileDebugging()` → `name: "FileDebugging"`
- Use PascalCase for skill names

**DON'T:**
- Use vague names: `Helper`, `Utils`, `Misc`
- Use abbreviations: `FDbg` instead of `FileDebugging`
- Mix naming conventions

### Descriptions

**DO:**
```csharp
description: "Debug application issues by analyzing log files"  // Clear outcome
description: "Review code changes with style checking and blame analysis"  // Specific capabilities
```

**DON'T:**
```csharp
description: "A skill for debugging"  // Too vague
description: "Uses ReadFile and GetStackTrace to debug issues"  // Implementation details
```

### Instructions

**DO:**
```csharp
instructions: @"
1. Use ReadFile to load the error log
2. Search for ERROR or EXCEPTION keywords
3. Use GetStackTrace to find the error location
4. Document findings in a structured format
"
```

**DON'T:**
```csharp
instructions: "Debug stuff"  // Too vague
instructions: "You have access to ReadFile, WriteFile, and GetStackTrace"  // Just listing functions
```

### Function Selection

**DO:**
- Include only relevant functions
- Group functions that work well together
- Keep skills focused on specific tasks

**DON'T:**
- Include every function from a plugin
- Mix unrelated functions
- Create overly broad skills

### Scoping Decisions

**Use InstructionOnly when:**
- Functions are safe to use anytime
- Functions are general-purpose utilities
- You want maximum flexibility

**Use Scoped when:**
- Functions are dangerous/destructive
- Functions require specific context
- You want deliberate activation

### Organization

**Organize skills by:**
- Task/workflow (FileDebugging, CodeReview)
- User role (DeveloperTools, AdminOperations)
- Complexity level (BasicFileOps, AdvancedFileOps)

**File structure:**
```
/Skills/
  /Development/
    DebuggingSkills.cs
    CodeReviewSkills.cs
  /Administration/
    SystemMaintenanceSkills.cs
    UserManagementSkills.cs
  /DataAnalysis/
    TextAnalysisSkills.cs
    LogAnalysisSkills.cs
```

---

## Complete Example

Here's a complete, real-world example:

```csharp
using HPD_Agent.Skills;

[PluginScope("Software development workflows")]
public static class DevelopmentSkills
{
    /// <summary>
    /// Comprehensive code review workflow with linting and testing
    /// </summary>
    public static Skill CodeReview(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "CodeReview",
            description: "Perform comprehensive code review with automated checks",
            instructions: @"
Code Review Workflow:

1. **Get Changes**
   - Use GetDiff to see what files changed
   - Use GetBlame to understand authorship and history

2. **Analyze Code**
   - Use ReadFile to examine changed files in detail
   - Use CheckStyle to verify code style compliance

3. **Run Tests**
   - Use RunTests to ensure no regressions
   - Document any test failures

4. **Document Findings**
   - Use WriteFile to create review summary
   - Include: style issues, test results, suggestions

5. **Final Check**
   - Verify all files compile
   - Ensure no breaking changes
",
            // Version control functions
            GitPlugin.GetDiff,
            GitPlugin.GetBlame,

            // File operations
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile,

            // Code quality
            LintPlugin.CheckStyle,
            TestPlugin.RunTests,

            // Options
            options: new SkillOptions
            {
                // Skills are always scoped by default,
                InstructionDocuments = new[] { "code_review_checklist.md" }
            }
        );
    }

    /// <summary>
    /// Debugging workflow for analyzing application errors
    /// </summary>
    public static Skill ErrorDebugging(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "ErrorDebugging",
            description: "Debug application errors using logs and stack traces",
            instructions: @"
Error Debugging Process:

1. **Locate Errors**
   - Use SearchFiles to find log files
   - Use FindPattern to search for ERROR/EXCEPTION keywords

2. **Analyze Errors**
   - Use ReadFile to read relevant log sections
   - Use GetStackTrace to identify error locations

3. **Investigate Code**
   - Use GetBlame to see who last modified the failing code
   - Use ReadFile to examine the problematic code

4. **Document Solution**
   - Use WriteFile to document the root cause
   - Include steps to reproduce and fix
",
            // File search and read
            FileSystemPlugin.SearchFiles,
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile,

            // Text analysis
            TextUtilsPlugin.FindPattern,

            // Debugging tools
            DebugPlugin.GetStackTrace,

            // Version control
            GitPlugin.GetBlame
        );
    }

    /// <summary>
    /// Composite skill combining code review and debugging
    /// </summary>
    public static Skill FullDevelopmentWorkflow(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "FullDevelopmentWorkflow",
            description: "Complete development workflow including review and debugging",
            instructions: "Use CodeReview for reviewing changes, then ErrorDebugging if issues found",

            // Nested skill references
            DevelopmentSkills.CodeReview,
            DevelopmentSkills.ErrorDebugging,

            // Additional tools
            BuildPlugin.Compile,
            BuildPlugin.RunTests
        );
    }
}
```

**Usage:**

```csharp
var agent = new AgentBuilder()
    .WithPlugin<DevelopmentSkills>()  // Auto-registers all referenced plugins!
    .Build();

// Agent now has access to:
// - CodeReview skill
// - ErrorDebugging skill
// - FullDevelopmentWorkflow skill
// Plus all their referenced functions
```

---

**Next:** [Troubleshooting Guide](TROUBLESHOOTING.md) | [API Reference](API_REFERENCE.md)

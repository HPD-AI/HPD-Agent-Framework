# Skills System - Implementation Architecture & Design Decisions

**Author:** HPD-Agent Team
**Date:** 2025-01-XX
**Status:** Implemented (Phase 1 - MVP)

---

## Table of Contents
1. [What Are Skills?](#what-are-skills)
2. [The Problem We're Solving](#the-problem-were-solving)
3. [High-Level Architecture](#high-level-architecture)
4. [Implementation Flow](#implementation-flow)
5. [Why String-Based References?](#why-string-based-references)
6. [Alternative Approaches Considered](#alternative-approaches-considered)
7. [The Validation Safeguard](#the-validation-safeguard)
8. [Future Enhancements (Phase 2+)](#future-enhancements-phase-2)
9. [How to Debug Issues](#how-to-debug-issues)

---

## What Are Skills?

**Skills are semantic groupings of functions from multiple plugins.**

### The Key Insight:
- **Plugins** = Implementation units (1:N ownership - plugin owns functions)
- **Skills** = Semantic grouping units (M:N membership - functions belong to multiple skills)

### Example:
```csharp
// FileSystemPlugin.ReadFile can belong to:
// - Debugging skill (read error logs)
// - FileManagement skill (manage files)
// - DataAnalysis skill (read CSV data)
```

**The same function appears in multiple skills with different contextual instructions!**

---

## The Problem We're Solving

### Before Skills:
```csharp
// Problem: ReadFile function owned by FileSystemPlugin only
// Can't have different instructions for different contexts
[PluginScope("File operations")]
public class FileSystemPlugin {
    [AIFunction(Description = "Reads a file")]  // ← Generic description
    public string ReadFile(string path) { ... }
}
```

When used for debugging, the LLM doesn't know to "read error logs first."
When used for data analysis, the LLM doesn't know to "validate data format."

### With Skills:
```csharp
// Debugging skill provides debugging context
CommonSkills.Debugging = {
    FunctionReferences = ["FileSystemPlugin.ReadFile", "DebugPlugin.GetStackTrace"],
    PostExpansionInstructions = "When debugging, ALWAYS read error logs first..."
}

// DataAnalysis skill provides analysis context
CommonSkills.DataAnalysis = {
    FunctionReferences = ["FileSystemPlugin.ReadFile", "DataPlugin.ParseCSV"],
    PostExpansionInstructions = "Always validate data format before analysis..."
}
```

**Same function (`ReadFile`), different semantic contexts!**

---

## High-Level Architecture

### Component Overview:

```
┌─────────────────────────────────────────────────────────────┐
│                    USER (AgentBuilder)                      │
│  .WithPlugin<FileSystemPlugin>()                            │
│  .WithSkill(CommonSkills.Debugging)                         │
│  .Build()                                                   │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│                 AgentBuilder.Build()                        │
│                                                             │
│  Step 1: Create plugin functions (line 515-521)            │
│  ├─ foreach registration in _pluginManager                 │
│  │   └─ ToAIFunctions() → Calls generated code             │
│  │       └─ FileSystemPluginRegistration.CreatePlugin()    │
│  └─ Result: pluginFunctions = [ReadFile, WriteFile, ...]   │
│                                                             │
│  Step 2: Add MCP tools (line 536-577)                      │
│  └─ pluginFunctions.AddRange(mcpTools)                     │
│                                                             │
│  Step 3: Build skills (line 579-607)                       │
│  ├─ skillManager.RegisterSkills(_skillDefinitions)         │
│  └─ skillManager.Build(pluginFunctions) ← VALIDATION!      │
│                                                             │
│  Step 4: Create Agent with SkillScopingManager             │
└─────────────────────────────────────────────────────────────┘
```

### Key Classes:

| Class | Responsibility | Lifecycle |
|-------|---------------|-----------|
| `SkillDefinition` | Blueprint for a skill (name, function refs, instructions) | Reusable across agents |
| `SkillManager` | Registers and validates skills at Build() time | One per agent build |
| `SkillScopingManager` | Runtime skill expansion and function resolution | One per agent instance |
| `AgentBuilder` | Orchestrates the entire build process | Per agent build |

---

## Implementation Flow

### Compile-Time (Source Generator):

```
HPDPluginSourceGenerator runs:
├─ Scans ALL classes with [AIFunction] methods
├─ Generates FileSystemPluginRegistration.cs
│   └─ public static List<AIFunction> CreatePlugin(FileSystemPlugin instance, ...)
├─ Generates DebugPluginRegistration.cs
│   └─ public static List<AIFunction> CreatePlugin(DebugPlugin instance, ...)
└─ Generates DataPluginRegistration.cs
    └─ public static List<AIFunction> CreatePlugin(DataPlugin instance, ...)

ALL plugins are generated, regardless of which agents will use them.
```

### Runtime (AgentBuilder.Build()):

```
User calls .Build():

┌─ PHASE 1: Plugin Function Creation ────────────────────────┐
│ Line 515-521: Create functions from registered plugins     │
│                                                             │
│ foreach (var registration in _pluginManager.GetPluginRegistrations())
│ {                                                           │
│     var functions = registration.ToAIFunctions(context);   │
│     pluginFunctions.AddRange(functions);                   │
│ }                                                           │
│                                                             │
│ ONLY registered plugins get their functions created!       │
│ If .WithPlugin<FileSystemPlugin>() wasn't called,          │
│ FileSystemPluginRegistration.CreatePlugin() is NOT called. │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ PHASE 2: MCP Tool Integration ────────────────────────────┐
│ Line 536-577: Add MCP tools to same list                   │
│                                                             │
│ pluginFunctions.AddRange(mcpTools);                        │
│                                                             │
│ Now pluginFunctions contains:                              │
│ - Functions from registered plugins                        │
│ - Functions from MCP servers                               │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ PHASE 3: Skill Validation & Building ─────────────────────┐
│ Line 586-587: Validate skills against available functions  │
│                                                             │
│ skillManager.RegisterSkills(_skillDefinitions)             │
│            .Build(pluginFunctions); ← VALIDATION HERE!     │
│                                                             │
│ For each skill:                                            │
│   For each FunctionReference in skill:                     │
│     if (!pluginFunctions.Contains(reference))              │
│       throw "Function not found!"                          │
│                                                             │
│ If validation passes:                                      │
│ - Load instruction documents                               │
│ - Create skill container functions                         │
│ - Create SkillScopingManager                               │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─ PHASE 4: Agent Creation ──────────────────────────────────┐
│ Line 613-624: Create Agent with SkillScopingManager        │
│                                                             │
│ var agent = new Agent(                                     │
│     config,                                                 │
│     client,                                                 │
│     mergedOptions,                                          │
│     ...                                                     │
│     skillScopingManager  ← Passed to agent                 │
│ );                                                          │
│                                                             │
│ Agent now has skills integrated!                           │
└─────────────────────────────────────────────────────────────┘
```

### Key Insight: **Order Matters!**

```
1. Create plugin functions FIRST   (line 515-521)
2. Validate skills SECOND           (line 587)
3. Create agent THIRD               (line 613)
```

You **cannot** validate skills before plugin functions are created because the validation checks if functions exist in the `pluginFunctions` list!

---

## Why String-Based References?

### Decision: Function references are plain strings

```csharp
FunctionReferences = new[] {
    "FileSystemPlugin.ReadFile",  // ← Plain string!
    "DebugPlugin.GetStackTrace"    // ← Plain string!
}
```

### Why We Chose This:

#### ✅ Pros:
1. **Flexibility**: Can reference ANY function source
   - Plugin functions: `"FileSystemPlugin.ReadFile"`
   - MCP tools: `"filesystem_read_file"`
   - Custom functions: `"MyCustomFunction"`
   - Server-configured tools: Any function name

2. **Simplicity**: No complex type system needed
   - Easy to serialize/deserialize (JSON/database)
   - Easy to store in configuration files
   - Easy to understand

3. **Dynamic Composition**: Can load from config
   ```csharp
   var skills = JsonSerializer.Deserialize<SkillDefinition[]>(json);
   ```

4. **AOT-Safe**: No reflection on generic types
   - Source generator creates concrete types
   - String lookups are AOT-compatible

#### ❌ Cons:
1. **No compile-time safety**: Typos compile fine
2. **No IntelliSense**: Can't autocomplete function names
3. **No refactoring support**: Rename breaks references
4. **Runtime validation only**: Errors at Build(), not compile

### Validation Strategy:

**Fail-Fast at Build() Time:**
```csharp
.Build();  // ← Validates ALL skills here
// If ANY function reference is invalid, throws immediately with helpful error:
// "Skill 'Debugging' references unknown function 'FileSystemPlugin.RedFile'.
//  Available functions: FileSystemPlugin.ReadFile, FileSystemPlugin.WriteFile, ..."
```

**Why this is acceptable:**
- Errors happen at app startup, not during conversation
- Clear error messages list available functions
- Testing will catch typos immediately
- Most codebases have startup tests

---

## Alternative Approaches Considered

### ❌ Alternative 1: Compile-Time Validation via Source Generator

**Idea:** Have the source generator validate skills at compile-time.

**Why It Won't Work:**

```
SOURCE GENERATOR PHASE (Compile-Time):
├─ Generator runs BEFORE user code compiles
├─ Generates FileSystemPluginRegistration.cs
└─ Finishes

USER CODE PHASE (Compile-Time):
├─ User writes:
│   .WithSkills(skills => {
│       skills.DefineSkill("Debugging", ...,
│           functionRefs: new[] { "FileSystemPlugin.ReadFile" });
│   })
└─ This compiles AFTER generator finished

The generator can't see user code that compiles after it!
```

**Problem:** Source generators can't see code that compiles after them. Skills are defined in user code (`.WithSkills()`), which compiles after the generator runs.

**Verdict:** ❌ Impossible due to compilation order.

---

### ❌ Alternative 2: Generate a Global Function Registry

**Idea:** Source generator creates a registry of all functions, validate against that.

**What we could generate:**
```csharp
// Generated by source generator
public static class GeneratedFunctionRegistry
{
    public static readonly HashSet<string> AllAvailableFunctions = new()
    {
        "FileSystemPlugin.ReadFile",
        "FileSystemPlugin.WriteFile",
        "DebugPlugin.GetStackTrace",
        "DataPlugin.ParseCSV"
    };
}

// Then validate:
foreach (var funcRef in skill.FunctionReferences)
{
    if (!GeneratedFunctionRegistry.AllAvailableFunctions.Contains(funcRef))
        throw new InvalidOperationException($"Function '{funcRef}' doesn't exist in ANY plugin!");
}
```

**Why We Didn't Do This:**

1. **Still runtime validation** - Not compile-time, just earlier runtime
2. **False positives** - Validates function exists somewhere, but not if it's registered for THIS agent
3. **Confusing errors** - "Function exists but not registered" vs "Function doesn't exist anywhere"
4. **Doesn't solve the real problem** - You still need to check if plugins are registered

**Example Problem:**
```csharp
var agent = AgentBuilder.Create()
    .WithSkill(CommonSkills.DataAnalysis)  // References "DataPlugin.ParseCSV"
    // .WithPlugin<DataPlugin>()  ❌ Forgot to register!
    .Build();

// With global registry:
// ✅ Validates "DataPlugin.ParseCSV" exists (it does, in the registry)
// ❌ But DataPlugin wasn't registered, so function doesn't exist in pluginFunctions
// 😕 Two-step validation is confusing

// Current approach:
// ❌ "Skill 'DataAnalysis' references unknown function 'DataPlugin.ParseCSV'"
// ✅ Clear: function not available because plugin not registered
```

**Verdict:** ❌ Adds complexity without solving the real problem.

---

### ❌ Alternative 3: Validate Before Plugin Registration

**Idea:** Validate skills before creating plugin functions.

**Why It Won't Work:**

```csharp
AgentBuilder.Build() {
    // ❌ Can't do this - skills need to know which functions are available
    skillManager.Build(???);  // What do we pass? Functions don't exist yet!

    // Plugin functions created here
    foreach (var registration in _pluginManager.GetPluginRegistrations()) {
        pluginFunctions.AddRange(registration.ToAIFunctions());
    }
}
```

**The fundamental problem:**
- Skills validate against **available functions**
- Available functions = functions from **registered plugins**
- You can't know which functions are available until plugins create them
- Therefore, validation MUST happen AFTER plugin registration

**Verdict:** ❌ Logically impossible - cart before horse.

---

### ❌ Alternative 4: Skills Auto-Register Plugins

**Idea:** Skills automatically register the plugins they reference.

```csharp
public class SkillDefinition
{
    public Type[] RequiredPluginTypes { get; set; }  // ← Auto-register these
}

// Then in Build():
foreach (var skill in skills) {
    foreach (var pluginType in skill.RequiredPluginTypes) {
        _pluginManager.RegisterPlugin(pluginType);  // ← Automatic!
    }
}
```

**Why We Didn't Do This:**

1. **Violates explicit configuration principle**
   - Users should explicitly register what they want
   - Auto-registration is "magic" and surprising

2. **Can't control plugin instances**
   - What if user wants to pass custom instance?
   - What if user wants specific plugin context?

3. **Can't conditionally exclude plugins**
   - User might want skill definition but not certain plugins

4. **Makes skills less reusable**
   - Skill becomes tightly coupled to specific plugin Types
   - Can't reference MCP tools or custom functions (they don't have Types)

**Verdict:** ❌ Too much magic, violates principle of explicit configuration.

---

### ✅ Alternative 5 (Future): Type-Safe Function References via Source Generator

**Idea:** Generate constants for function references.

```csharp
// Phase 2: Source generator could generate this
public static class FileSystemPluginFunctions
{
    public const string ReadFile = "FileSystemPlugin.ReadFile";
    public const string WriteFile = "FileSystemPlugin.WriteFile";
}

public static class DebugPluginFunctions
{
    public const string GetStackTrace = "DebugPlugin.GetStackTrace";
}

// Then use with IntelliSense:
.WithSkills(skills => {
    skills.DefineSkill("Debugging", "...",
        functionRefs: new[] {
            FileSystemPluginFunctions.ReadFile,     // ✅ IntelliSense!
            DebugPluginFunctions.GetStackTrace      // ✅ Refactor-safe!
        });
})
```

**Benefits:**
- ✅ IntelliSense support
- ✅ Refactoring support (rename updates constants)
- ✅ Still strings under the hood (serializable, AOT-safe)
- ✅ Optional (can still use plain strings)

**Why Not Phase 1:**
- Adds complexity to source generator
- String-based works fine for MVP
- Can be added non-breaking in Phase 2

**Verdict:** ✅ Good enhancement for Phase 2!

---

## The Validation Safeguard

### Where Validation Happens:

**File:** `SkillDefinition.cs`
**Method:** `Build(Dictionary<string, AIFunction> allFunctions)`
**Lines:** 86-100

```csharp
// Validate all function references exist
var missingFunctions = new List<string>();
foreach (var reference in FunctionReferences)
{
    if (!allFunctions.ContainsKey(reference))  // ← The safeguard!
    {
        missingFunctions.Add(reference);
    }
}

if (missingFunctions.Any())
{
    var availableFunctions = string.Join(", ", allFunctions.Keys.Take(20));
    throw new InvalidOperationException(
        $"Skill '{Name}' references {missingFunctions.Count} unknown function(s): {string.Join(", ", missingFunctions)}. " +
        $"Available functions: {availableFunctions}{(allFunctions.Count > 20 ? "..." : "")}");
}
```

### What Makes This The Right Safeguard:

1. **Happens at the right time**: After plugin registration, before agent creation
2. **Clear error messages**: Lists missing functions AND available functions
3. **Fail-fast**: Errors at app startup, not during conversation
4. **Complete validation**: Checks ALL function references in ALL skills
5. **Context-aware**: Shows what's actually available for THIS agent

### Example Error Messages:

```
❌ Forgot to register plugin:
InvalidOperationException: Skill 'Debugging' references 1 unknown function(s): DebugPlugin.GetStackTrace.
Available functions: FileSystemPlugin.ReadFile, FileSystemPlugin.WriteFile, FileSystemPlugin.ListDirectory

❌ Typo in function name:
InvalidOperationException: Skill 'DataAnalysis' references 1 unknown function(s): DataPlugin.ParseCSV.
Available functions: FileSystemPlugin.ReadFile, DataPlugin.ParseCsv, MathPlugin.Statistics
                                                           ^^^^^^^^ (note lowercase 's')

✅ All good:
Successfully integrated 3 skills into agent
```

---

## Future Enhancements (Phase 2+)

### Phase 2: Type-Safe Function References

**Generate constants for IntelliSense:**
```csharp
// Generated
public static class FunctionRefs
{
    public static class FileSystemPlugin
    {
        public const string ReadFile = "FileSystemPlugin.ReadFile";
        public const string WriteFile = "FileSystemPlugin.WriteFile";
    }
}

// Usage
functionRefs: new[] { FunctionRefs.FileSystemPlugin.ReadFile }
```

**Effort:** 2-3 days (source generator enhancement)
**Value:** High (IntelliSense, refactoring support)

---

### Phase 3: Skill Attributes (Alternative Syntax)

**Allow attribute-based skill definition:**
```csharp
[Skill("Debugging", "Debugging capabilities")]
[SkillFunction("FileSystemPlugin.ReadFile")]
[SkillFunction("DebugPlugin.GetStackTrace")]
[SkillInstructions("debugging-protocol.md")]
public static class DebuggingSkill { }

// Auto-registered by source generator
```

**Effort:** 3-4 days (new source generator)
**Value:** Medium (alternative syntax, some find it cleaner)

---

### Phase 4: Conditional Skills

**Skills that appear based on context:**
```csharp
[ConditionalSkill("IsDevelopmentMode")]
public class DebuggingSkill { ... }

// Only available when context.IsDevelopmentMode == true
```

**Effort:** 2 days (reuse existing conditional function infrastructure)
**Value:** Low (edge case, workaround exists)

---

### Phase 5: Skill Composition

**Skills that reference other skills:**
```csharp
new SkillDefinition {
    Name = "FullStackDebugging",
    SkillReferences = new[] { "Debugging", "FileManagement" },  // ← Compose!
    FunctionReferences = new[] { "NetworkPlugin.TraceTCP" }     // ← Add more
}
```

**Effort:** 3-4 days
**Value:** Medium (nice-to-have for complex scenarios)

---

## How to Debug Issues

### Issue: "Skill references unknown function"

**Error:**
```
InvalidOperationException: Skill 'Debugging' references unknown function 'FileSystemPlugin.ReadFile'.
Available functions: FileSystemPlugin.WriteFile, DebugPlugin.GetStackTrace
```

**Diagnosis:**
1. Check the error message - it lists available functions
2. Function is missing from available list

**Common Causes:**
```csharp
// ❌ Cause 1: Forgot to register plugin
.WithSkill(CommonSkills.Debugging)  // References FileSystemPlugin.ReadFile
// .WithPlugin<FileSystemPlugin>()  ← Missing!

// ✅ Fix: Register the plugin
.WithPlugin<FileSystemPlugin>()
.WithSkill(CommonSkills.Debugging)

// ❌ Cause 2: Typo in function reference
FunctionReferences = new[] { "FileSystemPlugin.RedFile" }  // ← Typo!

// ✅ Fix: Correct the typo
FunctionReferences = new[] { "FileSystemPlugin.ReadFile" }

// ❌ Cause 3: Wrong function name format
FunctionReferences = new[] { "ReadFile" }  // ← Ambiguous!

// ✅ Fix: Use qualified name
FunctionReferences = new[] { "FileSystemPlugin.ReadFile" }
```

---

### Issue: "Skills not working / functions not appearing"

**Symptom:** Agent doesn't see skill containers or skill functions.

**Diagnosis:**
Check if plugin scoping is enabled:

```csharp
// ❌ Skills require plugin scoping
var config = new AgentConfig {
    PluginScoping = null  // ← Skills won't work!
};

// ✅ Enable plugin scoping
var config = new AgentConfig {
    PluginScoping = new PluginScopingConfig {
        Enabled = true  // ← Required!
    }
};
```

**Why:** Skills use the same container pattern as plugins. If scoping is disabled, containers are filtered out.

---

### Issue: Skill instructions not showing

**Symptom:** Skill expands but instructions don't appear.

**Diagnosis:**
1. Check if instruction documents exist:
   ```bash
   ls skills/documents/debugging-protocol.md
   ```

2. Check file permissions (can the app read it?)

3. Check `InstructionDocumentBaseDirectory` path:
   ```csharp
   InstructionDocumentBaseDirectory = "skills/documents/"  // ← Relative to app root
   ```

4. Check for document size limit (1MB max):
   ```csharp
   // File too large?
   ls -lh skills/documents/debugging-protocol.md
   ```

**Common Causes:**
```csharp
// ❌ Wrong base directory
InstructionDocumentBaseDirectory = "skills/"  // ← Missing "documents/"
PostExpansionInstructionDocuments = new[] { "debugging-protocol.md" }
// Looks for: skills/debugging-protocol.md (not found!)

// ✅ Correct base directory
InstructionDocumentBaseDirectory = "skills/documents/"
PostExpansionInstructionDocuments = new[] { "debugging-protocol.md" }
// Looks for: skills/documents/debugging-protocol.md (found!)
```

---

### Issue: Reusable skills not working across agents

**Symptom:**
```csharp
// Define once
public static class CommonSkills {
    public static readonly SkillDefinition Debugging = ...;
}

// Agent 1 works
var agent1 = AgentBuilder.Create()
    .WithPlugin<FileSystemPlugin>()
    .WithSkill(CommonSkills.Debugging)
    .Build();  // ✅ Works

// Agent 2 fails
var agent2 = AgentBuilder.Create()
    .WithSkill(CommonSkills.Debugging)  // ❌ No plugins!
    .Build();  // 💥 Error
```

**Diagnosis:**
Skills are reusable **definitions**, but each agent must still register the required plugins!

**Fix:**
```csharp
var agent2 = AgentBuilder.Create()
    .WithPlugin<FileSystemPlugin>()  // ← Must register!
    .WithPlugin<DebugPlugin>()        // ← Must register!
    .WithSkill(CommonSkills.Debugging)
    .Build();  // ✅ Works
```

**Remember:** SkillDefinition is reusable (blueprint), but plugins must be registered per agent.

---

## Phase 1 Enhancement: Plugin References & AutoExpand

**Status:** Implemented (January 2025)
**Motivation:** Skills should support referencing entire plugins, not just individual functions

### The Problem

Original design required listing every function individually:

```csharp
// Original: Tedious and brittle
new SkillDefinition {
    Name = "FileManagement",
    FunctionReferences = new[] {
        "FileSystemPlugin.ReadFile",
        "FileSystemPlugin.WriteFile",
        "FileSystemPlugin.DeleteFile",
        "FileSystemPlugin.ListDirectory",
        "FileSystemPlugin.MoveFile",
        "FileSystemPlugin.CopyFile"
        // ... every function must be listed
    }
}
```

**Problems:**
1. **Verbose**: Must list every function from a plugin
2. **Brittle**: Adding new functions to plugin breaks skill (must update skill too)
3. **Duplication**: Plugin already groups functions semantically
4. **Common case unsupported**: "Include all functions from this plugin" requires listing all

### The Solution: PluginReferences

**New properties added to SkillDefinition:**

```csharp
/// <summary>
/// Plugin references - references all functions from entire plugins.
/// </summary>
public string[] PluginReferences { get; set; } = Array.Empty<string>();

/// <summary>
/// If true, this skill is automatically expanded at the start of each conversation.
/// Replaces the "always visible" use case from plugin scoping.
/// </summary>
public bool AutoExpand { get; set; } = false;
```

**Usage patterns:**

```csharp
// Pattern 1: Reference entire plugin (simple, maintainable)
new SkillDefinition {
    Name = "FileManagement",
    PluginReferences = new[] { "FileSystemPlugin" }  // All functions!
}

// Pattern 2: Mix plugin + specific functions
new SkillDefinition {
    Name = "Debugging",
    PluginReferences = new[] { "DebugPlugin" },      // All DebugPlugin functions
    FunctionReferences = new[] {
        "FileSystemPlugin.ReadFile",                  // Plus specific FileSystem functions
        "FileSystemPlugin.ListDirectory"
    }
}

// Pattern 3: Auto-expanded (always visible)
new SkillDefinition {
    Name = "CoreUtilities",
    PluginReferences = new[] { "CorePlugin" },
    AutoExpand = true  // Functions always available, no expansion needed
}
```

### Implementation Details

**File:** [SkillDefinition.cs](../HPD-Agent/Skills/SkillDefinition.cs)

**Key changes:**

1. **Added PluginReferences property** (line 35)
2. **Added AutoExpand property** (line 47)
3. **Added ResolvedFunctionReferences** (line 77) - stores combined result of PluginReferences + FunctionReferences
4. **Updated Build() method** (line 87-171):
   ```csharp
   // Process PluginReferences - expand to all functions from those plugins
   if (PluginReferences != null && PluginReferences.Length > 0)
   {
       foreach (var pluginRef in PluginReferences)
       {
           // Find all functions with ParentPlugin = pluginRef
           var pluginFunctions = allFunctions
               .Where(kvp => kvp.Value.AdditionalProperties
                   ?.TryGetValue("ParentPlugin", out var parent) == true
                   && parent is string p && p.Equals(pluginRef, StringComparison.OrdinalIgnoreCase))
               .Select(kvp => kvp.Key);

           // Add to resolved set (deduplication happens automatically via HashSet)
           foreach (var funcName in pluginFunctions)
           {
               resolvedFunctions.Add(funcName);
           }
       }
   }
   ```

**File:** [SkillScopingManager.cs](../HPD-Agent/Skills/SkillScopingManager.cs)

**Key changes:**

1. **Updated GetFunctionsForExpandedSkills** (line 92) - uses ResolvedFunctionReferences instead of FunctionReferences
2. **Updated GetUnexpandedSkillContainers** (line 151) - excludes auto-expanded skills from container list
3. **Added GetSkills()** (line 161) - returns all registered skills for auto-expand logic

**File:** [Agent.cs](../HPD-Agent/Agent/Agent.cs)

**Key change:**

Added auto-expand logic after expandedSkills initialization (line 593-603):

```csharp
// Auto-expand skills marked with AutoExpand = true (replaces "always visible" use case)
if (_skillScopingManager != null)
{
    foreach (var skill in _skillScopingManager.GetSkills())
    {
        if (skill.AutoExpand)
        {
            expandedSkills.Add(skill.Name);
        }
    }
}
```

This runs BEFORE the first agent turn, ensuring auto-expanded skills are always visible.

### Benefits

1. **Simpler common case:**
   ```csharp
   // Before: List all 10 functions
   FunctionReferences = new[] { "Plugin.F1", "Plugin.F2", ..., "Plugin.F10" }

   // After: One line
   PluginReferences = new[] { "Plugin" }
   ```

2. **Maintainable:**
   - Add new function to plugin → automatically included in skills
   - No need to update skill definitions

3. **Replaces "always visible" use case:**
   ```csharp
   // Before: Plugin scoping with [PluginScope] attribute had "always visible" feature
   // After: Use AutoExpand = true for same behavior
   AutoExpand = true  // Functions always visible, no expansion needed
   ```

4. **More flexible:**
   - Can mix PluginReferences + FunctionReferences
   - Deduplication automatic (HashSet in Build())
   - Choose granularity per skill

### Why Plugin Scoping and Skills Both Exist (Complementary, Not Redundant)

**Design Decision: Keep Both**

Plugin scoping and skills serve **different use cases** and are **complementary**:

#### Plugin Scoping: Convenience for 1:1 Mappings

**Best for:** When your plugin IS a cohesive semantic unit

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
- ✅ **Perfect for cohesive plugins** - when all functions belong together

#### Skills: Flexibility for M:N Relationships

**Best for:** Cross-plugin semantic groupings

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

#### When to Use Which?

| Scenario | Recommended Approach |
|----------|---------------------|
| All functions in plugin are semantically related | **Plugin Scoping** (simpler) |
| Need to group functions from multiple plugins | **Skills** (more flexible) |
| Want maximum convenience with minimal code | **Plugin Scoping** |
| Want reusable semantic definitions across agents | **Skills** |
| Plugin is tightly focused (e.g., FileSystemPlugin) | **Plugin Scoping** |
| Have cross-cutting concerns (e.g., "Debugging") | **Skills** |

#### They Work Together!

```csharp
// FileSystemPlugin uses plugin scoping (convenient)
[PluginScope("File system operations")]
public class FileSystemPlugin { ... }

// Skills can reference those functions too
var debuggingSkill = new SkillDefinition {
    Name = "Debugging",
    PluginReferences = new[] { "DebugPlugin" },
    FunctionReferences = new[] { "FileSystemPlugin.ReadFile" }  // Borrow one function
};

// Both coexist:
// - FileSystemPlugin container appears when plugin scoping triggers
// - Debugging skill container appears when skill is invoked
```

**No plans to deprecate plugin scoping** - it's valuable for the simple case!

---

## Complete Visibility Decision Matrix

**This matrix shows EXACTLY what the agent sees in all possible scenarios.**

Understanding what functions are visible to the agent in each scenario is critical for system design. The visibility is determined by:
1. Whether the plugin has `[PluginScope]` attribute (adds `ParentPlugin` metadata)
2. Whether the plugin/skill has been expanded
3. Whether skills reference the functions

### Legend

- **Container**: Plugin/skill container function (agent invokes to expand)
- **✅ Function Visible**: Function available for agent to call
- **❌ Function Hidden**: Function registered but not visible to agent

---

### Scenario 1: Plugin WITHOUT [PluginScope], NOT referenced by any skill

```csharp
// Plugin definition (NO [PluginScope])
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()  // No [PluginScope], no skill references it
```

**What Agent Sees (ALWAYS):**
```
✅ ReadFile (always visible)
✅ WriteFile (always visible)
```

**Explanation:**
- Source generator does NOT add `ParentPlugin` metadata (no [PluginScope])
- PluginScopingManager treats them as "non-plugin functions" (PluginScopingManager.cs:54-57)
- **Always visible, no expansion needed**
- **This is how plugins worked before plugin scoping existed**

**Code Path:**
```csharp
// PluginScopingManager.GetToolsForAgentTurn()
var parentPlugin = GetParentPlugin(tool);  // Returns null (no ParentPlugin metadata)
if (parentPlugin != null) {
    // ... scoped plugin function
} else {
    // Non-plugin function - always visible (THIS PATH!)
    nonPluginFunctions.Add(tool);
}
```

---

### Scenario 2: Plugin WITH [PluginScope], NOT expanded yet

```csharp
// Plugin definition (HAS [PluginScope])
[PluginScope("File operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()
```

**What Agent Sees (Turn 1 - Before Expansion):**
```
✅ FileSystemPlugin (container)
❌ ReadFile (hidden - parent not expanded)
❌ WriteFile (hidden - parent not expanded)
```

**After agent invokes FileSystemPlugin container (Turn 2):**
```
❌ FileSystemPlugin (container hidden - already expanded)
✅ ReadFile (visible - parent expanded)
✅ WriteFile (visible - parent expanded)
```

**Explanation:**
- Source generator ADDS `ParentPlugin = "FileSystemPlugin"` metadata (HPDPluginSourceGenerator.cs:464-471)
- Source generator CREATES container function (HPDPluginSourceGenerator.cs:1383-1430)
- Functions only visible after container expanded

**Code Path:**
```csharp
// PluginScopingManager.GetToolsForAgentTurn()
var parentPlugin = GetParentPlugin(tool);  // Returns "FileSystemPlugin"
if (parentPlugin != null) {
    // Plugin function - only show if parent is expanded
    if (expandedPlugins.Contains(parentPlugin)) {  // FALSE initially
        expandedFunctions.Add(tool);  // NOT added until expanded
    }
}
```

---

### Scenario 3: Plugin WITH [PluginScope], referenced by skill

```csharp
// Plugin definition (HAS [PluginScope])
[PluginScope("File operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()
.WithSkill(new SkillDefinition {
    Name = "Debugging",
    PluginReferences = new[] { "FileSystemPlugin" }  // References the plugin
})
```

**What Agent Sees (Turn 1 - Nothing Expanded):**
```
✅ FileSystemPlugin (container from [PluginScope])
✅ Debugging (skill container)
❌ ReadFile (hidden - neither expanded)
❌ WriteFile (hidden - neither expanded)
```

**After agent invokes "Debugging" skill (Turn 2):**
```
✅ FileSystemPlugin (container still visible - plugin not expanded)
❌ Debugging (container hidden - skill expanded)
✅ ReadFile (visible via Debugging skill)
✅ WriteFile (visible via Debugging skill)
```

**After agent ALSO invokes "FileSystemPlugin" container (Turn 3):**
```
❌ FileSystemPlugin (container hidden - plugin expanded)
❌ Debugging (container hidden - skill expanded)
✅ ReadFile (visible - NO duplication!)
✅ WriteFile (visible - NO duplication!)
```

**Explanation:**
- Functions visible through BOTH plugin scoping AND skills
- Deduplication prevents duplicate function entries (Agent.cs:670-682)
- Both expansion paths lead to same functions

---

### Scenario 4: Skill with PluginReferences (plugin has NO [PluginScope])

**✅ THIS NOW WORKS! (Source Generator Enhancement)**

```csharp
// Plugin definition (NO [PluginScope])
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()
.WithSkill(new SkillDefinition {
    Name = "FileManagement",
    PluginReferences = new[] { "FileSystemPlugin" }  // NOW WORKS!
})
```

**What Agent Sees (Turn 1 - Before Expansion):**
```
✅ FileManagement (skill container)
✅ ReadFile (ALREADY visible - non-scoped plugin)
✅ WriteFile (ALREADY visible - non-scoped plugin)
```

**After agent invokes "FileManagement" skill (Turn 2):**
```
❌ FileManagement (container hidden - skill expanded)
✅ ReadFile (visible - no change, was already visible)
✅ WriteFile (visible - no change, was already visible)
```

**Explanation - Why This Now Works:**

**Source Generator Change (HPDPluginSourceGenerator.cs:463-470):**
- **Now:** ALWAYS adds `ParentPlugin` metadata to ALL functions
- **Before:** Only added `ParentPlugin` when `[PluginScope]` present

```csharp
// Source generator NOW generates this for ALL plugins:
AdditionalProperties = new Dictionary<string, object>
{
    ["ParentPlugin"] = "FileSystemPlugin",  // ← Always added now!
    ["IsContainer"] = false
}
```

**PluginScopingManager Change (PluginScopingManager.cs:31-73):**
- **Now:** Two-pass algorithm checks if plugin HAS a container
- **Logic:** If plugin has `ParentPlugin` metadata BUT no container → always visible

```csharp
// First pass: Build set of plugins with containers (= scoped plugins)
var pluginsWithContainers = new HashSet<string>();
foreach (var tool in allTools)
{
    if (IsContainer(tool))  // Only plugins with [PluginScope] have containers
    {
        pluginsWithContainers.Add(GetPluginName(tool));
    }
}

// Second pass: Functions scoped ONLY if their plugin has a container
var parentPlugin = GetParentPlugin(tool);
if (parentPlugin != null && pluginsWithContainers.Contains(parentPlugin))
{
    // SCOPED: Plugin has container, functions hidden until expanded
    expandedFunctions.Add(tool);
}
else
{
    // ALWAYS VISIBLE: No container OR no ParentPlugin
    nonPluginFunctions.Add(tool);
}
```

**Result:**
- **Plugins WITH `[PluginScope]`**: Functions scoped (container exists)
- **Plugins WITHOUT `[PluginScope]`**: Functions always visible (no container)
- **Skills can use `PluginReferences` on ANY plugin**: Metadata now always present!

**When This Is Useful:**
- You want to provide `PostExpansionInstructions` for non-scoped functions
- You want semantic grouping even though functions are already visible
- Skill container serves organizational purpose (shows intent, provides instructions)

---

### Scenario 5: Skill with FunctionReferences (plugin has NO [PluginScope])

**✅ THIS WORKS**

```csharp
// Plugin definition (NO [PluginScope])
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()
.WithSkill(new SkillDefinition {
    Name = "FileManagement",
    FunctionReferences = new[] {  // Using FunctionReferences, not PluginReferences
        "FileSystemPlugin.ReadFile",
        "FileSystemPlugin.WriteFile"
    }
})
```

**What Agent Sees (Turn 1):**
```
✅ FileManagement (skill container)
✅ ReadFile (ALREADY visible - non-plugin function)
✅ WriteFile (ALREADY visible - non-plugin function)
```

**After agent invokes "FileManagement" skill (Turn 2):**
```
❌ FileManagement (container hidden - skill expanded)
✅ ReadFile (visible - no change, was already visible)
✅ WriteFile (visible - no change, was already visible)
```

**Explanation:**
- `FunctionReferences` works by direct function name lookup (doesn't need `ParentPlugin` metadata)
- Functions are already visible (no [PluginScope])
- **Skill expansion is cosmetic only** - functions don't change visibility
- **The skill container serves no functional purpose** (just organizational)

**When This Makes Sense:**
- You want to provide PostExpansionInstructions for non-scoped functions
- You want semantic grouping even though scoping isn't possible

---

### Scenario 6: Skill with AutoExpand = true

```csharp
// Plugin definition (HAS [PluginScope])
[PluginScope("File operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()
.WithSkill(new SkillDefinition {
    Name = "CoreUtilities",
    PluginReferences = new[] { "FileSystemPlugin" },
    AutoExpand = true  // Always expanded
})
```

**What Agent Sees (Turn 1):**
```
✅ FileSystemPlugin (container from [PluginScope])
❌ CoreUtilities (skill container HIDDEN - AutoExpand = true)
✅ ReadFile (visible - CoreUtilities auto-expanded)
✅ WriteFile (visible - CoreUtilities auto-expanded)
```

**Explanation:**
- AutoExpand skills don't show containers (SkillScopingManager.cs:151)
- Functions immediately visible (expandedSkills initialized with AutoExpand skills, Agent.cs:593-603)
- Plugin container STILL visible (plugin scoping independent)
- **Functions visible through TWO paths:** AutoExpand skill + potential plugin expansion

**Code Path:**
```csharp
// Agent.cs - Before agentic loop starts
var expandedSkills = new HashSet<string>();

// Auto-expand skills marked with AutoExpand = true
if (_skillScopingManager != null)
{
    foreach (var skill in _skillScopingManager.GetSkills())
    {
        if (skill.AutoExpand)
        {
            expandedSkills.Add(skill.Name);  // "CoreUtilities" added here!
        }
    }
}
```

---

### Scenario 7: Mix of everything (Complex Real-World Example)

```csharp
// Plugins
[PluginScope("File operations")]
public class FileSystemPlugin  // Has scoping
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

public class UtilityPlugin  // No scoping
{
    [AIFunction] public string FormatString(...) { }
}

// Agent configuration
.WithPlugin<FileSystemPlugin>()
.WithPlugin<UtilityPlugin>()
.WithSkill(new SkillDefinition {
    Name = "Debugging",
    FunctionReferences = new[] {
        "FileSystemPlugin.ReadFile",  // From scoped plugin
        "UtilityPlugin.FormatString"   // From non-scoped plugin
    }
})
```

**What Agent Sees (Turn 1 - Nothing Expanded):**
```
✅ FileSystemPlugin (container - has [PluginScope])
✅ Debugging (skill container)
✅ FormatString (ALREADY visible - from UtilityPlugin, no ParentPlugin)
❌ ReadFile (hidden - FileSystemPlugin not expanded)
❌ WriteFile (hidden - FileSystemPlugin not expanded)
```

**After agent invokes "Debugging" skill (Turn 2):**
```
✅ FileSystemPlugin (container still visible - plugin not expanded yet)
❌ Debugging (container hidden - skill expanded)
✅ FormatString (visible - still always visible)
✅ ReadFile (NOW visible via Debugging skill)
❌ WriteFile (still hidden - Debugging skill doesn't reference it)
```

**After agent invokes "FileSystemPlugin" container (Turn 3):**
```
❌ FileSystemPlugin (container hidden - plugin expanded)
❌ Debugging (container hidden - skill expanded)
✅ FormatString (visible - always visible)
✅ ReadFile (visible - NO duplication from two sources!)
✅ WriteFile (NOW visible - plugin expanded)
```

**Explanation:**
- Three types of functions coexist:
  1. Scoped plugin functions (FileSystemPlugin) - need expansion
  2. Non-scoped plugin functions (UtilityPlugin) - always visible
  3. Skill-referenced functions - alternate expansion path
- Deduplication prevents ReadFile from appearing twice

---

## Key Takeaways from Decision Matrix

### 1. **Functions without [PluginScope] = Always Visible**
- **Now have `ParentPlugin` metadata** (source generator change)
- But still treated as "non-plugin functions" (no container exists)
- ✅ **Can be scoped via `PluginReferences`** (now works!)
- Can be referenced via `FunctionReferences` (but no scoping benefit since already visible)

### 2. **Functions with [PluginScope] = Scoped**
- Have `ParentPlugin` metadata AND container exists
- Only visible after container expansion
- Can ALSO be referenced by skills (alternate expansion path)

### 3. **PluginReferences NOW works with ANY plugin** ✨
- **Changed:** Source generator ALWAYS adds `ParentPlugin` metadata
- **Logic:** PluginScopingManager checks if plugin has container (two-pass algorithm)
- **Result:** Skills can reference ANY plugin, scoped or not!
- **Behavior:** Non-scoped plugins remain always visible (container doesn't hide them)

### 4. **FunctionReferences works with ANY function**
- Doesn't require `ParentPlugin` metadata
- Works with scoped AND non-scoped plugins
- Works with MCP tools, custom functions, anything

### 5. **AutoExpand skills = "Always visible" replacement**
- Functions visible from turn 1
- No container shown
- Best for core utilities
- Replaces the "always visible" use case from non-scoped plugins

### 6. **Deduplication is automatic**
- Same function referenced by multiple skills/plugins
- Appears only once in tools list
- Implemented in Agent.cs:670-682

### 7. **Visibility Order (Agent.cs:657-683)**
```
1. Containers (collapsed plugins + non-auto-expanded skills)
2. Non-Plugin Functions (always visible)
3. Expanded Plugin Functions (from expanded plugins)
4. Expanded Skill Functions (from expanded skills, deduplicated)
```

---

## Phase 1.5 Enhancement: Configurable Skill Scoping

**Status:** Implemented (January 2025)
**Motivation:** Skills should have configurable scoping behavior to solve the discoverability problem

### The Discoverability Problem

After implementing Phase 1, we discovered a critical UX issue: **Skills are goal-oriented, not capability-oriented**.

```ascii
Problem Scenario:

User asks: "Read the log file and tell me what happened"

Agent sees:
  ✅ AdvancedDatabaseDebugging (skill)  ← Specialized description
  ❌ ReadFile (hidden in skill)          ← Function agent needs

Agent reasoning:
  "User wants to read a log file. I need ReadFile function.
   I see 'AdvancedDatabaseDebugging' but that's for DATABASE debugging,
   not just reading files. I'll tell the user I don't have ReadFile."

❌ AGENT FAILS because skill description is too specialized!
```

**The fundamental tension:**
- **Plugins** (capability-oriented): "Here are file operations" → Agent invokes when it needs file capabilities
- **Skills** (goal-oriented): "Here's how to debug databases" → Agent won't invoke unless goal matches exactly

**Previous behavior (Phase 1):**
Skills worked like plugin scoping - functions hidden until skill expanded. This broke discoverability for general tasks.

### The Solution: Two Scoping Modes

We added `ScopingMode` property to allow developers to choose the right behavior:

```csharp
public enum SkillScopingMode
{
    /// <summary>
    /// Functions remain visible, skill provides instructions only (DEFAULT).
    /// Use for general-purpose skills where discoverability is important.
    /// </summary>
    InstructionOnly,

    /// <summary>
    /// Functions hidden until skill expanded (token efficient).
    /// Use for highly specialized workflows.
    /// </summary>
    Scoped
}
```

### Mode 1: InstructionOnly (Default)

**Behavior:** Functions stay visible, skill provides instructions when expanded

```csharp
new SkillDefinition {
    Name = "DebuggingBasics",
    ScopingMode = SkillScopingMode.InstructionOnly,  // Default
    FunctionReferences = new[] { "ReadFile", "WriteFile" },
    PostExpansionInstructions = "Use ReadFile for logs, WriteFile for notes"
}
```

**What Agent Sees (Turn 1):**
```
✅ DebuggingBasics (skill container)
✅ ReadFile (VISIBLE - not hidden)
✅ WriteFile (VISIBLE - not hidden)
```

**What Agent Sees (Turn 2 - after expanding skill):**
```
❌ DebuggingBasics (container hidden)
✅ ReadFile (still visible - no change)
✅ WriteFile (still visible - no change)
+ Receives PostExpansionInstructions
```

**Benefits:**
- ✅ **Max discoverability** - Agent can find functions anytime
- ✅ **Skill provides guidance** - Instructions available when needed
- ✅ **Works for general tasks** - Agent uses functions even without skill
- ✅ **No false negatives** - Agent doesn't miss available functions

**Use Cases:**
- General-purpose skills
- Skills with broad applicability
- When functions should be accessible without context
- Teaching "how to use" rather than "when to use"

---

### Mode 2: Scoped

**Behavior:** Functions hidden until skill expanded (like plugin scoping)

```csharp
new SkillDefinition {
    Name = "AdvancedDatabaseMigration",
    ScopingMode = SkillScopingMode.Scoped,  // Hide functions
    FunctionReferences = new[] { "ExecuteSQL", "BackupDB", "RollbackDB" },
    PostExpansionInstructions = "CRITICAL: Backup → Validate → Execute → Rollback if error"
}
```

**What Agent Sees (Turn 1):**
```
✅ AdvancedDatabaseMigration (skill container)
❌ ExecuteSQL (HIDDEN)
❌ BackupDB (HIDDEN)
❌ RollbackDB (HIDDEN)
```

**What Agent Sees (Turn 2 - after expanding skill):**
```
❌ AdvancedDatabaseMigration (container hidden)
✅ ExecuteSQL (NOW VISIBLE)
✅ BackupDB (NOW VISIBLE)
✅ RollbackDB (NOW VISIBLE)
+ Receives PostExpansionInstructions
```

**Benefits:**
- ✅ **Token efficient** - Functions hidden when not needed
- ✅ **Controlled access** - Agent must explicitly choose skill
- ✅ **Safety** - Dangerous functions hidden by default
- ✅ **Specialized workflows** - Functions grouped with specific instructions

**Use Cases:**
- Highly specialized workflows
- Dangerous operations requiring strict protocols
- Token efficiency is important
- Want to enforce skill-based access pattern

---

### Critical Feature: Selective Function Scoping

**Most powerful nuance:** `Scoped` mode works on **individual functions** from non-scoped plugins!

```csharp
// Plugin with NO [PluginScope] - all functions normally always visible
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }   // Safe
    [AIFunction] public string WriteFile(...) { }  // Safe
    [AIFunction] public string DeleteFile(...) { } // DANGEROUS
}

// Skill hides ONLY the dangerous function
new SkillDefinition {
    Name = "DangerousOps",
    ScopingMode = SkillScopingMode.Scoped,
    FunctionReferences = new[] { "DeleteFile" }  // Only this one!
}
```

**What Agent Sees (Turn 1):**
```
✅ DangerousOps (skill container)
✅ ReadFile (visible - not referenced by skill)
✅ WriteFile (visible - not referenced by skill)
❌ DeleteFile (HIDDEN by skill's Scoped mode) ✨
```

**What Agent Sees (Turn 2 - after expanding DangerousOps):**
```
❌ DangerousOps (container hidden)
✅ ReadFile (visible - unchanged)
✅ WriteFile (visible - unchanged)
✅ DeleteFile (NOW VISIBLE) ✨
```

**Why This Is Powerful:**
- ✅ **Cherry-pick dangerous functions** - Scope only what needs scoping
- ✅ **Mix safe and dangerous** - Safe functions always available
- ✅ **No plugin-level attribute needed** - Works with any plugin
- ✅ **Granular control** - Function-level scoping

**Real-World Example:**
```csharp
// Plugin with mix of safe/dangerous functions (NO [PluginScope])
public class SystemPlugin
{
    [AIFunction] public string GetProcessList(...) { }      // Safe
    [AIFunction] public string ReadEnvironmentVar(...) { }  // Safe
    [AIFunction] public void KillProcess(...) { }           // DANGEROUS
    [AIFunction] public void DeleteRegistry(...) { }        // DANGEROUS
    [AIFunction] public void ModifyFirewall(...) { }        // DANGEROUS
}

// Scope only dangerous operations
new SkillDefinition {
    Name = "SystemAdministration",
    ScopingMode = SkillScopingMode.Scoped,
    FunctionReferences = new[] {
        "KillProcess",      // Hide these
        "DeleteRegistry",   // Hide these
        "ModifyFirewall"    // Hide these
    },
    PostExpansionInstructions = "CRITICAL: Verify all parameters before system modifications!"
}

// Result:
// ✅ GetProcessList - always visible (safe)
// ✅ ReadEnvironmentVar - always visible (safe)
// ❌ KillProcess - hidden until SystemAdministration expanded (dangerous)
// ❌ DeleteRegistry - hidden until SystemAdministration expanded (dangerous)
// ❌ ModifyFirewall - hidden until SystemAdministration expanded (dangerous)
```

---

### Additional Feature: SuppressPluginContainers

**Problem:** When skill references a `[PluginScope]` plugin, both containers appear:

```csharp
[PluginScope("File operations")]
public class FileSystemPlugin { ... }

new SkillDefinition {
    Name = "Debugging",
    ScopingMode = SkillScopingMode.InstructionOnly,
    PluginReferences = new[] { "FileSystemPlugin" }
}

// Agent sees BOTH containers (confusing):
//   ✅ FileSystemPlugin (plugin container)
//   ✅ Debugging (skill container)
//   ❌ Functions (hidden by plugin scoping)
```

**Solution:** `SuppressPluginContainers` property

```csharp
new SkillDefinition {
    Name = "Debugging",
    ScopingMode = SkillScopingMode.InstructionOnly,
    PluginReferences = new[] { "FileSystemPlugin" },
    SuppressPluginContainers = true  // Hide plugin container!
}
```

**What Agent Sees:**
```
✅ Debugging (skill container - only one!)
❌ FileSystemPlugin (SUPPRESSED)
✅ ReadFile (visible - InstructionOnly mode)
✅ WriteFile (visible - InstructionOnly mode)
```

**Benefits:**
- ✅ **Single expansion path** - Only skill container shown
- ✅ **Cleaner UX** - No duplicate/competing containers
- ✅ **Skill "takes ownership"** - Clear semantic grouping
- ✅ **Controls visibility** - Skill's ScopingMode determines function visibility

**Use Cases:**
- Skill provides better semantic grouping than plugin
- Want single expansion path for functions
- Plugin container name less clear than skill name
- Skill adds important context that plugin doesn't provide

---

### Complete Scoping Behavior Matrix

This shows **EVERY possible combination** and what the agent sees:

#### Scenario A: Non-Scoped Plugin + Scoped Skill (Selective Function Hiding)

```csharp
public class FileSystemPlugin  // NO [PluginScope]
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
    [AIFunction] public string DeleteFile(...) { }
}

new SkillDefinition {
    Name = "SafeFileOps",
    ScopingMode = SkillScopingMode.Scoped,
    FunctionReferences = new[] { "DeleteFile" }  // Only hide this one
}
```

**Turn 1 (Before expansion):**
```
✅ SafeFileOps (skill)
✅ ReadFile (visible - not referenced by skill)
✅ WriteFile (visible - not referenced by skill)
❌ DeleteFile (HIDDEN by skill's Scoped mode)
```

**Turn 2 (After expanding SafeFileOps):**
```
❌ SafeFileOps (hidden)
✅ ReadFile (visible - unchanged)
✅ WriteFile (visible - unchanged)
✅ DeleteFile (NOW VISIBLE via skill)
```

**Implementation:**
- `SkillScopingManager.GetHiddenFunctionsBySkills()` returns `{"DeleteFile"}`
- `Agent.cs` filters out `DeleteFile` from tools list (line 677-685)
- Other functions unaffected (not in hidden set)

---

#### Scenario B: Non-Scoped Plugin + InstructionOnly Skill (No Hiding)

```csharp
public class FileSystemPlugin  // NO [PluginScope]
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

new SkillDefinition {
    Name = "FileManagement",
    ScopingMode = SkillScopingMode.InstructionOnly,  // Default
    FunctionReferences = new[] { "ReadFile", "WriteFile" }
}
```

**Turn 1 (Before expansion):**
```
✅ FileManagement (skill)
✅ ReadFile (visible - InstructionOnly mode)
✅ WriteFile (visible - InstructionOnly mode)
```

**Turn 2 (After expanding FileManagement):**
```
❌ FileManagement (hidden)
✅ ReadFile (visible - no change)
✅ WriteFile (visible - no change)
+ PostExpansionInstructions delivered
```

**Why:** `InstructionOnly` mode doesn't hide functions - skill is instruction delivery only

---

#### Scenario C: Scoped Plugin + InstructionOnly Skill + SuppressPluginContainers

```csharp
[PluginScope("File operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

new SkillDefinition {
    Name = "FileOps",
    ScopingMode = SkillScopingMode.InstructionOnly,
    PluginReferences = new[] { "FileSystemPlugin" },
    SuppressPluginContainers = true  // Key!
}
```

**Turn 1:**
```
✅ FileOps (skill container)
❌ FileSystemPlugin (SUPPRESSED by skill)
✅ ReadFile (visible - InstructionOnly mode overrides plugin scoping)
✅ WriteFile (visible - InstructionOnly mode overrides plugin scoping)
```

**Why This Works:**
1. `SkillScopingManager.GetSuppressedPluginContainers()` returns `{"FileSystemPlugin"}`
2. `Agent.cs` filters out FileSystemPlugin container (line 669-675)
3. Skill's `InstructionOnly` mode makes functions visible despite plugin having `[PluginScope]`

**This is the MOST powerful feature** - allows skills to completely replace plugin containers!

---

#### Scenario D: Scoped Plugin + Scoped Skill (Both Hide)

```csharp
[PluginScope("File operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

new SkillDefinition {
    Name = "FileOps",
    ScopingMode = SkillScopingMode.Scoped,
    PluginReferences = new[] { "FileSystemPlugin" }
}
```

**Turn 1:**
```
✅ FileSystemPlugin (plugin container)
✅ FileOps (skill container)
❌ ReadFile (hidden by plugin scoping)
❌ WriteFile (hidden by plugin scoping)
```

**Turn 2 (Expand FileOps skill):**
```
✅ FileSystemPlugin (plugin container still there)
❌ FileOps (hidden - expanded)
✅ ReadFile (visible via skill expansion)
✅ WriteFile (visible via skill expansion)
```

**Turn 3 (Also expand FileSystemPlugin):**
```
❌ FileSystemPlugin (hidden - expanded)
❌ FileOps (hidden - expanded)
✅ ReadFile (visible - no duplication)
✅ WriteFile (visible - no duplication)
```

**Why:** Both plugin scoping and skill scoping align - functions hidden until either is expanded

---

#### Scenario E: Scoped Plugin + Scoped Skill + SuppressPluginContainers (Single Path)

```csharp
[PluginScope("File operations")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
}

new SkillDefinition {
    Name = "FileOps",
    ScopingMode = SkillScopingMode.Scoped,
    PluginReferences = new[] { "FileSystemPlugin" },
    SuppressPluginContainers = true
}
```

**Turn 1:**
```
✅ FileOps (skill container - only one!)
❌ FileSystemPlugin (SUPPRESSED)
❌ ReadFile (hidden by skill's Scoped mode)
❌ WriteFile (hidden by skill's Scoped mode)
```

**Turn 2 (Expand FileOps):**
```
❌ FileOps (hidden - expanded)
❌ FileSystemPlugin (suppressed - never visible)
✅ ReadFile (visible via skill)
✅ WriteFile (visible via skill)
```

**Perfect for:** Dangerous operations requiring strict workflow - only ONE expansion path exists

---

### Implementation Details

**Files Modified:**

1. **[SkillScopingMode.cs](../HPD-Agent/Skills/SkillScopingMode.cs)** - New enum (InstructionOnly, Scoped)

2. **[SkillDefinition.cs](../HPD-Agent/Skills/SkillDefinition.cs):**
   - Added `ScopingMode` property (line 47)
   - Added `SuppressPluginContainers` property (line 55)

3. **[SkillScopingManager.cs](../HPD-Agent/Skills/SkillScopingManager.cs):**
   - Added `GetHiddenFunctionsBySkills()` method (line 76-97)
   - Added `GetSuppressedPluginContainers()` method (line 104-120)
   - Added `ExtractFunctionName()` helper (line 199-203)

4. **[Agent.cs](../HPD-Agent/Agent/Agent.cs):**
   - Added suppression logic (line 663-675)
   - Added hiding logic (line 677-685)

5. **[PluginScopingManager.cs](../HPD-Agent/Plugins/PluginScopingManager.cs):**
   - Made `GetPluginName()` public (line 102)

**Key Runtime Logic (Agent.cs:663-707):**

```csharp
// Get plugin containers to suppress
var suppressedPlugins = _skillScopingManager.GetSuppressedPluginContainers();

// Get functions hidden by Scoped skills
var hiddenBySkills = _skillScopingManager.GetHiddenFunctionsBySkills(expandedSkills);

// Filter out suppressed plugin containers
if (suppressedPlugins.Count > 0)
{
    scopedFunctions.RemoveAll(f =>
        _pluginScopingManager.IsContainer(f) &&
        suppressedPlugins.Contains(_pluginScopingManager.GetPluginName(f)));
}

// Filter out functions hidden by Scoped skills
if (hiddenBySkills.Count > 0)
{
    scopedFunctions.RemoveAll(f =>
        !_pluginScopingManager.IsContainer(f) &&
        !_skillScopingManager.IsSkillContainer(f) &&
        f.Name != null &&
        hiddenBySkills.Contains(f.Name));
}
```

**Order of Operations:**
1. Plugin scoping runs first (determines base visibility)
2. Skill suppression runs second (removes plugin containers)
3. Skill hiding runs third (removes functions from Scoped skills)
4. Skill expansion runs fourth (adds functions from expanded skills)
5. Deduplication runs last (ensures functions appear once)

---

### Design Philosophy

**Skills vs Plugins - Clarified Roles:**

| Aspect | Plugin Scoping | Skill Scoping |
|--------|---------------|---------------|
| **Ownership** | 1:N (plugin owns functions) | M:N (functions belong to multiple skills) |
| **Purpose** | Convenience + token efficiency | Semantic grouping + instructions |
| **Granularity** | All-or-nothing (entire plugin) | Selective (cherry-pick functions) |
| **Attribute** | `[PluginScope]` on class | Property in `SkillDefinition` |
| **Mental Model** | **Toolbox** - "Here are tools you can use" | **Recipe** - "Here's how to accomplish X" |
| **Discoverability** | High (capability-oriented) | Variable (depends on ScopingMode) |
| **Use Case** | Cohesive capability groups | Cross-cutting workflows |

**When to Use Each:**

**Use `InstructionOnly` Skills When:**
- ✅ Functions should be generally discoverable
- ✅ Skill provides "how to use" guidance
- ✅ Multiple use cases for same functions
- ✅ Want maximum discoverability

**Use `Scoped` Skills When:**
- ✅ Highly specialized workflow
- ✅ Dangerous operations requiring protocols
- ✅ Token efficiency important
- ✅ Want controlled access pattern

**Use `SuppressPluginContainers` When:**
- ✅ Skill provides better semantic grouping
- ✅ Want single expansion path
- ✅ Plugin container name confusing
- ✅ Skill context more valuable than plugin context

---

### Examples

See [Example_SkillScopingModes.cs](../HPD-Agent/Skills/Example_SkillScopingModes.cs) for comprehensive examples of all scoping modes.

---

### Backward Compatibility

**100% backward compatible:**
- Default `ScopingMode = InstructionOnly` (non-breaking)
- Default `SuppressPluginContainers = false` (non-breaking)
- Existing skills work unchanged
- `AutoExpand` property preserved (orthogonal to ScopingMode)

---

### Key Takeaways - Configurable Skill Scoping

1. **`InstructionOnly` (Default)** - Max discoverability, functions always visible
2. **`Scoped`** - Token efficient, functions hidden until expanded
3. **Selective Scoping** - `Scoped` mode works on individual functions from non-scoped plugins (most powerful!)
4. **`SuppressPluginContainers`** - Allows skill to "take ownership" from plugin
5. **Orthogonal Properties** - `ScopingMode`, `SuppressPluginContainers`, and `AutoExpand` are independent
6. **Problem Solved** - Discoverability issue resolved while maintaining token efficiency option

---

## Phase 1.6 Enhancement: Skills-Only Mode

**Status:** Implemented (January 2025)
**Motivation:** Enable "pure skill" interfaces where plugins are registered for validation but only skills are visible

### The Registration Problem

With the Skills system, users face a constraint: **plugins must be registered for validation**, even if they only want skill-based access:

```csharp
// Problem: Must register plugins for validation
.WithPlugin<FileSystemPlugin>()      // Required for Build() validation
.WithPlugin<DatabasePlugin>()        // Required for Build() validation
.WithPlugin<NetworkPlugin>()         // Required for Build() validation
.WithSkill(new SkillDefinition {
    Name = "Debugging",
    FunctionReferences = new[] { "ReadFile", "ExecuteSQL" }
})

// But agent sees ALL these unreferenced functions:
//   ✅ Debugging (skill)
//   ✅ ReadFile (good - referenced by skill)
//   ✅ WriteFile (unwanted - not in skill)
//   ✅ DeleteFile (unwanted - not in skill)
//   ✅ QueryDatabase (unwanted - not in skill)
//   ✅ SendHTTPRequest (unwanted - not in skill)
//   ... dozens of other unreferenced functions
```

**The Issue:**
- Plugins **must** be registered (validation requires it)
- But non-scoped plugins = **all functions visible**
- Result: **Skill-based design polluted with unreferenced functions**

### The Solution: Skills-Only Mode

**Enable global "hide everything not in skills" mode:**

```csharp
.WithPlugin<FileSystemPlugin>()      // Must register (for validation)
.WithPlugin<DatabasePlugin>()        // Must register (for validation)
.WithPlugin<NetworkPlugin>()         // Must register (for validation)
.WithSkill(new SkillDefinition {
    Name = "Debugging",
    FunctionReferences = new[] { "ReadFile", "ExecuteSQL" }
})
.EnableSkillsOnlyMode()  // ← ONE LINE!
```

**What Agent Sees:**
```
Turn 1:
  ✅ Debugging (skill container - ONLY interface)
  ❌ ReadFile (hidden until skill expanded)
  ❌ WriteFile (hidden - not referenced by any skill)
  ❌ DeleteFile (hidden - not referenced by any skill)
  ❌ ExecuteSQL (hidden until skill expanded)
  ❌ QueryDatabase (hidden - not referenced by any skill)
  ❌ SendHTTPRequest (hidden - not referenced by any skill)
  ❌ ALL plugin containers (hidden)
  ❌ ALL unreferenced functions (hidden)

Turn 2 (Expand Debugging):
  ❌ Debugging (hidden - expanded)
  ✅ ReadFile (NOW VISIBLE)
  ❌ WriteFile (STILL HIDDEN - not in any skill)
  ✅ ExecuteSQL (NOW VISIBLE)
  ❌ QueryDatabase (STILL HIDDEN - not in any skill)
```

### Usage

**Option 1: Builder Method (Recommended)**

```csharp
var agent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DatabasePlugin>()
    .WithSkill(new SkillDefinition {
        Name = "Debugging",
        FunctionReferences = new[] { "ReadFile", "ExecuteSQL" }
    })
    .EnableSkillsOnlyMode()  // ← Easy!
    .Build();
```

**Option 2: Config Property**

```csharp
var config = new AgentConfig {
    PluginScoping = new PluginScopingConfig {
        Enabled = true,
        SkillsOnlyMode = true  // ← Global flag
    }
};
```

### How It Works

**Runtime Logic (Agent.cs:663-676):**

```csharp
if (Config?.PluginScoping?.SkillsOnlyMode == true && _skillScopingManager != null)
{
    // Get ALL functions referenced by ANY skill
    var skillReferencedFunctions = _skillScopingManager.GetAllSkillReferencedFunctions();

    // Remove ALL plugin containers (skills become only interface)
    scopedFunctions.RemoveAll(f => _pluginScopingManager.IsContainer(f));

    // Remove all functions NOT referenced by any skill
    scopedFunctions.RemoveAll(f =>
        !_skillScopingManager.IsSkillContainer(f) &&
        f.Name != null &&
        !skillReferencedFunctions.Contains(f.Name));
}
```

**What Happens:**
1. **Collect**: Get all function names from ALL skills' `ResolvedFunctionReferences`
2. **Hide Containers**: Remove ALL plugin containers (even `[PluginScope]` plugins)
3. **Hide Functions**: Remove all functions NOT in the skill-referenced set
4. **Keep Skills**: Skill containers remain visible (the ONLY interface)

### Complete Example: Pure Skill Interface

```csharp
// Plugins with many functions
public class FileSystemPlugin  // NO [PluginScope]
{
    [AIFunction] public string ReadFile(...) { }
    [AIFunction] public string WriteFile(...) { }
    [AIFunction] public string DeleteFile(...) { }
    [AIFunction] public string ListDirectory(...) { }
    [AIFunction] public string MoveFile(...) { }
    [AIFunction] public string CopyFile(...) { }
}

public class DatabasePlugin  // NO [PluginScope]
{
    [AIFunction] public string ExecuteSQL(...) { }
    [AIFunction] public string QueryDatabase(...) { }
    [AIFunction] public string BackupDatabase(...) { }
    [AIFunction] public string RollbackDatabase(...) { }
}

// Agent with Skills-Only Mode
var agent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()   // Must register (for validation)
    .WithPlugin<DatabasePlugin>()     // Must register (for validation)
    .WithSkill(new SkillDefinition {
        Name = "LogAnalysis",
        FunctionReferences = new[] { "ReadFile", "ListDirectory" },
        PostExpansionInstructions = "Always start by listing directory to find log files"
    })
    .WithSkill(new SkillDefinition {
        Name = "DataInvestigation",
        FunctionReferences = new[] { "ExecuteSQL", "QueryDatabase" },
        PostExpansionInstructions = "Query before executing! Always check data before modifying"
    })
    .EnableSkillsOnlyMode()  // ← Pure skill interface!
    .Build();
```

**Turn 1 (Nothing expanded):**
```
✅ LogAnalysis (skill container)
✅ DataInvestigation (skill container)
❌ ReadFile (hidden - in LogAnalysis skill, not expanded)
❌ WriteFile (hidden - not in any skill)
❌ DeleteFile (hidden - not in any skill)
❌ ListDirectory (hidden - in LogAnalysis skill, not expanded)
❌ MoveFile (hidden - not in any skill)
❌ CopyFile (hidden - not in any skill)
❌ ExecuteSQL (hidden - in DataInvestigation skill, not expanded)
❌ QueryDatabase (hidden - in DataInvestigation skill, not expanded)
❌ BackupDatabase (hidden - not in any skill)
❌ RollbackDatabase (hidden - not in any skill)
```

**Turn 2 (Expand LogAnalysis):**
```
❌ LogAnalysis (hidden - expanded)
✅ DataInvestigation (skill container still visible)
✅ ReadFile (NOW VISIBLE via LogAnalysis)
❌ WriteFile (STILL HIDDEN - not in any skill)
❌ DeleteFile (STILL HIDDEN - not in any skill)
✅ ListDirectory (NOW VISIBLE via LogAnalysis)
❌ MoveFile (STILL HIDDEN - not in any skill)
❌ CopyFile (STILL HIDDEN - not in any skill)
❌ ExecuteSQL (still hidden - DataInvestigation not expanded)
❌ QueryDatabase (still hidden - DataInvestigation not expanded)
❌ BackupDatabase (STILL HIDDEN - not in any skill)
❌ RollbackDatabase (STILL HIDDEN - not in any skill)
+ PostExpansionInstructions delivered
```

### Interaction with Other Scoping Features

**Skills-Only Mode + ScopingMode:**

```csharp
.WithSkill(new SkillDefinition {
    Name = "SafeOps",
    ScopingMode = SkillScopingMode.InstructionOnly,  // Functions always visible
    FunctionReferences = new[] { "ReadFile", "ListDirectory" }
})
.WithSkill(new SkillDefinition {
    Name = "DangerousOps",
    ScopingMode = SkillScopingMode.Scoped,  // Functions hidden until expanded
    FunctionReferences = new[] { "DeleteFile", "ExecuteSQL" }
})
.EnableSkillsOnlyMode()
```

**WITHOUT EnableSkillsOnlyMode:**
```
Turn 1:
  ✅ SafeOps (skill)
  ✅ DangerousOps (skill)
  ✅ ReadFile (visible - InstructionOnly)
  ✅ WriteFile (VISIBLE - not in any skill, but plugin not scoped)
  ❌ DeleteFile (hidden - Scoped mode)
  ❌ ExecuteSQL (hidden - Scoped mode)
```

**WITH EnableSkillsOnlyMode:**
```
Turn 1:
  ✅ SafeOps (skill)
  ✅ DangerousOps (skill)
  ✅ ReadFile (visible - InstructionOnly)
  ❌ WriteFile (HIDDEN - not in any skill, SkillsOnlyMode trumps plugin visibility)
  ❌ DeleteFile (hidden - Scoped mode)
  ❌ ExecuteSQL (hidden - Scoped mode)
```

**Key Insight:** `SkillsOnlyMode` filters out unreferenced functions **before** `ScopingMode` is applied!

### Implementation Details

**Files Modified:**

1. **[AgentConfig.cs](../HPD-Agent/Agent/AgentConfig.cs)** (line 673):
   - Added `SkillsOnlyMode` property to `PluginScopingConfig`

2. **[AgentBuilderSkillExtensions.cs](../HPD-Agent/Skills/AgentBuilderSkillExtensions.cs)** (line 144):
   - Added `EnableSkillsOnlyMode()` builder method

3. **[SkillScopingManager.cs](../HPD-Agent/Skills/SkillScopingManager.cs)** (line 127):
   - Added `GetAllSkillReferencedFunctions()` method

4. **[Agent.cs](../HPD-Agent/Agent/Agent.cs)** (line 663-676):
   - Added Skills-Only Mode filtering logic

**Key Method (SkillScopingManager.cs:127-142):**

```csharp
public HashSet<string> GetAllSkillReferencedFunctions()
{
    var referencedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var skill in _skillsByName.Values)
    {
        foreach (var reference in skill.ResolvedFunctionReferences)
        {
            // Extract function name (strip plugin prefix if present)
            var functionName = ExtractFunctionName(reference);
            referencedFunctions.Add(functionName);
        }
    }

    return referencedFunctions;
}
```

**Order of Operations (Agent.cs runtime):**
1. Plugin scoping runs first (base visibility)
2. **Skills-Only Mode check** (if enabled, hide everything not in skills)
3. Normal skill scoping (ScopingMode, SuppressPluginContainers)
4. Skill expansion (add functions from expanded skills)
5. Deduplication (ensure functions appear once)

### Benefits

1. ✅ **Pure skill interface** - Skills are the ONLY way to access functions
2. ✅ **Plugins still registered** - Validation works, no build errors
3. ✅ **Clean agent view** - No clutter from unreferenced functions
4. ✅ **Easy to enable** - Single flag OR single method call
5. ✅ **Backward compatible** - Default is `false` (current behavior)
6. ✅ **Auto-enables plugin scoping** - Builder method ensures `PluginScoping.Enabled = true`
7. ✅ **Composable** - Works with `ScopingMode`, `SuppressPluginContainers`, `AutoExpand`

### Use Cases

**Use Skills-Only Mode When:**
- ✅ Want skill-based architecture exclusively
- ✅ Have many plugins with many functions
- ✅ Only want functions accessible through skills
- ✅ Want maximum control over function visibility
- ✅ Building domain-specific agent with curated capabilities

**Don't Use Skills-Only Mode When:**
- ❌ Want plugins to be directly accessible
- ❌ Have few plugins with few functions (not worth it)
- ❌ Want flexibility of both skills and direct plugin access
- ❌ Building general-purpose agent

### Backward Compatibility

**100% backward compatible:**
- Default `SkillsOnlyMode = false` (current behavior)
- No breaking changes to existing code
- Opt-in feature via builder method or config property

---

### Key Takeaways - Skills-Only Mode

1. **Pure Skill Interface** - Plugins registered but hidden, skills are ONLY interface
2. **Validation Still Works** - Plugins must be registered for Build() validation
3. **Global Filter** - ALL unreferenced functions hidden, regardless of plugin scoping
4. **Composable** - Works with `ScopingMode`, `SuppressPluginContainers`, `AutoExpand`
5. **Easy to Enable** - Single method: `.EnableSkillsOnlyMode()`
6. **Backward Compatible** - Default behavior unchanged

---

## Design Implication: When to Use [PluginScope]

**✅ Use [PluginScope] when:**
- Plugin has cohesive functions that belong together
- You want token-efficient scoping
- You might reference entire plugin in skills (`PluginReferences`)

**❌ Don't use [PluginScope] when:**
- Plugin has unrelated utility functions
- Functions should always be visible
- Plugin is a collection of independent tools

**Example Decision:**
```csharp
// ✅ Good use of [PluginScope] - cohesive semantic unit
[PluginScope("File system operations")]
public class FileSystemPlugin { ... }

// ❌ Bad use of [PluginScope] - unrelated utilities
[PluginScope("Utility functions")]  // Don't do this!
public class UtilityPlugin
{
    [AIFunction] public string FormatString(...) { }  // Unrelated
    [AIFunction] public int CalculateHash(...) { }     // Unrelated
    [AIFunction] public string GenerateGuid(...) { }   // Unrelated
}
// These should be always visible, not scoped!
```

### Validation

**PluginReferences are validated at Build() time:**

```csharp
// If plugin has no registered functions, validation fails
if (!functionsAdded)
{
    throw new InvalidOperationException(
        $"Skill '{Name}' references plugin '{pluginRef}' which has no registered functions. " +
        $"Ensure the plugin is registered before building skills.");
}
```

**Example error:**
```
InvalidOperationException: Skill 'FileManagement' references plugin 'FileSystemPlugin' which has no registered functions.
Ensure the plugin is registered before building skills.
```

This happens when:
```csharp
// ❌ Forgot to register plugin
.WithSkill(new SkillDefinition {
    PluginReferences = new[] { "FileSystemPlugin" }
})
// .WithPlugin<FileSystemPlugin>()  ← Missing!
```

**Fix:**
```csharp
// ✅ Register plugin first
.WithPlugin<FileSystemPlugin>()
.WithSkill(new SkillDefinition {
    PluginReferences = new[] { "FileSystemPlugin" }
})
```

### Documentation Updates

1. **README.md** - Added "Quick Start: Plugin References" section
2. **README.md** - Updated examples to show PluginReferences + AutoExpand
3. **README.md** - Added "Plugin References vs Function References" best practice
4. **This document** - Added Phase 1 Enhancement section

---

## Summary

### What We Built:
- ✅ Skills as semantic grouping layer on top of plugins
- ✅ M:N relationships (functions in multiple skills)
- ✅ Document-based instructions (SOPs/protocols)
- ✅ Ephemeral container pattern (token-efficient)
- ✅ Fail-fast validation at Build() time
- ✅ Reusable skill definitions
- ✅ AOT-safe (no reflection)

### Why This Design:
- ✅ Validation happens at the right time (after plugin registration)
- ✅ Clear error messages with helpful context
- ✅ Flexible (works with plugins, MCP, custom functions)
- ✅ Simple (string-based references)
- ✅ Backward compatible (skills are optional)

### What We Didn't Do (and Why):
- ❌ Compile-time validation: Impossible due to compilation order
- ❌ Global function registry: Adds complexity without solving real problem
- ❌ Validate before plugin registration: Logically impossible (cart before horse)
- ❌ Auto-register plugins: Too much magic, violates explicit configuration

### What's Next (Phase 2):
- Type-safe function references (IntelliSense support)
- Skill attributes (alternative syntax)
- Conditional skills (context-based visibility)

---

**Remember in 3 months:**
1. Skills validate AFTER plugin registration (line 587 in AgentBuilder.Build())
2. This is the ONLY correct place for validation
3. String-based references are intentional (flexibility > type safety)
4. Reusable skills = reusable blueprints (not portable units)
5. The safeguard is perfect - don't second-guess it!

**End of Document**

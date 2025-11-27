# Plugin Architecture: Fundamental Problem Space

**Date**: 2025-01-20
**Status**: Analysis - No Solutions Proposed
**Purpose**: Document the fundamental architectural constraints and problems in the current plugin system

---

## Overview

This document outlines the core architectural problems in the current plugin scoping design. These issues stem from fundamental design assumptions that conflict with the requirement for dynamic, semantic, many-to-many function grouping.

---

## Problem 1: Rigid 1:N Ownership Model

### Current Design
```
Plugin = Container of Functions (1:N, fixed at compile-time)
```

### The Constraint
- A function has **exactly ONE parent** (`ParentPlugin` metadata)
- This parent is **determined at compile-time** (source generator)
- The parent **never changes** (metadata is immutable)

### Why This Breaks
```csharp
// ReadFile is implemented in FileSystemPlugin
// Source generator sets: ParentPlugin = "FileSystemPlugin"

// But conceptually, ReadFile is useful for:
// - Debugging (read log files)
// - Data Analysis (read CSV/JSON)
// - File Management (read any file)

// You CANNOT express this many-to-many relationship
// ReadFile is "owned" by FileSystemPlugin forever
```

### The Deeper Issue
This conflates **implementation location** (where the code lives) with **semantic grouping** (how it's used). These are orthogonal concerns that your architecture treats as the same thing.

---

## Problem 2: Filtering vs. Composition

### Current Design
Your scoping is **purely subtractive**:

```csharp
// Agent.cs:639
var scopedFunctions = _pluginScopingManager.GetToolsForAgentTurn(
    aiFunctions,        // Start with ALL registered functions
    expandedPlugins);   // HIDE the ones whose parents aren't expanded

// PluginScopingManager.cs:23-67
// Logic: foreach function, if parent NOT expanded, remove it
```

### The Constraint
- You can only **hide/show** what's already in `effectiveOptions.Tools`
- You **cannot add** anything that wasn't registered during `.Build()`
- The "universe" of available functions is **fixed at agent initialization**

### Why This Breaks
```csharp
// Scenario: User talks about debugging
// Ideal: Add debugging-specific tools dynamically
// Reality: Can only show/hide from pre-registered tools

// If InspectToken wasn't registered during .Build():
//   → It can NEVER appear, even if user needs it
//   → No mechanism to load it on-demand
//   → No lazy loading or context-aware tool addition
```

### The Deeper Issue
Your architecture assumes **all possible tools are known upfront**. There's no mechanism for:
- Lazy loading plugins
- Context-aware tool activation
- User-specific tool sets
- Progressive tool disclosure

---

## Problem 3: Tool Metadata is Static

### Current Design
When a function is registered, its properties are **frozen**:

```csharp
// At compile-time (source generator):
AdditionalProperties = {
    ["ParentPlugin"] = "FileSystemPlugin",
    ["Description"] = "Reads a file from disk"
}

// At runtime:
function.Description  // ← Single string, immutable
function.AdditionalProperties  // ← IReadOnlyDictionary, immutable
```

### The Constraint
- Metadata is **set once** during registration
- Same function always has **same description**, regardless of context
- No way to provide **context-specific metadata**

### Why This Breaks
```csharp
// ReadFile in "Debugging" context:
//   Description: "Read log files and stack traces for error analysis"
//   Hint: "Focus on .log and .txt files"

// ReadFile in "Data Analysis" context:
//   Description: "Load CSV, JSON, or Parquet data files"
//   Hint: "Use for structured data files"

// Current design: ONE description for both contexts
// Can't adapt metadata based on which "group" activated the function
```

### The Deeper Issue
Functions are **context-blind**. They don't know:
- Which skill activated them
- What the user is trying to accomplish
- What other functions are available
- What iteration/conversation state we're in

---

## Problem 4: Container Semantics Don't Scale

### Current Design
Your containers represent **implementation plugins**:

```csharp
// MathPlugin container contains ALL math functions
[PluginScope("Mathematical operations")]
public class MathPlugin { ... }

// Container semantics:
//   - Expand MathPlugin → Show ALL math functions
//   - No selectivity
//   - No composition
```

### The Constraint
- A container expands to **exactly its plugin's functions**
- You can't have **cross-plugin containers**
- You can't have **overlapping containers**
- You can't have **conditional expansion** (some functions but not others)

### Why This Breaks
```csharp
// Desired: "Debugging" container
//   Functions from: FileSystemPlugin, LoggingPlugin, AuthPlugin, DatabasePlugin
//   Semantically coherent: "These are all debugging tools"

// Current design:
//   - Debugging ≠ a plugin
//   - No way to create cross-plugin containers
//   - Would need to expand 4 separate containers
//   - User mental model misalignment
```

### The Deeper Issue
Containers are **structurally defined** (based on code organization) rather than **semantically defined** (based on user tasks/goals).

---

## Problem 5: No Mechanism for Dynamic State

### Current Design
Your scoping state is **iteration-local**:

```csharp
// Agent.cs:585
var expandedPlugins = new HashSet<string>();  // ← Lives only in this method

// Lifetime:
//   - Created at start of message turn
//   - Persists across iterations WITHIN the turn
//   - Garbage collected when turn ends

// No persistence across:
//   - Message turns
//   - Conversations
//   - User sessions
```

### The Constraint
- Expansion state **resets every user message**
- No **persistent preferences** ("always show debugging tools")
- No **learning** ("user frequently uses database tools, keep them visible")
- No **conversation-level state** ("we're in debugging mode for this conversation")

### Why This Breaks
```csharp
// Turn 1: User: "Debug this error"
//   → Agent expands Debugging container
//   → Uses GetLogs, ReadFile

// Turn 2: User: "Now check the database"
//   → Debugging container Collapse (state reset)
//   → Agent must re-expand if needed
//   → No "memory" that we're in a debugging session
```

### The Deeper Issue
Your architecture is **stateless** when users think of conversations as **stateful**. "We're debugging" is a multi-turn concept, but your scoping is single-turn.

---

## Problem 6: Deduplication is Impossible

### Current Design
If you naively extend to many-to-many:

```csharp
// ReadFile belongs to: [Debugging, DataAnalysis, FileManagement]

// User activates: Debugging + DataAnalysis
expandedPlugins = ["Debugging", "DataAnalysis"]

// Current filter logic:
foreach (var function in allFunctions)
{
    var parentPlugin = GetParentPlugin(function);  // ← Returns ONE parent
    if (expandedPlugins.Contains(parentPlugin))
        result.Add(function);
}

// With many-to-many:
var parentPlugins = GetParentPlugins(function);  // ← Returns MULTIPLE parents
foreach (var parent in parentPlugins)
{
    if (expandedPlugins.Contains(parent))
        result.Add(function);  // ← ADDED MULTIPLE TIMES!
}

// ReadFile appears TWICE in the tools list sent to LLM
```

### The Constraint
- No **deduplication logic** exists
- The design assumes **disjoint sets** (each function in exactly one plugin)
- No strategy for **which metadata wins** when duplicates exist

### Why This Breaks
```csharp
// LLM sees:
Tools = [
    ReadFile,  // From Debugging expansion
    GetLogs,
    ReadFile,  // From DataAnalysis expansion (DUPLICATE!)
    QueryDatabase
]

// LLM behavior:
//   - Might call the "wrong" ReadFile
//   - Confused by duplicates
//   - Wasted tokens
```

### The Deeper Issue
Your filter logic assumes **set membership** (in/out), not **multiplicity** (how many times).

---

## Problem 7: Plugin Registration is All-or-Nothing

### Current Design
When you register a plugin:

```csharp
// AgentBuilder
.WithPlugin<FileSystemPlugin>()  // ← Registers ALL functions in the plugin
```

### The Constraint
- You register **entire plugins** (all functions)
- No way to register **individual functions**
- No way to register **subsets of a plugin**
- No way to **conditionally register** based on context

### Why This Breaks
```csharp
// FileSystemPlugin has 20 functions:
//   - ReadFile, WriteFile, DeleteFile (common, always needed)
//   - EncryptFile, DecryptFile (security, rarely needed)
//   - CompressFile, DecompressFile (performance, rarely needed)
//   - SyncToCloud, SyncFromCloud (cloud, rarely needed)
//   ... 12 more

// Current design:
//   - Register plugin → ALL 20 functions in memory
//   - Even if user never uses encryption/compression/cloud
//   - Token waste (even with scoping, container metadata includes all 20 names)

// Desired:
//   - Register core functions always
//   - Lazy-load advanced functions when needed
//   - Progressive plugin loading
```

### The Deeper Issue
You conflate **unit of code organization** (plugin class) with **unit of registration** (what gets loaded).

---

## Problem 8: Source Generator Assumptions

### Current Design
Your source generator makes hard assumptions:

```csharp
// HPDPluginSourceGenerator.cs:55-68
var (hasScopeAttribute, scopeDescription, postExpansionInstructions) =
    GetPluginScopeAttribute(classDecl);

return new PluginInfo
{
    Name = classDecl.Identifier.ValueText,  // ← Plugin name = class name
    HasScopeAttribute = hasScopeAttribute,
    ScopeDescription = scopeDescription,
    PostExpansionInstructions = postExpansionInstructions
};
```

### The Constraints
- One `[PluginScope]` per plugin class
- Plugin name is **class name** (not customizable)
- All functions in the class belong to **that plugin only**
- No way to annotate "this function also belongs to other groups"

### Why This Breaks
```csharp
// Can't express:
[PluginScope("File System Operations")]
public class FileSystemPlugin
{
    [AIFunction]
    [AlsoBelongsTo("Debugging", "DataAnalysis")]  // ← Doesn't exist
    public string ReadFile(string path) { ... }
}

// Or:
[Skill("Debugging", functions: ["ReadFile", "GetLogs"])]  // ← Can't reference cross-plugin
[Skill("DataAnalysis", functions: ["ReadFile", "QueryDatabase"])]
public class SkillDefinitions { }  // ← Not a plugin, no functions, just metadata
```

### The Deeper Issue
Your source generator is **plugin-centric** (operates on classes) when you need **function-centric** (operates on relationships between functions and groups).

---

## Problem 9: No Composition Primitives

### Current Design
Your architecture has **filters** but no **composers**:

```csharp
// You have:
PluginScopingManager.GetToolsForAgentTurn(tools, expandedPlugins)
  → Returns SUBSET of input tools

// You don't have:
ToolComposer.Compose(staticTools, dynamicSources, context)
  → Returns static + dynamically generated tools

// You have:
[PluginScope("Math")]  // ← Single classification

// You don't have:
[BelongsToGroups("Math", "Science", "Education")]  // ← Multiple classifications
```

### The Constraint
- No **additive operations** (only subtractive filtering)
- No **dynamic generation** (only static registration)
- No **composition logic** (only filtering logic)

### Why This Breaks
You can't express:
- "Give me tools from groups A, B, C" (composition)
- "Add these context-specific tools" (dynamic generation)
- "Merge tool sets with different priorities" (composition strategies)

---

## Problem 10: IChatClient Interaction Mismatch

### Current Design
Your architecture fights the API design:

```csharp
// Microsoft.Extensions.AI expects:
Task<ChatResponse> GetResponseAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options = null)  // ← Tools are HERE, passed once per call

// Your agent does:
while (iteration < maxIterations)
{
    // Each iteration needs potentially DIFFERENT tools
    // But IChatClient expects ONE ChatOptions per GetResponseAsync call

    // You hack around this by creating new ChatOptions each iteration
    scopedOptions = new ChatOptions { Tools = scopedTools };
}
```

### The Constraint
- You create **new ChatOptions every iteration**
- You **manually copy all properties** (error-prone)
- No clean API for "modify tools, keep everything else"

### Why This Breaks
```csharp
// If ChatOptions has 15 properties:
scopedOptions = new ChatOptions
{
    ModelId = effectiveOptions.ModelId,
    Tools = scopedTools,  // ← Only thing that changed
    ToolMode = effectiveOptions.ToolMode,
    Temperature = effectiveOptions.Temperature,
    // ... 11 more manual copies
};

// Maintainability nightmare:
//   - Easy to forget a property
//   - When Microsoft adds new properties, you break
//   - Verbose, repetitive
```

### The Deeper Issue
You're building **stateful iteration** on top of a **stateless per-call API**. The impedance mismatch creates complexity.

---

## Problem 11: AOT Compatibility Constrains Dynamism

### Current Design
Your architecture is designed for Native AOT compatibility, using source generators instead of reflection:

```csharp
// Source generator creates parsers at compile-time:
private static InspectTokenArgs ParseInspectTokenArgs(JsonElement json)
{
    var result = new InspectTokenArgs();
    if (json.TryGetProperty("token", out var tokenProp))
    {
        result.token = tokenProp.GetString();  // ← No reflection!
    }
    return result;
}

// Manual schema generation (compile-time):
var schema = new JsonSchemaBuilder().FromType<InspectTokenArgs>().Build();
```

### The Constraint
- All functions must be **compile-time generated** (source generator)
- No runtime type creation (no `Activator.CreateInstance`, no IL emit)
- No runtime parser generation (no expression trees, no reflection)
- No reflection in hot paths (function invocation must be delegate-based)

### Why This Breaks Dynamic Composition
```csharp
// You WANT to do this (add function at runtime):
public AIFunction CreateInspectTokenFunction()
{
    return HPDAIFunctionFactory.Create(
        async (args, ct) =>
        {
            var token = args.GetValue<string>("token");  // ← How to parse without reflection?
            return ValidateToken(token);
        },
        new HPDAIFunctionFactoryOptions
        {
            Name = "InspectToken",
            Description = "Inspects JWT tokens",
            // ⚠️ PROBLEM: Schema generation needs compile-time type info
            // ⚠️ PROBLEM: JSON parser needs source generator
            // ⚠️ PROBLEM: Validator needs generated code
        });
}

// What you CAN'T do with AOT:
// ❌ Create DTO types at runtime
// ❌ Generate parsers at runtime
// ❌ Compile expression trees
// ❌ Use System.Reflection.Emit
// ❌ Reflect over runtime types for schema generation
```

### What Actually Works
```csharp
// ✅ AOT-Compatible: Pre-register everything, filter at runtime
.WithPlugin<FileSystemPlugin>()    // ReadFile
.WithPlugin<LoggingPlugin>()       // GetLogs
.WithPlugin<AuthPlugin>()          // InspectToken (registered but hidden)
.WithPlugin<DatabasePlugin>()      // QueryDatabase

// Skills are METADATA ONLY (reference existing functions):
DefineSkill("Debugging", functionNames: new[]
{
    "ReadFile",      // ← Reference to pre-registered function
    "GetLogs",       // ← Reference to pre-registered function
    "InspectToken"   // ← Reference to pre-registered function
});

// Runtime: Filter from pre-registered (no creation):
var debuggingTools = allRegisteredFunctions
    .Where(f => debuggingSkill.FunctionNames.Contains(f.Name))
    .ToList();  // ← Pure filtering, AOT-safe!
```

### The Constraint Matrix

| Operation | AOT Compatible | Notes |
|-----------|---------------|-------|
| Register all functions at build | ✅ | Source generator creates code |
| Filter based on metadata at runtime | ✅ | No reflection needed |
| Compose subsets from registered functions | ✅ | Just list manipulation |
| Change visibility based on context | ✅ | Metadata-driven filtering |
| Use factory methods (compile-time) | ✅ | Factories are generated code |
| Multiple metadata tags per function | ✅ | Static data structure |
| Create functions at runtime | ❌ | Needs type generation |
| Generate parsers at runtime | ❌ | Source gen is compile-time only |
| Compile validators at runtime | ❌ | No expression trees in AOT |
| Reflect over types in hot paths | ❌ | Breaks trimming/AOT |
| Use `Activator.CreateInstance` | ❌ | Requires reflection |
| Emit IL or use expression trees | ❌ | Not supported in AOT |

### Where Reflection Currently Exists
```csharp
// PluginRegistration.cs:92-128 (initialization only, not hot path)
[RequiresUnreferencedCode("This method uses reflection...")]
public List<AIFunction> ToAIFunctions(IPluginMetadataContext? context = null)
{
    // ⚠️ Reflection here, but ONE-TIME at build:
    var registrationType = PluginType.Assembly.GetType($"{PluginType.Namespace}.{registrationTypeName}");
    var createPluginMethod = registrationType.GetMethod("CreatePlugin", ...);
    var result = createPluginMethod.Invoke(null, new[] { instance, context });

    // This is acceptable: happens during agent initialization, not per-message
}
```

### The Deeper Issue
Your AOT requirement fundamentally **limits dynamism to filtering/composition**, never **creation/generation**. Any "dynamic tool addition" must actually be "lazy visibility of pre-registered tools".

True runtime function creation (like creating a function based on user-provided schema) is **architecturally impossible** with Native AOT. All dynamism must be **pre-planned at compile-time** and **activated at runtime** through metadata.

---

## The Core Architectural Tension

All these problems stem from one fundamental mismatch:

```
Your Current Design           vs          What You Actually Need
├─ Static registration              ├─ Dynamic composition
├─ 1:N ownership                    ├─ M:N relationships
├─ Compile-time grouping            ├─ Runtime grouping
├─ Implementation-centric           ├─ Semantic-centric
├─ Filtering (subtractive)          ├─ Composing (additive)
├─ Plugin = Container               ├─ Plugin ≠ Semantic group
├─ Context-blind                    ├─ Context-aware
├─ Stateless per-turn               ├─ Stateful across conversation
└─ AOT-constrained                  └─ Runtime flexibility (but AOT required!)
```

---

## Impact Summary

### What Works Today
- ✅ Static plugin registration with compile-time safety
- ✅ Token optimization through hierarchical scoping
- ✅ Container expansion within a single message turn
- ✅ Type-safe source generation
- ✅ Post-expansion instructions

### What Doesn't Work
- ❌ Many-to-many function-to-group relationships
- ❌ Cross-plugin semantic groupings
- ❌ Dynamic tool addition based on context
- ❌ Context-specific function metadata
- ❌ Persistent scoping state across message turns
- ❌ Lazy loading of plugins/functions
- ❌ Function deduplication in overlapping groups
- ❌ Partial plugin registration
- ❌ Composition-based tool management
- ❌ Runtime function creation (blocked by AOT requirement)
- ❌ True dynamic tool generation (all tools must be compile-time known)

---

## Related Documents

- [plugin-scoping.md](./plugin-scoping.md) - Current implementation details
- [plugin-scoping-implementation-notes.md](./plugin-scoping-implementation-notes.md) - Technical notes
- [dynamic-plugin-metadata.md](./dynamic-plugin-metadata.md) - Context-aware metadata system

---

## Next Steps

This document identifies the problem space. Solution proposals should address:

1. **Ownership Model**: How to support M:N relationships between functions and groups
2. **Composition Primitives**: How to add tools dynamically, not just filter (within AOT constraints)
3. **Metadata Flexibility**: How to provide context-specific function metadata
4. **State Management**: How to maintain scoping state across conversation boundaries
5. **Deduplication Strategy**: How to handle functions in multiple groups
6. **Registration Granularity**: How to support partial/lazy plugin loading
7. **Source Generator Evolution**: How to support function-centric (not plugin-centric) metadata
8. **API Alignment**: How to cleanly integrate with IChatClient's stateless design
9. **AOT Compliance**: How to achieve dynamism while maintaining Native AOT compatibility

**Critical Constraint**: All solutions must work within Native AOT limitations:
- ✅ All functions compile-time registered
- ✅ Runtime filtering/composition only (no creation)
- ✅ Metadata-driven visibility changes
- ❌ No runtime type/parser generation
- ❌ No reflection in hot paths

Each solution must maintain backward compatibility with existing plugin scoping behavior.

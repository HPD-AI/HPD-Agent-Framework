# Skills API Reference

Complete API reference for the HPD-Agent Skill System.

---

## Table of Contents

1. [Core Types](#core-types)
   - [Skill](#skill)
   - [SkillOptions](#skilloptions)
2. [Factory Methods](#factory-methods)
   - [SkillFactory.Create](#skillfactorycreate)
3. [Source Generator](#source-generator)
   - [Generated Methods](#generated-methods)
   - [Generated Metadata](#generated-metadata)
4. [Agent Integration](#agent-integration)
   - [Skill Registration](#skill-registration)
   - [Auto-Registration](#auto-registration)
5. [Scoping Manager](#scoping-manager)
   - [UnifiedScopingManager](#unifiedscopingmanager)
6. [Diagnostic Codes](#diagnostic-codes)

---

## Core Types

### Skill

Represents a skill as a first-class citizen in the agent system.

**Namespace:** `HPD_Agent.Skills`

**Definition:**
```csharp 
public class Skill
{
    public string Name { get; internal set; }
    public string Description { get; internal set; }
    public string? Instructions { get; internal set; }
    public Delegate[] References { get; internal set; }
    public SkillOptions Options { get; internal set; }

    // Internal - populated by source generator
    internal string[] ResolvedFunctionReferences { get; set; }
    internal string[] ResolvedPluginTypes { get; set; }
}
```

**Properties:**

| Property | Type | Access | Description |
|----------|------|--------|-------------|
| `Name` | `string` | Get (internal set) | Unique identifier for the skill |
| `Description` | `string` | Get (internal set) | User-facing description shown before activation |
| `Instructions` | `string?` | Get (internal set) | Detailed instructions shown after activation |
| `References` | `Delegate[]` | Get (internal set) | Array of function/skill references |
| `Options` | `SkillOptions` | Get (internal set) | Configuration options for the skill |
| `ResolvedFunctionReferences` | `string[]` | Internal | Flattened function references (format: "PluginName.FunctionName") |
| `ResolvedPluginTypes` | `string[]` | Internal | Plugin types referenced by this skill |

**Usage:**

Skills are created using `SkillFactory.Create()`, not by directly instantiating this class.

```csharp
// Correct:
var skill = SkillFactory.Create(
    "MySkill",
    "Description",
    "Instructions",
    FileSystemPlugin.ReadFile,
    FileSystemPlugin.WriteFile
);

// Incorrect:
var skill = new Skill(); // Constructor is public but should not be used directly
```

---

### SkillOptions

Configuration options for skill behavior.

**Namespace:** `HPD_Agent.Skills`

**Definition:**
```csharp
public class SkillOptions
{
    public bool AutoExpand { get; set; } = false;
    public string[]? InstructionDocuments { get; set; }
    public string InstructionDocumentBaseDirectory { get; set; } = "skills/documents/";
}
```

**Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `AutoExpand` | `bool` | `false` | Whether skill auto-expands at conversation start |
| `InstructionDocuments` | `string[]?` | `null` | Paths to additional instruction documents |
| `InstructionDocumentBaseDirectory` | `string` | `"skills/documents/"` | Base directory for instruction documents |

**Usage:**

```csharp
// Default options (default scoped mode)
var skill = SkillFactory.Create(
    "MySkill",
    "Description",
    "Instructions",
    FileSystemPlugin.ReadFile
);

// Custom options
var skill = SkillFactory.Create(
    "MySkill",
    "Description",
    "Instructions",
    new SkillOptions
    {
        AutoExpand = true,
        InstructionDocuments = new[] { "debugging_guide.md", "troubleshooting.md" },
        InstructionDocumentBaseDirectory = "docs/skills/"
    },
    FileSystemPlugin.ReadFile,
    FileSystemPlugin.WriteFile
);
```

---


Enum controlling function visibility behavior.

**Namespace:** `HPD_Agent.Skills`

**Definition:**
```csharp
{
    InstructionOnly = 0,
    Scoped = 1
}
```

**Values:**

| Value | Description | Behavior |
|-------|-------------|----------|
| `InstructionOnly` | Functions always visible | Skill activation only adds instructions to context. Referenced functions remain visible at all times. |
| `Scoped` | Functions hidden until activation | Skill functions are hidden behind a container. Agent must activate skill to access functions. |

**Comparison:**

```csharp
// InstructionOnly (default)
// Tools: [file_debugging, ReadFile, WriteFile, ListDirectory]
// Agent can call ReadFile directly without activating file_debugging

var instructionOnlySkill = SkillFactory.Create(
    "file_debugging",
    "Debug file issues",
    "Use these tools to debug...",
    FileSystemPlugin.ReadFile,
    FileSystemPlugin.WriteFile
);

// Scoped
// Tools: [file_debugging_container]
// Agent must call file_debugging_container first
// Then gets: [file_debugging, ReadFile, WriteFile]

var scopedSkill = SkillFactory.Create(
    "file_debugging",
    "Debug file issues",
    "Use these tools to debug...",
    FileSystemPlugin.ReadFile,
    FileSystemPlugin.WriteFile
);
```

---

## Factory Methods

### SkillFactory.Create

Static factory method for creating skills with type-safe references.

**Namespace:** `HPD_Agent.Skills`

**Overload 1: Without Options**

```csharp
public static Skill Create(
    string name,
    string description,
    string instructions,
    params Delegate[] references)
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `name` | `string` | Unique skill identifier (must be non-empty) |
| `description` | `string` | User-facing description (must be non-empty) |
| `instructions` | `string` | Detailed instructions (can be null or empty) |
| `references` | `params Delegate[]` | Function/skill references (can be empty) |

**Returns:** `Skill` instance

**Exceptions:**
- `ArgumentException`: If `name` or `description` is null/whitespace

**Example:**
```csharp
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug file-related issues",
        "Use these tools to investigate file problems...",
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile,
        FileSystemPlugin.ListDirectory
    );
}
```

---

**Overload 2: With Options**

```csharp
public static Skill Create(
    string name,
    string description,
    string instructions,
    SkillOptions? options,
    params Delegate[] references)
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `name` | `string` | Unique skill identifier (must be non-empty) |
| `description` | `string` | User-facing description (must be non-empty) |
| `instructions` | `string` | Detailed instructions (can be null or empty) |
| `options` | `SkillOptions?` | Configuration options (null uses defaults) |
| `references` | `params Delegate[]` | Function/skill references (can be empty) |

**Returns:** `Skill` instance

**Exceptions:**
- `ArgumentException`: If `name` or `description` is null/whitespace

**Example:**
```csharp
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug file-related issues",
        "Use these tools to investigate file problems...",
        options ?? new SkillOptions
        {
            AutoExpand = false
        },
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile
    );
}
```

---

**Parameter Passing Modes:**

```csharp
// 1. Positional arguments (no options)
SkillFactory.Create(
    "skill_name",
    "description",
    "instructions",
    Plugin.Function1,
    Plugin.Function2
);

// 2. Positional arguments (with options)
SkillFactory.Create(
    "skill_name",
    "description",
    "instructions",
    Plugin.Function1,
    Plugin.Function2
);

// 3. Named parameter (options)
SkillFactory.Create(
    "skill_name",
    "description",
    "instructions",
    Plugin.Function1,
    Plugin.Function2,
);
```

---

## Source Generator

The HPD-Agent source generator automatically detects skills and generates registration code.

### Generated Methods

For each plugin class containing skills, the source generator creates:

#### 1. GetReferencedPlugins()

Returns an array of plugin names referenced by all skills in the class.

**Signature:**
```csharp
public static string[] GetReferencedPlugins()
```

**Generated Code Example:**
```csharp
public static partial class DevelopmentSkills
{
    public static string[] GetReferencedPlugins()
    {
        return new[]
        {
            "FileSystemPlugin",
            "GitPlugin",
            "ProcessPlugin"
        };
    }
}
```

**Usage:**
Called by `AgentBuilder` during auto-registration to discover plugin dependencies.

---

#### 2. Skill Container Functions (Scoped Mode Only)

For skills with `ScopingMode = Scoped`, generates a container function.

**Signature:**
```csharp
[AIFunction]
[Description("Activate {skill_name} skill")]
public static string {SkillName}_container()
```

**Generated Code Example:**
```csharp
[AIFunction]
[Description("Activate file_debugging skill to debug file-related issues")]
public static string file_debugging_container()
{
    return "file_debugging skill activated. You now have access to file debugging tools.";
}
```

**Metadata:**
- `AdditionalProperties["IsContainer"] = true`
- `AdditionalProperties["SkillName"] = "file_debugging"`
- `AdditionalProperties["ScopingMode"] = "Scoped"`

---

#### 3. Skill Activation Functions

For all skills, generates an activation function that returns instructions.

**Signature:**
```csharp
[AIFunction]
[Description("{skill_description}")]
public static string {SkillName}()
```

**Generated Code Example:**
```csharp
[AIFunction]
[Description("Debug file-related issues")]
public static string file_debugging()
{
    return @"Use these tools to investigate file problems:

1. ReadFile - Read file contents to check for corruption
2. WriteFile - Create test files to verify write permissions
3. ListDirectory - Check directory structure and permissions

Always verify file paths before operations.";
}
```

**Metadata:**
- `AdditionalProperties["IsSkill"] = true`
- `AdditionalProperties["SkillName"] = "file_debugging"`
- `AdditionalProperties["ScopingMode"] = "InstructionOnly"` or `"Scoped"`
- `AdditionalProperties["ReferencedFunctions"] = ["FileSystemPlugin.ReadFile", ...]`

---

### Generated Metadata

All generated functions include metadata in `AdditionalProperties` dictionary:

| Key | Type | Present In | Description |
|-----|------|------------|-------------|
| `IsContainer` | `bool` | Container functions | `true` for skill containers |
| `IsSkill` | `bool` | Skill functions | `true` for skill activation functions |
| `SkillName` | `string` | Container & skill functions | Name of the skill |
| `ScopingMode` | `string` | Skill functions | "InstructionOnly" or "Scoped" |
| `ReferencedFunctions` | `string[]` | Skill functions | Flattened function references |

**Example:**
```csharp
// Container metadata
function.AdditionalProperties = new Dictionary<string, object?>
{
    ["IsContainer"] = true,
    ["SkillName"] = "file_debugging",
    ["ScopingMode"] = "Scoped"
};

// Skill metadata
function.AdditionalProperties = new Dictionary<string, object?>
{
    ["IsSkill"] = true,
    ["SkillName"] = "file_debugging",
    ["ScopingMode"] = "Scoped",
    ["ReferencedFunctions"] = new[] { "FileSystemPlugin.ReadFile", "FileSystemPlugin.WriteFile" }
};
```

---

## Agent Integration

### Skill Registration

Skills are automatically registered by the source generator. No manual registration required.

**Automatic Process:**

1. Source generator detects skill methods (returns `Skill` type)
2. Analyzes `SkillFactory.Create()` calls
3. Resolves nested skill references recursively
4. Generates registration code in partial class
5. Generates `GetReferencedPlugins()` method

**Plugin Class Example:**
```csharp
// Your code
public static partial class DevelopmentSkills
{
    public static Skill FileDebugging(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "file_debugging",
            "Debug file issues",
            "Instructions...",
            options,
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile
        );
    }
}

// Generated code (automatic)
public static partial class DevelopmentSkills
{
    public static string[] GetReferencedPlugins()
    {
        return new[] { "FileSystemPlugin" };
    }

    [AIFunction]
    [Description("Activate file_debugging skill")]
    public static string file_debugging_container() { ... }

    [AIFunction]
    [Description("Debug file issues")]
    public static string file_debugging() { ... }
}
```

---

### Auto-Registration

Plugins referenced by skills are automatically registered during agent building.

**Process Flow:**

1. **Discovery Phase:**
   ```csharp
   var builder = Agent.CreateBuilder();
   builder.AddPlugin<DevelopmentSkills>();
   ```

2. **Reference Discovery:**
   - AgentBuilder calls `DevelopmentSkills.GetReferencedPlugins()`
   - Returns: `["FileSystemPlugin"]`

3. **Auto-Registration:**
   - Checks if `FileSystemPlugin` is already registered
   - If not, searches loaded assemblies for `FileSystemPlugin` type
   - Registers plugin automatically: `builder.AddPlugin<FileSystemPlugin>()`

4. **Function Creation:**
   - Creates AI functions for both `DevelopmentSkills` and `FileSystemPlugin`
   - Links skill metadata to function metadata

**Code Example:**
```csharp
// Manual approach (old)
var builder = Agent.CreateBuilder();
builder.AddPlugin<DevelopmentSkills>();
builder.AddPlugin<FileSystemPlugin>(); // Manual registration
builder.AddPlugin<GitPlugin>();        // Manual registration
var agent = builder.Build();

// Auto-registration (new)
var builder = Agent.CreateBuilder();
builder.AddPlugin<DevelopmentSkills>(); // FileSystemPlugin and GitPlugin auto-registered
var agent = builder.Build();
```

**Logging:**

Auto-registration emits log messages:

```
[INFO] Auto-registered plugin 'FileSystemPlugin' (referenced by DevelopmentSkills)
[INFO] Auto-registered plugin 'GitPlugin' (referenced by DevelopmentSkills)
```

**Error Handling:**

If a referenced plugin cannot be found:

```
[WARN] Cannot auto-register plugin 'UnknownPlugin' (referenced by DevelopmentSkills): Type not found in loaded assemblies
```

**Assembly Search:**

Auto-registration searches all loaded assemblies in order:
1. Current assembly
2. Referenced assemblies
3. All loaded assemblies in AppDomain

**Namespace Handling:**

Plugin types can be in any namespace. The search is name-based:

```csharp
// Finds these:
namespace HPD_Agent.Plugins { public class FileSystemPlugin { } }
namespace CustomNamespace { public class FileSystemPlugin { } }
namespace MyPlugins.Core { public class FileSystemPlugin { } }

// Disambiguates by requiring exact type name match
```

---

## Scoping Manager

### UnifiedScopingManager

Manages tool visibility for both plugins and skills in a unified way.

**Namespace:** `HPD_Agent.Scoping`

**Constructor:**
```csharp
public UnifiedScopingManager(
    Dictionary<string, Skill> skills,
    List<AIFunction> initialTools,
    ILogger? logger = null)
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|------------|
| `skills` | `Dictionary<string, Skill>` | All available skills (key: skill name) |
| `initialTools` | `List<AIFunction>` | All AI functions (plugins + skills) |
| `logger` | `ILogger?` | Optional logger for debugging |

---

**Primary Method: GetToolsForAgentTurn**

Returns the ordered list of visible tools for the current agent turn.

**Signature:**
```csharp
public List<AIFunction> GetToolsForAgentTurn(
    List<AIFunction> allTools,
    HashSet<string> expandedPlugins,
    HashSet<string> expandedSkills)
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|------------|
| `allTools` | `List<AIFunction>` | All available AI functions |
| `expandedPlugins` | `HashSet<string>` | Set of expanded plugin names |
| `expandedSkills` | `HashSet<string>` | Set of expanded (activated) skill names |

**Returns:** Ordered `List<AIFunction>` with duplicates removed

**Tool Ordering:**

1. **Plugin Containers** - Unexpanded scoped plugins
2. **Skill Containers** - Unexpanded scoped skills
3. **Non-Scoped Functions** - Always-visible plugin functions
4. **Instruction-Only Skills** - Skills with `InstructionOnly` mode
5. **Expanded Plugin Functions** - Functions from expanded plugins
6. **Expanded Skill Functions** - Functions from expanded skills

**Deduplication:**

Uses `DistinctBy(f => f.Name)` to remove duplicate functions:
- If both plugin and skill reference `ReadFile`, only first occurrence appears
- Order determines priority (earlier categories take precedence)

**Example:**

```csharp
var scopingManager = new UnifiedScopingManager(skills, allTools, logger);

var expandedPlugins = new HashSet<string>(); // No plugins expanded yet
var expandedSkills = new HashSet<string> { "file_debugging" }; // file_debugging activated

var visibleTools = scopingManager.GetToolsForAgentTurn(
    allTools,
    expandedPlugins,
    expandedSkills
);

// Returns:
// [
//   plugin_container_1,        // Plugin containers (unexpanded)
//   file_ops_container,         // Skill containers (unexpanded scoped skills)
//   WriteLog,                   // Non-scoped functions
//   file_debugging,             // Instruction-only skills (or activated scoped skills)
//   ReadFile,                   // Functions from file_debugging skill
//   WriteFile,
//   ListDirectory
// ]
```

---

**Helper Method: IsContainer**

Checks if a function is a container (plugin or skill).

**Signature:**
```csharp
public bool IsContainer(AIFunction function)
```

**Returns:** `true` if function has `IsContainer = true` in metadata

---

**Helper Method: IsSkill**

Checks if a function is a skill activation function.

**Signature:**
```csharp
public bool IsSkill(AIFunction function)
```

**Returns:** `true` if function has `IsSkill = true` in metadata

---

**Helper Method: GetSkillName**

Extracts skill name from function metadata.

**Signature:**
```csharp
public string? GetSkillName(AIFunction function)
```

**Returns:** Skill name or `null` if not a skill/container

---

## Diagnostic Codes

Complete list of diagnostic codes emitted by the source generator.

### Skill Definition Errors (HPD001-HPD099)

| Code | Severity | Message |
|------|----------|---------|
| HPD001 | Error | Skill method '{0}' must return 'Skill' type |
| HPD002 | Error | Skill method '{0}' must be public |
| HPD003 | Error | Skill method '{0}' must have signature: [Skill] public Skill MethodName(SkillOptions? options = null) |
| HPD004 | Error | Skill method '{0}' is missing SkillFactory.Create() call |
| HPD005 | Error | Skill '{0}': Name cannot be empty |
| HPD006 | Error | Skill '{0}': Description cannot be empty |

**Example:**
```csharp
// HPD001: Must return Skill
public static void FileDebugging() // Error: returns void

// HPD002: Must be public static
private static Skill FileDebugging() // Error: private
public Skill FileDebugging() // Error: not static

// HPD004: Missing SkillFactory.Create()
public static Skill FileDebugging()
{
    return null; // Error: no SkillFactory.Create()
}

// HPD005: Empty name
public static Skill FileDebugging()
{
    return SkillFactory.Create("", "desc", "instr"); // Error
}
```

---

### Skill Reference Errors (HPD100-HPD199)

| Code | Severity | Message |
|------|----------|---------|
| HPD100 | Error | Skill '{0}': Reference '{1}' is not a valid function or skill |
| HPD101 | Error | Skill '{0}': Function reference '{1}' is missing [AIFunction] attribute |
| HPD102 | Warning | Skill '{0}': References skill '{1}' which was not found |
| HPD103 | Error | Skill '{0}': Circular reference detected: {1} |
| HPD104 | Warning | Skill '{0}': No function or skill references provided |
| HPD105 | Error | Skill '{0}': Reference '{1}' could not be resolved |

**Example:**
```csharp
// HPD100: Invalid reference
public static Skill BadSkill()
{
    return SkillFactory.Create(
        "bad_skill",
        "desc",
        "instr",
        Console.WriteLine // Error: Not a function or skill
    );
}

// HPD101: Missing [AIFunction]
public static string NotAFunction() => "test"; // Missing [AIFunction]

public static Skill BadSkill2()
{
    return SkillFactory.Create(
        "bad_skill",
        "desc",
        "instr",
        NotAFunction // Error: Missing [AIFunction]
    );
}

// HPD103: Circular reference
public static Skill SkillA() => SkillFactory.Create("a", "d", "i", SkillB);
public static Skill SkillB() => SkillFactory.Create("b", "d", "i", SkillA); // Error
```

---

### Skill Options Errors (HPD200-HPD299)

| Code | Severity | Message |
|------|----------|---------|
| HPD200 | Warning | Skill '{0}': Invalid ScopingMode value '{1}' |
| HPD201 | Warning | Skill '{0}': InstructionDocumentBaseDirectory is not a valid path |
| HPD202 | Warning | Skill '{0}': InstructionDocument '{1}' not found at '{2}' |

**Example:**
```csharp
// HPD201: Invalid base directory
public static Skill BadSkill()
{
    return SkillFactory.Create(
        "bad_skill",
        "desc",
        "instr",
        new SkillOptions
        {
            InstructionDocumentBaseDirectory = "C:\\Invalid\0Path" // Error
        }
    );
}
```

---

### Code Generation Errors (HPD300-HPD399)

| Code | Severity | Message |
|------|----------|---------|
| HPD300 | Error | Failed to generate code for skill '{0}': {1} |
| HPD301 | Error | Failed to resolve skill '{0}': {1} |
| HPD302 | Warning | Skill '{0}': Generated function name '{1}' conflicts with existing function |

---

### Advanced Diagnostic Information

**Diagnostic Properties:**

Each diagnostic includes:
- **Code**: HPD### identifier
- **Severity**: Error, Warning, or Info
- **Message**: Formatted message with parameters
- **Location**: File path, line number, character position
- **HelpLink**: URL to documentation (when available)

**Example Diagnostic Output:**

```
HPD103: Skill 'skill_a': Circular reference detected: skill_a -> skill_b -> skill_a
  at DevelopmentSkills.cs(45,20)
  Help: https://docs.hpd-agent.com/diagnostics/HPD103
```

**Diagnostic Suppression:**

Warnings can be suppressed using `#pragma`:

```csharp
#pragma warning disable HPD104 // Suppress "no references" warning
public static Skill EmptySkill()
{
    return SkillFactory.Create("empty", "desc", "instr"); // No references
}
#pragma warning restore HPD104
```

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2025-10-26 | Initial release of skill system API |

---

## See Also

- [Skills Guide](SKILLS_GUIDE.md) - Comprehensive usage guide
- [Skill Diagnostics](../SKILL_DIAGNOSTICS.md) - Detailed diagnostic reference
- [Implementation Progress](../IMPLEMENTATION_PROGRESS.md) - Development timeline

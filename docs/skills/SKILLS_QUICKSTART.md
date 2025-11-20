# Skills Quick Start Guide

Get up and running with HPD-Agent skills in 5 minutes.

---

## What You'll Build

A simple skill that helps debug file issues by bundling file reading, writing, and listing functions together.

---

## Prerequisites

- HPD-Agent project set up
- Basic understanding of C# and static methods
- Familiarity with AI function concepts

---

## Step 1: Create Your Plugin Class (30 seconds)

Create a new file `MySkills.cs`:

```csharp
using HPD_Agent.Skills;

namespace HPD_Agent.Plugins;

public static partial class MySkills
{
    // Skills will go here
}
```

**Key Points:**
- Must be `partial` (source generator adds code)
- Can be in any namespace
- Can coexist with AI functions

---

## Step 2: Define Your First Skill (1 minute)

Add a skill method:

```csharp
using HPD_Agent.Skills;

namespace HPD_Agent.Plugins;

public static partial class MySkills
{
    public static Skill FileDebugging(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "file_debugging",
            "Debug file-related issues in the codebase",
            @"Use these tools to investigate file problems:

1. ReadFile - Read file contents to check for corruption
2. WriteFile - Create test files to verify write permissions
3. ListDirectory - Check directory structure and permissions

Always verify file paths exist before performing operations.",
            options,
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile,
            FileSystemPlugin.ListDirectory
        );
    }
}
```

**What This Does:**
- Creates a skill named `file_debugging`
- Bundles three file system functions together
- Provides instructions for when to use these tools
- Uses default options (default scoped mode)

---

## Step 3: Build Your Project (30 seconds)

```bash
dotnet build
```

**What Happens:**
- Source generator detects your skill
- Generates `GetReferencedPlugins()` method
- Generates skill activation function
- Validates references are correct

**Check For Errors:**
If you see errors like `HPD001` or `HPD100`, check:
- Method is `public static`
- Method returns `Skill` type
- References are valid AI functions

---

## Step 4: Register Your Skill (1 minute)

In your agent setup code:

```csharp
using HPD_Agent.Agent;
using HPD_Agent.Plugins;

var builder = Agent.CreateBuilder();

// Add your skills plugin
builder.AddPlugin<MySkills>();

// FileSystemPlugin is auto-registered!
// No need to manually add it

var agent = builder.Build();
```

**What Happens:**
- `MySkills` plugin is registered
- Source generator created `GetReferencedPlugins()` returning `["FileSystemPlugin"]`
- `FileSystemPlugin` is automatically discovered and registered
- Skill functions are added to agent's tool list

---

## Step 5: Test Your Skill (1 minute)

Run your agent and check available tools:

```csharp
var agent = builder.Build();

// Print available tools
foreach (var tool in agent.Tools)
{
    Console.WriteLine($"- {tool.Name}: {tool.Description}");
}
```

**Expected Output:**
```
- file_debugging: Debug file-related issues in the codebase
- ReadFile: Read contents of a file
- WriteFile: Write contents to a file
- ListDirectory: List files in a directory
```

**Agent Behavior:**
1. Agent sees `file_debugging` skill in tool list
2. When agent calls `file_debugging()`, instructions are added to context
3. Agent can now use `ReadFile`, `WriteFile`, `ListDirectory` with guidance

---

## Step 6: Try Scoped Mode (1 minute)

Want to hide functions until skill is activated? Change to Scoped mode:

```csharp
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug file-related issues in the codebase",
        @"Use these tools to investigate file problems:

1. ReadFile - Read file contents
2. WriteFile - Create test files
3. ListDirectory - Check directory structure",
        options ?? new SkillOptions
        {
            // Skills are always scoped by default
        },
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile,
        FileSystemPlugin.ListDirectory
    );
}
```

**Rebuild and test:**

```bash
dotnet build
```

**Expected Output (initial):**
```
- file_debugging_container: Activate file_debugging skill
```

**After agent calls `file_debugging_container()`:**
```
- file_debugging: Debug file-related issues
- ReadFile: Read contents of a file
- WriteFile: Write contents to a file
- ListDirectory: List files in a directory
```

---

## Common Patterns

### Pattern 1: Nested Skills

Reference other skills:

```csharp
public static Skill BasicFileOps(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "basic_file_ops",
        "Basic file operations",
        "Read and write files",
        options,
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile
    );
}

public static Skill AdvancedFileOps(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "advanced_file_ops",
        "Advanced file operations",
        "Includes basic ops plus directory listing",
        options,
        BasicFileOps, // Reference another skill
        FileSystemPlugin.ListDirectory
    );
}
```

**Result:**
- `advanced_file_ops` includes all functions from `basic_file_ops` plus `ListDirectory`
- Source generator flattens references automatically

---

### Pattern 2: Skill Families

Organize related skills:

```csharp
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

    public static Skill GitWorkflow(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "git_workflow",
            "Git workflow operations",
            "Instructions...",
            options,
            GitPlugin.Commit,
            GitPlugin.Push,
            GitPlugin.Status
        );
    }

    public static Skill CodeReview(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "code_review",
            "Review code changes",
            "Instructions...",
            options,
            FileDebugging, // Nested skill
            GitWorkflow    // Nested skill
        );
    }
}
```

**Usage:**
```csharp
builder.AddPlugin<DevelopmentSkills>();
// All three skills registered
// Both FileSystemPlugin and GitPlugin auto-registered
```

---

### Pattern 3: Conditional Options

Allow callers to override options:

```csharp
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug file issues",
        "Instructions...",
        options ?? new SkillOptions // Default if not provided
        {
            // Skills are always scoped by default,
            AutoExpand = false
        },
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile
    );
}

// Usage:
var skill1 = FileDebugging(); // Uses defaults
var skill2 = FileDebugging(new SkillOptions { // Skills are always scoped by default}); // Custom
```

---

## Next Steps

### Learn More

- **[Complete Skills Guide](SKILLS_GUIDE.md)** - Comprehensive documentation
- **[API Reference](SKILLS_API_REFERENCE.md)** - Full API details
- **[Troubleshooting](SKILLS_TROUBLESHOOTING.md)** - Common issues and solutions

### Advanced Topics

1. **Instruction Documents:**
   ```csharp
   return SkillFactory.Create(
       "skill",
       "description",
       "instructions",
       new SkillOptions
       {
           InstructionDocuments = new[] { "detailed_guide.md", "examples.md" },
           InstructionDocumentBaseDirectory = "skills/docs/"
       },
       Functions...
   );
   ```

2. **Auto-Expand Skills:**
   ```csharp
   new SkillOptions
   {
       AutoExpand = true // Skill activated at conversation start
   }
   ```

3. **Skills With Plugin Scope:**
   Reference scoped plugins and skills together for fine-grained control.

---

## Cheat Sheet

### Skill Definition Template

```csharp
public static Skill SkillName(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "skill_name",              // snake_case identifier
        "Short description",       // 1-2 sentences
        @"Detailed instructions",  // Multi-line markdown
        options,                   // Options parameter
        Plugin.Function1,          // Function references
        Plugin.Function2,
        OtherSkill.NestedSkill    // Skill references
    );
}
```

---

### Options Configuration

```csharp
new SkillOptions
{
    // Skills are always scoped by default,  // or InstructionOnly
    AutoExpand = false,                      // or true
    InstructionDocuments = new[] { "doc.md" },
    InstructionDocumentBaseDirectory = "docs/"
}
```

---

### Agent Setup

```csharp
var builder = Agent.CreateBuilder();
builder.AddPlugin<MySkills>();  // Auto-registers referenced plugins
var agent = builder.Build();
```

---

## Complete Working Example

Here's a complete, copy-paste-ready example:

```csharp
using HPD_Agent.Skills;

namespace HPD_Agent.Plugins;

public static partial class QuickStartSkills
{
    public static Skill FileDebugger(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "file_debugger",
            "Debug file-related issues in the codebase",
            @"Use these tools to investigate file problems:

## When to Use
- File not found errors
- Permission denied errors
- Unexpected file contents

## Available Tools
1. **ReadFile** - Read file contents to verify data
2. **WriteFile** - Create test files to check permissions
3. **ListDirectory** - Examine directory structure

## Best Practices
- Always check file exists before reading
- Verify write permissions before creating files
- Use relative paths when possible",
            options ?? new SkillOptions
            {
                // Skills are always scoped by default,
                AutoExpand = false
            },
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile,
            FileSystemPlugin.ListDirectory
        );
    }
}
```

**Agent Setup:**
```csharp
using HPD_Agent.Agent;
using HPD_Agent.Plugins;

var builder = Agent.CreateBuilder();
builder.AddPlugin<QuickStartSkills>();
var agent = builder.Build();

// Test
Console.WriteLine("Available tools:");
foreach (var tool in agent.Tools)
{
    Console.WriteLine($"  - {tool.Name}");
}
```

**Build and Run:**
```bash
dotnet build
dotnet run
```

---

## Troubleshooting Quick Fixes

### Build Error: "Must return Skill type"
```csharp
// Wrong:
public static void MySkill() { ... }

// Correct:
public static Skill MySkill() { ... }
```

---

### Build Error: "Must be public static"
```csharp
// Wrong:
private static Skill MySkill() { ... }

// Correct:
public static Skill MySkill() { ... }
```

---

### Build Error: "Missing SkillFactory.Create()"
```csharp
// Wrong:
public static Skill MySkill()
{
    return null;
}

// Correct:
public static Skill MySkill()
{
    return SkillFactory.Create("name", "desc", "instr");
}
```

---

### Runtime Issue: Skill Not Appearing
```csharp
// Did you add the plugin?
builder.AddPlugin<MySkills>();
```

---

### Runtime Issue: Functions Hidden
```csharp
// Change to default scoped mode:
new SkillOptions { // Skills are always scoped by default}
```

---

## Summary

You now know how to:
- ✅ Create a skill with `SkillFactory.Create()`
- ✅ Reference AI functions and other skills
- ✅ Use InstructionOnly and Scoped modes
- ✅ Auto-register referenced plugins
- ✅ Test skills in your agent

**Time to explore:** Check out the [Complete Skills Guide](SKILLS_GUIDE.md) for advanced patterns and best practices!

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2025-10-26 | Initial quick start guide |

---

## See Also

- [Complete Skills Guide](SKILLS_GUIDE.md) - In-depth documentation
- [API Reference](SKILLS_API_REFERENCE.md) - Complete API
- [Troubleshooting](SKILLS_TROUBLESHOOTING.md) - Common issues

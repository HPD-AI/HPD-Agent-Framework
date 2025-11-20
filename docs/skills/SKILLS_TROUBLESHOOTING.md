# Skills Troubleshooting Guide

Common issues, solutions, and debugging techniques for the HPD-Agent Skill System.

---

## Table of Contents

1. [Compilation Errors](#compilation-errors)
2. [Runtime Issues](#runtime-issues)
3. [Source Generator Problems](#source-generator-problems)
4. [Auto-Registration Issues](#auto-registration-issues)
5. [Scoping Behavior](#scoping-behavior)
6. [Performance Issues](#performance-issues)
7. [Debugging Techniques](#debugging-techniques)

---

## Compilation Errors

### Error: "Skill method must return 'Skill' type" (HPD001)

**Problem:**
```csharp
public static void FileDebugging() // Error: returns void
{
    return SkillFactory.Create(...);
}
```

**Solution:**
Change return type to `Skill`:
```csharp
public static Skill FileDebugging()
{
    return SkillFactory.Create(...);
}
```

---

### Error: "Skill method must be public" (HPD002)

**Problem:**
```csharp
private Skill FileDebugging() { ... } // Error: private
```

**Solution:**
Make method `public` (static is optional):
```csharp
public Skill FileDebugging()
{
    return SkillFactory.Create(...);
}

// Or static for shared definitions:
public static Skill FileDebugging()
{
    return SkillFactory.Create(...);
}
```

---

### Error: "Missing SkillFactory.Create() call" (HPD004)

**Problem:**
```csharp
public static Skill FileDebugging()
{
    return null; // Error: no SkillFactory.Create()
}
```

**Solution:**
Always use `SkillFactory.Create()`:
```csharp
public static Skill FileDebugging()
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug file issues",
        "Instructions...",
        FileSystemPlugin.ReadFile
    );
}
```

---

### Error: "Name cannot be empty" (HPD005)

**Problem:**
```csharp
return SkillFactory.Create("", "desc", "instr"); // Error: empty name
```

**Solution:**
Provide a non-empty name:
```csharp
return SkillFactory.Create("file_debugging", "desc", "instr");
```

**Best Practice:**
Use snake_case for skill names:
- Good: `"file_debugging"`, `"git_workflow"`, `"code_review"`
- Bad: `""`, `"FileDebugging"`, `"file-debugging"`

---

### Error: "Description cannot be empty" (HPD006)

**Problem:**
```csharp
return SkillFactory.Create("skill", "", "instr"); // Error: empty description
```

**Solution:**
Provide a clear description:
```csharp
return SkillFactory.Create(
    "file_debugging",
    "Debug file-related issues in the codebase",
    "Instructions..."
);
```

---

### Error: "Reference is not a valid function or skill" (HPD100)

**Problem:**
```csharp
return SkillFactory.Create(
    "skill",
    "desc",
    "instr",
    Console.WriteLine // Error: Not a function or skill
);
```

**Solution:**
Only reference methods with `[AIFunction]` attribute or other skills:
```csharp
// Correct:
return SkillFactory.Create(
    "skill",
    "desc",
    "instr",
    FileSystemPlugin.ReadFile,      // Valid: has [AIFunction]
    OtherSkills.AnotherSkill        // Valid: returns Skill
);
```

---

### Error: "Function reference missing [AIFunction] attribute" (HPD101)

**Problem:**
```csharp
public static string HelperMethod() => "test"; // Missing [AIFunction]

public static Skill MySkill()
{
    return SkillFactory.Create(
        "my_skill",
        "desc",
        "instr",
        HelperMethod // Error: Missing [AIFunction]
    );
}
```

**Solution:**
Add `[AIFunction]` attribute:
```csharp
[AIFunction]
public static string HelperMethod() => "test";

public static Skill MySkill()
{
    return SkillFactory.Create(
        "my_skill",
        "desc",
        "instr",
        HelperMethod // Now valid
    );
}
```

---

### Error: "Circular reference detected" (HPD103)

**Problem:**
```csharp
public static Skill SkillA()
{
    return SkillFactory.Create("a", "d", "i", SkillB);
}

public static Skill SkillB()
{
    return SkillFactory.Create("b", "d", "i", SkillA); // Error: Circular
}
```

**Solution:**
Break the circular dependency:

**Option 1: Remove circular reference**
```csharp
public static Skill SkillA()
{
    return SkillFactory.Create(
        "a", "d", "i",
        FileSystemPlugin.ReadFile // Direct function reference
    );
}

public static Skill SkillB()
{
    return SkillFactory.Create(
        "b", "d", "i",
        SkillA // Now valid: one-way reference
    );
}
```

**Option 2: Extract common functions to a base skill**
```csharp
public static Skill CommonFunctions()
{
    return SkillFactory.Create(
        "common",
        "Common functions",
        "Shared utilities",
        FileSystemPlugin.ReadFile,
        FileSystemPlugin.WriteFile
    );
}

public static Skill SkillA()
{
    return SkillFactory.Create("a", "d", "i", CommonFunctions);
}

public static Skill SkillB()
{
    return SkillFactory.Create("b", "d", "i", CommonFunctions);
}
```

**Note:** Circular references are detected at compile-time, not runtime. The error message shows the full circular chain:
```
HPD103: Circular reference detected: SkillA -> SkillB -> SkillA
```

---

### Warning: "No function or skill references provided" (HPD104)

**Problem:**
```csharp
public static Skill EmptySkill()
{
    return SkillFactory.Create("empty", "desc", "instr"); // Warning: No references
}
```

**When This Is OK:**
- Instruction-only skills that provide guidance without tools
- Skills that dynamically reference other skills

**When This Is a Problem:**
- Intended to reference functions but forgot to add them

**Solution (if unintended):**
Add function references:
```csharp
public static Skill EmptySkill()
{
    return SkillFactory.Create(
        "empty",
        "desc",
        "instr",
        FileSystemPlugin.ReadFile // Added reference
    );
}
```

**Solution (if intentional):**
Suppress warning:
```csharp
#pragma warning disable HPD104
public static Skill EmptySkill()
{
    return SkillFactory.Create("empty", "desc", "instr");
}
#pragma warning restore HPD104
```

---

## Runtime Issues

### Skill Not Appearing in Tools List

**Symptoms:**
- Skill method defined correctly
- No compilation errors
- Skill doesn't appear when agent starts

**Possible Causes & Solutions:**

#### 1. Plugin Not Registered

**Problem:**
```csharp
public static partial class DevelopmentSkills
{
    public static Skill FileDebugging() { ... }
}

// Agent setup
var builder = Agent.CreateBuilder();
// Forgot to add DevelopmentSkills
var agent = builder.Build();
```

**Solution:**
Register the plugin containing the skill:
```csharp
var builder = Agent.CreateBuilder();
builder.AddPlugin<DevelopmentSkills>(); // Add this
var agent = builder.Build();
```

---

#### 2. Skill Is Scoped But Not Activated

**Problem:**
Skill with `ScopingMode = Scoped` doesn't show functions until activated.

**Expected Behavior:**
- Initial tools: `[file_debugging_container]`
- After activation: `[file_debugging, ReadFile, WriteFile]`

**Solution:**
This is correct behavior. Agent must activate the skill first:
```
Agent: Uses file_debugging_container
System: "file_debugging skill activated. You now have access to..."
Agent: Now sees file_debugging and all referenced functions
```

---

#### 3. Source Generator Didn't Run

**Problem:**
Source generator failed to generate code.

**Check:**
1. Build output for generator errors
2. Generated code in `obj/Debug/net8.0/generated/` folder

**Solution:**
```bash
# Clean and rebuild
dotnet clean
dotnet build

# Check for generator errors
dotnet build -v detailed
```

---

### Skill Activates But Functions Still Hidden

**Symptoms:**
- Agent activates skill successfully
- Referenced functions still don't appear

**Possible Causes:**

#### 1. Functions Are Scoped in Plugin

**Problem:**
```csharp
// Plugin definition
public class FileSystemPlugin
{
    [AIFunction]
    [Description("Read file")]
    public static string ReadFile() { ... } // Scoped plugin function
}

// Skill definition
public static Skill FileDebugging()
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug files",
        "Instructions...",
        FileSystemPlugin.ReadFile // References scoped function
    );
}
```

**Solution:**
Activate both the skill AND the plugin:
```csharp
// Agent must do:
1. file_debugging_container (if Scoped skill)
2. FileSystemPlugin_container (if scoped plugin)
3. file_debugging (to get instructions)
4. ReadFile (now visible)
```

**Better Solution:**
If skill should reveal functions, make referenced functions non-scoped or use default scoped mode:
```csharp
public static Skill FileDebugging()
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug files",
        "Instructions...",
        new SkillOptions { // Skills are always scoped by default},
        FileSystemPlugin.ReadFile // Functions always visible
    );
}
```

---

### Referenced Plugin Not Auto-Registered

**Symptoms:**
- Skill references a plugin
- Plugin not automatically registered
- Agent can't find referenced functions

**Possible Causes:**

#### 1. Plugin Type Not Found

**Check Log:**
```
[WARN] Cannot auto-register plugin 'FileSystemPlugin' (referenced by DevelopmentSkills):
       Type not found in loaded assemblies
```

**Solution:**
Manually register the plugin:
```csharp
var builder = Agent.CreateBuilder();
builder.AddPlugin<DevelopmentSkills>();
builder.AddPlugin<FileSystemPlugin>(); // Manual registration
var agent = builder.Build();
```

**Or ensure plugin assembly is referenced:**
```xml
<ItemGroup>
  <ProjectReference Include="..\PluginProject\PluginProject.csproj" />
</ItemGroup>
```

---

#### 2. Multiple Plugins With Same Name

**Problem:**
Two plugins in different namespaces have the same name.

**Check:**
```csharp
namespace PluginsV1 { public class FileSystemPlugin { } }
namespace PluginsV2 { public class FileSystemPlugin { } }
```

**Solution:**
Auto-registration picks the first match. To control which one:
```csharp
// Manual registration
var builder = Agent.CreateBuilder();
builder.AddPlugin<DevelopmentSkills>();
builder.AddPlugin<PluginsV2.FileSystemPlugin>(); // Specify namespace
var agent = builder.Build();
```

---

### Skill Instructions Not Showing

**Symptoms:**
- Skill activated successfully
- Instructions not appearing in agent context

**Possible Causes:**

#### 1. Instructions Are Null or Empty

**Problem:**
```csharp
return SkillFactory.Create(
    "skill",
    "description",
    null, // No instructions
    FileSystemPlugin.ReadFile
);
```

**Solution:**
Provide instructions:
```csharp
return SkillFactory.Create(
    "skill",
    "description",
    @"Detailed instructions here:

1. First step
2. Second step
3. Third step",
    FileSystemPlugin.ReadFile
);
```

---

#### 2. Skill Function Not Being Called

**Problem:**
Agent activates container but doesn't call the skill function itself.

**Expected Flow (Scoped):**
1. Agent calls `skill_container()` → Skill activated
2. Agent calls `skill()` → Instructions added to context

**Solution:**
Ensure your instructions are clear that agent should call the skill function:
```csharp
[AIFunction]
[Description("Activate file_debugging skill to debug file issues")]
public static string file_debugging_container()
{
    return "Skill activated. Call 'file_debugging' to see detailed instructions.";
}
```

---

## Source Generator Problems

### Generated Code Not Appearing

**Symptoms:**
- Skill defined correctly
- Build succeeds
- No generated code in output

**Solutions:**

#### 1. Check Generator Output

```bash
# Build with detailed logging
dotnet build -v detailed

# Look for lines like:
# Source generator 'HPDPluginSourceGenerator' generated file '...'
```

---

#### 2. Verify Partial Class

**Problem:**
```csharp
public static class DevelopmentSkills // Error: Missing 'partial'
{
    public static Skill FileDebugging() { ... }
}
```

**Solution:**
Add `partial` keyword:
```csharp
public static partial class DevelopmentSkills
{
    public static Skill FileDebugging() { ... }
}
```

---

#### 3. Check Build Action

Ensure source generator project is correctly referenced:

```xml
<!-- In HPD-Agent.csproj -->
<ItemGroup>
  <ProjectReference Include="..\HPD-Agent.SourceGenerator\HPD-Agent.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

---

### Generated Code Has Compilation Errors

**Symptoms:**
- Source generator runs
- Generated code doesn't compile
- Errors in `obj/Debug/net8.0/generated/` files

**Solutions:**

#### 1. Check Generated File

Navigate to generated code:
```
obj/Debug/net8.0/generated/HPD-Agent.SourceGenerator/HPDPluginSourceGenerator/
```

Look for errors in generated methods.

---

#### 2. Report Issue

If generated code is malformed, this is a source generator bug. Report with:
- Skill definition code
- Generated code (from `obj/` folder)
- Build output

---

### GetReferencedPlugins() Returns Wrong Plugins

**Symptoms:**
- Source generator creates `GetReferencedPlugins()`
- Returns incorrect plugin names

**Debugging:**

Check generated code:
```csharp
// Should see:
public static string[] GetReferencedPlugins()
{
    return new[]
    {
        "FileSystemPlugin", // All referenced plugins
        "GitPlugin"
    };
}
```

If incorrect:
1. Verify skill references are correct
2. Check for namespace issues
3. Rebuild from clean state: `dotnet clean && dotnet build`

---

## Auto-Registration Issues

### Plugin Not Found During Auto-Registration

**Error Message:**
```
[WARN] Cannot auto-register plugin 'FileSystemPlugin' (referenced by DevelopmentSkills):
       Type not found in loaded assemblies
```

**Solutions:**

#### 1. Ensure Assembly Is Referenced

```xml
<ItemGroup>
  <ProjectReference Include="..\FileSystem\FileSystem.csproj" />
</ItemGroup>
```

---

#### 2. Check Plugin Name Matches

Verify plugin type name matches exactly:

```csharp
// Skill references "FileSystemPlugin"
return SkillFactory.Create(..., FileSystemPlugin.ReadFile);

// Plugin class must be named exactly "FileSystemPlugin"
public class FileSystemPlugin // Must match
{
    [AIFunction]
    public static string ReadFile() { ... }
}
```

---

#### 3. Manual Registration Fallback

If auto-registration fails, register manually:

```csharp
var builder = Agent.CreateBuilder();
builder.AddPlugin<DevelopmentSkills>();

// Manual fallback
builder.AddPlugin<FileSystemPlugin>();
builder.AddPlugin<GitPlugin>();

var agent = builder.Build();
```

---

### Auto-Registration Registers Wrong Plugin

**Problem:**
Multiple plugins with same name, wrong one gets registered.

**Solution:**
Use manual registration with fully qualified name:

```csharp
builder.AddPlugin<PluginsV2.FileSystemPlugin>(); // Specify namespace
```

---

## Scoping Behavior

### Functions Always Visible (Expected Scoped)

**Problem:**
Skill has `ScopingMode = Scoped` but functions appear immediately.

**Possible Causes:**

#### 1. Options Not Applied

**Check:**
```csharp
// Wrong: options parameter not used
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug files",
        "Instructions...",
        // Missing options parameter here!
        FileSystemPlugin.ReadFile
    );
}
```

**Solution:**
```csharp
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug files",
        "Instructions...",
        options ?? new SkillOptions { // Skills are always scoped by default},
        FileSystemPlugin.ReadFile
    );
}
```

---

#### 2. Functions Are Non-Scoped

Referenced functions themselves are non-scoped in their plugin:

```csharp
// FileSystemPlugin
[AIFunction]
public static string ReadFile() { ... } // Non-scoped function
```

Even if skill is scoped, non-scoped plugin functions are always visible.

**Solution:**
This is expected behavior. Skill scoping only controls skill activation, not referenced function scoping.

---

### Functions Hidden (Expected Visible)

**Problem:**
Skill has `ScopingMode = InstructionOnly` but functions are hidden.

**Possible Causes:**

#### 1. Referenced Plugin Is Scoped

```csharp
// FileSystemPlugin is scoped
public class FileSystemPlugin { } // Has scoped functions

// Skill references scoped plugin
public static Skill FileDebugging()
{
    return SkillFactory.Create(
        "file_debugging",
        "Debug files",
        "Instructions...",
        new SkillOptions { // Skills are always scoped by default},
        FileSystemPlugin.ReadFile // Scoped in plugin
    );
}
```

**Solution:**
Activate the plugin first:
```
Agent: Uses FileSystemPlugin_container
Agent: Now ReadFile is visible
```

Or make plugin functions non-scoped.

---

## Performance Issues

### Build Time Too Long

**Symptoms:**
- Source generator adds significant build time
- Rebuild takes several seconds

**Solutions:**

#### 1. Enable Incremental Build

Source generator should support incremental builds. Verify in generator code:

```csharp
[Generator]
public class HPDPluginSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Incremental pipeline
    }
}
```

---

#### 2. Reduce Skill Complexity

Very deeply nested skills can slow down resolution:

```csharp
// Avoid:
SkillA -> SkillB -> SkillC -> SkillD -> SkillE (5 levels)

// Prefer:
SkillA -> Functions (direct references)
SkillB -> SkillA (1 level nesting)
```

---

### Runtime Performance Degradation

**Symptoms:**
- Agent tool selection is slow
- High CPU usage during tool filtering

**Solutions:**

#### 1. Reduce Total Tool Count

Too many skills/functions can slow down tool selection:

```csharp
// Avoid:
builder.AddPlugin<Skills1>(); // 50 functions
builder.AddPlugin<Skills2>(); // 50 functions
builder.AddPlugin<Skills3>(); // 50 functions
// 150 total tools!

// Prefer:
builder.AddPlugin<EssentialSkills>(); // 20 functions
// Only add specialized skills when needed
```

---

#### 2. Use Scoped Mode

Scoped skills reduce tool count per turn:

```csharp
// InstructionOnly: All 50 functions always visible
public static Skill AllFunctions()
{
    return SkillFactory.Create(
        "all_functions",
        "All functions",
        "Instructions...",
        new SkillOptions { // Skills are always scoped by default},
        // 50 function references
    );
}

// Scoped: Only 1 container initially, then 50 after activation
public static Skill AllFunctions()
{
    return SkillFactory.Create(
        "all_functions",
        "All functions",
        "Instructions...",
        new SkillOptions { // Skills are always scoped by default},
        // 50 function references
    );
}
```

---

## Debugging Techniques

### Enable Source Generator Logging

Add to `HPD-Agent.csproj`:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

Generated files appear in `obj/Generated/`.

---

### View Generated Code

Navigate to generated code:

```bash
cd obj/Debug/net8.0/generated/HPD-Agent.SourceGenerator/HPDPluginSourceGenerator/

# View generated files
ls -la
cat DevelopmentSkills.g.cs
```

---

### Check Metadata at Runtime

Inspect function metadata during debugging:

```csharp
foreach (var tool in agent.Tools)
{
    Console.WriteLine($"Tool: {tool.Name}");

    if (tool.AdditionalProperties != null)
    {
        foreach (var kvp in tool.AdditionalProperties)
        {
            Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
        }
    }
}

// Output:
// Tool: file_debugging_container
//   IsContainer: True
//   SkillName: file_debugging
//   ScopingMode: Scoped
```

---

### Trace Skill Activation

Add logging to track skill activation:

```csharp
var builder = Agent.CreateBuilder()
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Debug);
    });

// Logs will show:
// [DEBUG] Skill 'file_debugging' activated
// [DEBUG] Adding functions: ReadFile, WriteFile, ListDirectory
```

---

### Test Skill Isolation

Test a skill in isolation:

```csharp
// Create agent with ONLY the skill under test
var builder = Agent.CreateBuilder();
builder.AddPlugin<TestSkillsClass>();
// Do NOT add other plugins

var agent = builder.Build();

// Verify tools
foreach (var tool in agent.Tools)
{
    Console.WriteLine(tool.Name);
}

// Should see only skills from TestSkillsClass
```

---

### Validate Function References

Manually verify skill references resolve correctly:

```csharp
var skill = DevelopmentSkills.FileDebugging();

Console.WriteLine($"Skill: {skill.Name}");
Console.WriteLine($"References: {skill.References.Length}");

foreach (var reference in skill.References)
{
    Console.WriteLine($"  - {reference.Method.DeclaringType?.Name}.{reference.Method.Name}");
}

// Output:
// Skill: file_debugging
// References: 3
//   - FileSystemPlugin.ReadFile
//   - FileSystemPlugin.WriteFile
//   - FileSystemPlugin.ListDirectory
```

---

### Reproduce Minimal Case

If encountering a complex issue, create minimal reproduction:

```csharp
// MinimalReproduction.cs
public static partial class MinimalSkills
{
    public static Skill TestSkill(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "test_skill",
            "Test skill description",
            "Test instructions",
            options,
            FileSystemPlugin.ReadFile
        );
    }
}

// Test
var builder = Agent.CreateBuilder();
builder.AddPlugin<MinimalSkills>();
var agent = builder.Build();

// Does it work? If yes, issue is in more complex setup
// If no, report minimal case as bug
```

---

## Common Gotchas

### 1. Forgetting Partial Keyword

```csharp
// Wrong:
public static class MySkills { }

// Correct:
public static partial class MySkills { }
```

---

### 2. Mixing Options Position

```csharp
// Wrong:
SkillFactory.Create(
    "name",
    "desc",
    "instr",
    Function1,
    new SkillOptions { ... }, // Options AFTER references
    Function2
);

// Correct:
SkillFactory.Create(
    "name",
    "desc",
    "instr",
    new SkillOptions { ... }, // Options BEFORE references
    Function1,
    Function2
);
```

---

### 3. Referencing Non-Static Methods

```csharp
public class MyPlugin
{
    [AIFunction]
    public string InstanceMethod() { ... } // Instance method
}

// Wrong:
SkillFactory.Create(
    "skill",
    "desc",
    "instr",
    MyPlugin.InstanceMethod // Error: Can't reference instance method
);
```

**Solution:**
Only static methods or instance methods from registered plugin instances work.

---

### 4. Assuming Options Override

```csharp
var skill = DevelopmentSkills.FileDebugging(
    new SkillOptions { // Skills are always scoped by default}
);

// If FileDebugging() ignores options parameter:
public static Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "file_debugging",
        "desc",
        "instr",
        new SkillOptions { // Skills are always scoped by default}, // Hardcoded!
        Functions...
    );
}

// Passed options are ignored!
```

**Solution:**
Always use `options ?? defaultOptions` pattern:
```csharp
return SkillFactory.Create(
    "file_debugging",
    "desc",
    "instr",
    options ?? new SkillOptions { // Skills are always scoped by default},
    Functions...
);
```

---

## Getting Help

### Check Documentation

1. [Skills Guide](SKILLS_GUIDE.md) - Usage guide
2. [API Reference](SKILLS_API_REFERENCE.md) - Complete API
3. [Diagnostics](../SKILL_DIAGNOSTICS.md) - Error codes

---

### Report Issues

When reporting issues, include:

1. **Skill Definition:**
   ```csharp
   public static Skill MySkill() { ... }
   ```

2. **Generated Code** (from `obj/Generated/`):
   ```csharp
   public static string[] GetReferencedPlugins() { ... }
   ```

3. **Build Output:**
   ```
   dotnet build -v detailed > build.log 2>&1
   ```

4. **Runtime Logs:**
   ```csharp
   builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
   ```

5. **Expected vs Actual Behavior:**
   - Expected: "Skill should show 3 functions"
   - Actual: "Skill shows 0 functions"

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2025-10-26 | Initial troubleshooting guide |

---

## See Also

- [Skills Guide](SKILLS_GUIDE.md) - Complete usage guide
- [API Reference](SKILLS_API_REFERENCE.md) - API documentation
- [Diagnostics](../SKILL_DIAGNOSTICS.md) - Diagnostic codes

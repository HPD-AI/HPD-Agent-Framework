# HPD-Agent Plugin Architecture

**Version:** 1.0.0
**Last Updated:** 2025-11-27
**Audience:** Framework developers and contributors

---

## Table of Contents

1. [Overview](#overview)
2. [Unified Plugin Model](#unified-plugin-model)
3. [Source Generator Pipeline](#source-generator-pipeline)
4. [Plugin Capabilities](#plugin-capabilities)
5. [Registration Flow](#registration-flow)
6. [Runtime Behavior](#runtime-behavior)
7. [Extensibility](#extensibility)
8. [Key Design Decisions](#key-design-decisions)

---

## Overview

The HPD-Agent plugin system is built on three core principles:

1. **Compile-time generation** - Registration code is generated at build time, not discovered at runtime
2. **Unified container model** - All capability types (AIFunctions, Skills, SubAgents) live in plugin classes
3. **Extensible by design** - Adding new capability types requires minimal changes

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        BUILD TIME                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Plugin Source (.cs)     HPDPluginSourceGenerator               │
│  ┌──────────────────┐    ┌────────────────────────────┐         │
│  │ [AIFunction]     │───▶│ Syntax Analysis            │         │
│  │ [Skill]          │    │ ├─ AIFunctionAnalyzer      │         │
│  │ [SubAgent]       │    │ ├─ SkillAnalyzer           │         │
│  │ [Scope]          │    │ └─ SubAgentAnalyzer        │         │
│  └──────────────────┘    └─────────────┬──────────────┘         │
│                                        │                         │
│                                        ▼                         │
│                          ┌────────────────────────────┐         │
│                          │ PluginInfo                 │         │
│                          │ ├─ Functions[]             │         │
│                          │ ├─ Skills[]                │         │
│                          │ ├─ SubAgents[]             │         │
│                          │ └─ RequiresInstance        │         │
│                          └─────────────┬──────────────┘         │
│                                        │                         │
│                                        ▼                         │
│                          ┌────────────────────────────┐         │
│                          │ Code Generation            │         │
│                          │ ├─ FunctionCodeGenerator   │         │
│                          │ ├─ SkillCodeGenerator      │         │
│                          │ └─ SubAgentCodeGenerator   │         │
│                          └─────────────┬──────────────┘         │
│                                        │                         │
│                                        ▼                         │
│                          ┌────────────────────────────┐         │
│                          │ {Plugin}Registration.g.cs  │         │
│                          │ └─ CreatePlugin() method   │         │
│                          └────────────────────────────┘         │
│                                                                  │
├─────────────────────────────────────────────────────────────────┤
│                        RUNTIME                                   │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  AgentBuilder                  PluginManager                    │
│  ┌──────────────────┐         ┌────────────────────────┐        │
│  │ .WithPlugin<T>() │────────▶│ RegisterPlugin<T>()    │        │
│  └──────────────────┘         │ └─ PluginRegistration  │        │
│                               └─────────────┬──────────┘        │
│                                             │                    │
│                                             ▼                    │
│                               ┌────────────────────────┐        │
│                               │ CreateAllFunctions()   │        │
│                               │ └─ Calls generated     │        │
│                               │    Registration class  │        │
│                               └─────────────┬──────────┘        │
│                                             │                    │
│                                             ▼                    │
│                               ┌────────────────────────┐        │
│                               │ List<AIFunction>       │        │
│                               │ (All capabilities as   │        │
│                               │  AIFunction instances) │        │
│                               └────────────────────────┘        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Unified Plugin Model

### Core Insight

A plugin is simply a container class. The system doesn't distinguish between "function plugins", "skill classes", or "agent plugins" - they're all plugins that can contain any mix of capabilities.

### PluginInfo Model

```csharp
internal class PluginInfo
{
    public string Name { get; set; }
    public string Namespace { get; set; }
    public List<FunctionInfo> Functions { get; set; }
    public List<SkillInfo> Skills { get; set; }
    public List<SubAgentInfo> SubAgents { get; set; }
    public bool HasScopeAttribute { get; set; }
    public string? ScopeDescription { get; set; }

    // Key extensibility point:
    // Adding a new capability type? Just add it here and update RequiresInstance
    public bool RequiresInstance => Functions.Any() || SubAgents.Any();
}
```

### RequiresInstance Logic

The `RequiresInstance` property determines whether the generated `CreatePlugin()` method needs an instance parameter:

| Capability | Needs Instance? | Reason |
|------------|-----------------|--------|
| AIFunctions | ✅ Yes | `instance.Method()` called at runtime |
| SubAgents | ✅ Yes | `instance.Method()` called to get config |
| Skills | ❌ No | Metadata extracted at compile time only |

```csharp
// Generated for plugins with instance-requiring capabilities
public static List<AIFunction> CreatePlugin(
    MyPlugin instance,  // Instance parameter included
    IPluginMetadataContext? context = null)

// Generated for skills-only plugins
public static List<AIFunction> CreatePlugin(
    IPluginMetadataContext? context = null)  // No instance needed
```

---

## Source Generator Pipeline

### 1. Syntax Predicate (Fast Filter)

The generator first does a quick syntax check to find candidate classes:

```csharp
// HPDPluginSourceGenerator.cs
static bool IsSyntaxTargetForGeneration(SyntaxNode node, CancellationToken _)
{
    if (node is not ClassDeclarationSyntax classDecl)
        return false;

    // Check for [AIFunction], [Skill], or [SubAgent] methods
    foreach (var member in classDecl.Members.OfType<MethodDeclarationSyntax>())
    {
        foreach (var attr in member.AttributeLists.SelectMany(al => al.Attributes))
        {
            var name = attr.Name.ToString();
            if (name.Contains("AIFunction") ||
                name.Contains("Skill") ||
                name.Contains("SubAgent"))
                return true;
        }
    }
    return false;
}
```

### 2. Semantic Transform (Detailed Analysis)

For each candidate, detailed analysis extracts capability info:

```csharp
static PluginInfo? GetSemanticTargetForGeneration(
    GeneratorSyntaxContext context,
    CancellationToken cancellationToken)
{
    var classDecl = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Analyze each capability type
    var functions = AIFunctionAnalyzer.AnalyzeFunctions(classDecl, semanticModel);
    var skills = SkillAnalyzer.AnalyzeSkills(classDecl, semanticModel);
    var subAgents = SubAgentAnalyzer.AnalyzeSubAgents(classDecl, semanticModel);

    if (!functions.Any() && !skills.Any() && !subAgents.Any())
        return null;

    return new PluginInfo
    {
        Name = classDecl.Identifier.ValueText,
        Functions = functions,
        Skills = skills,
        SubAgents = subAgents,
        // ... other properties
    };
}
```

### 3. Code Generation

Each capability type has its own code generator:

```csharp
// Generate registration code
var sb = new StringBuilder();

// Functions
foreach (var function in plugin.Functions)
{
    sb.Append(FunctionCodeGenerator.Generate(function, plugin.Name));
}

// Skills
foreach (var skill in plugin.Skills)
{
    sb.Append(SkillCodeGenerator.Generate(skill, plugin.Name));
}

// SubAgents
foreach (var subAgent in plugin.SubAgents)
{
    sb.Append(SubAgentCodeGenerator.Generate(subAgent, plugin.Name));
}
```

---

## Plugin Capabilities

### AI Functions

**Analysis:** `AIFunctionAnalyzer.cs`
**Generation:** `FunctionCodeGenerator.cs`

AIFunctions are methods marked with `[AIFunction]` that become tools the agent can call.

```csharp
// Source
[AIFunction]
[AIDescription("Adds two numbers")]
public int Add(int a, int b) => a + b;

// Generated (simplified)
functions.Add(HPDAIFunctionFactory.Create(
    (args, ct) => instance.Add(
        args.GetValue<int>("a"),
        args.GetValue<int>("b")),
    new AIFunctionMetadata("Add")
    {
        Description = "Adds two numbers",
        Parameters = { /* parameter schemas */ }
    }));
```

### Skills

**Analysis:** `SkillAnalyzer.cs`
**Generation:** `SkillCodeGenerator.cs`

Skills are workflow containers analyzed at compile time. The method body is parsed (not executed) to extract metadata.

```csharp
// Source
[Skill]
public Skill MySkill() => SkillFactory.Create(
    "MySkill",
    "Description",
    "Instructions",
    "Plugin.Function1",
    "Plugin.Function2");

// Generated (simplified) - note: method is never called at runtime
functions.Add(HPDAIFunctionFactory.Create(
    (args, ct) => Task.FromResult("MySkill activated. Functions: Function1, Function2"),
    new AIFunctionMetadata("MySkill")
    {
        Description = "Description",
        AdditionalProperties = {
            ["IsSkill"] = true,
            ["ReferencedFunctions"] = new[] { "Plugin.Function1", "Plugin.Function2" }
        }
    }));
```

### SubAgents

**Analysis:** `SubAgentAnalyzer.cs`
**Generation:** `SubAgentCodeGenerator.cs`

SubAgents create callable child agents. Unlike Skills, the method IS called at runtime to get the agent configuration.

```csharp
// Source
[SubAgent]
public SubAgent Expert() => SubAgentFactory.Create(
    "Expert",
    "Domain expert",
    new AgentConfig { ... });

// Generated (simplified)
functions.Add(HPDAIFunctionFactory.Create(
    async (args, ct) => {
        var subAgentDef = instance.Expert();  // Method called at runtime
        var agent = new AgentBuilder(subAgentDef.AgentConfig).Build();
        return await agent.Run(args.GetValue<string>("query"), ct);
    },
    new AIFunctionMetadata("Expert")
    {
        Description = "Domain expert",
        AdditionalProperties = { ["IsSubAgent"] = true }
    }));
```

---

## Registration Flow

### Compile Time

```
Source Code
    │
    ▼
┌───────────────────────────────────────┐
│ HPDPluginSourceGenerator              │
│   1. Find classes with attributes     │
│   2. Analyze capabilities             │
│   3. Generate Registration classes    │
└───────────────────────────────────────┘
    │
    ▼
{PluginName}Registration.g.cs
```

### Runtime

```
AgentBuilder.WithPlugin<T>()
    │
    ▼
PluginManager.RegisterPlugin<T>()
    │
    ▼
PluginRegistration.FromType<T>()
    │
    ▼
PluginRegistration.ToAIFunctions()
    │
    ▼
Reflection: Find {PluginName}Registration class
    │
    ▼
Call generated CreatePlugin() method
    │
    ▼
List<AIFunction> returned
```

### Key Code Path

```csharp
// PluginRegistration.ToAIFunctions()
public List<AIFunction> ToAIFunctions(IPluginMetadataContext? context = null)
{
    // Find generated registration class
    var registrationTypeName = $"{PluginType.Name}Registration";
    var registrationType = PluginType.Assembly
        .GetTypes()
        .FirstOrDefault(t => t.Name == registrationTypeName);

    if (registrationType == null)
        throw new InvalidOperationException(
            $"Generated registration class {registrationTypeName} not found.");

    // Find CreatePlugin method
    var createMethod = registrationType.GetMethod("CreatePlugin");

    // Invoke with appropriate parameters
    if (RequiresInstance)
    {
        var instance = GetOrCreateInstance();
        return (List<AIFunction>)createMethod.Invoke(null, new[] { instance, context });
    }
    else
    {
        return (List<AIFunction>)createMethod.Invoke(null, new[] { context });
    }
}
```

---

## Runtime Behavior

### All Capabilities Become AIFunctions

At runtime, everything is an `AIFunction`:

```csharp
List<AIFunction> functions = pluginManager.CreateAllFunctions();

// functions contains:
// - AIFunctions from [AIFunction] methods
// - Container functions from [Skill] methods
// - Wrapper functions from [SubAgent] methods

// They're differentiated by AdditionalProperties:
foreach (var f in functions)
{
    if (f.AdditionalProperties.TryGetValue("IsSkill", out var isSkill) && (bool)isSkill)
        // This is a skill container
    else if (f.AdditionalProperties.TryGetValue("IsSubAgent", out var isSub) && (bool)isSub)
        // This is a sub-agent wrapper
    else
        // This is a regular AI function
}
```

### Scoping Integration

Scoped plugins integrate with `ToolVisibilityManager`:

```csharp
// Container function for scoped plugin
functions.Add(HPDAIFunctionFactory.Create(
    (args, ct) => {
        // Mark container as expanded
        return "Plugin expanded. Available functions: ...";
    },
    new AIFunctionMetadata($"expand_{pluginName}")
    {
        Description = scopeDescription,
        AdditionalProperties = {
            ["IsContainer"] = true,
            ["ContainerType"] = "Plugin",
            ["ContainedFunctions"] = containedFunctionNames
        }
    }));
```

---

## Extensibility

### Adding a New Capability Type

To add a new capability type (e.g., `[Workflow]`):

1. **Create the attribute:**
```csharp
[AttributeUsage(AttributeTargets.Method)]
public class WorkflowAttribute : Attribute
{
    public string? Category { get; set; }
}
```

2. **Create the analyzer:**
```csharp
// WorkflowAnalyzer.cs
internal static class WorkflowAnalyzer
{
    public static List<WorkflowInfo> AnalyzeWorkflows(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel) { ... }
}
```

3. **Create the code generator:**
```csharp
// WorkflowCodeGenerator.cs
internal static class WorkflowCodeGenerator
{
    public static string Generate(WorkflowInfo workflow, string pluginName) { ... }
}
```

4. **Update PluginInfo:**
```csharp
internal class PluginInfo
{
    // ... existing properties
    public List<WorkflowInfo> Workflows { get; set; } = new();

    // Update if workflows need instance access
    public bool RequiresInstance =>
        Functions.Any() || SubAgents.Any() || Workflows.Any();
}
```

5. **Update HPDPluginSourceGenerator:**
```csharp
// In syntax predicate
if (name.Contains("Workflow")) return true;

// In semantic transform
var workflows = WorkflowAnalyzer.AnalyzeWorkflows(classDecl, semanticModel);
pluginInfo.Workflows = workflows;

// In code generation
foreach (var workflow in plugin.Workflows)
{
    sb.Append(WorkflowCodeGenerator.Generate(workflow, plugin.Name));
}
```

### The RequiresInstance Pattern

This is the key extensibility point. When adding a new capability:

1. **Does it need to call instance methods at runtime?**
   - Yes → Add to `RequiresInstance` check
   - No → Don't add (like Skills - analyzed at compile time only)

```csharp
public bool RequiresInstance =>
    Functions.Any() ||      // Calls instance.Method()
    SubAgents.Any() ||      // Calls instance.Method() for config
    Workflows.Any();        // If workflows need instance access
    // Skills NOT included - compile-time only
```

---

## Key Design Decisions

### 1. Why Source Generation?

**Alternative:** Runtime reflection to discover plugins

**Chosen:** Compile-time source generation

**Reasons:**
- Zero startup cost
- Compile-time error detection
- Better tree-shaking for AOT/trimming
- No reflection permission requirements

### 2. Why Unified Plugin Model?

**Alternative:** Separate `IFunctionPlugin`, `ISkillProvider`, `ISubAgentHost` interfaces

**Chosen:** Single container class with attributes

**Reasons:**
- Simpler mental model
- Flexible composition
- Single registration path
- Easier to extend

### 3. Why RequiresInstance Property?

**Alternative:** Track each capability's instance needs separately

**Chosen:** Single boolean computed from capabilities

**Reasons:**
- Single decision point
- Easy to update when adding capabilities
- Clear semantic meaning
- Prevents combinatorial explosion of boolean flags

### 4. Why Skills Don't Need Instance?

**Alternative:** Call skill methods at runtime like SubAgents

**Chosen:** Parse skill method bodies at compile time

**Reasons:**
- Skills are static definitions (name, description, references)
- No runtime state needed
- Allows skills in static methods
- Reduces runtime overhead

---

## File Reference

| File | Purpose |
|------|---------|
| `HPDPluginSourceGenerator.cs` | Main source generator entry point |
| `PluginInfo.cs` | Plugin metadata model |
| `AIFunctionAnalyzer.cs` | Analyzes `[AIFunction]` methods |
| `SkillAnalyzer.cs` | Analyzes `[Skill]` methods |
| `SubAgentAnalyzer.cs` | Analyzes `[SubAgent]` methods |
| `FunctionCodeGenerator.cs` | Generates AIFunction registration code |
| `SkillCodeGenerator.cs` | Generates Skill container code |
| `SubAgentCodeGenerator.cs` | Generates SubAgent wrapper code |
| `PluginRegistration.cs` | Runtime registration and invocation |

---

## See Also

- [User Guide](./USER_GUIDE.md) - Getting started with plugins
- [API Reference](./API_REFERENCE.md) - Complete API documentation
- [Skills Architecture](../SKILLS_ARCHITECTURE.md) - Deep-dive on skills
- [SubAgents Architecture](../SubAgents/ARCHITECTURE.md) - Deep-dive on sub-agents

# HPD-Agent Plugin API Reference

**Version:** 1.0.0
**Last Updated:** 2025-11-27

---

## Table of Contents

1. [Attributes](#attributes)
   - [AIFunctionAttribute](#aifunctionattribute)
   - [AIFunctionAttribute\<TContext\>](#aifunctionattributetcontext)
   - [AIDescriptionAttribute](#aidescriptionattribute)
   - [SkillAttribute](#skillattribute)
   - [SubAgentAttribute](#subagentattribute)
   - [ScopeAttribute](#scopeattribute)
2. [Classes](#classes)
   - [PluginManager](#pluginmanager)
   - [PluginRegistration](#pluginregistration)
3. [Extension Methods](#extension-methods)
   - [AgentBuilder Extensions](#agentbuilder-extensions)
4. [Interfaces](#interfaces)
   - [IPluginMetadataContext](#ipluginmetadatacontext)

---

## Attributes

### AIFunctionAttribute

Marks a method as an AI function that can be called by the agent.

**Namespace:** Global (no namespace)

**Syntax:**
```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIFunctionAttribute : Attribute
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string?` | Custom name for the function. If not specified, uses the method name. |
| `Description` | `string?` | Static description of the function. |

**Example:**
```csharp
[AIFunction]
[AIDescription("Calculates the sum of two numbers")]
public int Add(int a, int b) => a + b;

// With custom name
[AIFunction(Name = "calculator_add")]
public int Add(int a, int b) => a + b;
```

---

### AIFunctionAttribute\<TContext\>

Generic version of AIFunctionAttribute that enables compile-time validation and context-aware features.

**Namespace:** Global (no namespace)

**Syntax:**
```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIFunctionAttribute<TContext> : Attribute
    where TContext : IPluginMetadataContext
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `ContextType` | `Type` | The context type used by this function (read-only). |
| `Name` | `string?` | Custom name for the function. |

**Example:**
```csharp
[AIFunction<FileSystemContext>]
[AIDescription("Reads a file. Only available when {context.IsFileAccessEnabled}")]
public string ReadFile(string path) { ... }
```

---

### AIDescriptionAttribute

Provides a description for an AI function or parameter.

**Namespace:** Global (no namespace)

**Syntax:**
```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class AIDescriptionAttribute : Attribute
```

**Constructor:**
```csharp
public AIDescriptionAttribute(string description)
```

**Example:**
```csharp
[AIFunction]
[AIDescription("Searches the web for information")]
public async Task<string> WebSearch(
    [AIDescription("The search query")] string query,
    [AIDescription("Maximum number of results")] int maxResults = 10)
{ ... }
```

---

### SkillAttribute

Marks a method as a skill for source generator detection.

**Namespace:** Global (no namespace)

**Syntax:**
```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SkillAttribute : Attribute
```

**Example:**
```csharp
[Skill]
public Skill DataAnalysis() => SkillFactory.Create(
    "DataAnalysis",
    "Comprehensive data analysis workflow",
    "Instructions for the agent...",
    "DataPlugin.LoadData",
    "DataPlugin.ProcessData"
);
```

**See:** [Skills Guide](../skills/SKILLS_GUIDE.md) for complete documentation.

---

### SubAgentAttribute

Marks a method as a sub-agent for source generator detection.

**Namespace:** Global (no namespace)

**Syntax:**
```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SubAgentAttribute : Attribute
```

**Example:**
```csharp
[SubAgent]
public SubAgent CodeReviewer()
{
    return SubAgentFactory.Create(
        "CodeReviewer",
        "Expert code reviewer",
        new AgentConfig { ... });
}
```

**See:** [SubAgents Guide](../SubAgents/USER_GUIDE.md) for complete documentation.

---

### CollapseAttribute

Marks a plugin class as Collapse. Collapse plugins appear as containers until expanded.

**Namespace:** Global (no namespace)

**Syntax:**
```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CollapseAttribute : Attribute
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Description` | `string` | Description of the container shown to the agent. |
| `PostExpansionInstructions` | `string?` | Optional instructions shown after container expansion. |

**Constructors:**
```csharp
public CollapseAttribute(string description)
public CollapseAttribute(string description, string? postExpansionInstructions)
```

**Example:**
```csharp
[Collapse("File system operations for reading, writing, and managing files")]
public class FileSystemPlugin
{
    [AIFunction] public string ReadFile(string path) { ... }
    [AIFunction] public void WriteFile(string path, string content) { ... }
}

// With post-expansion instructions
[Collapse(
    "Database operations",
    @"Always use transactions:
      1. BeginTransaction
      2. Execute operations
      3. CommitTransaction or RollbackTransaction")]
public class DatabasePlugin { ... }
```

> **Note:** `[Scope]` is deprecated but still supported for backward compatibility. Use `[Collapse]` for new code.

---

## Classes

### PluginManager

Manager for plugin registrations and AIFunction creation.

**Namespace:** `HPD.Agent`

**Syntax:**
```csharp
public class PluginManager
```

**Constructors:**

| Constructor | Description |
|-------------|-------------|
| `PluginManager()` | Creates a new PluginManager with no default context. |
| `PluginManager(IPluginMetadataContext? defaultContext)` | Creates a PluginManager with a default context. |

**Methods:**

#### RegisterPlugin\<T\>()

Registers a plugin by type. Creates a new instance when needed.

```csharp
public PluginManager RegisterPlugin<T>() where T : class, new()
```

**Returns:** The PluginManager instance for method chaining.

**Example:**
```csharp
var manager = new PluginManager()
    .RegisterPlugin<FileSystemPlugin>()
    .RegisterPlugin<WebSearchPlugin>();
```

---

#### RegisterPlugin(Type)

Registers a plugin by runtime type.

```csharp
public PluginManager RegisterPlugin(Type pluginType)
```

**Parameters:**
- `pluginType`: The type of the plugin to register.

**Returns:** The PluginManager instance for method chaining.

---

#### RegisterPlugin\<T\>(T instance)

Registers a plugin instance (for dependency injection scenarios).

```csharp
public PluginManager RegisterPlugin<T>(T instance) where T : class
```

**Parameters:**
- `instance`: The plugin instance to register.

**Returns:** The PluginManager instance for method chaining.

**Example:**
```csharp
var dbPlugin = new DatabasePlugin(connectionString);
var manager = new PluginManager()
    .RegisterPlugin(dbPlugin);
```

---

#### RegisterPluginFunctions(Type, string[])

Registers specific functions from a plugin (selective registration).

```csharp
public PluginManager RegisterPluginFunctions(Type pluginType, string[] functionNames)
```

**Parameters:**
- `pluginType`: The plugin type containing the functions.
- `functionNames`: Array of function names to register.

**Returns:** The PluginManager instance for method chaining.

**Example:**
```csharp
var manager = new PluginManager()
    .RegisterPluginFunctions(
        typeof(FileSystemPlugin),
        new[] { "ReadFile", "WriteFile" });
```

---

#### CreateAllFunctions(IPluginMetadataContext?)

Creates all AIFunctions from registered plugins.

```csharp
[RequiresUnreferencedCode("This method calls plugin registration methods that use reflection.")]
public List<AIFunction> CreateAllFunctions(IPluginMetadataContext? context = null)
```

**Parameters:**
- `context`: Optional context for conditional function registration.

**Returns:** List of all AIFunction instances from registered plugins.

---

#### GetPluginRegistrations()

Gets all plugin registrations.

```csharp
public IReadOnlyList<PluginRegistration> GetPluginRegistrations()
```

---

#### GetRegisteredPluginTypes()

Gets all registered plugin types.

```csharp
public IReadOnlyList<Type> GetRegisteredPluginTypes()
```

---

#### Clear()

Removes all plugin registrations.

```csharp
public void Clear()
```

---

### PluginRegistration

Represents a plugin registration with support for type-based and instance-based registration.

**Namespace:** `HPD.Agent`

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `PluginType` | `Type` | The type of the registered plugin. |

**Static Methods:**

#### FromType\<T\>()

Creates a registration from a type.

```csharp
public static PluginRegistration FromType<T>() where T : class, new()
```

---

#### FromType(Type)

Creates a registration from a runtime type.

```csharp
public static PluginRegistration FromType(Type pluginType)
```

---

#### FromInstance\<T\>(T)

Creates a registration from an existing instance.

```csharp
public static PluginRegistration FromInstance<T>(T instance) where T : class
```

---

#### FromTypeFunctions(Type, string[])

Creates a registration for specific functions only.

```csharp
public static PluginRegistration FromTypeFunctions(Type pluginType, string[] functionNames)
```

---

**Instance Methods:**

#### ToAIFunctions(IPluginMetadataContext?)

Converts the registration to AIFunction instances.

```csharp
[RequiresUnreferencedCode("This method uses reflection to call generated plugin registration code.")]
public List<AIFunction> ToAIFunctions(IPluginMetadataContext? context = null)
```

---

#### GetOrCreateInstance()

Gets or creates the plugin instance.

```csharp
public object GetOrCreateInstance()
```

---

## Extension Methods

### AgentBuilder Extensions

Extensions for registering plugins with AgentBuilder.

**Namespace:** `HPD.Agent`

---

#### WithPlugin\<T\>()

Registers a plugin by type.

```csharp
public static AgentBuilder WithPlugin<T>(
    this AgentBuilder builder,
    IPluginMetadataContext? context = null) where T : class, new()
```

**Example:**
```csharp
var agent = new AgentBuilder()
    .WithPlugin<FileSystemPlugin>()
    .Build();
```

---

#### WithPlugin\<T\>(T instance)

Registers a plugin instance.

```csharp
public static AgentBuilder WithPlugin<T>(
    this AgentBuilder builder,
    T instance,
    IPluginMetadataContext? context = null) where T : class
```

**Example:**
```csharp
var plugin = new DatabasePlugin(connectionString);
var agent = new AgentBuilder()
    .WithPlugin(plugin)
    .Build();
```

---

#### WithPlugin(Type)

Registers a plugin by runtime type.

```csharp
public static AgentBuilder WithPlugin(
    this AgentBuilder builder,
    Type pluginType,
    IPluginMetadataContext? context = null)
```

---

#### WithPlugins(PluginManager)

Registers all plugins from a PluginManager.

```csharp
public static AgentBuilder WithPlugins(
    this AgentBuilder builder,
    PluginManager pluginManager,
    IPluginMetadataContext? context = null)
```

**Example:**
```csharp
var sharedPlugins = new PluginManager()
    .RegisterPlugin<CommonPlugin>()
    .RegisterPlugin<UtilityPlugin>();

var agent = new AgentBuilder()
    .WithPlugins(sharedPlugins)
    .Build();
```

---

## Interfaces

### IPluginMetadataContext

Interface for plugin metadata context used in conditional function registration and dynamic descriptions.

**Namespace:** `HPD.Agent`

**Syntax:**
```csharp
public interface IPluginMetadataContext
{
    // Implement properties for your specific context
}
```

**Example Implementation:**
```csharp
public class FileSystemContext : IPluginMetadataContext
{
    public bool IsFileAccessEnabled { get; set; } = true;
    public string[] AllowedPaths { get; set; } = Array.Empty<string>();
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB
}
```

**Usage with AIFunction:**
```csharp
[AIFunction<FileSystemContext>]
[AIDescription("Reads file contents. Max size: {context.MaxFileSizeBytes} bytes")]
[Conditional("context.IsFileAccessEnabled")]
public string ReadFile(string path) { ... }
```

---

## Source Generator

The HPD-Agent source generator automatically processes plugins at compile time and generates registration code. This provides:

- **Zero-reflection registration** - Functions are registered without runtime reflection
- **Compile-time validation** - Errors caught during build, not at runtime
- **Optimal performance** - No startup cost for discovering plugins

### Generated Classes

For each plugin, the source generator creates:

```csharp
// Generated: {PluginName}Registration.g.cs
public static class {PluginName}Registration
{
    public static List<AIFunction> CreatePlugin(
        {PluginName} instance,
        IPluginMetadataContext? context = null)
    {
        // Generated registration code
    }
}
```

### Troubleshooting Source Generation

If plugins aren't being detected:

1. Ensure the plugin class is `public`
2. Ensure methods have appropriate attributes
3. Clean and rebuild the project
4. Check build output for source generator warnings

---

## See Also

- [User Guide](./USER_GUIDE.md) - Getting started with plugins
- [Architecture](./ARCHITECTURE.md) - Technical deep-dive
- [AI Functions](./AIFUNCTIONS.md) - Detailed AI function documentation
- [Skills Guide](../skills/SKILLS_GUIDE.md) - Workflow containers
- [SubAgents Guide](../SubAgents/USER_GUIDE.md) - Specialized child agents

# Plugin Scoping

**Status**: ✅ Implemented & Working
**Version**: 2.1 (Added Post-Expansion Instructions)
**Last Updated**: 2025-01-17

## Overview

Plugin Scoping is an **opt-in** token optimization feature that organizes plugin functions behind container functions, reducing the initial tool list sent to the LLM by up to 87.5%. Instead of exposing all functions immediately, the agent presents only container functions initially. Individual functions become visible only after the agent invokes the container in a two-turn expansion flow.

**Supported Tool Sources**:
- **C# Plugins** - Functions marked with `[PluginScope]` attribute (source generator)
- **MCP Tools** - Tools from Model Context Protocol servers (runtime wrapper)
- **Frontend Tools** - AGUI frontend tools for human-in-the-loop interactions (runtime wrapper)

**Default**: Disabled (all functions visible immediately)
**To Enable**: Configure in `AgentConfig.PluginScoping`

## Problem Statement

### Token Consumption Challenge

When registering plugins with many functions, the tools list grows large:

```
Without Plugin Scoping (40 tools):
- CreateMemoryAsync
- UpdateMemoryAsync
- DeleteMemoryAsync
- Add
- Multiply
- Subtract
- Divide
- Square
- Min
- Max
- Abs
- ... (30+ more functions)
```

**Cost**: ~4,000 tokens per LLM call for tool definitions

### Solution: Hierarchical Organization

```
With Plugin Scoping (5 tools):
- CreateMemoryAsync
- UpdateMemoryAsync
- DeleteMemoryAsync
- MathPlugin [CONTAINER]
- FileSystemPlugin [CONTAINER]
```

**Cost**: ~500 tokens per LLM call (87.5% reduction)

After the agent calls `MathPlugin()`, the math functions expand and become available.

## Architecture

### Two-Turn Expansion Flow

```
┌─────────────────────────────────────────────────────────────┐
│ Turn 1: Initial State (Collapse)                           │
├─────────────────────────────────────────────────────────────┤
│ User: "Add 5 and 3"                                         │
│                                                              │
│ Tools visible to agent:                                      │
│  • MathPlugin [CONTAINER]                                   │
│  • CreateMemoryAsync                                         │
│  • UpdateMemoryAsync                                         │
│                                                              │
│ Agent invokes: MathPlugin()                                 │
│ Result: "✓ Expanded MathPlugin. Functions: Add, Multiply..." │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ Turn 2: Expanded State                                      │
├─────────────────────────────────────────────────────────────┤
│ Tools visible to agent:                                      │
│  • CreateMemoryAsync                                         │
│  • UpdateMemoryAsync                                         │
│  • Add [Plugin: MathPlugin]                                 │
│  • Multiply [Plugin: MathPlugin]                            │
│  • Subtract [Plugin: MathPlugin]                            │
│                                                              │
│ Agent invokes: Add(a: 5, b: 3)                              │
│ Result: 8                                                    │
└─────────────────────────────────────────────────────────────┘
```

### Container Results Not Persisted in History

**Important**: Container expansion results are **filtered out of the persistent chat history**. They are only visible to the LLM during the current message turn.

**Why?**
- Expansion state resets after each message turn (containers collapse)
- If the agent needs the plugin again next turn, it must re-invoke the container
- Keeping expansion messages in history would accumulate redundant data
- Post-expansion instructions would waste tokens on every subsequent message

**Implementation**:
```csharp
// In Agent.cs - after ExecuteToolsAsync
// Filter out container expansion results from persistent history
var nonContainerResults = toolResultMessage.Contents
    .Where(c => !IsContainerExpansionResult(c))
    .ToList();

currentMessages.Add(new ChatMessage(ChatRole.Tool, nonContainerResults)); // ← Only non-container results
turnHistory.Add(toolResultMessage); // ← LLM sees ALL results within current turn
```

**Result**: Container expansions provide context within the turn, then disappear from history.

### Message-Turn-Bounded State

Expansion state is **message-scoped** and auto-collapses after the message turn completes:

```csharp
// In Agent.cs - RunAgenticAsync method
var expandedPlugins = new HashSet<string>(); // ← Local variable, not persisted

// Expansion state lives only within this method
while (iteration < maxIterations)
{
    // Plugin scoping applies here
    var scopedTools = _pluginScopingManager.GetToolsForAgentTurn(
        allTools,
        expandedPlugins); // ← Tracks expansions across iterations

    // ... agent turn ...
}

// After method exits, expandedPlugins is garbage collected
// Next message starts fresh with Collapse state
```

**Key Properties**:
- ✅ Expansion persists **across agent iterations** within a single message turn
- ✅ Expansion **auto-collapses** when the message turn completes
- ✅ No persistent state - each user message starts fresh
- ✅ No memory leaks - expansion state is GC'd automatically

### Container-First Ordering

Tools are ordered to prioritize discoverability:

```
Priority 1: Containers (Collapse plugins)
Priority 2: Non-Plugin Functions (core utilities)
Priority 3: Expanded Functions (from expanded plugins)
```

**Ordering Logic** (from `PluginScopingManager.cs`):

```csharp
return containers.OrderBy(c => c.Name)
    .Concat(nonPluginFunctions.OrderBy(f => f.Name))
    .Concat(expandedFunctions.OrderBy(f => f.Name))
    .ToList();
```

**Why This Matters**:
- Containers appear first, making them discoverable
- Core functions (memory, planning) always visible
- Expanded functions appear after expansion
- Alphabetical sorting within each group

## Usage

### 1. Mark Plugin with `[PluginScope]` Attribute

```csharp
[PluginScope("Mathematical operations including addition, subtraction, multiplication, and more.")]
public class MathPlugin
{
    [AIFunction]
    [AIDescription("Adds two numbers and returns the sum.")]
    public decimal Add(decimal a, decimal b) => a + b;

    [AIFunction]
    [AIDescription("Multiplies two numbers and returns the product.")]
    public decimal Multiply(decimal a, decimal b) => a * b;

    // ... more functions
}
```

**What happens**:
- Source generator creates a **container function** named `MathPlugin`
- Individual functions (`Add`, `Multiply`) are marked with `ParentPlugin = "MathPlugin"` metadata
- Container has `IsContainer = true` metadata

### 2. Enable Plugin Scoping in Config

```csharp
var agentConfig = new AgentConfig
{
    Name = "AI Assistant",
    // ... other config ...
    PluginScoping = new PluginScopingConfig
    {
        Enabled = true,              // ← Enable C# plugin scoping (default: false)
        ScopeMCPTools = true,        // ← Enable MCP tool scoping (default: false)
        ScopeFrontendTools = true,   // ← Enable Frontend tool scoping (default: false)
        MaxFunctionNamesInDescription = 10  // ← Max names in template descriptions (default: 10)
    }
};
```

**Configuration Options**:
- **`Enabled`** - Scope C# plugins marked with `[PluginScope]` attribute
- **`ScopeMCPTools`** - Group MCP tools by server (one container per MCP server)
- **`ScopeFrontendTools`** - Group all AGUI frontend tools in a single container
- **`MaxFunctionNamesInDescription`** - Limit function names shown in container descriptions

**Important**: Plugin Scoping is **opt-in** (all disabled by default). You must explicitly enable each source type in your `AgentConfig`.

### 3. Register Plugin Normally

```csharp
var agent = new AgentBuilder(agentConfig)
    .WithPlugin<MathPlugin>()  // ← No changes needed!
    .Build();
```

Once enabled in config, scoping is automatic for all plugins with `[PluginScope]` attribute.

### 4. Agent Behavior

The agent will:
1. See `MathPlugin [CONTAINER]` in the initial tool list
2. Invoke `MathPlugin()` when it needs math operations
3. See individual math functions (`Add`, `Multiply`, etc.) after expansion
4. Call the specific function it needs

## Post-Expansion Instructions

**Version**: 2.1+
**Purpose**: Provide just-in-time guidance to the LLM after plugin expansion

### Overview

Post-Expansion Instructions allow you to include plugin-specific guidance, best practices, workflow patterns, and safety warnings that are shown to the agent **only when the plugin is expanded**. This provides context-aware documentation without wasting tokens on unused plugins.

**Key Benefits**:
- **Zero-cost until needed** - Instructions only consume tokens when the plugin is actually used
- **Plugin-specific guidance** - Tailor instructions to each plugin's unique behavior
- **Reduced hallucination** - Explicit instructions prevent common usage errors
- **Workflow documentation** - Guide the LLM through multi-step patterns

### Usage for C# Plugins

Add the optional `postExpansionInstructions` parameter to `[PluginScope]`:

```csharp
[PluginScope(
    description: "Mathematical operations including addition, subtraction, multiplication, and more.",
    postExpansionInstructions: @"
Best practices for using MathPlugin:
- For complex calculations, break them into atomic operations (Add, Subtract, Multiply)
- Use Square(x) for x², it's optimized and clearer than Multiply(x, x)
- Some functions are conditional based on context (see function descriptions)

Performance tips:
- Chain operations by calling functions sequentially
- Add() requires permission - approve once to use multiple times
- All operations return immediately (no async overhead)

Example workflow:
1. Use Multiply for basic calculations
2. If you need x², prefer Square over Multiply(x,x)
3. For negative results, ensure Subtract is available in current context
    "
)]
public class MathPlugin
{
    // ... functions ...
}
```

**What the agent sees after expansion**:

```
MathPlugin expanded. Available functions: Add, Multiply, Square, Subtract

Best practices for using MathPlugin:
- For complex calculations, break them into atomic operations (Add, Subtract, Multiply)
- Use Square(x) for x², it's optimized and clearer than Multiply(x, x)
...
```

### Usage for MCP Tools

Configure instructions in `PluginScopingConfig` using the `MCPServerInstructions` dictionary:

```csharp
var agentConfig = new AgentConfig
{
    PluginScoping = new PluginScopingConfig
    {
        ScopeMCPTools = true,
        MCPServerInstructions = new Dictionary<string, string>
        {
            ["filesystem"] = @"
IMPORTANT SAFETY:
- Always use absolute paths, not relative
- Call FileExists before operations to avoid errors
- DeleteFile is permanent - confirm with user first

Performance tips:
- For large files >10MB, use streaming operations
- Batch operations when possible to reduce overhead
            ",
            ["github"] = @"
Authentication required:
- Call GetAuthToken first before any other operations
- Tokens expire after 1 hour - re-authenticate if you get 401 errors

Rate limits:
- 100 requests/minute for this API tier
- Cache results aggressively to avoid hitting limits

Error handling:
- 429 (rate limit): Wait 60 seconds, retry
- 401 (auth failed): Call GetAuthToken again
            "
        }
    }
};
```

**What the agent sees after expanding MCP_filesystem**:

```
filesystem server expanded. Available functions: ReadFile, WriteFile, DeleteFile, ...

IMPORTANT SAFETY:
- Always use absolute paths, not relative
- Call FileExists before operations to avoid errors
...
```

### Usage for Frontend Tools

Configure instructions for all frontend tools:

```csharp
var agentConfig = new AgentConfig
{
    PluginScoping = new PluginScopingConfig
    {
        ScopeFrontendTools = true,
        FrontendToolsInstructions = @"
These tools interact with the user interface:
- ConfirmAction: ALWAYS use before destructive operations
- ShowNotification: For non-blocking status updates
- RequestInput: When you need user input to proceed
- ShowProgress: For operations taking >2 seconds

Best practices:
- Always provide clear, user-friendly messages
- Use ConfirmAction for anything that modifies data
- ShowProgress for long-running operations
        "
    }
};
```

### Use Cases & Examples

#### Use Case 1: API Client Safety

```csharp
[PluginScope(
    description: "External weather API client",
    postExpansionInstructions: @"
CRITICAL - READ BEFORE USING:
1. Authentication: Call GetAuthToken() first - required for all operations
2. Rate limits: Max 10 calls per minute - cache results for 5+ minutes
3. Error handling:
   - 429 (rate limit): Wait 60 seconds, retry once
   - 401 (auth failed): Call GetAuthToken() again
   - 503 (service down): Inform user, don't retry

Required workflow:
1. GetAuthToken()
2. Then call GetWeather/GetForecast/etc
3. Cache results to avoid redundant calls
    "
)]
public class WeatherAPIPlugin { ... }
```

#### Use Case 2: Database Operations

```csharp
[PluginScope(
    description: "Database operations for customer data",
    postExpansionInstructions: @"
TRANSACTION WORKFLOW (REQUIRED):
1. BeginTransaction()
2. Execute Query/Update/Delete operations
3. CommitTransaction() on success OR RollbackTransaction() on error

NEVER call Query/Update/Delete without an active transaction.

Performance:
- Use batch operations for >10 records (BatchInsert, BatchUpdate)
- Avoid SELECT * - specify columns for better performance
- Use ExecuteScalar for single values

Safety:
- ALL destructive operations require user confirmation first
- Never expose sensitive data (use RedactedLog for logging)
    "
)]
public class DatabasePlugin { ... }
```

#### Use Case 3: File System Safety

```csharp
[PluginScope(
    description: "File system operations",
    postExpansionInstructions: @"
Path handling:
- ALWAYS use absolute paths (use GetAbsolutePath helper)
- Check FileExists/DirectoryExists before operations
- Use PathSeparator constant for cross-platform compatibility

File size guidelines:
- Files <10MB: Use ReadFile/WriteFile (in-memory)
- Files >10MB: Use ReadFileStream/WriteFileStream (streaming)

Safety rules:
- DeleteFile/DeleteDirectory are PERMANENT - confirm with user
- When overwriting files, warn user first
- Validate file extensions before opening (security)
    "
)]
public class FileSystemPlugin { ... }
```

### Token Economics

**Traditional Approach** (system prompt):
```
System Prompt includes:
- MathPlugin usage guide: 200 tokens
- FileSystemPlugin safety guide: 300 tokens
- DatabasePlugin patterns: 250 tokens
= 750 tokens EVERY message (even if plugins unused)
```

**With PostExpansionInstructions**:
```
- Turn 1 (no plugins): 0 instruction tokens
- Turn 2 (expand MathPlugin): 200 instruction tokens (only Math)
- Turn 3 (expand FileSystemPlugin): 300 instruction tokens (only FileSystem)
= Pay only for what you use, when you use it
```

### Benefits

1. **Just-In-Time Guidance** - Instructions appear exactly when the agent needs them
2. **Zero Token Cost Until Used** - No token waste on unused plugins
3. **Plugin-Specific Context** - Each plugin has its own tailored instructions
4. **Reduced Errors** - Explicit guidance prevents common mistakes
5. **Workflow Documentation** - Multi-step patterns documented inline
6. **Environment-Specific** - Different instructions for dev vs production

### Best Practices

1. **Be Specific** - Include exact function names and call order
2. **Highlight Safety** - Emphasize destructive operations and confirmations
3. **Provide Examples** - Show concrete workflow patterns
4. **Mention Rate Limits** - Include API quotas and retry logic
5. **Document Context** - Explain when certain functions are available
6. **Keep It Concise** - Focus on critical information only

**Good Example**:
```csharp
postExpansionInstructions: @"
Required workflow:
1. Call GetAuthToken() first
2. Then call GetWeather(location)
3. Cache results for 5+ minutes

Rate limit: 10 calls/minute
Error handling: On 429, wait 60 seconds and retry once
"
```

**Bad Example**:
```csharp
postExpansionInstructions: "Be careful with this plugin"  // ❌ Too vague
```

## MCP Tools Scoping

### How It Works

When `ScopeMCPTools = true`, tools from each MCP server are grouped behind a container:

```
Before Scoping (30 MCP tools):
- ReadFile (from filesystem server)
- WriteFile (from filesystem server)
- DeleteFile (from filesystem server)
- SearchCode (from github server)
- CreateIssue (from github server)
- OpenPR (from github server)
... (24 more tools)

After Scoping (2 containers):
- MCP_filesystem [CONTAINER]
- MCP_github [CONTAINER]
```

### Template Descriptions

Since users don't write MCP tool descriptions, containers use **template descriptions** generated from function names:

```
"MCP Server 'filesystem'. Contains 15 functions: ReadFile, WriteFile, DeleteFile, CopyFile, MoveFile, ListDirectory, SearchFiles, GetFileInfo, CreateDirectory, MoveDirectory and 5 more"
```

The `MaxFunctionNamesInDescription` config controls how many names are listed before "and X more" appears.

### Registration

MCP tools are loaded automatically when you configure an MCP manifest:

```csharp
var agentConfig = new AgentConfig
{
    Mcp = new McpConfig
    {
        ManifestPath = "./MCP.json"  // or inline JSON content
    },
    PluginScoping = new PluginScopingConfig
    {
        ScopeMCPTools = true  // ← Groups by server
    }
};

var agent = new AgentBuilder(agentConfig)
    .WithMCP(agentConfig.Mcp.ManifestPath)
    .Build();
```

### Container Naming

MCP containers are prefixed with `MCP_` to avoid conflicts with C# plugins:

- Server named `filesystem` → Container named `MCP_filesystem`
- Server named `github` → Container named `MCP_github`

### Metadata

MCP tool containers include additional metadata:

```csharp
AdditionalProperties = new Dictionary<string, object>
{
    ["IsContainer"] = true,
    ["PluginName"] = "MCP_filesystem",
    ["FunctionNames"] = new[] { "ReadFile", "WriteFile", ... },
    ["FunctionCount"] = 15,
    ["SourceType"] = "MCP",           // ← Identifies source
    ["MCPServerName"] = "filesystem"   // ← Original server name
}
```

## Frontend Tools Scoping

### How It Works

When `ScopeFrontendTools = true`, all AGUI frontend tools are grouped in a single container:

```
Before Scoping (12 frontend tools):
- ConfirmAction
- ShowNotification
- RequestInput
- ShowProgress
- UpdateStatus
... (7 more tools)

After Scoping (1 container):
- FrontendTools [CONTAINER]
```

### Why One Container?

Frontend tools are **human-in-the-loop** interactions executed by the UI, not the backend. They're logically related (all UI operations), so grouping them together makes sense.

### Template Description

```
"Frontend UI tools for user interaction. Contains 12 functions: ConfirmAction, ShowNotification, RequestInput, ShowProgress, UpdateStatus, CancelAction, ShowDialog, HideDialog, PlaySound, Vibrate and 2 more"
```

### Registration

Frontend tools come from AGUI `RunAgentInput.Tools` and are automatically scoped:

```csharp
var agentConfig = new AgentConfig
{
    PluginScoping = new PluginScopingConfig
    {
        ScopeFrontendTools = true  // ← Groups all frontend tools
    }
};

// Frontend tools are passed via AGUI protocol
// Scoping is applied automatically in AGUIEventConverter
```

### Metadata

Frontend tool containers include:

```csharp
AdditionalProperties = new Dictionary<string, object>
{
    ["IsContainer"] = true,
    ["PluginName"] = "FrontendTools",
    ["FunctionNames"] = new[] { "ConfirmAction", "ShowNotification", ... },
    ["FunctionCount"] = 12,
    ["SourceType"] = "Frontend"  // ← Identifies source
}
```

## Implementation Details

### Architecture Overview

Plugin scoping uses **two different approaches** depending on the tool source:

| Tool Source | Implementation | When Applied |
|-------------|----------------|--------------|
| **C# Plugins** | Source Generator | Compile-time |
| **MCP Tools** | Runtime Wrapper | Runtime (during load) |
| **Frontend Tools** | Runtime Wrapper | Runtime (during AGUI conversion) |

Both approaches produce the same metadata structure, allowing `PluginScopingManager` to handle all tools uniformly.

### Source Generator for C# Plugins (`HPDPluginSourceGenerator.cs`)

#### Container Function Generation

When a plugin has `[PluginScope]`, the generator creates a container function:

```csharp
private static AIFunction CreateMathPluginContainer()
{
    return HPDAIFunctionFactory.Create(
        async (arguments, cancellationToken) =>
        {
            return "MathPlugin expanded. Available functions: Add, Multiply, Abs, Square, Subtract, Min";
        },
        new HPDAIFunctionFactoryOptions
        {
            Name = "MathPlugin",
            Description = "Mathematical operations including addition, subtraction, multiplication, and more.",
            SchemaProvider = () => CreateEmptyContainerSchema(),
            AdditionalProperties = new Dictionary<string, object>
            {
                ["IsContainer"] = true,
                ["PluginName"] = "MathPlugin",
                ["FunctionNames"] = new[] { "Add", "Multiply", "Abs", "Square", "Subtract", "Min" },
                ["FunctionCount"] = 6
            }
        });
}
```

**Key Metadata**:
- `IsContainer = true` - Identifies this as a container
- `PluginName = "MathPlugin"` - The plugin this contains
- `FunctionNames` - List of functions in this plugin
- `FunctionCount` - Number of functions (for display)

#### Individual Function Metadata

Each function in a scoped plugin gets metadata:

```csharp
AdditionalProperties = new Dictionary<string, object>
{
    ["ParentPlugin"] = "MathPlugin",
    ["PluginName"] = "MathPlugin"
}
```

This allows `PluginScopingManager` to filter based on expansion state.

### Runtime Wrapper for MCP & Frontend Tools (`ExternalToolScopingWrapper.cs`)

For tools that don't go through the source generator (MCP, Frontend), we wrap them at runtime:

#### Wrapping MCP Server Tools

```csharp
public static (AIFunction container, List<AIFunction> scopedTools) WrapMCPServerTools(
    string serverName,
    List<AIFunction> tools,
    int maxFunctionNamesInDescription = 10)
{
    var containerName = $"MCP_{serverName}";  // Prefix to avoid conflicts
    var allFunctionNames = tools.Select(t => t.Name).ToList();

    // Generate template description from function names
    var displayedNames = allFunctionNames.Take(maxFunctionNamesInDescription);
    var functionNamesList = string.Join(", ", displayedNames);
    var moreCount = allFunctionNames.Count > maxFunctionNamesInDescription
        ? $" and {allFunctionNames.Count - maxFunctionNamesInDescription} more"
        : "";

    var description = $"MCP Server '{serverName}'. Contains {allFunctionNames.Count} functions: {functionNamesList}{moreCount}";

    // Create container (same structure as source generator)
    var container = HPDAIFunctionFactory.Create(
        async (arguments, cancellationToken) =>
        {
            return $"{serverName} server expanded. Available functions: {string.Join(", ", allFunctionNames)}";
        },
        new HPDAIFunctionFactoryOptions
        {
            Name = containerName,
            Description = description,
            AdditionalProperties = new Dictionary<string, object>
            {
                ["IsContainer"] = true,
                ["PluginName"] = containerName,
                ["FunctionNames"] = allFunctionNames.ToArray(),
                ["FunctionCount"] = allFunctionNames.Count,
                ["SourceType"] = "MCP",
                ["MCPServerName"] = serverName
            }
        });

    // Wrap each tool with ParentPlugin metadata
    var scopedTools = tools.Select(tool => AddParentPluginMetadata(tool, containerName, "MCP")).ToList();

    return (container, scopedTools);
}
```

**Key Points**:
- **Template description** generated from function names
- **`MCP_` prefix** prevents naming conflicts with C# plugins
- **`SourceType`** metadata tracks origin
- **Same structure** as source-generated containers

#### Integration Points

**MCP Tools**: Wrapped in `MCPClientManager.LoadToolsFromManifestAsync()` during server tool loading

**Frontend Tools**: Wrapped in `AGUIEventConverter.ConvertToExtensionsAIChatOptions()` during AGUI input processing

### Runtime Management (`PluginScopingManager.cs`)

The manager filters tools based on expansion state:

```csharp
public List<AIFunction> GetToolsForAgentTurn(
    List<AIFunction> allTools,
    HashSet<string> expandedPlugins)
{
    var containers = new List<AIFunction>();
    var nonPluginFunctions = new List<AIFunction>();
    var expandedFunctions = new List<AIFunction>();

    foreach (var tool in allTools)
    {
        if (IsContainer(tool))
        {
            // Only show containers that haven't been expanded
            var pluginName = GetPluginName(tool);
            if (!expandedPlugins.Contains(pluginName))
            {
                containers.Add(tool);
            }
        }
        else
        {
            var parentPlugin = GetParentPlugin(tool);
            if (parentPlugin != null)
            {
                // Plugin function - only show if parent is expanded
                if (expandedPlugins.Contains(parentPlugin))
                {
                    expandedFunctions.Add(tool);
                }
            }
            else
            {
                // Non-plugin function - always visible
                nonPluginFunctions.Add(tool);
            }
        }
    }

    // Order: Containers → Non-Plugin Functions → Expanded Functions
    return containers.OrderBy(c => c.Name)
        .Concat(nonPluginFunctions.OrderBy(f => f.Name))
        .Concat(expandedFunctions.OrderBy(f => f.Name))
        .ToList();
}
```

### Container Invocation Flow (`Agent.cs`)

Containers go through the **normal function invocation pipeline** (this is critical!):

```csharp
// ExecuteInParallelAsync - handles multiple tool calls
private async Task<ChatMessage> ExecuteInParallelAsync(...)
{
    // PHASE 0: Identify containers for expansion tracking
    var containerExpansions = new Dictionary<string, string>(); // callId → pluginName
    foreach (var toolRequest in toolRequests)
    {
        var function = options?.Tools?.OfType<AIFunction>()
            .FirstOrDefault(f => f.Name == toolRequest.Name);

        if (function != null && _pluginScopingManager.IsContainer(function))
        {
            var pluginName = function.AdditionalProperties
                ?.TryGetValue("PluginName", out var value) == true && value is string pn
                ? pn
                : toolRequest.Name;

            containerExpansions[toolRequest.CallId] = pluginName;
        }
    }

    // PHASE 1 & 2: ALL tools (containers + regular) go through normal pipeline
    // - Permission checks
    // - Filter pipeline (logging, observability, scoped filters)
    // - Function execution

    // PHASE 3: Mark containers as expanded AFTER successful invocation
    foreach (var result in results)
    {
        if (result.Success)
        {
            // ... add result contents ...

            // If this was a container, mark the plugin as expanded
            if (containerExpansions.TryGetValue(result.ToolRequest.CallId, out var pluginName))
            {
                expandedPlugins.Add(pluginName);
            }
        }
    }
}
```

**Critical Design Decision**: Containers are **NOT special-cased**. They:
- ✅ Go through permission filters
- ✅ Go through logging filters
- ✅ Go through observability filters
- ✅ Go through scoped filters
- ✅ Execute their implementation (returns expansion message)
- ✅ **After success**, plugin is marked as expanded

### Filter Integration

Because containers go through the normal pipeline, all filters see them:

#### Logging Filter Example:
```
[LOG][PRE] Function: MathPlugin
Args: <empty>
[LOG][POST] Function: MathPlugin Result: MathPlugin expanded. Available functions: Add, Multiply, Abs, Square, Subtract, Min
--------------------------------------------------
```

#### Permission Filter Example:
```
[PERMISSION REQUIRED]
Function: MathPlugin
Description: Mathematical operations including addition, subtraction, multiplication, and more.
Arguments: <none>

Choose an option:
  [A]llow once
  [D]eny once
  [Y] Always allow (Global)
  [N] Never allow (Global)
```

#### Scoped Filter Example:
```csharp
// Apply plugin-specific logging to MathPlugin
var agent = new AgentBuilder(config)
    .WithPlugin<MathPlugin>()
    .ForPlugin<MathPlugin>()  // ← Enter plugin scope
        .WithFilter(new CustomMathLogger())  // ← Only applies to MathPlugin
    .Build();
```

The scoped filter will run for:
- ✅ `MathPlugin()` container invocation
- ✅ `Add()`, `Multiply()`, etc. individual function invocations

## Metadata Reference

### Container Function Metadata

| Key | Type | Description | Example |
|-----|------|-------------|---------|
| `IsContainer` | `bool` | Identifies this as a container | `true` |
| `PluginName` | `string` | Plugin name | `"MathPlugin"` |
| `FunctionNames` | `string[]` | Functions in this plugin | `["Add", "Multiply"]` |
| `FunctionCount` | `int` | Number of functions | `6` |

### Individual Function Metadata

| Key | Type | Description | Example |
|-----|------|-------------|---------|
| `ParentPlugin` | `string` | Parent plugin name | `"MathPlugin"` |
| `PluginName` | `string` | Plugin name (duplicate) | `"MathPlugin"` |

### Accessing Metadata

```csharp
// Check if function is a container
var isContainer = function.AdditionalProperties
    ?.TryGetValue("IsContainer", out var value) == true
    && value is bool isCont && isCont;

// Get parent plugin
var parentPlugin = function.AdditionalProperties
    ?.TryGetValue("ParentPlugin", out var value) == true
    && value is string parent ? parent : null;

// Get plugin name (for containers)
var pluginName = function.AdditionalProperties
    ?.TryGetValue("PluginName", out var value) == true
    && value is string pn ? pn : null;
```

## Performance Benefits

### Token Reduction

**Before Plugin Scoping** (40 functions):
```json
{
  "tools": [
    {"name": "CreateMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "UpdateMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "DeleteMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "Add", "description": "...", "parameters": {...}},
    {"name": "Multiply", "description": "...", "parameters": {...}},
    // ... 35 more functions
  ]
}
```
**Tokens**: ~4,000 tokens per call

**After Plugin Scoping** (5 tools initially):
```json
{
  "tools": [
    {"name": "CreateMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "UpdateMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "DeleteMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "MathPlugin", "description": "Mathematical operations...", "parameters": {}},
    {"name": "FileSystemPlugin", "description": "File operations...", "parameters": {}}
  ]
}
```
**Tokens**: ~500 tokens per call (Turn 1)

**After Expansion** (Turn 2):
```json
{
  "tools": [
    {"name": "CreateMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "UpdateMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "DeleteMemoryAsync", "description": "...", "parameters": {...}},
    {"name": "Add", "description": "...", "parameters": {...}},
    {"name": "Multiply", "description": "...", "parameters": {...}},
    // ... only MathPlugin functions
  ]
}
```
**Tokens**: ~1,200 tokens per call (Turn 2)

**Savings**:
- Turn 1: 87.5% reduction (4000 → 500 tokens)
- Turn 2: 70% reduction (4000 → 1200 tokens)
- Average: ~80% token savings for multi-plugin agents

### Cost Impact

Example: OpenAI GPT-4 Turbo ($0.01/1K input tokens)

**Without Plugin Scoping** (10 messages):
- 10 messages × 4,000 tokens = 40,000 tokens
- Cost: **$0.40**

**With Plugin Scoping** (10 messages, 30% require expansion):
- 7 messages × 500 tokens = 3,500 tokens (Collapse)
- 3 messages × 1,200 tokens = 3,600 tokens (expanded)
- Total: 7,100 tokens
- Cost: **$0.071**

**Savings**: **82% cost reduction** ($0.40 → $0.071)

### Real-World Example: Multi-Source Agent

**Scenario**: Agent with C# plugins, MCP servers, and frontend tools

**Tool Inventory**:
- **Core Functions** (always visible): 3 (CreateMemory, UpdateMemory, DeleteMemory)
- **C# Plugins**: MathPlugin (6 functions), FileSystemPlugin (8 functions)
- **MCP Tools**: 2 servers with 15 functions each (30 total)
- **Frontend Tools**: 10 AGUI UI interaction tools
- **Total**: 57 tools

**Without Scoping** (57 tools):
```json
{
  "tools": [
    // 3 core functions
    {"name": "CreateMemoryAsync", ...},
    {"name": "UpdateMemoryAsync", ...},
    {"name": "DeleteMemoryAsync", ...},
    // 6 MathPlugin functions
    {"name": "Add", ...}, {"name": "Multiply", ...}, ...
    // 8 FileSystemPlugin functions
    {"name": "ReadFile", ...}, {"name": "WriteFile", ...}, ...
    // 30 MCP tools
    {"name": "SearchCode", ...}, {"name": "CreateIssue", ...}, ...
    // 10 Frontend tools
    {"name": "ConfirmAction", ...}, {"name": "ShowNotification", ...}, ...
  ]
}
```
**Tokens**: ~6,500 tokens per call

**With Full Scoping** (8 tools):
```json
{
  "tools": [
    // 3 core functions (always visible)
    {"name": "CreateMemoryAsync", ...},
    {"name": "UpdateMemoryAsync", ...},
    {"name": "DeleteMemoryAsync", ...},
    // 5 containers
    {"name": "MathPlugin", "description": "Mathematical operations...", ...},
    {"name": "FileSystemPlugin", "description": "File operations...", ...},
    {"name": "MCP_github", "description": "MCP Server 'github'. Contains 15 functions: SearchCode, CreateIssue, ...", ...},
    {"name": "MCP_filesystem", "description": "MCP Server 'filesystem'. Contains 15 functions: ReadFile, WriteFile, ...", ...},
    {"name": "FrontendTools", "description": "Frontend UI tools for user interaction. Contains 10 functions: ...", ...}
  ]
}
```
**Tokens**: ~800 tokens per call (Turn 1)

**Token Reduction**: **87.7%** (6,500 → 800 tokens)

**Annual Savings** (100k messages/year, GPT-4 Turbo pricing):
- Without scoping: 650M tokens × $0.01/1K = **$6,500/year**
- With scoping: 80M tokens × $0.01/1K = **$800/year**
- **Savings**: **$5,700/year** (87.7% reduction)

## Design Rationale

### Why Two Turns?

**Why not auto-expand when the user mentions a plugin?**

1. **Agent autonomy**: The agent decides when to expand based on task needs
2. **Explicit intent**: Agent must explicitly call the container, showing reasoning
3. **Consistent API**: All tools are invoked the same way (no special parsing)
4. **Filter support**: Container invocation goes through filters like any function

### Why Message-Scoped State?

**Why not persist expansion across messages?**

1. **Predictability**: Each message starts fresh - no hidden state
2. **Simplicity**: No state management, serialization, or persistence needed
3. **No memory leaks**: Expansion state is GC'd automatically
4. **Fresh context**: Each message re-evaluates what plugins it needs

### Why Container-First Ordering?

**Why not alphabetical across all tools?**

1. **Discoverability**: Containers must be visible for the system to work
2. **Logical grouping**: Containers → Core → Expanded creates clear hierarchy
3. **Intent clarity**: Agent sees "expand me first" containers before expanded functions

### Why Full Pipeline for Containers?

**Why not bypass filters for containers?**

This was actually a **bug** in the original implementation! Containers bypassed the filter pipeline, causing:

❌ **Problems with bypass**:
- No logging - container invocations were invisible
- No observability - telemetry lost expansion events
- No permissions - security hole for plugin access
- No scoped filters - plugin-specific filtering broke
- Inconsistent API - containers behaved differently than functions

✅ **Benefits of full pipeline**:
- Logging works - see container invocations in logs
- Observability works - telemetry tracks expansion patterns
- Permissions work - control plugin access
- Scoped filters work - plugin-specific behavior applies
- Consistent API - containers are just functions with side effects

**Key Insight**: Container invocation IS a meaningful event that should be logged, observed, and controlled like any other function call.

## Debugging & Monitoring

### Enable Debug Logging

Add logging to see container detection and expansion:

```csharp
var agent = new AgentBuilder(config)
    .WithLogging()  // ← Enables function logging
    .WithPlugin<MathPlugin>()
    .Build();
```

**Output**:
```
[LOG][PRE] Function: MathPlugin
Args: <empty>
[LOG][POST] Function: MathPlugin Result: MathPlugin expanded. Available functions: Add, Multiply, Abs, Square, Subtract, Min
--------------------------------------------------

[LOG][PRE] Function: Add
Args: a: 5, b: 3
[LOG][POST] Function: Add Result: 8
--------------------------------------------------
```

### Check Registered Tools

Debug what tools are registered:

```csharp
var registeredTools = agent.DefaultOptions?.Tools;
foreach (var tool in registeredTools.OfType<AIFunction>())
{
    var isContainer = tool.AdditionalProperties?.TryGetValue("IsContainer", out var val) == true
        && val is bool isCont && isCont;
    var parentPlugin = tool.AdditionalProperties?.TryGetValue("ParentPlugin", out var parent) == true
        && parent is string p ? p : null;

    var metadata = isContainer ? " [CONTAINER]" :
                   parentPlugin != null ? $" [Plugin: {parentPlugin}]" : "";

    Console.WriteLine($" - {tool.Name}{metadata} : {tool.Description}");
}
```

**Expected Output**:
```
🔧 Registered tools:
 - MathPlugin [CONTAINER] : Mathematical operations including addition, subtraction, multiplication, and more.
 - Add [Plugin: MathPlugin] : Adds two numbers and returns the sum.
 - Multiply [Plugin: MathPlugin] : Multiplies two numbers and returns the product.
 - CreateMemoryAsync : Create a new persistent memory
```

### Trace Expansion State

Add tracing in `PluginScopingManager.GetToolsForAgentTurn`:

```csharp
public List<AIFunction> GetToolsForAgentTurn(
    List<AIFunction> allTools,
    HashSet<string> expandedPlugins)
{
    Console.WriteLine($"[Plugin Scoping] Expanded plugins: {string.Join(", ", expandedPlugins)}");

    // ... filtering logic ...

    Console.WriteLine($"[Plugin Scoping] Containers: {containers.Count}");
    Console.WriteLine($"[Plugin Scoping] Non-plugin functions: {nonPluginFunctions.Count}");
    Console.WriteLine($"[Plugin Scoping] Expanded functions: {expandedFunctions.Count}");

    return /* ... */;
}
```

## Troubleshooting

### Container Not Showing

**Symptom**: Container function not visible to agent

**Causes**:
1. Missing `[PluginScope]` attribute on plugin class
2. Source generator not running (clean + rebuild)
3. Generated code not included in compilation

**Solution**:
```bash
# Clean everything
dotnet clean

# Delete obj folders to clear caches
rm -rf **/obj

# Rebuild
dotnet build
```

### Individual Functions Visible Immediately

**Symptom**: Individual functions show up without expanding container

**Causes**:
1. Plugin not marked with `[PluginScope]` attribute
2. Metadata lost during source generation (partial class bug - now fixed)

**Solution**: Check that `[PluginScope]` is on the plugin class:
```csharp
[PluginScope("Description here")]  // ← Must be present
public class MathPlugin
{
    // ...
}
```

### Filters Not Running for Containers

**Symptom**: Logging/observability filters don't see container invocations

**Cause**: This was the bug we just fixed! Containers were bypassing the filter pipeline.

**Solution**: Ensure you have the latest code with the fixes in:
- `HPD-Agent/Agent/Agent.cs` (lines 2385-2475 for parallel, 2318-2362 for sequential)

### Expansion Not Persisting

**Symptom**: Agent must expand plugin on every turn

**Expected Behavior**: This is **correct**! Expansion state is message-scoped and auto-collapses.

**Why**: Each message starts fresh with Collapse plugins. If you want different behavior, you'd need to persist `expandedPlugins` across messages (not currently implemented).

## Best Practices

### 1. Use Plugin Scoping for Large Plugins

**Good candidates**:
- ✅ Math plugins with many operations (Add, Multiply, Sqrt, Log, Sin, Cos, etc.)
- ✅ File system plugins with many operations (Read, Write, Delete, List, Search, etc.)
- ✅ Database plugins with CRUD operations
- ✅ API client plugins with many endpoints

**Poor candidates**:
- ❌ Plugins with 1-2 functions (overhead not worth it)
- ❌ Core plugins that are always needed (Memory, Planning - should be always visible)

### 2. Write Clear Container Descriptions

The container description is what the agent sees initially:

**Bad**:
```csharp
[PluginScope("Math stuff")]  // ❌ Vague
```

**Good**:
```csharp
[PluginScope("Mathematical operations including addition, subtraction, multiplication, division, square root, and trigonometric functions.")]  // ✅ Specific
```

### 3. Group Related Functions

Don't create artificial plugin boundaries:

**Bad** (over-fragmentation):
```csharp
[PluginScope("Addition operations")]
public class AdditionPlugin { ... }

[PluginScope("Multiplication operations")]
public class MultiplicationPlugin { ... }
```

**Good** (logical grouping):
```csharp
[PluginScope("Mathematical operations")]
public class MathPlugin { ... }
```

### 4. Test with Scoped Filters

If you use scoped filters, test both:
- Container invocation (filter should run)
- Individual function invocation (filter should run)

```csharp
var agent = new AgentBuilder(config)
    .ForPlugin<MathPlugin>()
        .WithFilter(new CustomLogger())  // ← Test this runs for containers AND functions
    .Build();
```

## Future Enhancements

### Potential Features

1. **Persistent Expansion State**
   - Persist `expandedPlugins` across messages
   - Collapse command: "Collapse MathPlugin"
   - Auto-collapse after N turns of inactivity

2. **Nested Plugin Scopes**
   - Sub-containers within plugins
   - Example: `MathPlugin.TrigonometryPlugin` → `Sin`, `Cos`, `Tan`

3. **Smart Auto-Expansion**
   - Analyze user query for keywords
   - Pre-expand likely-needed plugins
   - Example: "calculate sine of 45" → auto-expand MathPlugin

4. **Expansion Hints**
   - Add hints to responses: "(Tip: Use MathPlugin for calculations)"
   - Help user understand why expansion is happening

5. **Rust FFI Support**
   - Implement equivalent proc macro for Rust plugins
   - Same `[PluginScope]` attribute behavior

## Related Documentation

- [Dynamic Plugin Metadata](./dynamic-plugin-metadata.md) - How conditional functions work with context-based filtering
- [Plugin Metadata Quick Reference](./plugin-metadata-quick-reference.md) - All metadata attributes and options
- [Orchestration Framework](./orchestration-framework.md) - How plugins integrate with the agent system
- [Agent Developer Documentation](./Agent-Developer-Documentation.md) - Complete agent API reference

## Changelog

### 2025-01-17 - v2.1 (Post-Expansion Instructions)

**New Features**:
- ✅ **Post-Expansion Instructions** - Provide just-in-time guidance to LLM after plugin expansion
- ✅ **C# Plugin Support** - Optional `postExpansionInstructions` parameter in `[PluginScope]` attribute
- ✅ **MCP Tool Support** - `MCPServerInstructions` dictionary in `PluginScopingConfig`
- ✅ **Frontend Tool Support** - `FrontendToolsInstructions` string in `PluginScopingConfig`
- ✅ **Zero-Cost Until Used** - Instructions only consume tokens when plugin is expanded
- ✅ **Workflow Documentation** - Document multi-step patterns, safety rules, and best practices

**Files Modified**:
- `PluginScopeAttribute.cs` - Added optional `postExpansionInstructions` parameter
- `PluginInfo.cs` (source generator) - Added `PostExpansionInstructions` property
- `HPDPluginSourceGenerator.cs` - Extract and embed post-expansion instructions in container return value
- `ExternalToolScopingWrapper.cs` - Added `postExpansionInstructions` parameter to wrapper methods
- `AgentConfig.cs` - Added `MCPServerInstructions` and `FrontendToolsInstructions` config properties

**Usage Example**:
```csharp
[PluginScope(
    description: "Mathematical operations",
    postExpansionInstructions: @"
        Best practices:
        - Break complex calculations into atomic operations
        - Use Square(x) for x², it's optimized
        - Chain operations by calling sequentially
    "
)]
public class MathPlugin { ... }
```

**Benefits**:
- Plugin-specific guidance appears exactly when needed
- Reduces hallucination by providing explicit instructions
- Zero token cost for unused plugins
- Supports environment-specific instructions (dev vs production)

**Important Implementation Detail**:
- Container expansion results are **filtered out** of persistent chat history
- They are only visible to the LLM during the current message turn
- This prevents redundant expansion messages from accumulating in history
- Keeps history clean and token-efficient

### 2025-01-12 - v2.0 (MCP & Frontend Tool Support)

**New Features**:
- ✅ **MCP Tool Scoping** - Group tools by MCP server behind containers
- ✅ **Frontend Tool Scoping** - Group AGUI frontend tools in a single container
- ✅ **Runtime Wrapper** - `ExternalToolScopingWrapper` for non-C# tools
- ✅ **Template Descriptions** - Auto-generated descriptions from function names
- ✅ **Source Type Tracking** - `SourceType` metadata distinguishes MCP/Frontend/C#
- ✅ **Independent Configuration** - Separate enable flags for each tool source

**Files Added**:
- `HPD-Agent/Agent/ExternalToolScopingWrapper.cs` - Runtime container creation

**Files Modified**:
- `AgentConfig.cs` - Added `ScopeMCPTools`, `ScopeFrontendTools`, `MaxFunctionNamesInDescription`
- `AGUIEventConverter.cs` - Frontend tool scoping support
- `Agent.cs` - Pass scoping config to AGUI converter
- `MCPClientManager.cs` - MCP tool scoping parameters
- `AgentBuilder.cs` - Pass scoping config to MCP loader

**Configuration**:
```csharp
PluginScoping = new PluginScopingConfig
{
    Enabled = true,                // C# plugins
    ScopeMCPTools = true,          // MCP tools
    ScopeFrontendTools = true,     // Frontend tools
    MaxFunctionNamesInDescription = 10
}
```

### 2025-01-12 - v1.0 (Initial Release)

**Features**:
- ✅ Two-turn expansion flow
- ✅ Container-first ordering
- ✅ Message-turn-bounded state
- ✅ Source generator support (C# plugins only)
- ✅ Runtime filtering (PluginScopingManager)
- ✅ Full filter integration (logging, observability, permissions, scoped filters)

**Bugs Fixed**:
- 🐛 Source generator lost `HasScopeAttribute` during partial class merging
- 🐛 Container functions bypassed filter pipeline (parallel execution)
- 🐛 Container functions bypassed filter pipeline (sequential execution)

**Known Limitations**:
- ⚠️ Expansion state not persisted across messages (by design)
- ⚠️ No nested plugin scopes (single level only)
- ⚠️ No Rust FFI support yet (C# only)
- ⚠️ MCP and Frontend tools not supported (fixed in v2.0)

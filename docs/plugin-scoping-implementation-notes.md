# Plugin Scoping Implementation Notes

**Date**: 2025-01-12
**Feature**: Plugin Scoping v2.0 (C# + MCP + Frontend Tools)
**Status**: ✅ Fully Implemented & Working

## What You'll Find Here

This document explains the **implementation journey** of Plugin Scoping, including:
- The bugs we discovered and fixed
- Why certain design decisions were made
- Common misconceptions and gotchas
- How the pieces fit together

If you're coming back to this code in 3 months and wondering "why did I do it this way?", this is your guide.

---

## The Problem We Solved

### Initial Symptom
Plugin Scoping was implemented but **filters weren't seeing container invocations**. Specifically:
- ✅ Individual functions (Add, Multiply) triggered logging: `[LOG][PRE]` and `[LOG][POST]`
- ❌ Container functions (MathPlugin) were **silent** - no logging, no observability, no filter execution

### Why This Mattered
The logging filter wasn't the only thing broken. **ALL filters** were bypassed for containers:
- Logging filters (couldn't see expansions)
- Observability filters (telemetry lost expansion events)
- Permission filters (security hole - containers bypassed permissions!)
- Scoped filters (plugin-specific filtering didn't work)

---

## The Three Bugs We Fixed

### Bug #1: Source Generator Lost Metadata During Partial Class Merging

**File**: `HPD-Agent.SourceGenerator/SourceGeneration/HPDPluginSourceGenerator.cs` (lines 73-94)

**The Problem**:
When a plugin was defined across multiple partial classes, the source generator would merge them but **lose the `HasScopeAttribute` and `ScopeDescription` metadata**.

**Original Code (BROKEN)**:
```csharp
var pluginGroups = plugins
    .Where(p => p != null)
    .GroupBy(p => $"{p!.Namespace}.{p.Name}")
    .Select(group =>
    {
        var first = group.First()!;
        var allFunctions = group.SelectMany(p => p!.Functions).ToList();

        return new PluginInfo
        {
            Name = first.Name,
            Description = $"Plugin containing {allFunctions.Count} AI functions.",
            Namespace = first.Namespace,
            Functions = allFunctions
            // ❌ HasScopeAttribute and ScopeDescription are MISSING!
        };
    })
    .ToList();
```

**Fixed Code**:
```csharp
var pluginGroups = plugins
    .Where(p => p != null)
    .GroupBy(p => $"{p!.Namespace}.{p.Name}")
    .Select(group =>
    {
        var first = group.First()!;
        var allFunctions = group.SelectMany(p => p!.Functions).ToList();

        // ✅ Preserve HasScopeAttribute and ScopeDescription from ANY partial class that has it
        var hasScopeAttribute = group.Any(p => p!.HasScopeAttribute);
        var scopeDescription = group.FirstOrDefault(p => p!.HasScopeAttribute)?.ScopeDescription;

        return new PluginInfo
        {
            Name = first.Name,
            Description = $"Plugin containing {allFunctions.Count} AI functions.",
            Namespace = first.Namespace,
            Functions = allFunctions,
            HasScopeAttribute = hasScopeAttribute,      // ✅ Now preserved
            ScopeDescription = scopeDescription          // ✅ Now preserved
        };
    })
    .ToList();
```

**Why This Happened**:
The `GroupBy` merges all partial class definitions into one `PluginInfo`, but the original code only took metadata from the `first` partial class. If the `[PluginScope]` attribute was on a different partial class, it was lost.

**Impact**:
Without this metadata, the source generator wouldn't generate the container function at all - the plugin would have individual functions but no container.

---

### Bug #2: Container Functions Bypassed Filter Pipeline (Parallel Execution)

**File**: `HPD-Agent/Agent/Agent.cs` (ExecuteInParallelAsync method, lines 2385-2475)

**The Problem**:
Container functions were being detected and handled **inline**, bypassing the entire function invocation pipeline.

**Original Code (BROKEN)**:
```csharp
// PHASE 0: Separate containers from regular tools
foreach (var toolRequest in toolRequests)
{
    var function = options?.Tools?.OfType<AIFunction>()
        .FirstOrDefault(f => f.Name == toolRequest.Name);

    if (function != null && _pluginScopingManager.IsContainer(function))
    {
        // ❌ INLINE HANDLING - bypasses all filters!
        var pluginName = function.AdditionalProperties
            ?.TryGetValue("PluginName", out var value) == true && value is string pn
            ? pn
            : toolRequest.Name;

        expandedPlugins.Add(pluginName);

        // Return a message directly without invoking the function
        var confirmationMessage = $"✓ Expanded {pluginName} plugin. Functions are now available.";
        allContents.Add(new FunctionResultContent(toolRequest.CallId, confirmationMessage));
    }
    else
    {
        // Regular functions go through normal pipeline
        nonContainerRequests.Add(toolRequest);
    }
}

// If all requests were containers, return early
if (nonContainerRequests.Count == 0)
{
    return new ChatMessage(ChatRole.Tool, allContents);
}

// Only non-container requests go through the pipeline
var approvedTools = new List<FunctionCallContent>();
```

**Fixed Code**:
```csharp
// PHASE 0: Identify containers and track them for expansion after invocation
var containerExpansions = new Dictionary<string, string>(); // callId → pluginName
foreach (var toolRequest in toolRequests)
{
    var function = options?.Tools?.OfType<AIFunction>()
        .FirstOrDefault(f => f.Name == toolRequest.Name);

    if (function != null && _pluginScopingManager.IsContainer(function))
    {
        // ✅ Track container for expansion, but DON'T handle it inline
        var pluginName = function.AdditionalProperties
            ?.TryGetValue("PluginName", out var value) == true && value is string pn
            ? pn
            : toolRequest.Name;

        containerExpansions[toolRequest.CallId] = pluginName;
    }
}

// ✅ ALL tools (containers + regular) go through normal pipeline
var approvedTools = new List<FunctionCallContent>();
var deniedTools = new List<FunctionCallContent>();

// PHASE 1: Permission checking (sequential)
foreach (var toolRequest in toolRequests)  // ← ALL requests, not just non-containers
{
    var approved = await _functionCallProcessor.CheckPermissionAsync(...);
    // ...
}

// PHASE 2: Execute approved tools in parallel
var executionTasks = approvedTools.Select(async toolRequest =>
{
    // Execute through the processor (which runs all filters)
    var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(...);
    return (Success: true, Messages: resultMessages, Error: (Exception?)null, ToolRequest: toolRequest);
}).ToArray();

var results = await Task.WhenAll(executionTasks).ConfigureAwait(false);

// ✅ PHASE 3: Mark containers as expanded AFTER successful invocation
foreach (var result in results)
{
    if (result.Success)
    {
        foreach (var message in result.Messages)
        {
            allContents.AddRange(message.Contents);
        }

        // If this was a container, mark the plugin as expanded
        if (containerExpansions.TryGetValue(result.ToolRequest.CallId, out var pluginName))
        {
            expandedPlugins.Add(pluginName);
        }
    }
}
```

**Key Design Change**:
- **Before**: Detect container → Handle inline → Skip pipeline → Expand immediately
- **After**: Detect container → Track for later → Run through pipeline → Expand after success

**Why This Matters**:
Containers are **real function invocations** from the AI model's perspective. They should go through the same pipeline as any other function:
1. Permission checks (security)
2. Filter pipeline (logging, observability, scoped filters)
3. Function execution (returns the expansion message)
4. **Then** mark as expanded (side effect)

---

### Bug #3: Container Functions Bypassed Filter Pipeline (Sequential Execution)

**File**: `HPD-Agent/Agent/Agent.cs` (ExecuteSequentiallyAsync method, lines 2318-2362)

**The Problem**:
Same bypass issue as Bug #2, but in the **sequential execution path** (used for single tool calls).

**Original Code (BROKEN)**:
```csharp
var allContents = new List<AIContent>();
var nonContainerRequests = new List<FunctionCallContent>();

// Check each tool request to see if it's a container expansion
foreach (var toolRequest in toolRequests)
{
    var function = options?.Tools?.OfType<AIFunction>()
        .FirstOrDefault(f => f.Name == toolRequest.Name);

    if (function != null && _pluginScopingManager.IsContainer(function))
    {
        // ❌ INLINE HANDLING - bypasses all filters!
        var pluginName = function.AdditionalProperties
            ?.TryGetValue("PluginName", out var value) == true && value is string pn
            ? pn
            : toolRequest.Name;

        expandedPlugins.Add(pluginName);

        var confirmationMessage = $"✓ Expanded {pluginName} plugin. Functions are now available.";
        allContents.Add(new FunctionResultContent(toolRequest.CallId, confirmationMessage));
    }
    else
    {
        nonContainerRequests.Add(toolRequest);
    }
}

// Process non-container functions through the existing processor
if (nonContainerRequests.Count > 0)
{
    var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
        currentHistory, options, nonContainerRequests, agentRunContext, agentName, cancellationToken);
    // ...
}
```

**Fixed Code**:
```csharp
var allContents = new List<AIContent>();

// Track which tool requests are containers for expansion after invocation
var containerExpansions = new Dictionary<string, string>(); // callId → pluginName
foreach (var toolRequest in toolRequests)
{
    var function = options?.Tools?.OfType<AIFunction>()
        .FirstOrDefault(f => f.Name == toolRequest.Name);

    if (function != null && _pluginScopingManager.IsContainer(function))
    {
        // ✅ Track this container for expansion after invocation
        var pluginName = function.AdditionalProperties
            ?.TryGetValue("PluginName", out var value) == true && value is string pn
            ? pn
            : toolRequest.Name;

        containerExpansions[toolRequest.CallId] = pluginName;
    }
}

// ✅ Process ALL tools (containers + regular) through the existing processor
var resultMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(
    currentHistory, options, toolRequests, agentRunContext, agentName, cancellationToken);

// Combine results and mark containers as expanded
foreach (var message in resultMessages)
{
    foreach (var content in message.Contents)
    {
        allContents.Add(content);

        // ✅ If this result is for a container, mark the plugin as expanded
        if (content is FunctionResultContent functionResult &&
            containerExpansions.TryGetValue(functionResult.CallId, out var pluginName))
        {
            expandedPlugins.Add(pluginName);
        }
    }
}
```

**Why Two Execution Paths?**
The agent has two strategies for tool execution:
- **Sequential**: Used for single tool calls (no parallelization overhead)
- **Parallel**: Used for multiple tool calls (better performance)

Both paths had the same bypass bug and needed the same fix.

---

## Why Containers Were Initially Special-Cased

### The Original Reasoning (Flawed)

Someone (probably us in an earlier session) thought:
> "Container functions don't do real work - they just flip a boolean and return a message. Why run them through the full pipeline? Let's optimize by handling them inline!"

This seemed logical because:
1. **No computation**: Containers don't perform calculations
2. **Simple result**: Just return "Expanded MathPlugin"
3. **Performance**: Skip the async invocation overhead
4. **Permissions**: No need to ask permission to see more functions

### Why This Was Wrong

**Containers ARE real function invocations** that should be:

1. **Logged** 📝
   - Expansion events are important for debugging
   - Need to see when plugins are expanded and why
   - Example: `[LOG][PRE] Function: MathPlugin` is valuable telemetry

2. **Permission-Checked** 🔐
   - You might want to control plugin access
   - Example: "Don't let the agent expand FileSystemPlugin without permission"
   - Bypassing permissions is a security hole

3. **Observed** 📊
   - Telemetry should track expansion patterns
   - Example: "Which plugins are most used? When do they expand?"
   - Useful for optimizing plugin organization

4. **Filtered** 🎯
   - Scoped filters can customize behavior per-plugin
   - Example: Custom logging for MathPlugin only
   - Filters expect to see ALL function calls

### The Correct Abstraction

From the AI model's perspective:
- It sends `FunctionCallContent` for `MathPlugin()`
- This is indistinguishable from any other function call
- The agent should treat it consistently

**Container invocation is a meaningful event**, not just a side effect to be optimized away.

---

## Bug #4: Container Expansion Results Polluting Chat History

**File**: `HPD-Agent/Agent/Agent.cs` (RunAgenticAsync method, lines 862-904)
**Date Discovered**: 2025-01-17
**Impact**: Medium (token waste, history pollution)

### The Problem

Container expansion results were being added to the **persistent chat history** (`currentMessages`), even though they become **stale and useless** after the message turn ends.

**What Was Happening**:
1. User says: "multiply 393943 and 394934"
2. Agent invokes `ExpandMathPlugin()` container
3. Container returns: `"ExpandMathPlugin expanded. Available functions: Add, Multiply, ... Best practices: ..."`
4. This result gets added to `currentMessages` (persistent history)
5. Agent uses `Multiply()` and returns result
6. Message turn ends → `expandedPlugins` goes out of scope (local variable)
7. **Next message turn**: Containers are Collapse again, but old expansion message is still in history
8. LLM sees stale expansion message referencing functions that are no longer available
9. Each expansion adds more stale messages to history

**Why This Matters**:
- **Token waste**: Old expansion messages consume tokens on every future message
- **History pollution**: Stale references to unavailable functions
- **Confusion**: LLM might think functions are available when they're not
- **PostExpansionInstructions waste**: Instructions accumulate but are useless after reset

### The Discovery Process

**User Testing Revealed**:
User asked the LLM in a follow-up turn: "Do you remember seeing these instructions?" (showing the PostExpansionInstructions).

LLM response: **"No, I do not have that specific text in my instructions."**

But then user asked: "Do you see the results of the previous calculations you made?"

LLM response: **"Yes, I can. I see the multiplication result: 155581484762"**

**Conclusion**: The LLM could see regular function results but NOT the container expansion instructions in the next turn!

### Why The Original Mechanism "Worked"

The original code DID add container results to history (line 867):
```csharp
// Add tool results to history
currentMessages.Add(toolResultMessage);  // ALL results, including containers
```

But it "worked" because:
1. `expandedPlugins` is a local variable created at the start of `RunAgenticAsync` (line 585)
2. When the method returns, `expandedPlugins` goes out of scope
3. Next message turn creates a NEW empty `expandedPlugins`
4. Old expansion messages are in history, but containers are Collapse again
5. The expansion messages become **semantically useless** (reference unavailable functions)
6. LLM learns to ignore them (but they still waste tokens)

**This is not a good design!** It relies on the LLM "figuring out" that the stale messages are useless, rather than explicitly filtering them.

### The Fix

**Explicitly filter container expansion results from persistent history**:

```csharp
// Execute tools
var toolResultMessage = await _toolScheduler.ExecuteToolsAsync(
    currentMessages, toolRequests, effectiveOptions, agentRunContext, _name, expandedPlugins, effectiveCancellationToken).ConfigureAwait(false);

// Filter out container expansion results from persistent history
// Container expansions are only relevant within the current message turn
// since expansion state resets after each message turn (expandedPlugins is local variable)
// Without filtering, expansion messages accumulate in history but become stale/useless
var nonContainerResults = new List<AIContent>();
foreach (var content in toolResultMessage.Contents)
{
    if (content is FunctionResultContent result)
    {
        // Check if this result is from a container function
        var isContainerResult = toolRequests.Any(tr =>
            tr.CallId == result.CallId &&
            effectiveOptions?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(t => t.Name == tr.Name)
                ?.AdditionalProperties?.TryGetValue("IsContainer", out var isContainer) == true &&
            isContainer is bool isCont && isCont);

        if (!isContainerResult)
        {
            nonContainerResults.Add(content);
        }
    }
    else
    {
        nonContainerResults.Add(content);
    }
}

// Add filtered results to persistent history (excluding container expansions)
// This keeps history clean and avoids accumulating stale expansion messages
if (nonContainerResults.Count > 0)
{
    var filteredMessage = new ChatMessage(ChatRole.Tool, nonContainerResults);
    currentMessages.Add(filteredMessage);  // ← Only non-container results
}

// Add ALL results (including container expansions) to turn history
// The LLM needs to see container expansions within the current turn to know what functions are available
turnHistory.Add(toolResultMessage);  // ← LLM sees containers within turn
```

### Key Design Points

**1. Two History Contexts**:
- `currentMessages` - Persistent history across message turns (FILTERED - no containers)
- `turnHistory` - Current turn context sent to LLM (UNFILTERED - includes containers)

**2. Container Results Are Ephemeral**:
- Needed within the turn (so LLM sees available functions and instructions)
- Useless after the turn (containers collapse, functions unavailable)
- Should not persist in history

**3. Regular Function Results Persist**:
- `Add(5, 3) → 8` stays in history (useful context)
- `ExpandMathPlugin() → "...Best practices..."` does NOT stay in history (becomes stale)

**4. PostExpansionInstructions Benefit Most**:
- Without filtering: Instructions accumulate in history, waste tokens
- With filtering: Instructions provide value when needed, then disappear

### Testing & Verification

**Before Fix** (❌ Polluted History):
```
Turn 1:
  User: "multiply 393943 and 394934"
  Tool Calls: ExpandMathPlugin(), Multiply(393943, 394934)
  Results in history:
    - ExpandMathPlugin → "Expanded. Available functions: Add, Multiply... Best practices..."
    - Multiply → 155581484762

Turn 2:
  User: "do you remember those instructions?"
  currentMessages contains:
    - Turn 1: ExpandMathPlugin expansion message (STALE - containers Collapse)
    - Turn 1: Multiply result (still useful)
  LLM: "No, I don't remember those instructions" (ignoring stale message)
```

**After Fix** (✅ Clean History):
```
Turn 1:
  User: "multiply 393943 and 394934"
  Tool Calls: ExpandMathPlugin(), Multiply(393943, 394934)
  Results in turnHistory (sent to LLM): Both expansion and multiplication
  Results in currentMessages (persistent): Only multiplication result

Turn 2:
  User: "do you remember those instructions?"
  currentMessages contains:
    - Turn 1: Multiply result (still useful)
    - (No stale expansion message)
  LLM: "No" (correctly doesn't see instructions - they weren't persisted)
```

### Why This Fix Is Better Than Original

**Original "Works"**:
- Containers collapse automatically (local variable)
- Expansion messages become semantically useless
- LLM learns to ignore them
- ⚠️ But still wastes tokens on every message

**New Fix**:
- Containers collapse automatically (same mechanism)
- Expansion messages explicitly filtered out
- History stays clean
- ✅ No token waste, no confusion

**The Difference**: **Explicit > Implicit**. Don't rely on the LLM "figuring out" that stale messages are useless. Filter them proactively.

### Impact on PostExpansionInstructions

This fix makes PostExpansionInstructions even more valuable:

**Token Economics**:
```
Without Fix:
- Turn 1: PostExpansionInstructions added (200 tokens)
- Turn 2: Stale instructions still in history (200 wasted tokens)
- Turn 3: Stale instructions still in history (200 wasted tokens)
= 400 tokens wasted

With Fix:
- Turn 1: PostExpansionInstructions visible in turn (200 tokens)
- Turn 2: Instructions filtered out (0 tokens)
- Turn 3: Instructions filtered out (0 tokens)
= 0 tokens wasted
```

**Scaling**:
If you have 5 plugins with instructions (200 tokens each):
- Without fix: Up to 5000 tokens wasted after 5 expansions
- With fix: 0 tokens wasted (all filtered)

---

## v2.0: Extending to MCP and Frontend Tools

### The New Challenge

After fixing the three bugs for C# plugin scoping, we realized we had **two more sources of AI functions** that couldn't use the source generator:

1. **MCP Tools** - External tools from Model Context Protocol servers
2. **Frontend Tools** - AGUI tools executed by the frontend (human-in-the-loop)

These tools are created at **runtime**, not compile-time, so the source generator can't add metadata.

### The Solution: Runtime Wrapper

We created `ExternalToolScopingWrapper.cs` to apply the same metadata pattern at runtime:

```csharp
// For MCP tools - group by server
var (container, scopedTools) = ExternalToolScopingWrapper.WrapMCPServerTools(
    serverName: "filesystem",
    tools: mcpToolsFromServer,
    maxFunctionNamesInDescription: 10
);

// For Frontend tools - single container for all UI tools
var (container, scopedTools) = ExternalToolScopingWrapper.WrapFrontendTools(
    tools: frontendTools,
    maxFunctionNamesInDescription: 10
);
```

### Key Design Decisions

#### Decision #1: Template Descriptions

**Problem**: Users don't write `[PluginScope("description")]` for MCP/Frontend tools - they come from external sources.

**Solution**: Auto-generate descriptions from function names:

```csharp
// Example: MCP server with 15 functions
"MCP Server 'filesystem'. Contains 15 functions: ReadFile, WriteFile, DeleteFile, CopyFile, MoveFile, ListDirectory, SearchFiles, GetFileInfo, CreateDirectory, MoveDirectory and 5 more"

// Example: Frontend tools with 12 functions
"Frontend UI tools for user interaction. Contains 12 functions: ConfirmAction, ShowNotification, RequestInput, ShowProgress, UpdateStatus, CancelAction and 6 more"
```

**Why This Works**:
- Agent can infer purpose from function names
- Still provides context about what's in the container
- `MaxFunctionNamesInDescription` config controls verbosity

#### Decision #2: MCP Naming Convention

**Problem**: MCP server named "filesystem" might conflict with a C# plugin named "FileSystemPlugin" (both use filesystem functions).

**Solution**: Prefix MCP containers with `MCP_`:

```csharp
var containerName = $"MCP_{serverName}";  // "MCP_filesystem"
```

**Benefits**:
- Clear distinction between C# plugins and MCP tools
- No naming conflicts
- Easy to identify source type

#### Decision #3: Source Type Metadata

**Problem**: Need to distinguish between C# plugins, MCP tools, and Frontend tools for debugging/telemetry.

**Solution**: Add `SourceType` to AdditionalProperties:

```csharp
AdditionalProperties = new Dictionary<string, object>
{
    ["IsContainer"] = true,
    ["PluginName"] = "MCP_filesystem",
    ["SourceType"] = "MCP",           // ← New in v2.0
    ["MCPServerName"] = "filesystem"   // ← Extra context for MCP
}
```

**Use Cases**:
- Telemetry can track MCP vs C# usage patterns
- Debugging can filter by source type
- Future features can customize behavior per source

#### Decision #4: One Container for All Frontend Tools

**Problem**: Frontend tools are all UI interactions (ConfirmAction, ShowNotification, etc.). Should each get its own container or one shared container?

**Solution**: Single `FrontendTools` container.

**Why**:
- Frontend tools are logically related (all UI operations)
- Human-in-the-loop pattern means they're used differently than backend functions
- Simpler UX - agent doesn't need to understand frontend tool categories

### Integration Points

#### MCP Integration (`MCPClientManager.cs`)

**Location**: Tool loading during agent startup

```csharp
public async Task<List<AIFunction>> LoadToolsFromManifestAsync(
    string manifestPath,
    bool enableScoping = false,        // ← NEW
    int maxFunctionNamesInDescription = 10,  // ← NEW
    CancellationToken cancellationToken = default)
{
    foreach (var serverConfig in enabledServers)
    {
        var tools = await LoadServerToolsAsync(serverConfig, cancellationToken);

        if (enableScoping && tools.Count > 0)
        {
            // ✅ Wrap at runtime
            var (container, scopedTools) = ExternalToolScopingWrapper.WrapMCPServerTools(
                serverConfig.Name, tools, maxFunctionNamesInDescription);

            allTools.Add(container);
            allTools.AddRange(scopedTools);
        }
        else
        {
            allTools.AddRange(tools);  // Original behavior
        }
    }
}
```

**Called From**: `AgentBuilder.Build()` when loading MCP tools

#### Frontend Integration (`AGUIEventConverter.cs`)

**Location**: AGUI input conversion during message processing

```csharp
public ChatOptions ConvertToExtensionsAIChatOptions(
    RunAgentInput input,
    ChatOptions? existingOptions = null,
    bool enableFrontendToolScoping = false,  // ← NEW
    int maxFunctionNamesInDescription = 10)  // ← NEW
{
    // Convert AGUI tools to FrontendTool instances
    var frontendTools = new List<AIFunction>();
    foreach (var tool in input.Tools)
    {
        frontendTools.Add(CreateFrontendToolStub(tool));
    }

    // ✅ Wrap at runtime if scoping enabled
    if (enableFrontendToolScoping && frontendTools.Count > 0)
    {
        var (container, scopedTools) = ExternalToolScopingWrapper.WrapFrontendTools(
            frontendTools, maxFunctionNamesInDescription);

        frontendTools = new List<AIFunction> { container };
        frontendTools.AddRange(scopedTools);
    }

    return options;
}
```

**Called From**: `Agent.ExecuteStreamingTurnAsync()` when processing AGUI input

### The Wrapper Implementation

The wrapper **delegates** to the original tool while adding metadata:

```csharp
private static AIFunction AddParentPluginMetadata(AIFunction tool, string parentPluginName, string sourceType)
{
    // Wrap the existing tool with metadata
    return HPDAIFunctionFactory.Create(
        async (args, ct) => await tool.InvokeAsync(args, ct),  // ← Delegate to original
        new HPDAIFunctionFactoryOptions
        {
            Name = tool.Name,
            Description = tool.Description,
            SchemaProvider = () => tool.JsonSchema,  // ← Preserve schema
            RequiresPermission = true,
            AdditionalProperties = new Dictionary<string, object>
            {
                ["ParentPlugin"] = parentPluginName,
                ["PluginName"] = parentPluginName,
                ["IsContainer"] = false,
                ["SourceType"] = sourceType  // "MCP" or "Frontend"
            }
        });
}
```

**Key Points**:
- Original tool invocation preserved
- Schema information copied
- Metadata added transparently
- No changes to original tool behavior

### Why This Approach Works

**Uniform Metadata Structure**:
```
All tools have the same metadata format:
- C# Plugin: IsContainer/ParentPlugin + PluginName
- MCP Tool: IsContainer/ParentPlugin + PluginName + SourceType="MCP"
- Frontend Tool: IsContainer/ParentPlugin + PluginName + SourceType="Frontend"
```

**`PluginScopingManager` doesn't care**:
```csharp
// This code works for ALL three sources:
if (IsContainer(tool))
{
    var pluginName = GetPluginName(tool);
    if (!expandedPlugins.Contains(pluginName))
    {
        containers.Add(tool);
    }
}
else
{
    var parentPlugin = GetParentPlugin(tool);
    if (parentPlugin != null && expandedPlugins.Contains(parentPlugin))
    {
        expandedFunctions.Add(tool);
    }
}
```

No special cases needed! The manager just checks metadata, regardless of source.

---

## Architecture Overview

### How The Pieces Fit Together

```
┌─────────────────────────────────────────────────────────────────┐
│ 1a. SOURCE GENERATOR (Compile-Time) - C# Plugins               │
├─────────────────────────────────────────────────────────────────┤
│ Input:  [PluginScope] on MathPlugin class                       │
│ Output: Generated code with:                                     │
│   • Container function (MathPlugin)                             │
│     - Metadata: IsContainer=true, PluginName="MathPlugin"       │
│   • Individual functions (Add, Multiply, etc.)                  │
│     - Metadata: ParentPlugin="MathPlugin"                       │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ 1b. RUNTIME WRAPPER (Startup/Message) - MCP & Frontend         │
├─────────────────────────────────────────────────────────────────┤
│ MCP Tools (at server load):                                     │
│   • MCPClientManager loads tools from server                    │
│   • ExternalToolScopingWrapper.WrapMCPServerTools()            │
│   • Creates MCP_filesystem container + scoped tools             │
│   • Metadata: IsContainer/ParentPlugin + SourceType="MCP"      │
│                                                                  │
│ Frontend Tools (per AGUI message):                              │
│   • AGUIEventConverter receives input.Tools                     │
│   • ExternalToolScopingWrapper.WrapFrontendTools()             │
│   • Creates FrontendTools container + scoped tools              │
│   • Metadata: IsContainer/ParentPlugin + SourceType="Frontend" │
└─────────────────────────────────────────────────────────────────┘
                               ↓
┌─────────────────────────────────────────────────────────────────┐
│ 2. AGENT BUILDER (Startup)                                      │
├─────────────────────────────────────────────────────────────────┤
│ • Loads C# plugins (from source generator)                      │
│ • Loads MCP tools (wrapped if ScopeMCPTools=true)              │
│ • Combines ALL functions (C# + MCP) with metadata               │
│ • Adds them to agent.DefaultOptions.Tools                       │
│ • Frontend tools added per-message during AGUI processing       │
└─────────────────────────────────────────────────────────────────┘
                               ↓
┌─────────────────────────────────────────────────────────────────┐
│ 3. PLUGIN SCOPING MANAGER (Per Agent Turn - Runtime)           │
├─────────────────────────────────────────────────────────────────┤
│ Input:  allTools (C# + MCP + Frontend) + expandedPlugins       │
│ Logic:  Check metadata (SAME for all sources):                  │
│   • IsContainer=true && NOT expanded → Show container           │
│   • ParentPlugin set && IS expanded → Show function             │
│   • No ParentPlugin → Always show (non-plugin function)         │
│ Output: Filtered tools list for this agent turn                 │
│ Note: Doesn't care about SourceType - uniform handling          │
└─────────────────────────────────────────────────────────────────┘
                               ↓
┌─────────────────────────────────────────────────────────────────┐
│ 4. AGENT EXECUTION (Per Function Call - Runtime)               │
├─────────────────────────────────────────────────────────────────┤
│ PHASE 0: Identify containers (ANY source type)                  │
│   • Check IsContainer metadata                                  │
│   • Track callId → pluginName for later                        │
│                                                                  │
│ PHASE 1 & 2: Execute ALL functions (containers + regular)      │
│   • Permission checks                                           │
│   • Filter pipeline (logging, observability, scoped)           │
│   • Function invocation (delegates to original for wrappers)   │
│                                                                  │
│ PHASE 3: Mark containers as expanded (AFTER success)           │
│   • If callId in containerExpansions → Add to expandedPlugins  │
└─────────────────────────────────────────────────────────────────┘
```

### Critical Flow Points

**Point A: Metadata Must Survive Source Generation**
- Bug #1 caused metadata loss during partial class merging
- Without metadata, no container gets generated
- Fix: Preserve `HasScopeAttribute` from ANY partial class

**Point B: Metadata Must Be Accessible at Runtime**
- `function.AdditionalProperties` is the correct location (inherited from `AITool` base class)
- NOT `function.Metadata.AdditionalProperties` (doesn't exist - AI hallucination)
- PluginScopingManager reads from `function.AdditionalProperties`
- Note: The inheritance chain is `AITool` → `AIFunctionDeclaration` → `AIFunction`
- `AdditionalProperties` is defined on `AITool`, and `AIFunction` inherits it

**Point C: Containers Must Go Through Filter Pipeline**
- Bugs #2 and #3 bypassed the pipeline for "performance"
- Fix: Track containers but execute them like any other function
- Expansion happens AFTER successful invocation

---

## Common Misconceptions & Gotchas

### Misconception #1: "Metadata is in function.Metadata.AdditionalProperties"

**Wrong** ❌:
```csharp
var isContainer = function.Metadata?.AdditionalProperties
    ?.TryGetValue("IsContainer", out var value) == true;
```

**Right** ✅:
```csharp
var isContainer = function.AdditionalProperties
    ?.TryGetValue("IsContainer", out var value) == true;
```

**Why**:
- The inheritance chain: `AITool` → `AIFunctionDeclaration` → `AIFunction`
- `AdditionalProperties` is defined on `AITool` (the base class)
- `AIFunction` inherits this property through the chain
- There is NO `Metadata` property in Microsoft.Extensions.AI at any level
- AI coding assistants may hallucinate this based on other APIs they've seen (like OpenAI's function calling API)

**Proof from Microsoft.Extensions.AI source code**:
```csharp
// AITool.cs:15 - Base class
public abstract class AITool
{
    // Line 29 - AdditionalProperties defined here
    public virtual IReadOnlyDictionary<string, object?> AdditionalProperties
        => EmptyReadOnlyDictionary<string, object?>.Instance;
}

// AIFunctionDeclaration.cs:16 - Inherits from AITool
public abstract class AIFunctionDeclaration : AITool
{
    // Inherits AdditionalProperties from AITool
}

// AIFunction.cs:12 - Inherits from AIFunctionDeclaration
public abstract class AIFunction : AIFunctionDeclaration
{
    // Inherits AdditionalProperties through the chain
}
```

### Misconception #2: "Containers don't need to go through filters"

**Wrong Thinking** ❌:
> "Containers just flip a boolean. Why waste cycles on permissions/logging?"

**Right Thinking** ✅:
> "Container invocation is a meaningful event that should be logged, observed, and controlled like any other function call."

**Why It Matters**:
- Logging: Need to see when plugins expand
- Permissions: May want to control plugin access
- Observability: Telemetry tracks expansion patterns
- Scoped filters: Plugin-specific behavior must work

**The Abstraction**:
From the AI model's perspective, calling `MathPlugin()` is identical to calling `Add()`. Both are `FunctionCallContent` tool requests that should be handled consistently.

### Misconception #3: "Expansion state should persist across messages"

**Current Design** (Correct for now):
- Expansion state is **message-scoped**
- `expandedPlugins` is a local variable in `RunAgenticAsync`
- Auto-collapses when the message turn completes
- Next user message starts fresh with all plugins Collapse

**Why**:
- Simplicity: No state management or persistence needed
- Predictability: Each message starts fresh - no hidden state
- No memory leaks: Expansion state is GC'd automatically
- Fresh context: Each message re-evaluates what plugins it needs

**Future Enhancement**:
If you want persistent expansion across messages:
1. Store `expandedPlugins` in `Conversation` or `Agent` state
2. Add a collapse command: "Collapse MathPlugin"
3. Auto-collapse after N turns of inactivity
4. Serialize/deserialize expansion state

But for v1.0, message-scoped is simpler and works well.

### Misconception #4: "Only MathPlugin needs plugin scoping"

**When to Use Plugin Scoping**:

✅ **Good Candidates**:
- Large plugins with many related functions (10+ functions)
- Example: MathPlugin (Add, Multiply, Sin, Cos, Sqrt, Log, Abs, Min, Max, etc.)
- Example: FileSystemPlugin (Read, Write, Delete, List, Search, Copy, Move, etc.)
- Example: DatabasePlugin (Create, Read, Update, Delete, Query, etc.)

❌ **Poor Candidates**:
- Small plugins with 1-3 functions (overhead not worth it)
- Core utilities always needed (Memory, Planning - should be always visible)
- Plugins where all functions are equally likely to be used

**Rule of Thumb**:
If the plugin has 5+ functions and they're logically grouped, use `[PluginScope]`.

---

## Testing & Validation

### How to Verify It's Working

**Test 1: Check Registered Tools**
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
```

**Test 2: Check Logging Output**

With `.WithLogging()` enabled, you should see:

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

If you DON'T see the first block (MathPlugin logs), the container is bypassing filters.

**Test 3: Check Permission Prompts**

With `.WithConsolePermissions()` enabled, you should be prompted for:
1. **MathPlugin** (container expansion)
2. **Add** (individual function)

If you're only prompted for Add, the container is bypassing permission filters.

### Debugging Commands

**Force Clean Rebuild**:
```bash
dotnet clean
rm -rf **/obj
dotnet build
```

Use this if:
- Source generator changes aren't reflected
- Metadata seems missing
- Container not showing up

**Check Generated Code**:
```bash
# Find the generated registration file
find . -name "*MathPluginRegistration.g.cs"

# View it
cat ./AgentConsoleTest/obj/Debug/net9.0/generated/HPD-Agent.SourceGenerator/HPD.Agent.SourceGenerator.HPDPluginSourceGenerator/MathPluginRegistration.g.cs
```

Look for:
- `CreateMathPluginContainer()` method (should exist)
- `["IsContainer"] = true` in AdditionalProperties
- `["ParentPlugin"] = "MathPlugin"` on individual functions

---

## Future Enhancements (Not Yet Implemented)

### 1. Persistent Expansion State
**Current**: Message-scoped (auto-collapses)
**Future**: Persist across messages

**Implementation**:
- Store `expandedPlugins` in `Conversation` or `Agent`
- Add commands: "Collapse MathPlugin", "Show expanded plugins"
- Auto-collapse after N turns of inactivity

**Benefits**:
- Less repeated expansion for frequently used plugins
- User can control what's expanded

**Tradeoffs**:
- More complex state management
- Potential memory leaks if not cleaned up
- Less predictable (hidden state)

### 2. Nested Plugin Scopes
**Current**: Single level (plugin → functions)
**Future**: Multi-level (plugin → category → functions)

**Example**:
```
MathPlugin [CONTAINER]
  ├─ BasicMath [CONTAINER]
  │   ├─ Add
  │   └─ Multiply
  └─ Trigonometry [CONTAINER]
      ├─ Sin
      ├─ Cos
      └─ Tan
```

**Implementation**:
- Extend `[PluginScope]` to support parent parameter
- Update PluginScopingManager to handle hierarchy
- Track expansion at each level

### 3. Smart Auto-Expansion
**Current**: Agent must explicitly call container
**Future**: Analyze query and pre-expand likely plugins

**Example**:
```
User: "calculate sine of 45 degrees"
Agent: (auto-expands MathPlugin before responding)
```

**Implementation**:
- Add query analysis to detect keywords
- Map keywords to plugins ("calculate" → MathPlugin, "file" → FileSystemPlugin)
- Pre-expand before sending to LLM

**Benefits**:
- Skip the first turn for obvious cases
- Better user experience

**Tradeoffs**:
- May expand unnecessary plugins (wasted tokens)
- Less explicit reasoning from agent

### 4. Rust FFI Support
**Current**: C# only
**Future**: Rust plugins with same behavior

**Implementation**:
- Create Rust proc macro equivalent to C# source generator
- Same `#[plugin_scope("description")]` attribute
- Generate FFI-compatible registration code

**Challenges**:
- Rust type system more complex
- FFI boundary requires careful memory management
- Need to maintain feature parity

---

## Key Takeaways (For Future You)

### When Debugging Plugin Scoping Issues:

1. **Check Metadata First**
   - Use the debug output to verify `[CONTAINER]` and `[Plugin: X]` tags
   - If missing, check source generator (Bug #1)

2. **Check Filter Execution**
   - Enable `.WithLogging()` and look for container logs
   - If missing, check execution flow (Bugs #2 & #3)

3. **Check Metadata Access**
   - Use `function.AdditionalProperties` directly (NOT `function.Metadata.AdditionalProperties`)
   - Trust the working code, not AI suggestions

4. **Force Clean Rebuild**
   - Source generator changes require cleaning obj folders
   - `dotnet clean` + `rm -rf **/obj` + `dotnet build`

### Design Principles:

1. **Containers are functions** - treat them consistently
2. **Metadata flows from compile-time to runtime** - preserve it carefully
3. **Filters see everything** - no special cases
4. **Message-scoped state** - keep it simple

### Common AI Hallucinations to Ignore:

- ❌ "Use `function.Metadata.AdditionalProperties`" (doesn't exist)
- ❌ "Containers should be handled differently" (they shouldn't)
- ❌ "Skip filters for performance" (wrong abstraction)

---

## References

**Related Documentation**:
- [plugin-scoping.md](./plugin-scoping.md) - User-facing documentation
- [dynamic-plugin-metadata.md](./dynamic-plugin-metadata.md) - How conditional functions work
- [orchestration-framework.md](./orchestration-framework.md) - Agent architecture

**Key Files**:
- `HPD-Agent.SourceGenerator/SourceGeneration/HPDPluginSourceGenerator.cs` - Container generation
- `HPD-Agent/Agent/PluginScopingManager.cs` - Runtime filtering
- `HPD-Agent/Agent/Agent.cs` - Execution flow (ExecuteInParallelAsync, ExecuteSequentiallyAsync)
- `HPD-Agent/Plugins/HPD-AIFunctionFactory.cs` - AIFunction creation with metadata

**Microsoft.Extensions.AI References**:
- `Reference/extensions/src/Libraries/Microsoft.Extensions.AI.Abstractions/Tools/AITool.cs` - Base class with AdditionalProperties
- `Reference/extensions/src/Libraries/Microsoft.Extensions.AI.Abstractions/Functions/AIFunction.cs` - Function class

---

## Changelog

### 2025-01-17 - v2.1 (Post-Expansion Instructions + History Filtering)

**New Features**:
- ✅ Post-Expansion Instructions for C# plugins, MCP tools, and Frontend tools
- ✅ Container expansion results explicitly filtered from persistent history
- ✅ Clean history management (no stale expansion messages)
- ✅ Optimal token economics (instructions only consume tokens when used)

**Bugs Fixed**:
- Bug #4: Container expansion results polluting chat history
  - Expansion messages were accumulating in persistent history
  - Became stale/useless after message turn ended (containers collapse)
  - Fix: Explicit filtering - only non-container results added to `currentMessages`
  - Impact: Eliminates token waste from stale expansion messages

**Files Modified**:
- `Agent.cs` - Added explicit filtering of container results from `currentMessages`
- `PluginScopeAttribute.cs` - Added `PostExpansionInstructions` parameter
- `PluginInfo.cs` - Added `PostExpansionInstructions` property
- `HPDPluginSourceGenerator.cs` - Extract and embed post-expansion instructions
- `ExternalToolScopingWrapper.cs` - Added post-expansion instructions support
- `AgentConfig.cs` - Added `MCPServerInstructions` and `FrontendToolsInstructions`

**Key Insight**:
- Two history contexts: `currentMessages` (persistent, filtered) vs `turnHistory` (current turn, unfiltered)
- Container results needed within turn (LLM sees available functions)
- Container results useless after turn (functions unavailable, containers Collapse)
- **Explicit > Implicit**: Don't rely on LLM ignoring stale messages, filter proactively

**Testing**:
- Verified LLM does NOT see expansion instructions in next turn (correct behavior)
- Verified LLM DOES see regular function results in next turn (correct behavior)
- Confirmed no token waste from accumulated expansion messages

### 2025-01-12 - v2.0 (MCP & Frontend Tool Support)

**New Features**:
- ✅ Runtime wrapper for external tools (`ExternalToolScopingWrapper.cs`)
- ✅ MCP tool scoping (group by server, template descriptions)
- ✅ Frontend tool scoping (single container for AGUI tools)
- ✅ Source type tracking (`SourceType` metadata)
- ✅ Template description generation from function names
- ✅ Independent configuration per tool source

**New Configuration**:
```csharp
PluginScoping = new PluginScopingConfig
{
    Enabled = true,                // C# plugins
    ScopeMCPTools = true,          // MCP tools
    ScopeFrontendTools = true,     // Frontend tools
    MaxFunctionNamesInDescription = 10
}
```

**Files Added**:
- `HPD-Agent/Agent/ExternalToolScopingWrapper.cs`

**Files Modified**:
- `AgentConfig.cs` - Added new config options
- `MCPClientManager.cs` - Added scoping parameters
- `AGUIEventConverter.cs` - Frontend tool scoping support
- `Agent.cs` - Pass scoping config
- `AgentBuilder.cs` - Pass scoping config to MCP loader

**Key Design Decisions**:
- Template descriptions auto-generated from function names
- MCP containers prefixed with `MCP_` to avoid conflicts
- Single container for all frontend tools (logically related)
- Uniform metadata structure across all sources
- PluginScopingManager source-agnostic (no special cases)

**Tested With**:
- MathPlugin (C# plugin, 6 functions)
- MCP filesystem server (15 functions)
- Frontend AGUI tools (10 functions)
- Mixed scenarios with all three sources active

### 2025-01-12 - v1.0 (Initial Release)
**Fixed**:
- Bug #1: Source generator losing metadata during partial class merging
- Bug #2: Container bypass in parallel execution path
- Bug #3: Container bypass in sequential execution path

**Verified**:
- Logging filters work for containers
- Permission filters work for containers
- Scoped filters work for containers
- Metadata accessible at runtime
- Two-turn expansion flow works correctly

**Tested With**:
- MathPlugin with 6 functions (C# only)
- Console test application
- Logging filter + Console permission filter
- Multiple agent turns in same message

---

## Questions For Future You

**Q: Why did we make containers go through the full pipeline?**
A: Because container invocation is a meaningful event that should be logged, observed, and controlled. Bypassing filters broke logging, observability, permissions, and scoped filters.

**Q: Why is expansion state message-scoped instead of persistent?**
A: Simplicity and predictability. Each message starts fresh with no hidden state. Future enhancement could add persistence if needed.

**Q: Why is metadata in `function.AdditionalProperties` not `function.Metadata.AdditionalProperties`?**
A: Because that's how Microsoft.Extensions.AI designed it. `AITool` has `AdditionalProperties` as a direct virtual property. There is no `Metadata` property.

**Q: Can I trust AI coding assistants for this code?**
A: NO. They hallucinate about the metadata location and suggest bypassing filters. Trust the working code and this documentation.

**Q: What if I add more plugins with `[PluginScope]`?**
A: It should just work. The system is designed to handle multiple scoped plugins simultaneously. Each gets its own container, and expansion is tracked independently.

**Q: How does plugin scoping work with MCP tools?**
A: MCP tools are wrapped at runtime when loading from the manifest. Each MCP server gets its own container (e.g., `MCP_filesystem`). Enable with `ScopeMCPTools = true` in config.

**Q: How does plugin scoping work with Frontend tools?**
A: Frontend tools are wrapped per-message during AGUI input processing. All frontend tools go in a single `FrontendTools` container. Enable with `ScopeFrontendTools = true` in config.

**Q: Why prefix MCP containers with "MCP_"?**
A: To avoid naming conflicts with C# plugins. A C# plugin named "FileSystemPlugin" won't conflict with an MCP server named "filesystem" (becomes "MCP_filesystem").

**Q: What if I want nested scopes?**
A: Not implemented yet. See "Future Enhancements" section for how to add it.

---

**Last Updated**: 2025-01-17
**Next Review**: When adding new plugin scoping features or encountering issues
**Contact**: This is your own code - you wrote this! Trust your past self.

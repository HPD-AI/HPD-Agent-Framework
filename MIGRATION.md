# Migration Guide: 0.1.x → 0.2.0

This guide helps you upgrade from HPD-Agent 0.1.x to 0.2.0, which introduces terminology changes to improve API clarity before 1.0.

## Quick Reference

| Old | New | Find & Replace Pattern |
|-----|-----|------------------------|
| `Plugin` (class suffix) | `Tools` | `class *Plugin` → `class *Tools` |
| `IPluginMetadata` | `IToolMetadata` | Direct replacement |
| `FrontendTool` | `ClientTool` | Direct replacement |
| `FrontendPlugin` | `ClientToolGroup` | Direct replacement |
| `frontendPlugins` | `clientToolGroups` | Direct replacement |

---

## C# Migration

### Step 1: Rename Tool Classes

**Before:**
```csharp
public class WeatherPlugin
{
    [AIFunction("Get weather for a location")]
    public string GetWeather(string city) => $"Weather in {city}: Sunny";
}

public class FileSystemPlugin
{
    [AIFunction("Read a file")]
    public string ReadFile(string path) => File.ReadAllText(path);
}
```

**After:**
```csharp
public class WeatherTools
{
    [AIFunction("Get weather for a location")]
    public string GetWeather(string city) => $"Weather in {city}: Sunny";
}

public class FileSystemTools
{
    [AIFunction("Read a file")]
    public string ReadFile(string path) => File.ReadAllText(path);
}
```

### Step 2: Update AgentBuilder Calls

**Before:**
```csharp
var agent = new AgentBuilder()
     .WithTools<WeatherPlugin>()
     .WithTools<FileSystemPlugin>()
    .WithFrontendTools()
    .Build();
```

**After:**
```csharp
var agent = new AgentBuilder()
    .WithTools<WeatherTools>()
    .WithTools<FileSystemTools>()
    .WithClientTools()
    .Build();
```

### Step 3: Update Metadata Interfaces

**Before:**
```csharp
public class MyPlugin : IPluginMetadata
{
    public string PluginName => "MyPlugin";
    public string Description => "Does something useful";
}
```

**After:**
```csharp
public class MyTools : IToolMetadata
{
    public string ToolsName => "MyTools";
    public string Description => "Does something useful";
}
```

### Step 4: Update Configuration

**Before:**
```csharp
var config = new AgentConfig
{
    Collapsing = new CollapsingConfig
    {
        CollapseFrontendTools = true,
        FrontendToolsInstructions = "IDE tools for user interaction"
    }
};
```

**After:**
```csharp
var config = new AgentConfig
{
    Collapsing = new CollapsingConfig
    {
        CollapseClientTools = true,
        ClientToolsInstructions = "IDE tools for user interaction"
    }
};
```

### Step 5: Update Attributes

**Before:**
```csharp
[PluginMetadata(Name = "Weather", Description = "Weather operations")]
public class WeatherPlugin { }
```

**After:**
```csharp
[ToolMetadata(Name = "Weather", Description = "Weather operations")]
public class WeatherTools { }
```

---

## TypeScript Client Migration

### Step 1: Update Import Statements

**Before:**
```typescript
import {
  FrontendPluginDefinition,
  FrontendToolDefinition,
  FrontendToolInvokeResponse,
  createCollapsedPlugin,
  createExpandedPlugin,
} from '@hpd/hpd-agent-client';
```

**After:**
```typescript
import {
  ClientToolGroupDefinition,
  ClientToolDefinition,
  ClientToolInvokeResponse,
  createCollapsedToolGroup,
  createExpandedToolGroup,
} from '@hpd/hpd-agent-client';
```

### Step 2: Update Type Annotations

**Before:**
```typescript
const idePlugin: FrontendPluginDefinition = {
  name: 'IDE',
  description: 'IDE interaction tools',
  tools: [
    {
      name: 'OpenFile',
      description: 'Open a file',
      parametersSchema: { type: 'object', properties: { path: { type: 'string' } } }
    } as FrontendToolDefinition
  ],
  startCollapsed: true
};
```

**After:**
```typescript
const ideToolGroup: ClientToolGroupDefinition = {
  name: 'IDE',
  description: 'IDE interaction tools',
  tools: [
    {
      name: 'OpenFile',
      description: 'Open a file',
      parametersSchema: { type: 'object', properties: { path: { type: 'string' } } }
    } as ClientToolDefinition
  ],
  startCollapsed: true
};
```

### Step 3: Update Helper Function Calls

**Before:**
```typescript
const plugin = createCollapsedPlugin('IDE', 'IDE tools', tools, {
  skills: mySkills,
  systemPrompt: 'Use these tools for IDE operations'
});

const expandedPlugin = createExpandedPlugin('Utils', utilityTools);
```

**After:**
```typescript
const toolGroup = createCollapsedToolGroup('IDE', 'IDE tools', tools, {
  skills: mySkills,
  systemPrompt: 'Use these tools for IDE operations'
});

const expandedToolGroup = createExpandedToolGroup('Utils', utilityTools);
```

### Step 4: Update AgentClient Usage

**Before:**
```typescript
const client = new AgentClient({
  baseUrl: 'http://localhost:5000',
  frontendPlugins: [idePlugin],
  onFrontendToolInvoke: async (request) => {
    // Handle tool invocation
    return { requestId: request.requestId, content: [], success: true };
  }
});

client.registerPlugin(anotherPlugin);
client.registerPlugins([plugin1, plugin2]);
console.log(client.plugins);
```

**After:**
```typescript
const client = new AgentClient({
  baseUrl: 'http://localhost:5000',
  clientToolGroups: [ideToolGroup],
  onClientToolInvoke: async (request) => {
    // Handle tool invocation
    return { requestId: request.requestId, content: [], success: true };
  }
});

client.registerToolGroup(anotherToolGroup);
client.registerToolGroups([toolGroup1, toolGroup2]);
console.log(client.toolGroups);
```

### Step 5: Update Stream Options

**Before:**
```typescript
await client.stream(conversationId, messages, handlers, {
  frontendPlugins: [myPlugin],
  resetFrontendState: true
});
```

**After:**
```typescript
await client.stream(conversationId, messages, handlers, {
  clientToolGroups: [myToolGroup],
  resetClientState: true
});
```

### Step 6: Update Event Handlers

**Before:**
```typescript
const handlers: EventHandlers = {
  onFrontendToolInvoke: async (request) => {
    console.log(`Tool requested: ${request.toolName}`);
    return createSuccessResponse(request.requestId, 'Done');
  },
  onFrontendPluginsRegistered: (event) => {
    console.log(`Registered: ${event.RegisteredToolGroups.join(', ')}`);
  }
};
```

**After:**
```typescript
const handlers: EventHandlers = {
  onClientToolInvoke: async (request) => {
    console.log(`Tool requested: ${request.toolName}`);
    return createSuccessResponse(request.requestId, 'Done');
  },
  onClientToolGroupsRegistered: (event) => {
    console.log(`Registered: ${event.registeredToolGroups.join(', ')}`);
  }
};
```

### Step 7: Update Event Type Checks

**Before:**
```typescript
import { EventTypes, isFrontendToolInvokeRequestEvent } from '@hpd/hpd-agent-client';

if (event.type === EventTypes.FRONTEND_TOOL_INVOKE_REQUEST) {
  // Handle frontend tool request
}

if (isFrontendToolInvokeRequestEvent(event)) {
  // Type-safe handling
}
```

**After:**
```typescript
import { EventTypes, isClientToolInvokeRequestEvent } from '@hpd/hpd-agent-client';

if (event.type === EventTypes.CLIENT_TOOL_INVOKE_REQUEST) {
  // Handle client tool request
}

if (isClientToolInvokeRequestEvent(event)) {
  // Type-safe handling
}
```

---

## Automated Migration

### Find and Replace (Regex)

Use these patterns in your IDE for bulk updates:

#### C# Files (*.cs)

```
# Class names
Plugin\b → Tools

# Interface
IPluginMetadata → IToolMetadata

# Builder methods
\ .WithTools → .WithTools
\.WithFrontendTools → .WithClientTools

# Config
CollapseFrontendTools → CollapseClientTools
FrontendToolsInstructions → ClientToolsInstructions
```

#### TypeScript Files (*.ts, *.tsx)

```
# Types
FrontendPluginDefinition → ClientToolGroupDefinition
FrontendToolDefinition → ClientToolDefinition
FrontendSkillDefinition → ClientSkillDefinition
FrontendToolAugmentation → ClientToolAugmentation
FrontendToolInvokeRequest → ClientToolInvokeRequest
FrontendToolInvokeResponse → ClientToolInvokeResponse

# Functions
createCollapsedPlugin → createCollapsedToolGroup
createExpandedPlugin → createExpandedToolGroup

# Properties
frontendPlugins → clientToolGroups
resetFrontendState → resetClientState

# Event handlers
onFrontendToolInvoke → onClientToolInvoke
onFrontendPluginsRegistered → onClientToolGroupsRegistered

# Methods
registerPlugin\( → registerToolGroup(
registerPlugins\( → registerToolGroups(
unregisterPlugin\( → unregisterToolGroup(
\.plugins\b → .toolGroups
```

---

## Breaking Wire Protocol Changes

If you have custom integrations that parse events directly:

### Event Type Changes

| Old Event Type | New Event Type |
|----------------|----------------|
| `FRONTEND_TOOL_INVOKE_REQUEST` | `CLIENT_TOOL_INVOKE_REQUEST` |
| `FRONTEND_TOOL_INVOKE_RESPONSE` | `CLIENT_TOOL_INVOKE_RESPONSE` |
| `FRONTEND_PLUGINS_REGISTERED` | `CLIENT_TOOL_GROUPS_REGISTERED` |

### Request Body Changes

**Before:**
```json
{
  "messages": [...],
  "frontendPlugins": [...],
  "resetFrontendState": true
}
```

**After:**
```json
{
  "messages": [...],
  "clientToolGroups": [...],
  "resetClientState": true
}
```

---

## Getting Help

If you encounter issues during migration:

1. Check that all find/replace operations completed successfully
2. Run your test suite to catch any missed renames
3. Look for TypeScript/C# compiler errors pointing to old names
4. Open an issue at https://github.com/anthropics/hpd-agent/issues

---

## Why These Changes?

1. **"Tools" is clearer than "Plugin"** - Plugins traditionally mean extensible modules, but these are really just tool collections for the agent.

2. **"Client" is clearer than "Frontend"** - "Frontend" implies web UI, but these tools work in any client: CLI, desktop apps, mobile, etc.

3. **Consistency** - The new naming is consistent across C#, TypeScript, documentation, and wire protocols.

4. **Before 1.0** - Making these changes now means a stable API for 1.0 without legacy naming baggage.

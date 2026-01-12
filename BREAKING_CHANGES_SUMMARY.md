# Breaking Changes Summary: v0.1.1 → HEAD

**Quick Reference Guide for Migration**

---

## TL;DR - What Changed?

This release includes **8 major breaking change categories** affecting:
- ✅ All plugin/toolkit code (terminology change)
- ✅ All custom middleware (complete API redesign)
- ✅ Session/conversation persistence (renamed types)
- ✅ Event handling (new standalone library)
- ✅ Frontend integrations (namespace rename)
- ✅ Build configuration (multi-targeting)
- ✅ Package structure (new libraries added)

**Migration Time:** 39-66 hours for medium-sized projects  
**Backward Compatibility:** None - complete rewrite required

---

## Top 10 Breaking Changes

### 1. Plugin → Toolkit/Tools Terminology (HIGH IMPACT)

**Every reference to "Plugin" has been renamed:**

```csharp
// OLD
using HPD.Agent.Plugins;
builder.WithPlugin<FileSystemPlugin>();
HPD-Agent.Plugins.FileSystem

// NEW  
using HPD.Agent.Toolkit;
builder.WithToolkit<FileSystemTools>();
HPD-Agent.Toolkit.FileSystem
```

**Find & Replace Required:**
- `Plugin` → `Toolkit` (in types)
- `plugin` → `toolkit` (in code)
- `Plugins` → `Tools` (in namespaces)
- `HPD-Agent.Plugins.*` → `HPD-Agent.Tools.*`

---

### 2. Middleware Architecture Overhaul (CRITICAL)

**Complete rewrite of IAgentMiddleware interface:**

```csharp
// OLD - Lifecycle hooks
Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CT ct);
Task AfterIterationAsync(AgentMiddlewareContext context, CT ct);
Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CT ct);

// NEW - Specialized contexts
Task BeforeModelRequestAsync(ModelRequest request, CT ct);
Task AfterModelResponseAsync(ModelResponse response, CT ct);  
Task BeforeFunctionAsync(HookContext context, CT ct);
Task OnErrorAsync(ErrorContext context, CT ct);
```

**Action Required:**
- Rewrite ALL custom middleware from scratch
- Replace `AgentMiddlewareContext` with specialized contexts
- Update state management API

---

### 3. ConversationThread → AgentSession (HIGH IMPACT)

```csharp
// OLD
using HPD.Agent.Conversation;
IConversationThreadStore store;
ConversationThread thread;

// NEW
using HPD.Agent.Session;
ISessionStore store;
AgentSession session;
```

**Files Moved:**
- `Conversation/` → `Session/`
- `Checkpointing/` → `Session/`

---

### 4. FrontendTools → ClientTools (MEDIUM IMPACT)

```csharp
// OLD
using HPD.Agent.FrontendTools;
FrontendToolConfig config;
FrontendToolMiddleware middleware;

// NEW
using HPD.Agent.ClientTools;
ClientToolConfig config;
ClientToolMiddleware middleware;
```

---

### 5. Event System Extracted (MEDIUM IMPACT)

**New standalone library:**

```csharp
// Add package reference
<PackageReference Include="HPD.Events" />

// OLD
BidirectionalEventCoordinator coordinator;
agent.MiddlewareEventWriter

// NEW
using HPD.Events;
IEventCoordinator coordinator;
agent.EventCoordinator.EmitAsync(event);
```

---

### 6. Multi-Framework Targeting (BUILD BREAKING)

```xml
<!-- OLD -->
<TargetFramework>net10.0</TargetFramework>

<!-- NEW (in Directory.Build.props) -->
<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
```

**Impact:** NuGet packages now contain 3 framework versions

---

### 7. FluentValidation Removed (LOW IMPACT)

```xml
<!-- REMOVED -->
<PackageReference Include="FluentValidation" Version="12.0.0" />
```

**Impact:** Internal validation only (no action unless extending validation)

---

### 8. New Libraries Added (MEDIUM IMPACT)

**Must know about:**
- `HPD.Events` - Event coordination
- `HPD.MultiAgent` - Multi-agent workflows  
- `HPD.Graph` - Graph-based orchestration
- `HPD-Agent.Audio` - Audio capabilities (TTS/STT)
- `HPD-Agent.Sandbox.Local` - Code sandboxing

---

### 9. Source Generator Changes (MEDIUM IMPACT)

```csharp
// Generated code changed
PluginRegistry.All → ToolkitRegistry.All
HPDPluginSourceGenerator → HPDToolSourceGenerator

// NEW
MiddlewareRegistry.All  // Middleware is now also generated
```

**Action:** Rebuild projects to regenerate registries

---

### 10. AgentBuilder API Changes (LOW-MEDIUM IMPACT)

```csharp
// Many internal fields renamed
_availablePlugins → _availableToolkits
_pluginContexts → _toolkitContexts
LoadPluginRegistryFromAssembly() → LoadToolRegistryFromAssembly()

// NEW fields added
_toolkitOverrides
_middlewareOverrides
_availableMiddlewares
```

---

## Quick Migration Checklist

### Phase 1: Terminology (2-4 hours)
- [ ] Find/Replace: `Plugin` → `Toolkit` (case-sensitive)
- [ ] Find/Replace: `plugin` → `toolkit` (case-sensitive)  
- [ ] Find/Replace: `Plugins` → `Tools` (in namespaces)
- [ ] Update package references: `HPD-Agent.Plugins.*` → `HPD-Agent.Tools.*`
- [ ] Update `using` statements
- [ ] Fix config JSON files (`plugins` → `toolkits`)

### Phase 2: Namespaces (1-2 hours)
- [ ] `HPD.Agent.FrontendTools` → `HPD.Agent.ClientTools`
- [ ] `HPD.Agent.Conversation` → `HPD.Agent.Session`
- [ ] `HPD.Agent.Checkpointing` → `HPD.Agent.Session`
- [ ] Add `using HPD.Events;` where needed

### Phase 3: Types (2-4 hours)
- [ ] `ConversationThread` → `AgentSession`
- [ ] `IConversationThreadStore` → `ISessionStore`
- [ ] `DurableExecutionConfig` → `SessionStoreOptions`
- [ ] `FrontendTool*` → `ClientTool*`
- [ ] `BidirectionalEventCoordinator` → `IEventCoordinator`

### Phase 4: Middleware Rewrite (8-16 hours)
- [ ] Identify all custom middleware classes
- [ ] Rewrite each to new `IAgentMiddleware` interface
- [ ] Replace `AgentMiddlewareContext` usage:
  - Model calls → `ModelRequest`/`ModelResponse`
  - Function calls → `HookContext`
  - Errors → `ErrorContext`
- [ ] Update state management:
  - `context.UpdateState<T>()` → `context.GetMiddlewareStateAsync<T>()`
- [ ] Update event emission:
  - `context.Emit()` → `coordinator.EmitAsync()`

### Phase 5: Session/Persistence (4-8 hours)
- [ ] Update session store implementations
- [ ] Implement `IAssetStore` if using attachments
- [ ] Update builder config:
  - `WithDurableExecution()` → `WithSession()`
- [ ] Update session loading/saving code
- [ ] Test checkpointing

### Phase 6: Events (4-6 hours)
- [ ] Add `HPD.Events` package reference
- [ ] Update event observation code
- [ ] Handle new event types:
  - Structured output events
  - Reasoning events  
  - Asset events
- [ ] Update event emission to use `IEventCoordinator`

### Phase 7: Build & Config (1-2 hours)
- [ ] Review multi-targeting implications
- [ ] Update CI/CD for .NET 8/9/10 outputs
- [ ] Remove hardcoded versions (now in Directory.Build.props)
- [ ] Rebuild all projects

### Phase 8: Testing (16-24 hours)
- [ ] Run ALL tests (expect failures)
- [ ] Update test assertions for new types
- [ ] Test middleware integrations
- [ ] Test session persistence
- [ ] Test event flows
- [ ] Test on all target frameworks (.NET 8/9/10)
- [ ] Test source generator output
- [ ] Regression testing

### Phase 9: Documentation (2-4 hours)
- [ ] Update README examples
- [ ] Update API documentation
- [ ] Update config file examples
- [ ] Update migration notes for team

---

## File Reference Guide

### Renamed Files (Most Common)

| Old (v0.1.1) | New (HEAD) | Category |
|--------------|------------|----------|
| `HPD-Agent.Plugins.FileSystem/FileSystemPlugin.cs` | `HPD-Agent.Toolkit.FileSystem/FileSystemTools.cs` | Plugin→Tool |
| `HPD-Agent/FrontendTools/` | `HPD-Agent/ClientTools/` | Frontend |
| `HPD-Agent/Conversation/ConversationThread.cs` | `HPD-Agent/Session/AgentSession.cs` | Session |
| `HPD-Agent/Middleware/AgentMiddlewareContext.cs` | DELETED (replaced by specialized contexts) | Middleware |
| `HPD-Agent/Plugins/PluginFactory.cs` | DELETED (replaced by ToolkitFactory) | Plugin |

### Deleted Files (No Replacement)

```
HPD-Agent/Plugins/Attributes/AIFunctionAttribute.cs
HPD-Agent/Plugins/Attributes/CollapsedAttribute.cs  
HPD-Agent/Plugins/Attributes/RequiresPermissionAttribute.cs
HPD-Agent/Middleware/Scoping/ToolScopingMiddleware.cs
HPD-Agent/Skills/SkillInstructionMiddleware.cs
```

### New Files (Must Be Aware Of)

```
HPD.Events/  (entire library)
HPD.MultiAgent/  (entire library)
HPD-Agent/Session/AgentSession.cs
HPD-Agent/Session/IAssetStore.cs
HPD-Agent/Middleware/HookContext.cs
HPD-Agent/Middleware/ModelRequest.cs
HPD-Agent/Middleware/ErrorContext.cs
Directory.Build.props  (centralized config)
```

---

## Common Errors & Fixes

### Error 1: "Plugin type not found"
```
Error: Type 'FileSystemPlugin' could not be found
```
**Fix:** Replace with `FileSystemTools`

### Error 2: "AgentMiddlewareContext doesn't exist"
```
Error: The type or namespace name 'AgentMiddlewareContext' could not be found
```
**Fix:** Use specialized contexts (`HookContext`, `ModelRequest`, etc.)

### Error 3: "ConversationThread not found"
```
Error: Type 'ConversationThread' could not be found
```
**Fix:** Replace with `AgentSession` and update namespace to `HPD.Agent.Session`

### Error 4: "Method BeforeSequentialFunctionAsync not found"
```
Error: 'IAgentMiddleware' does not contain a definition for 'BeforeSequentialFunctionAsync'
```
**Fix:** Implement new interface with `BeforeFunctionAsync(HookContext...)`

### Error 5: "BidirectionalEventCoordinator not found"
```
Error: Type 'BidirectionalEventCoordinator' could not be found
```
**Fix:** Use `IEventCoordinator` from `HPD.Events` namespace

### Error 6: "Package HPD-Agent.Plugins.FileSystem not found"
```
Error: Unable to find package 'HPD-Agent.Plugins.FileSystem'
```
**Fix:** Update to `HPD-Agent.Toolkit.FileSystem`

---

## API Mapping Table

### Builder API

| Old | New | Notes |
|-----|-----|-------|
| `WithPlugin<T>()` | `WithToolkit<T>()` | All plugin methods renamed |
| `WithDurableExecution()` | `WithSession()` | Session config |
| `LoadPluginRegistryFromAssembly()` | `LoadToolRegistryFromAssembly()` | Internal |

### Middleware API

| Old | New | Notes |
|-----|-----|-------|
| `BeforeMessageTurnAsync(AgentMiddlewareContext)` | Removed | Lifecycle changed |
| `BeforeIterationAsync(AgentMiddlewareContext)` | `BeforeModelRequestAsync(ModelRequest)` | New context |
| `BeforeSequentialFunctionAsync(AgentMiddlewareContext)` | `BeforeFunctionAsync(HookContext)` | New context |
| `AfterFunctionAsync(AgentMiddlewareContext)` | `AfterFunctionAsync(HookContext)` | New context |
| `context.Emit()` | `coordinator.EmitAsync()` | Via IEventCoordinator |
| `context.UpdateState<T>()` | `context.GetMiddlewareStateAsync<T>()` | New state API |

### Session API

| Old | New | Notes |
|-----|-----|-------|
| `ConversationThread` | `AgentSession` | Renamed |
| `IConversationThreadStore` | `ISessionStore` | Renamed |
| `JsonConversationThreadStore` | `JsonSessionStore` | Renamed |
| `DurableExecutionConfig` | `SessionStoreOptions` | Renamed |
| N/A | `IAssetStore` | NEW for files |

### Event API

| Old | New | Notes |
|-----|-----|-------|
| `BidirectionalEventCoordinator` | `IEventCoordinator` | From HPD.Events |
| `agent.MiddlewareEventWriter` | `agent.EventCoordinator` | Interface changed |
| `channel.Writer.WriteAsync()` | `coordinator.EmitAsync()` | New API |

---

## Dependencies Update

### Add These:
```xml
<PackageReference Include="HPD.Events" />
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.1.1" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.1.1" />
```

### Remove These:
```xml
<PackageReference Include="FluentValidation" Version="12.0.0" />
```

### Update Package Names:
```xml
<!-- OLD -->
<PackageReference Include="HPD-Agent.Plugins.FileSystem" Version="0.1.1" />
<PackageReference Include="HPD-Agent.Plugins.WebSearch" Version="0.1.1" />

<!-- NEW -->
<PackageReference Include="HPD-Agent.Toolkit.FileSystem" Version="0.2.0" />
<PackageReference Include="HPD-Agent.Toolkit.WebSearch" Version="0.2.0" />
```

---

## Config File Changes

### Before (v0.1.1):
```json
{
  "name": "MyAgent",
  "plugins": [
    {
      "name": "FileSystemPlugin",
      "context": { "rootPath": "/data" }
    }
  ],
  "frontendTools": {
    "enabled": true
  }
}
```

### After (HEAD):
```json
{
  "name": "MyAgent",
  "toolkits": [
    {
      "name": "FileSystemTools",
      "metadata": { "rootPath": "/data" }
    }
  ],
  "clientTools": {
    "enabled": true
  }
}
```

---

## When in Doubt

1. **Search the codebase:** Find examples in test projects
2. **Check generated code:** Look at `ToolkitRegistry` and `MiddlewareRegistry`
3. **Review HPD.Events:** Most event handling moved there
4. **Read middleware examples:** CircuitBreaker, ErrorTracking updated
5. **Check test files:** Test projects show new patterns

---

## Support Resources

- Full Report: `BREAKING_CHANGES_REPORT_v0.1.1_to_HEAD.md`
- Release Notes: `RELEASE_SUMMARY_0.2.0.md`
- Changelog: `CHANGELOG.md`
- GitHub Issues: Tag with "migration"

---

**Generated:** 2026-01-12  
**For Migration From:** v0.1.1 → HEAD (v0.2.0)

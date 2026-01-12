# Comprehensive Breaking Changes Report: v0.1.1 to HEAD

**Report Generated:** 2026-01-12  
**Comparison Range:** v0.1.1 → HEAD (main branch)  
**Total Changed Files:** 688 C# files modified  

---

## Executive Summary

This report documents all breaking changes between version v0.1.1 and the current HEAD. The changes represent a significant architectural evolution with multiple breaking changes across naming conventions, package structure, middleware architecture, event systems, and session management.

### Major Breaking Changes Categories:
1. **Terminology & Naming Convention Changes** (Plugin → Toolkit/Tools)
2. **Package Restructuring & New Libraries**
3. **Middleware Architecture Complete Overhaul**
4. **Event System Refactoring** (New HPD.Events Library)
5. **Session Management Changes** (ConversationThread → AgentSession)
6. **Dependency Changes**
7. **Multi-Target Framework Support**
8. **Provider Architecture Changes**

---

## 1. Terminology & Naming Convention Changes

### 1.1 Plugin → Toolkit/Tools Rename (BREAKING)

**Impact:** HIGH - Affects all code using plugins

The framework has undergone a complete terminology shift from "Plugin" to "Toolkit" and "Tools":

#### Namespace Changes:
```csharp
// OLD (v0.1.1)
HPD.Agent.Plugins
HPD.Agent.FrontendTools

// NEW (HEAD)
HPD.Agent.Toolkit
HPD.Agent.ClientTools
```

#### Package Renames:
```csharp
// OLD
HPD-Agent.Plugins.FileSystem
HPD-Agent.Plugins.WebSearch

// NEW
HPD-Agent.Toolkit.FileSystem
HPD-Agent.Toolkit.WebSearch
```

#### Class & Interface Renames:
```csharp
// Source Generator
HPDPluginSourceGenerator → HPDToolSourceGenerator
PluginRegistry → ToolkitRegistry
PluginInfo → ToolkitInfo
PluginFactory → ToolkitFactory

// Core Types
IPluginMetadataContext → IToolMetadataContext
PluginMetadata → ToolMetadata
PluginInstanceRegistration → ToolInstanceRegistration

// Builder Methods
WithPlugin<T>() → WithToolkit<T>()
_availablePlugins → _availableToolkits
_selectedPluginFactories → _selectedToolkitFactories
_pluginContexts → _toolkitContexts
_explicitlyRegisteredPlugins → _explicitlyRegisteredToolkits
_functionToPluginMap → _functionToToolkitMap
_pluginFunctionFilters → _toolFunctionFilters

// FFI Types
PluginRegistry → ToolkitRegistry
PluginStats → ToolkitStats
PluginSummary → ToolkitSummary
PluginExecutionResult → ToolkitExecutionResult
```

#### File Class Names:
```csharp
// File System
FileSystemPlugin → FileSystemTools
FileSystemPlugin.Advanced → FileSystemTools.Advanced
FileSystemPlugin.Shell → FileSystemTools.Shell

// Web Search
WebSearchPlugin → WebSearchTools
```

### 1.2 FrontendTools → ClientTools Rename (BREAKING)

**Impact:** HIGH - Affects all frontend integration code

Complete namespace and file structure rename:

```csharp
// OLD
HPD.Agent.FrontendTools
AgentBuilderFrontendToolsExtensions
FrontendToolConfig
FrontendToolMiddleware
FrontendSkillDocumentRegistrar
FrontendToolStateData
FrontendToolEvents
FrontendPluginDefinition
FrontendSkillDefinition
FrontendToolDefinition
FrontendToolAugmentation

// NEW
HPD.Agent.ClientTools
AgentBuilderClientToolsExtensions
ClientToolConfig
ClientToolMiddleware
ClientSkillDocumentRegistrar
ClientToolStateData
ClientToolEvents
ClientToolGroupDefinition
ClientSkillDefinition
ClientToolDefinition
ClientToolAugmentation
```

### 1.3 Attribute Changes (BREAKING)

```csharp
// DELETED - No longer exist
[AIFunction]       // Use standard method naming conventions
[Collapsed]        // Replaced by collapsing configuration
[RequiresPermission]  // Moved to different permission system
```

### 1.4 Scoping → Collapsing Terminology

```csharp
// Terminology shift throughout codebase
"scoping" → "collapsing"
"tool scoping" → "tool collapsing"
ToolScopingMiddleware → ContainerMiddleware
```

---

## 2. Package Restructuring & New Libraries

### 2.1 New Packages Added (BREAKING for users expecting old structure)

#### HPD.Events (NEW)
**Package:** `HPD.Events`  
**Purpose:** Standalone event coordination library

```csharp
namespace HPD.Events
{
    // Core abstractions
    public abstract class Event
    public enum EventDirection
    public enum EventKind
    public enum EventPriority
    public interface IEventCoordinator
    public interface IEventHandler
    public interface IEventObserver
    public interface IBidirectionalEvent
    public interface IStreamHandle
    public interface IStreamRegistry
    
    // Core implementation
    public class EventCoordinator : IEventCoordinator
    public class StreamHandle : IStreamHandle
    public class StreamRegistry : IStreamRegistry
}
```

**Migration Impact:**
- Event coordination moved from internal Agent class to standalone library
- `BidirectionalEventCoordinator` → `IEventCoordinator`
- Event emission API changed

#### HPD.MultiAgent (NEW)
**Package:** `HPD.MultiAgent`  
**Purpose:** Multi-agent orchestration with workflow support

```csharp
namespace HPD.MultiAgent
{
    public class MultiAgent
    public class AgentWorkflowInstance
    public class AgentGraphContext
    public class AgentNodeOptions
    public class MultiAgentWorkflowConfig
    public class ApprovalConfig
    public class WorkflowEvents
    public class WorkflowMetrics
    // + routing, observability, and internal types
}
```

#### HPD.Graph (NEW)
**Package:** `HPD.Graph.Abstractions`, `HPD.Graph.Core`, `HPD.Graph.SourceGenerator`  
**Purpose:** Graph-based workflow orchestration

New dependency for multi-agent workflows.

#### HPD-Agent.Audio (NEW)
**Packages:** 
- `HPD-Agent.Audio`
- `HPD-Agent.AudioProviders.ElevenLabs`
- `HPD-Agent.AudioProviders.OpenAI`

**Purpose:** Audio capabilities (TTS, STT, VAD, turn detection)

Major new feature area with provider architecture.

#### HPD-Agent.Sandbox.Local (NEW)
**Package:** `HPD-Agent.Sandbox.Local`  
**Purpose:** Local code execution sandboxing

Includes platform-specific implementations (Linux/Bubblewrap, Seccomp).

#### HPD-Agent.Providers.AzureAI (NEW)
**Package:** `HPD-Agent.Providers.AzureAI`  
**Purpose:** Azure AI Foundry provider

New provider alongside existing AzureAIInference.

### 2.2 Package Removals

```
HPD-Agent.Plugins.FileSystem → HPD-Agent.Toolkit.FileSystem
HPD-Agent.Plugins.WebSearch → HPD-Agent.Toolkit.WebSearch
```

### 2.3 Core Package Changes

#### HPD-Agent.Framework (Main Package)

**v0.1.1:**
```xml
<PackageId>HPD.Agent.Framework</PackageId>
<Version>0.1.1</Version>
<TargetFramework>net10.0</TargetFramework>
```

**HEAD:**
```xml
<PackageId>HPD-Agent.Framework</PackageId>
<!-- Version now inherited from Directory.Build.props -->
<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
```

**New Dependencies:**
- `HPD.Events` project reference added
- `Microsoft.Extensions.AI.Abstractions` updated from 10.0.0 → 10.1.1
- `Microsoft.Extensions.AI` updated from 10.0.0 → 10.1.1
- `FluentValidation` dependency **REMOVED**

---

## 3. Middleware Architecture Complete Overhaul

### 3.1 IAgentMiddleware Interface Changes (BREAKING)

**Impact:** CRITICAL - All custom middleware must be rewritten

The middleware interface has been completely redesigned from lifecycle hooks to a context-based model.

#### OLD Interface (v0.1.1):
```csharp
public interface IAgentMiddleware
{
    // Message Turn Level
    Task BeforeMessageTurnAsync(AgentMiddlewareContext context, CancellationToken ct);
    Task AfterMessageTurnAsync(AgentMiddlewareContext context, CancellationToken ct);
    
    // Iteration Level
    Task BeforeIterationAsync(AgentMiddlewareContext context, CancellationToken ct);
    Task ExecuteLLMCallAsync(AgentMiddlewareContext context, 
        Func<Task<ChatCompletion>> next, CancellationToken ct);
    Task AfterIterationAsync(AgentMiddlewareContext context, CancellationToken ct);
    
    // Tool Execution Level
    Task BeforeToolExecutionAsync(AgentMiddlewareContext context, CancellationToken ct);
    Task BeforeParallelFunctionsAsync(AgentMiddlewareContext context, CancellationToken ct);
    Task BeforeSequentialFunctionAsync(AgentMiddlewareContext context, CancellationToken ct);
    Task AfterFunctionAsync(AgentMiddlewareContext context, CancellationToken ct);
}
```

#### NEW Interface (HEAD):
```csharp
public interface IAgentMiddleware
{
    // Hook-based architecture with individual contexts
    Task BeforeModelRequestAsync(ModelRequest request, CancellationToken ct);
    Task AfterModelResponseAsync(ModelResponse response, CancellationToken ct);
    Task BeforeFunctionAsync(HookContext context, CancellationToken ct);
    Task AfterFunctionAsync(HookContext context, CancellationToken ct);
    Task OnErrorAsync(ErrorContext context, CancellationToken ct);
    
    // New hook contexts provide specific capabilities
    // No more single "AgentMiddlewareContext" god object
}
```

### 3.2 Context Object Changes (BREAKING)

#### AgentMiddlewareContext → Multiple Specialized Contexts

**DELETED:**
```csharp
public class AgentMiddlewareContext // God object with everything
{
    // Had 50+ properties covering all scenarios
}
```

**NEW - Specialized Contexts:**
```csharp
// Core context for agent state
public class AgentContext
{
    public required string AgentName { get; init; }
    public required AgentSession Session { get; init; }
    public Dictionary<string, object> State { get; }
    // ... focused on agent-level state
}

// Hook context for function execution
public class HookContext
{
    public AgentContext Agent { get; }
    public FunctionRequest Function { get; }
    public Task<T> WaitForResponseAsync<T>(string requestId);
    public void Emit(Event @event);
    // ... focused on function execution
}

// Model request context
public class ModelRequest
{
    public IList<ChatMessage> Messages { get; }
    public ChatOptions Options { get; }
    public IChatClient ChatClient { get; }
    // ... focused on LLM calls
}

// Model response context
public class ModelResponse
{
    public ChatCompletion Completion { get; }
    public IList<ChatMessage> Messages { get; }
    // ... focused on LLM responses
}

// Error context
public class ErrorContext
{
    public Exception Exception { get; }
    public string Phase { get; }
    public AgentContext Agent { get; }
    // ... focused on error handling
}

// Function request
public class FunctionRequest
{
    public required string Name { get; init; }
    public required FunctionCallContent CallContent { get; init; }
    public AIFunction? Metadata { get; init; }
    // ... focused on function calls
}
```

### 3.3 Middleware State Management Changes (BREAKING)

**OLD (v0.1.1):**
```csharp
// Direct access to state
context.UpdateState<TState>(state => { /* mutate */ });
var state = context.State.GetState<TState>();
```

**NEW (HEAD):**
```csharp
// Middleware state via MiddlewareStateExtensions
using HPD.Agent.Middleware;

[MiddlewareState] // Attribute for automatic state management
public class MyState { }

// Access in middleware
var state = await context.GetMiddlewareStateAsync<MyState>();
await context.SetMiddlewareStateAsync(newState);
```

New `MiddlewareStateAttribute` and `MiddlewareStateContainer` for automated state persistence.

### 3.4 Middleware Registration Changes

**OLD:**
```csharp
builder.WithMiddleware(new MyMiddleware());
```

**NEW:**
```csharp
// Now supports config-driven registration
builder.WithMiddleware(new MyMiddleware());

// Plus config-based registration from MiddlewareRegistry
// Generated by source generator
```

### 3.5 Key Middleware Renames/Deletions

**DELETED:**
```csharp
ToolScopingMiddleware  // Replaced by ContainerMiddleware
SkillInstructionMiddleware  // Removed
```

**RENAMED:**
```csharp
// All iteration middleware updated for new architecture
CircuitBreakerMiddleware  // Significant internal changes
ErrorTrackingMiddleware  // Rewritten for new context model
HistoryReductionMiddleware  // Updated for new state management
```

**NEW:**
```csharp
AssetUploadMiddleware  // New for asset management
ContainerMiddleware  // Replaces tool scoping
```

---

## 4. Event System Refactoring

### 4.1 Event Coordinator Changes (BREAKING)

**OLD (v0.1.1):**
```csharp
// Internal event system in Agent class
public class Agent
{
    private readonly BidirectionalEventCoordinator _eventCoordinator;
    internal ChannelWriter<AgentEvent> MiddlewareEventWriter { get; }
    internal ChannelReader<AgentEvent> MiddlewareEventReader { get; }
}
```

**NEW (HEAD):**
```csharp
// Standalone event library
using HPD.Events;

public class Agent
{
    private readonly IEventCoordinator _eventCoordinator;
    internal IEventCoordinator MiddlewareEventCoordinator { get; }
    
    // Channel-based API replaced with IEventCoordinator interface
}

// New event coordinator interface
public interface IEventCoordinator
{
    Task EmitAsync(Event @event, CancellationToken ct = default);
    IAsyncEnumerable<Event> ObserveAsync(CancellationToken ct = default);
    Task<TResponse> RequestAsync<TResponse>(Event request, CancellationToken ct);
    // ... priority-based routing, filtering, etc.
}
```

### 4.2 Event Type System Changes

**NEW Event Base Class:**
```csharp
namespace HPD.Events
{
    public abstract class Event
    {
        public string EventId { get; init; }
        public DateTime Timestamp { get; init; }
        public EventKind Kind { get; init; }
        public EventPriority Priority { get; init; }
        // ...
    }
    
    public enum EventKind
    {
        System, Agent, User, Tool, Error, Diagnostic
    }
    
    public enum EventPriority
    {
        Low, Normal, High, Critical
    }
}
```

### 4.3 AgentEvents Changes

**NEW Event Types Added:**
```csharp
// Structured output events
StructuredOutputStartEvent
StructuredOutputPartialEvent
StructuredOutputCompleteEvent
StructuredOutputErrorEvent

// Reasoning events (extended thinking)
ReasoningMessageStartEvent
ReasoningDeltaEvent
ReasoningMessageEndEvent

// Asset events
AssetUploadStartEvent
AssetUploadProgressEvent
AssetUploadCompleteEvent

// Session events
SessionCreatedEvent
SessionRestoredEvent
SessionCheckpointedEvent
```

**Event Type Changes:**
```csharp
// Reasoning structure changed
Reasoning class → Separate event types
ReasoningPhase enum → Event-based flow
```

### 4.4 Event Context Attribution

**NEW (HEAD):**
```csharp
// Execution context now set explicitly
agent.SetExecutionContext(new Dictionary<string, object>
{
    ["userId"] = "user123",
    ["sessionId"] = "session456"
});

// Context no longer auto-attached via AsyncLocal
// More explicit, less magic
```

---

## 5. Session Management Changes

### 5.1 ConversationThread → AgentSession (BREAKING)

**Impact:** HIGH - All session/persistence code affected

Complete rename and restructure of conversation/session management:

#### Namespace Changes:
```csharp
// OLD
HPD.Agent.Conversation
HPD.Agent.Checkpointing

// NEW
HPD.Agent.Session
```

#### Type Renames:
```csharp
// Core types
ConversationThread → AgentSession
IConversationThreadStore → ISessionStore
InMemoryConversationThreadStore → InMemorySessionStore
JsonConversationThreadStore → JsonSessionStore
DurableExecutionConfig → SessionStoreOptions

// Extensions
AgentBuilderCheckpointingExtensions → AgentBuilderSessionExtensions (NEW)
```

#### AsyncLocal Storage:
```csharp
// OLD
private static readonly AsyncLocal<ConversationThread?> _currentThread;
public static ConversationThread? CurrentThread { get; set; }

// NEW
private static readonly AsyncLocal<AgentSession?> _currentThread;
public static AgentSession? CurrentThread { get; set; }
```

#### Builder Extension Changes:
```csharp
// OLD (v0.1.1)
builder.WithDurableExecution(config =>
{
    config.ThreadStore = new JsonConversationThreadStore(path);
    config.AutoCheckpoint = true;
});

// NEW (HEAD)
builder.WithSession(options =>
{
    options.SessionStore = new JsonSessionStore(path);
    options.AssetStore = new LocalFileAssetStore(assetPath);
    options.AutoCheckpoint = true;
});
```

### 5.2 Asset Management (NEW)

**New Asset Store Abstraction:**
```csharp
namespace HPD.Agent.Session;

public interface IAssetStore
{
    Task<string> StoreAssetAsync(Stream content, string fileName, 
        string? sessionId, CancellationToken ct);
    Task<Stream> RetrieveAssetAsync(string assetId, CancellationToken ct);
    Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct);
}

// Implementations
public class InMemoryAssetStore : IAssetStore
public class LocalFileAssetStore : IAssetStore
```

Assets (files, images, etc.) are now managed separately from session history.

### 5.3 Session Store API Changes

**OLD Interface:**
```csharp
public interface IConversationThreadStore
{
    Task SaveAsync(ConversationThread thread);
    Task<ConversationThread?> LoadAsync(string threadId);
    Task DeleteAsync(string threadId);
}
```

**NEW Interface:**
```csharp
public interface ISessionStore
{
    Task SaveAsync(AgentSession session, CancellationToken ct = default);
    Task<AgentSession?> LoadAsync(string sessionId, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task<IEnumerable<string>> ListSessionsAsync(CancellationToken ct = default);
}
```

### 5.4 Checkpointing Changes

**File Structure:**
```csharp
// OLD
HPD-Agent/Checkpointing/Services/
    AgentBuilderCheckpointingExtensions.cs
    DurableExecutionConfig.cs
    DurableExecutionService.cs
    ServiceCollectionExtensions.cs

// NEW
HPD-Agent/Session/
    AgentBuilderCheckpointingExtensions.cs  // Kept for compat
    AgentBuilderSessionExtensions.cs  // NEW
    SessionStoreOptions.cs
    DurableExecutionService.cs
    ServiceCollectionExtensions.cs
    CheckpointTypes.cs  // NEW
    CheckpointExceptions.cs  // Moved
    SessionJsonContext.cs  // NEW
```

**DELETED Files:**
```
HPD-Agent/Conversation/ConversationThread.cs
HPD-Agent/Conversation/IConversationThreadStore.cs
HPD-Agent/Conversation/InMemoryConversationThreadStore.cs
HPD-Agent/Conversation/InMemoryThreadStore.cs
HPD-Agent/Conversation/JsonConversationThreadStore.cs
HPD-Agent/Conversation/CheckpointExceptions.cs
```

---

## 6. Dependency Changes

### 6.1 Removed Dependencies

```xml
<!-- REMOVED from HPD-Agent.csproj -->
<PackageReference Include="FluentValidation" Version="12.0.0" />
```

**Impact:** Custom validation is now internal (`HPD.Agent.Validation` namespace)

### 6.2 Updated Dependencies

```xml
<!-- Updated -->
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.1.1" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.1.1" />

<!-- Previously 10.0.0 -->
```

### 6.3 New Project References

```xml
<!-- HPD-Agent.csproj now references -->
<ProjectReference Include="..\HPD.Events\HPD.Events.csproj" />
<ProjectReference Include="..\HPD-Agent.TextExtraction\HPD-Agent.TextExtraction.csproj" />
<ProjectReference Include="..\HPD-Agent.SourceGenerator\HPD-Agent.SourceGenerator.csproj" />
```

---

## 7. Multi-Target Framework Support

### 7.1 Framework Targeting (BREAKING for build/deployment)

**v0.1.1:**
```xml
<TargetFramework>net10.0</TargetFramework>
```

**HEAD:**
```xml
<!-- Inherited from Directory.Build.props -->
<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
```

**Impact:**
- All packages now multi-target .NET 10, 9, and 8
- NuGet packages will contain multiple framework versions
- Build outputs will be per-framework
- Users on .NET 8 and 9 now officially supported

### 7.2 Centralized Build Configuration

**NEW: Directory.Build.props**

All version, author, license, and build metadata now centralized:

```xml
<Project>
  <PropertyGroup>
    <VersionPrefix>0.1.4</VersionPrefix>
    <Version>$(VersionPrefix)</Version>
    <Authors>Einstein Essibu</Authors>
    <Company>HPD</Company>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <!-- ... all common properties -->
  </PropertyGroup>
</Project>
```

**Impact:** Individual project files no longer contain version/metadata (breaking for custom builds).

### 7.3 Source Generator Targeting

```xml
<!-- Source generators remain netstandard2.0 -->
<TargetFramework>netstandard2.0</TargetFramework>
<TargetFrameworks></TargetFrameworks>  <!-- Clear multi-targeting -->
```

---

## 8. Provider Architecture Changes

### 8.1 Provider Auto-Discovery Rename

```csharp
// File rename
ProviderAutoDiscovery.cs → AutoDiscovery.cs

// Still in HPD.Agent namespace
```

### 8.2 New Provider: Azure AI Foundry

**NEW Package:** `HPD-Agent.Providers.AzureAI`

```csharp
namespace HPD.Agent.Providers.AzureAI;

public class AzureAIProvider
public class AzureAIProviderConfig
public class AzureAIProviderModule
public class AzureAIErrorHandler
public class AzureAIJsonContext
```

### 8.3 All Provider Projects Updated

Every provider project now includes:
- `AgentBuilderExtensions.cs` - Fluent extension methods
- `*ProviderConfig.cs` - Configuration POCOs
- `*JsonContext.cs` - AOT-compatible JSON serialization

**Providers with Config/Extension Updates:**
- Anthropic
- AzureAI (NEW)
- AzureAIInference
- Bedrock
- GoogleAI
- HuggingFace
- Mistral
- Ollama
- OnnxRuntime
- OpenAI
- OpenRouter

### 8.4 Provider Registry (NEW)

```csharp
// Agent.cs - NEW field
private readonly Providers.IProviderRegistry? _providerRegistry;

// Runtime provider switching via AgentRunOptions
public class AgentRunOptions
{
    public string? ProviderKey { get; set; }
    public string? ModelId { get; set; }
}
```

---

## 9. Agent Core API Changes

### 9.1 Agent.cs Breaking Changes

#### CurrentFunctionContext Changes

**v0.1.1:**
```csharp
public static AgentMiddlewareContext? CurrentFunctionContext { get; set; }
```

**HEAD:**
```csharp
public static HookContext? CurrentFunctionContext { get; set; }
// Now provides HookContext instead of AgentMiddlewareContext
```

#### Event Coordinator API

**v0.1.1:**
```csharp
public BidirectionalEventCoordinator EventCoordinator { get; }
internal ChannelWriter<AgentEvent> MiddlewareEventWriter { get; }
internal ChannelReader<AgentEvent> MiddlewareEventReader { get; }
```

**HEAD:**
```csharp
public IEventCoordinator EventCoordinator { get; }
internal IEventCoordinator MiddlewareEventCoordinator { get; }
// Channel-based access removed, replaced with coordinator interface
```

#### Execution Context

**v0.1.1:**
```csharp
public Dictionary<string, object>? ExecutionContext 
{ 
    get; 
    set 
    {
        _executionContextValue = value;
        _eventCoordinator.SetExecutionContext(value);
    }
}
```

**HEAD:**
```csharp
public Dictionary<string, object>? ExecutionContext { get; set; }
// No automatic syncing with event coordinator
// Use agent.SetExecutionContext() explicitly
```

### 9.2 AgentBuilder Breaking Changes

#### Plugin → Toolkit Fields

```csharp
// ALL RENAMED (see section 1.1 for complete list)
_availablePlugins → _availableToolkits
_selectedPluginFactories → _selectedToolkitFactories
_pluginContexts → _toolkitContexts
_explicitlyRegisteredPlugins → _explicitlyRegisteredToolkits
_functionToPluginMap → _functionToToolkitMap
_pluginFunctionFilters → _toolFunctionFilters
```

#### NEW Builder Fields

```csharp
// NEW - Toolkit overrides for config merging
internal readonly Dictionary<string, ToolkitReference> _toolkitOverrides;
internal readonly HashSet<string> _builderAddedToolkits;

// NEW - Middleware overrides for config merging
internal readonly Dictionary<Type, IAgentMiddleware> _middlewareOverrides;
internal readonly HashSet<Type> _configMiddlewareTypes;

// NEW - Middleware catalog
internal readonly Dictionary<string, MiddlewareFactory> _availableMiddlewares;

// NEW - Toolkit configs from config file
internal readonly Dictionary<string, JsonElement> _toolkitConfigs;

// NEW - Logging options
private LoggingMiddlewareOptions? _loggingOptions;

// NEW - Deferred provider support
internal bool _deferredProvider;
```

#### Method Renames

```csharp
// OLD
LoadPluginRegistryFromAssembly(Assembly assembly)

// NEW
LoadToolRegistryFromAssembly(Assembly assembly)
LoadToolkitRegistryFromAssembly(Assembly assembly)
LoadMiddlewareRegistryFromAssembly(Assembly assembly)
```

### 9.3 AgentConfig Changes

**Config file structure changes:**

```json
// OLD (v0.1.1)
{
  "plugins": [
    {
      "name": "FileSystemPlugin",
      "context": { }
    }
  ],
  "frontendTools": {
    "enabled": true
  }
}

// NEW (HEAD)
{
  "toolkits": [
    {
      "name": "FileSystemTools",
      "metadata": { }
    }
  ],
  "clientTools": {
    "enabled": true
  }
}
```

### 9.4 New Agent APIs

```csharp
// NEW - Structured output support
public class AgentRunOptions
{
    public StructuredOutputOptions? StructuredOutput { get; set; }
}

// NEW - Runtime provider switching
public class AgentRunOptions  
{
    public string? ProviderKey { get; set; }
    public string? ModelId { get; set; }
}

// NEW - Structured output types
namespace HPD.Agent.StructuredOutput;
public class StructuredOutputOptions
public class PartialJsonCloser
```

---

## 10. Source Generator Changes

### 10.1 Generator Renames

```csharp
// File renames
HPDPluginSourceGenerator.cs → HPDToolSourceGenerator.cs
PluginInfo.cs → ToolkitInfo.cs

// NEW
MiddlewareInfo.cs
```

### 10.2 Generated Code Changes

**OLD Generated Code:**
```csharp
// Generated by HPDPluginSourceGenerator
public static class PluginRegistry
{
    public static PluginFactory[] All => new[]
    {
        // ...
    };
}
```

**NEW Generated Code:**
```csharp
// Generated by HPDToolSourceGenerator
public static class ToolkitRegistry
{
    public static ToolkitFactory[] All => new[]
    {
        // ...
    };
}

public static class MiddlewareRegistry
{
    public static MiddlewareFactory[] All => new[]
    {
        // ...
    };
}
```

### 10.3 Capability Analysis (NEW)

**New source generator capabilities:**
```csharp
namespace HPD.Agent.SourceGenerator.Capabilities;

public enum CapabilityType
{
    Function,
    Skill,
    SubAgent,
    MultiAgent
}

public interface ICapability
public class FunctionCapability : ICapability
public class SkillCapability : ICapability
public class SubAgentCapability : ICapability
public class MultiAgentCapability : ICapability
public class CapabilityAnalyzer
```

---

## 11. FFI (Foreign Function Interface) Changes

### 11.1 Package Rename (BREAKING)

```csharp
// All "Plugin" references in FFI → "Toolkit"
NativePluginFFI.cs  // Still exists but types renamed internally
```

### 11.2 FFI Type Changes

```csharp
// OLD
public class PluginRegistry
public class PluginInfo
public class PluginStats
public class PluginSummary
public class PluginExecutionResult

// NEW
public class ToolkitRegistry
public class ToolkitInfo
public class ToolkitStats
public class ToolkitSummary
public class ToolkitExecutionResult
```

### 11.3 FFI Event Types

**NEW in FFI context:**
```csharp
[JsonSerializable(typeof(StructuredOutputErrorEvent))]
[JsonSerializable(typeof(StructuredResultEventDto))]
[JsonSerializable(typeof(StructuredOutputStartEvent))]
[JsonSerializable(typeof(StructuredOutputPartialEvent))]
[JsonSerializable(typeof(StructuredOutputCompleteEvent))]

[JsonSerializable(typeof(ReasoningMessageStartEvent))]
[JsonSerializable(typeof(ReasoningDeltaEvent))]
[JsonSerializable(typeof(ReasoningMessageEndEvent))]
```

---

## 12. Memory & MCP Changes

### 12.1 Memory Plugin Naming

```csharp
// Class names remain "Plugin" for now (not breaking)
DynamicMemoryPlugin  // Still Plugin
AgentPlanPlugin  // Still Plugin
DocumentRetrievalPlugin  // Still Plugin

// But they use the new Toolkit registry system
```

### 12.2 MCP Changes

**NEW:**
```csharp
HPD-Agent.MCP/MCPSandboxConfig.cs  // Added sandbox configuration
```

---

## 13. Structural & Organizational Changes

### 13.1 Deleted Files/Features

**Complete File Removals:**
```
HPD-Agent/Conversation/ConversationThread.cs
HPD-Agent/Conversation/IConversationThreadStore.cs
HPD-Agent/Conversation/InMemoryConversationThreadStore.cs
HPD-Agent/Conversation/InMemoryThreadStore.cs
HPD-Agent/Conversation/JsonConversationThreadStore.cs
HPD-Agent/Conversation/CheckpointExceptions.cs

HPD-Agent/Middleware/AgentMiddlewareContext.cs
HPD-Agent/Middleware/Scoping/ToolScopingMiddleware.cs

HPD-Agent/Plugins/Attributes/AIFunctionAttribute.cs
HPD-Agent/Plugins/Attributes/CollapsedAttribute.cs
HPD-Agent/Plugins/Attributes/RequiresPermissionAttribute.cs
HPD-Agent/Plugins/PluginFactory.cs
HPD-Agent/Plugins/PluginRegistration.cs

HPD-Agent/Permissions/IPermissionStorage.cs

HPD-Agent/Skills/SkillInstructionMiddleware.cs

HPD-Agent/Middleware/MIDDLEWARE_EVENTS_USAGE.md
HPD-Agent/Middleware/Iteration/ITERATION_MIDDLEWARE_GUIDE.md
HPD-Agent/Middleware/PromptMiddleware/PROMPT_FILTER_GUIDE.md

HPD-Agent.Plugins/HPD-Agent.Plugins.FileSystem/CONDITIONAL_FUNCTIONS_EXPLAINED.md
```

### 13.2 Directory Structure Changes

**OLD Structure:**
```
HPD-Agent/
├── Checkpointing/Services/
├── Conversation/
├── FrontendTools/
├── Plugins/
└── ...

HPD-Agent.Plugins/
├── HPD-Agent.Plugins.FileSystem/
└── HPD-Agent.Plugins.WebSearch/
```

**NEW Structure:**
```
HPD-Agent/
├── Session/  (was Checkpointing + Conversation)
├── ClientTools/  (was FrontendTools)
├── Tools/  (was Plugins)
└── ...

HPD-Agent.Tools/
├── HPD-Agent.Toolkit.FileSystem/  (was Plugins.FileSystem)
└── HPD-Agent.Toolkit.WebSearch/  (was Plugins.WebSearch)

HPD.Events/  (NEW)
HPD.MultiAgent/  (NEW)
HPD.Graph/  (NEW)
HPD-Agent.Audio/  (NEW)
HPD-Agent.Sandbox.Local/  (NEW)
```

---

## 14. Testing Impact

### 14.1 Test Project Changes

**NEW Test Projects:**
```
test/HPD-Agent.Audio.Tests/
test/HPD-Agent.Sandbox.Local.Tests/
test/HPD-Agent.Providers.Tests/
test/HPD.Events.Tests/
test/HPD.Graph.Tests/
test/HPD.MultiAgent.Tests/
```

### 14.2 Test File Changes

**Source Generator Tests:**
```
// OLD
test/HPD-Agent.Tests/Skills/Phase3SourceGeneratorTests.cs

// NEW
test/HPD-Agent.Tests/SourceGenerator/Phase3SourceGeneratorTests.cs
test/HPD-Agent.Tests/SourceGenerator/HPDToolSourceGeneratorTests.cs
test/HPD-Agent.Tests/SourceGenerator/CollapsingRegressionTests.cs
test/HPD-Agent.Tests/SourceGenerator/DualContextAttributeTests.cs
test/HPD-Agent.Tests/SourceGenerator/InstanceMethodContextTests.cs
test/HPD-Agent.Tests/SourceGenerator/Phase3CombinatorialValidationTests.cs
test/HPD-Agent.Tests/SourceGenerator/Phase3GenerationValidationTests.cs

// NEW
test/HPD-Agent.Tests/MultiAgents/MultiAgentSourceGeneratorTests.cs
```

---

## 15. Configuration & Serialization Changes

### 15.1 JSON Context Changes

**NEW JSON Contexts Added:**
```csharp
// Session
SessionJsonContext

// Audio
AudioJsonContext
AudioEventJsonContext

// Events
(Contexts now in HPD.Events library)

// All Providers
AnthropicJsonContext
AzureAIJsonContext
AzureAIInferenceJsonContext
BedrockJsonContext
GoogleAIJsonContext
HuggingFaceJsonContext
MistralJsonContext
OllamaJsonContext
OnnxRuntimeJsonContext
OpenAIJsonContext
OpenRouterJsonContext

// Audio Providers
ElevenLabsTtsJsonContext
OpenAISttJsonContext
OpenAITtsJsonContext
```

### 15.2 AOT Compatibility

All new JSON contexts are source-generated for Native AOT compatibility.

---

## Migration Guide Summary

### Critical Actions Required:

1. **Update All Plugin References:**
   - Replace `Plugin` with `Toolkit` or `Tools` throughout codebase
   - Update namespace imports from `HPD.Agent.Plugins` → `HPD.Agent.Toolkit`
   - Update package references from `HPD-Agent.Plugins.*` → `HPD-Agent.Tools.*`

2. **Rewrite All Custom Middleware:**
   - Implement new `IAgentMiddleware` interface
   - Replace `AgentMiddlewareContext` with specialized contexts
   - Update event emission to use `IEventCoordinator`
   - Migrate state management to new middleware state system

3. **Update Session/Persistence Code:**
   - Replace `ConversationThread` with `AgentSession`
   - Update store implementations (`IConversationThreadStore` → `ISessionStore`)
   - Implement `IAssetStore` if using file/image attachments
   - Update builder configuration from `WithDurableExecution` → `WithSession`

4. **Update Frontend/Client Tool Integration:**
   - Replace `FrontendTools` with `ClientTools` in all references
   - Update event handling for renamed event types
   - Update configuration keys in JSON files

5. **Update Event Handling:**
   - Add reference to `HPD.Events` package
   - Replace `BidirectionalEventCoordinator` with `IEventCoordinator`
   - Update event observation and emission code
   - Handle new event types (structured output, reasoning, assets)

6. **Update Build Configuration:**
   - Review multi-targeting implications (.NET 8/9/10)
   - Update CI/CD for multiple framework outputs
   - Remove any hardcoded version numbers (now in Directory.Build.props)

7. **Remove FluentValidation:**
   - If you extended agent validation, use internal validation system
   - No action needed if you didn't customize validation

8. **Update Agent Builder Code:**
   - Replace `LoadPluginRegistryFromAssembly` → `LoadToolRegistryFromAssembly`
   - Update config JSON files with new structure
   - Review and update any reflection-based plugin loading

9. **Test Source Generation:**
   - Rebuild projects to regenerate `ToolkitRegistry` and `MiddlewareRegistry`
   - Verify generated code compiles
   - Update any tests that assert on generated code structure

10. **Review Provider Changes:**
    - Update provider configuration if using Azure AI
    - Test runtime provider switching if using `AgentRunOptions`
    - Review provider-specific extension methods

---

## Compatibility Notes

### No Backward Compatibility
- **Breaking:** This is a major architectural revision
- **No migration path:** Old plugin/middleware code will not compile
- **Recommendation:** Treat as new major version (0.2.0)

### Source Breaking vs Binary Breaking
- **Source Breaking:** ALL changes are source-breaking
- **Binary Breaking:** ALL changes are binary-breaking
- **No IL compatibility:** Recompilation required

### Framework Support
- **.NET 8+:** Now officially supported (multi-targeting)
- **.NET 7 and below:** Not supported
- **Native AOT:** Still supported with expanded JSON contexts

---

## Testing Recommendations

1. **Comprehensive Integration Tests:** All middleware and plugin integrations must be retested
2. **Event Flow Testing:** Verify event emission/observation with new HPD.Events
3. **Session Persistence:** Test session save/restore with new `AgentSession`
4. **Multi-Framework Testing:** Test on .NET 8, 9, and 10 if multi-targeting
5. **Audio Testing:** Test new audio capabilities if used
6. **Multi-Agent Testing:** Test orchestration workflows if using HPD.MultiAgent
7. **Provider Testing:** Test all LLM providers in use
8. **Source Generator Testing:** Verify all generated registries compile

---

## Documentation Updates Required

All existing documentation referencing:
- "Plugin" → Update to "Toolkit" or "Tools"
- "FrontendTools" → Update to "ClientTools"
- "ConversationThread" → Update to "AgentSession"
- Middleware API → Complete rewrite
- Event handling → Update for HPD.Events
- Configuration files → Update JSON schema examples

---

## Estimated Migration Effort

| Component | Complexity | Estimated Hours |
|-----------|------------|-----------------|
| Plugin → Toolkit Rename | Low | 2-4 hours |
| Custom Middleware Rewrite | High | 8-16 hours |
| Session/Persistence Migration | Medium | 4-8 hours |
| Frontend Integration Updates | Medium | 4-6 hours |
| Event Handling Updates | Medium | 4-6 hours |
| Configuration Updates | Low | 1-2 hours |
| Testing | High | 16-24 hours |
| **Total Estimate** | - | **39-66 hours** |

*Estimate assumes medium-sized project with custom middleware and plugins*

---

## Questions & Support

For migration assistance, please refer to:
- Updated documentation (pending)
- BREAKING_CHANGES_0.2.0.md (if available)
- RELEASE_SUMMARY_0.2.0.md (if available)
- GitHub Issues: Tag with "migration" label

---

**Report End**

**Generated by:** HPD-Agent Analysis Tool  
**Date:** 2026-01-12  
**Commit Range:** v0.1.1 → HEAD (main)

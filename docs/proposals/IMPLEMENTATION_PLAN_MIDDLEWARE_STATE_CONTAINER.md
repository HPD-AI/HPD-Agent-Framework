# Implementation Plan: Source-Generated Middleware State Container

**Based on:** [SOURCE_GENERATED_MIDDLEWARE_STATE_CONTAINER_PROPOSAL.md](SOURCE_GENERATED_MIDDLEWARE_STATE_CONTAINER_PROPOSAL.md)
**Start Date:** 2025-01-28
**Estimated Duration:** 3 weeks
**Complexity:** Medium (Breaking Change - Targeted Refactor)

---

## Executive Summary

This plan outlines the step-by-step implementation of the source-generated middleware state container. The implementation is divided into **7 phases** that can be executed sequentially to minimize risk and ensure each component is tested before proceeding.

**Key Principles:**
- ✅ **Build incrementally** - Each phase is independently testable
- ✅ **Test thoroughly** - Write tests before moving to next phase
- ✅ **Document as you go** - Keep documentation in sync with code
- ✅ **Maintain backward compatibility** - Old API works until Phase 7

---

## Phase 1: Core Infrastructure (Days 1-2)

### Objectives
- Create base `MiddlewareStateContainer` class
- Implement smart accessor pattern
- Set up attribute for source generator

### Tasks

#### 1.1: Create `[MiddlewareState]` Attribute
**File:** `HPD-Agent/Middleware/State/MiddlewareStateAttribute.cs`

```csharp
namespace HPD.Agent;

/// <summary>
/// Marks a record as middleware state, triggering source generation
/// of properties on MiddlewareStateContainer.
/// </summary>
/// <remarks>
/// <para><b>Requirements:</b></para>
/// <list type="bullet">
/// <item>Must be applied to a record type (not class)</item>
/// <item>Record should be sealed for performance</item>
/// <item>All members must be JSON-serializable</item>
/// </list>
///
/// <para><b>Example:</b></para>
/// <code>
/// [MiddlewareState]
/// public sealed record MyMiddlewareState
/// {
///     public int Count { get; init; }
/// }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MiddlewareStateAttribute : Attribute
{
}
```

**Acceptance Criteria:**
- [ ] Attribute compiles
- [ ] Can be applied to records
- [ ] XML documentation is complete

---

#### 1.2: Create `MiddlewareStateContainer` Base Class
**File:** `HPD-Agent/Middleware/State/MiddlewareStateContainer.cs`

```csharp
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Container for all middleware state.
/// Properties are source-generated from [MiddlewareState] types.
/// </summary>
/// <remarks>
/// <para><b>Internal Storage:</b></para>
/// <para>
/// Uses ImmutableDictionary&lt;string, object?&gt; as backing storage.
/// This pattern is proven by Microsoft.Extensions.AI (see AIJsonUtilities.Defaults.cs).
/// During deserialization from checkpoints, values become JsonElement which are
/// transparently converted to concrete types by the smart accessor.
/// </para>
///
/// <para><b>Performance:</b></para>
/// <list type="bullet">
/// <item>Runtime reads: ~20-25ns (dictionary lookup + pattern match)</item>
/// <item>Post-checkpoint first read: ~150ns (JsonElement deserialize)</item>
/// <item>Post-checkpoint cached reads: ~20ns (from cache)</item>
/// <item>Immutable updates: ~30ns (ImmutableDictionary.SetItem)</item>
/// </list>
/// </remarks>
public sealed partial class MiddlewareStateContainer
{
    // ═══════════════════════════════════════════════════════
    // BACKING STORAGE (Internal)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Internal storage for middleware states.
    /// Keys: Fully-qualified type names (e.g., "HPD.Agent.CircuitBreakerStateData")
    /// Values: State instances (runtime) or JsonElement (deserialized)
    /// </summary>
    private readonly ImmutableDictionary<string, object?> _states;

    /// <summary>
    /// Lazy cache for deserialized JsonElement states.
    /// Each container instance gets its own cache to maintain immutability.
    /// Only initialized when deserialization occurs (zero overhead for runtime-only scenarios).
    /// </summary>
    private readonly Lazy<ConcurrentDictionary<string, object?>> _deserializedCache;

    // ═══════════════════════════════════════════════════════
    // CONSTRUCTORS
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Creates an empty middleware state container.
    /// </summary>
    public MiddlewareStateContainer()
    {
        _states = ImmutableDictionary<string, object?>.Empty;
        _deserializedCache = new Lazy<ConcurrentDictionary<string, object?>>(
            () => new ConcurrentDictionary<string, object?>());
    }

    /// <summary>
    /// Internal constructor for immutable updates.
    /// </summary>
    private MiddlewareStateContainer(ImmutableDictionary<string, object?> states)
    {
        _states = states;
        _deserializedCache = new Lazy<ConcurrentDictionary<string, object?>>(
            () => new ConcurrentDictionary<string, object?>());
    }

    // ═══════════════════════════════════════════════════════
    // SMART ACCESSOR (Protected - Used by Generated Code)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Smart accessor that handles both runtime and deserialized states.
    /// </summary>
    /// <typeparam name="TState">The middleware state type</typeparam>
    /// <param name="key">Fully-qualified type name (e.g., "HPD.Agent.CircuitBreakerStateData")</param>
    /// <returns>The state instance, or null if not present</returns>
    /// <remarks>
    /// <para><b>State Transitions:</b></para>
    /// <list type="bullet">
    /// <item>Runtime: value is TState (direct cast, ~20-25ns)</item>
    /// <item>Deserialized: value is JsonElement (deserialize first access ~150ns, cached ~20ns)</item>
    /// </list>
    /// </remarks>
    protected TState? GetState<TState>(string key) where TState : class
    {
        // Fast path: Check deserialization cache first (post-checkpoint scenario)
        if (_deserializedCache.IsValueCreated &&
            _deserializedCache.Value.TryGetValue(key, out var cached))
        {
            return cached as TState;
        }

        if (!_states.TryGetValue(key, out var value) || value is null)
            return null;

        // Pattern match handles both cases transparently
        var result = value switch
        {
            TState typed => typed,  // Runtime: already correct type
            JsonElement elem => elem.Deserialize<TState>(
                AIJsonUtilities.DefaultOptions),  // Deserialized from checkpoint
            _ => throw new InvalidOperationException(
                $"Unexpected type {value.GetType().Name} for middleware state '{key}'. " +
                $"Expected {typeof(TState).Name} or JsonElement.")
        };

        // Cache deserialized JsonElement results for subsequent accesses
        if (value is JsonElement && result != null)
        {
            _deserializedCache.Value.TryAdd(key, result);
        }

        return result;
    }

    /// <summary>
    /// Creates new container with updated state (immutable).
    /// </summary>
    /// <typeparam name="TState">The middleware state type</typeparam>
    /// <param name="key">Fully-qualified type name</param>
    /// <param name="state">New state value</param>
    /// <returns>New container with updated state</returns>
    protected MiddlewareStateContainer SetState<TState>(
        string key,
        TState state) where TState : class
    {
        return new MiddlewareStateContainer(
            _states.SetItem(key, state));
    }
}
```

**Acceptance Criteria:**
- [ ] Class compiles
- [ ] `GetState<T>()` handles null correctly
- [ ] `SetState<T>()` creates new instance
- [ ] XML documentation is complete

---

#### 1.3: Unit Tests for Container Base
**File:** `test/HPD-Agent.Tests/Middleware/MiddlewareStateContainerTests.cs`

```csharp
using System.Text.Json;
using HPD.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

public class MiddlewareStateContainerTests
{
    // Dummy state for testing (will be replaced with real states in Phase 3)
    private record TestState
    {
        public int Value { get; init; }
    }

    [Fact]
    public void GetState_WithNull_ReturnsNull()
    {
        // Arrange
        var container = new MiddlewareStateContainer();

        // Act & Assert - requires generator, so this will be tested in Phase 2
        // For now, just verify constructor works
        Assert.NotNull(container);
    }

    [Fact]
    public void Constructor_CreatesEmptyContainer()
    {
        // Arrange & Act
        var container = new MiddlewareStateContainer();

        // Assert
        Assert.NotNull(container);
    }

    // More tests will be added in Phase 2 when we have generated properties
}
```

**Acceptance Criteria:**
- [ ] Basic constructor test passes
- [ ] Test project compiles

---

## Phase 2: Source Generator (Days 3-7)

### Objectives
- Implement incremental source generator
- Detect `[MiddlewareState]` types
- Generate properties and `WithX()` methods
- Generate diagnostics (HPD001-HPD005)

### Tasks

#### 2.1: Create Generator Project Structure
**File:** `HPD-Agent.SourceGenerator/MiddlewareStateGenerator.cs`

**Dependencies:**
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" />
  <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" />
</ItemGroup>
```

**Acceptance Criteria:**
- [ ] Generator project compiles
- [ ] Project references added to main project

---

#### 2.2: Implement Core Generator Logic

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Text;

namespace HPD.Agent.SourceGenerator;

[Generator]
public class MiddlewareStateGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all [MiddlewareState] types
        var stateTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "HPD.Agent.MiddlewareStateAttribute",
                predicate: (node, _) => node is RecordDeclarationSyntax,
                transform: GetStateInfo)
            .Where(static m => m is not null);

        // Generate container properties
        context.RegisterSourceOutput(stateTypes.Collect(),
            (spc, types) => GenerateContainerProperties(spc, types!));
    }

    private StateInfo? GetStateInfo(
        GeneratorAttributeSyntaxContext context,
        CancellationToken ct)
    {
        // Implementation with diagnostics HPD001-HPD005
        // See Appendix B in proposal for full logic
    }

    private void GenerateContainerProperties(
        SourceProductionContext context,
        ImmutableArray<StateInfo> types)
    {
        // Generate properties and WithX methods
        // See Appendix B in proposal for full logic
    }
}

internal record StateInfo(
    string TypeName,
    string FullyQualifiedName,
    string PropertyName,
    string Namespace);
```

**Acceptance Criteria:**
- [ ] Generator detects `[MiddlewareState]` attributes
- [ ] Emits HPD001 for non-record types
- [ ] Emits HPD002 for unsealed records
- [ ] Generates properties correctly
- [ ] Generates `WithX()` methods correctly

---

#### 2.3: Generator Unit Tests
**File:** `test/HPD-Agent.Tests/SourceGenerator/MiddlewareStateGeneratorTests.cs`

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace HPD.Agent.Tests.SourceGenerator;

public class MiddlewareStateGeneratorTests
{
    [Fact]
    public void Generator_WithValidRecord_GeneratesProperty()
    {
        // Test that valid [MiddlewareState] record generates property
    }

    [Fact]
    public void Generator_WithClass_EmitsHPD001Error()
    {
        // Test HPD001 diagnostic
    }

    [Fact]
    public void Generator_WithUnsealedRecord_EmitsHPD002Warning()
    {
        // Test HPD002 diagnostic
    }

    // Add tests for HPD003, HPD004, HPD005
}
```

**Acceptance Criteria:**
- [ ] All diagnostic tests pass
- [ ] Property generation tests pass
- [ ] `WithX()` method generation tests pass

---

## Phase 3: Built-in Middleware Migration (Days 8-10)

### Objectives
- Convert existing middleware state types to use `[MiddlewareState]`
- Update middleware implementations to use generated properties
- Verify generated code works correctly

### Tasks

#### 3.1: Create CircuitBreakerStateData
**File:** `HPD-Agent/Middleware/State/CircuitBreakerStateData.cs`

```csharp
using System.Collections.Generic;

namespace HPD.Agent;

/// <summary>
/// State for CircuitBreakerMiddleware.
/// Tracks consecutive identical tool calls to detect infinite loops.
/// </summary>
[MiddlewareState]
public sealed record CircuitBreakerStateData
{
    /// <summary>
    /// Last function signature per tool (for detecting duplicates).
    /// Key: Tool name, Value: JSON signature of arguments
    /// </summary>
    public Dictionary<string, string> LastSignaturePerTool { get; init; } = new();

    /// <summary>
    /// Consecutive count of identical calls per tool.
    /// Key: Tool name, Value: Consecutive call count
    /// </summary>
    public Dictionary<string, int> ConsecutiveCountPerTool { get; init; } = new();

    /// <summary>
    /// Helper method to record a tool call and update counts.
    /// </summary>
    public CircuitBreakerStateData RecordToolCall(string toolName, string signature)
    {
        var lastSig = LastSignaturePerTool.GetValueOrDefault(toolName);
        var count = ConsecutiveCountPerTool.GetValueOrDefault(toolName, 0);

        var isIdentical = lastSig == signature;
        var newCount = isIdentical ? count + 1 : 1;

        return this with
        {
            LastSignaturePerTool = new Dictionary<string, string>(LastSignaturePerTool)
            {
                [toolName] = signature
            },
            ConsecutiveCountPerTool = new Dictionary<string, int>(ConsecutiveCountPerTool)
            {
                [toolName] = newCount
            }
        };
    }
}
```

**Acceptance Criteria:**
- [ ] Compiles successfully
- [ ] Source generator creates property on `MiddlewareStateContainer`
- [ ] Property appears in IntelliSense

---

#### 3.2: Create ErrorTrackingStateData
**File:** `HPD-Agent/Middleware/State/ErrorTrackingStateData.cs`

```csharp
namespace HPD.Agent;

/// <summary>
/// State for ErrorTrackingMiddleware.
/// Tracks consecutive failures to prevent infinite error loops.
/// </summary>
[MiddlewareState]
public sealed record ErrorTrackingStateData
{
    /// <summary>
    /// Number of consecutive failures in this run.
    /// </summary>
    public int ConsecutiveFailures { get; init; }
}
```

**Acceptance Criteria:**
- [ ] Compiles successfully
- [ ] Source generator creates property

---

#### 3.3: Create ContinuationPermissionStateData
**File:** `HPD-Agent/Middleware/State/ContinuationPermissionStateData.cs`

```csharp
namespace HPD.Agent;

/// <summary>
/// State for ContinuationPermissionMiddleware.
/// Tracks whether user has approved continuation for this run.
/// </summary>
[MiddlewareState]
public sealed record ContinuationPermissionStateData
{
    /// <summary>
    /// Whether user has approved continuation beyond threshold.
    /// </summary>
    public bool HasApprovedContinuation { get; init; }
}
```

**Acceptance Criteria:**
- [ ] Compiles successfully
- [ ] Source generator creates property

---

#### 3.4: Update CircuitBreakerMiddleware
**File:** `HPD-Agent/Middleware/Iteration/CircuitBreakerMiddleware.cs`

**BEFORE:**
```csharp
var state = context.State.GetState<CircuitBreakerState>();
context.UpdateState<CircuitBreakerState>(s => s.RecordToolCall(...));
```

**AFTER:**
```csharp
var state = context.State.MiddlewareState.CircuitBreaker ?? new();
context.UpdateState(s => s with
{
    MiddlewareState = s.MiddlewareState.WithCircuitBreaker(
        state.RecordToolCall(toolName, signature))
});
```

**Acceptance Criteria:**
- [ ] Middleware compiles
- [ ] Uses generated properties
- [ ] Logic unchanged (behavior identical)

---

#### 3.5: Update ErrorTrackingMiddleware
**File:** `HPD-Agent/Middleware/Iteration/ErrorTrackingMiddleware.cs`

Similar migration pattern.

**Acceptance Criteria:**
- [ ] Middleware compiles
- [ ] Uses generated properties

---

#### 3.6: Update ContinuationPermissionMiddleware
**File:** `HPD-Agent/Permissions/ContinuationPermissionMiddleware.cs`

Similar migration pattern.

**Acceptance Criteria:**
- [ ] Middleware compiles
- [ ] Uses generated properties

---

## Phase 4: AgentLoopState Integration (Days 11-12)

### Objectives
- Add `MiddlewareState` property to `AgentLoopState`
- Update initialization logic
- Verify checkpointing works

### Tasks

#### 4.1: Add Property to AgentLoopState
**File:** `HPD-Agent/Agent/AgentCore.cs` (line ~2903)

**BEFORE:**
```csharp
[JsonIgnore]
public ImmutableDictionary<string, object> MiddlewareStates { get; init; }
    = ImmutableDictionary<string, object>.Empty;
```

**AFTER:**
```csharp
/// <summary>
/// Middleware state container.
/// Contains all stateful middleware data (circuit breaker, error tracking, etc.).
/// </summary>
/// <remarks>
/// This replaces the old MiddlewareStates dictionary with a type-safe,
/// source-generated container. Properties are generated from [MiddlewareState] types.
/// </remarks>
public MiddlewareStateContainer MiddlewareState { get; init; } = new();

// DEPRECATED: Will be removed in Phase 7
[JsonIgnore]
[Obsolete("Use MiddlewareState property instead. This will be removed in next major version.")]
public ImmutableDictionary<string, object> MiddlewareStates { get; init; }
    = ImmutableDictionary<string, object>.Empty;
```

**Acceptance Criteria:**
- [ ] Property compiles
- [ ] Checkpoint serialization includes `MiddlewareState` (if not `[JsonIgnore]`)
- [ ] Backward compatibility maintained with old property

---

#### 4.2: Update AgentLoopState.Initial()
**File:** `HPD-Agent/Agent/AgentCore.cs` (line ~2919)

```csharp
public static AgentLoopState Initial(...) => new()
{
    // ... existing properties ...
    MiddlewareState = new MiddlewareStateContainer(),
    MiddlewareStates = ImmutableDictionary<string, object>.Empty, // DEPRECATED
    // ... rest
};
```

**Acceptance Criteria:**
- [ ] Initial state creates empty container
- [ ] Tests pass

---

#### 4.3: Update AgentMiddlewareContext
**File:** `HPD-Agent/Middleware/AgentMiddlewareContext.cs`

Ensure `AgentMiddlewareContext.State` exposes `MiddlewareState` property.

**Acceptance Criteria:**
- [ ] Context provides access to middleware state
- [ ] IntelliSense shows generated properties

---

## Phase 5: Testing (Days 13-15)

### Objectives
- Comprehensive unit tests
- Integration tests
- Critical checkpoint round-trip test
- Performance benchmarks

### Tasks

#### 5.1: Container Tests
**File:** `test/HPD-Agent.Tests/Middleware/MiddlewareStateContainerTests.cs`

```csharp
[Fact]
public void GetState_WithNull_ReturnsNull()
{
    var container = new MiddlewareStateContainer();
    Assert.Null(container.CircuitBreaker);
}

[Fact]
public void SetState_CreatesNewInstance()
{
    var container = new MiddlewareStateContainer();
    var newState = new CircuitBreakerStateData();

    var updated = container.WithCircuitBreaker(newState);

    Assert.NotSame(container, updated);
    Assert.Equal(newState, updated.CircuitBreaker);
}

[Fact]
public void GetState_WithUnexpectedType_ThrowsException()
{
    // Test fail-fast behavior
}
```

**Acceptance Criteria:**
- [ ] 100% code coverage for container
- [ ] All edge cases tested

---

#### 5.2: Critical Checkpoint Round-Trip Test
**File:** `test/HPD-Agent.Tests/Middleware/CheckpointRoundTripTests.cs`

Implement the test from **Appendix C** of the proposal.

**Acceptance Criteria:**
- [ ] Test passes
- [ ] Verifies JsonElement pattern works
- [ ] Verifies state preservation across serialization

---

#### 5.3: Integration Tests
**File:** `test/HPD-Agent.Tests/Integration/MiddlewareStateIntegrationTests.cs`

```csharp
[Fact]
public async Task CircuitBreaker_WithGeneratedState_PreventsDuplicateCalls()
{
    // End-to-end test with real agent execution
}
```

**Acceptance Criteria:**
- [ ] All 3 built-in middlewares work with new state
- [ ] No regressions in behavior

---

#### 5.4: Performance Benchmarks
**File:** `test/HPD-Agent.Benchmarks/MiddlewareStateBenchmarks.cs`

```csharp
[Benchmark]
public void GetState_Runtime()
{
    var state = _container.CircuitBreaker;
}

[Benchmark]
public void GetState_PostCheckpoint()
{
    var state = _deserializedContainer.CircuitBreaker;
}
```

**Acceptance Criteria:**
- [ ] Runtime reads: ≤30ns
- [ ] Post-checkpoint first read: ≤200ns
- [ ] Cached reads: ≤30ns

---

## Phase 6: Documentation (Days 16-18)

### Objectives
- Update API documentation
- Write migration guide
- Create examples

### Tasks

#### 6.1: API Reference
**File:** `docs/middleware/MIDDLEWARE_STATE_API.md`

Document:
- How to create middleware state types
- `[MiddlewareState]` attribute usage
- Generated properties and `WithX()` methods
- Serialization behavior

**Acceptance Criteria:**
- [ ] Complete API documentation
- [ ] Code examples for common scenarios

---

#### 6.2: Migration Guide
**File:** `docs/middleware/MIGRATION_GUIDE_MIDDLEWARE_STATE.md`

Step-by-step guide:
1. Convert `IMiddlewareState` → `[MiddlewareState]` record
2. Update middleware implementation
3. Test changes
4. Remove old code

**Acceptance Criteria:**
- [ ] Clear before/after examples
- [ ] Covers built-in and custom middleware

---

#### 6.3: Update Existing Docs
Update references in:
- `docs/ARCHITECTURE_OVERVIEW.md`
- `docs/middleware/MIDDLEWARE_ARCHITECTURE.md`
- README.md

**Acceptance Criteria:**
- [ ] All docs updated
- [ ] No broken links

---

## Phase 7: Cleanup (Days 19-21)

### Objectives
- Remove deprecated code
- Final testing
- Prepare for release

### Tasks

#### 7.1: Delete Old Files
```bash
rm HPD-Agent/Middleware/State/IMiddlewareState.cs
rm HPD-Agent/Middleware/State/MiddlewareStateExtensions.cs
rm HPD-Agent/Middleware/State/CircuitBreakerState.cs
rm HPD-Agent/Middleware/State/ErrorTrackingState.cs
rm HPD-Agent/Middleware/State/ContinuationPermissionState.cs
```

**Acceptance Criteria:**
- [ ] Old files deleted
- [ ] Project compiles
- [ ] All tests pass

---

#### 7.2: Remove Deprecated Properties
**File:** `HPD-Agent/Agent/AgentCore.cs`

Remove:
```csharp
[Obsolete("Use MiddlewareState property instead.")]
public ImmutableDictionary<string, object> MiddlewareStates { get; init; }
```

**Acceptance Criteria:**
- [ ] No obsolete code remains
- [ ] No compilation warnings

---

#### 7.3: Final Verification
- [ ] All tests pass (unit + integration)
- [ ] Performance benchmarks meet targets
- [ ] Documentation complete
- [ ] No breaking changes to public API (except planned deprecations)

---

## Risk Mitigation

### Risk: Source generator doesn't work in IDE
**Mitigation:** Test in both VS Code and Visual Studio
**Fallback:** Add manual trigger or fallback to manual properties

### Risk: JsonElement deserialization fails
**Mitigation:** Comprehensive checkpoint round-trip tests in Phase 5
**Fallback:** Add explicit serialization converters

### Risk: Performance regression
**Mitigation:** Benchmarks in Phase 5
**Fallback:** Add caching layer (already designed)

---

## Success Criteria

### Phase Completion
- [ ] All 7 phases completed
- [ ] All acceptance criteria met
- [ ] All tests passing

### Performance Targets
- [ ] Runtime reads: ≤30ns
- [ ] Post-checkpoint reads: ≤200ns (first), ≤30ns (cached)
- [ ] Immutable updates: ≤50ns

### Quality Targets
- [ ] 100% unit test coverage for container
- [ ] 100% unit test coverage for generator
- [ ] All integration tests passing
- [ ] Documentation complete

---

## Timeline Summary

| Phase | Days | Key Deliverables |
|-------|------|------------------|
| **1** | 1-2 | Base container, attribute |
| **2** | 3-7 | Source generator, diagnostics |
| **3** | 8-10 | Migrate 3 built-in middlewares |
| **4** | 11-12 | AgentLoopState integration |
| **5** | 13-15 | Tests, benchmarks |
| **6** | 16-18 | Documentation |
| **7** | 19-21 | Cleanup, final verification |

**Total: 21 days (~3 weeks)**

---

## Next Steps

1. **Review this plan** - Ensure all stakeholders agree
2. **Set up project board** - Track progress per phase
3. **Start Phase 1** - Create attribute and base container
4. **Daily check-ins** - Review progress and blockers

**Ready to proceed with Phase 1!**

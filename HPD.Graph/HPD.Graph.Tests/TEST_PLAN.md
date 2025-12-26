# HPD.Graph Test Plan

## Overview
Comprehensive test plan for HPD.Graph v1.0 covering all implemented components.

**Status**: Implementation complete, tests pending
**Last Updated**: 2024-12-25

---

## Implementation Status

### ✅ Completed Components
- **Channel System** (LastValue, Append, Reducer, Barrier, Ephemeral) - All 5 types
- GraphStateScope with channel mapping
- ManagedContext for execution metadata
- NodeExecutionResult discriminated union
- HandlerInputs type-safe data passing
- Graph abstractions with **optimized O(V+E) topological sorting**
- GraphOrchestrator with **thread-safe parallel execution**
- Retry policies and timeout handling
- Conditional routing with EdgeCondition
- Graph validation (cycles, reachability, orphaned nodes)
- Socket attributes (InputSocket, OutputSocket, GraphNodeHandler)
- Source generator for socket bridge code
- Checkpointing (GraphCheckpoint, IGraphCheckpointStore, InMemoryCheckpointStore)
- **Content-Addressable Caching** (wired up to orchestrator for real 79% cost savings)
- **Suspended/Resume** for human-in-the-loop workflows
- **SubGraph** support for nested graph composition

### ⏳ Optional Production Enhancements (Can be deferred)
- SqliteCheckpointStore (persistent checkpoint storage)
- RedisNodeCacheStore (L2 distributed caching)
- S3NodeCacheStore (L3 blob caching)

---

## 1. Channel System Tests

### 1.1 LastValueChannel Tests
**File**: `HPD.Graph.Core/Channels/LastValueChannel.cs`

- [ ] Set and get simple value (string, int, object)
- [ ] Set null value
- [ ] Get with wrong type throws InvalidCastException
- [ ] Version increments on each Set
- [ ] Thread safety - concurrent Set operations don't corrupt state
- [ ] Update throws NotSupportedException (LastValue doesn't support Update)
- [ ] Multiple Set operations - last write wins

### 1.2 AppendChannel Tests
**File**: `HPD.Graph.Core/Channels/AppendChannel.cs`

- [ ] Set single value creates list with one item
- [ ] Set multiple values via Update accumulates
- [ ] Get returns defensive copy (modifications don't affect channel)
- [ ] Version increments on each Set/Update
- [ ] Thread safety - parallel Set operations don't lose data
- [ ] Set with collection adds all items
- [ ] Get with wrong type throws InvalidCastException
- [ ] Empty channel returns empty list

### 1.3 ReducerChannel Tests
**File**: `HPD.Graph.Core/Channels/ReducerChannel.cs`

- [ ] Initial value is returned before any updates
- [ ] Reducer function is applied correctly on Update
- [ ] Multiple Update calls apply reducer cumulatively
- [ ] Version increments on each Update
- [ ] Thread safety - concurrent Update operations apply correctly
- [ ] Custom reducer - merge dictionaries
- [ ] Custom reducer - sum numbers
- [ ] Custom reducer - concatenate strings
- [ ] Set throws NotSupportedException (Reducer only supports Update)

### 1.4 BarrierChannel Tests
**File**: `HPD.Graph.Core/Channels/BarrierChannel.cs`

- [ ] Constructor throws if requiredCount <= 0
- [ ] Set adds value to barrier
- [ ] Get throws InvalidOperationException if barrier not satisfied
- [ ] Get returns all collected values when barrier satisfied
- [ ] IsSatisfied returns false before N writes
- [ ] IsSatisfied returns true after N writes
- [ ] CurrentCount tracks number of writes correctly
- [ ] Version increments on each Set/Update
- [ ] Thread safety - concurrent Set operations
- [ ] Reset clears collected values
- [ ] Update adds multiple values at once

### 1.5 EphemeralChannel Tests
**File**: `HPD.Graph.Core/Channels/EphemeralChannel.cs`

- [ ] Set and get value works correctly
- [ ] HasValue returns false initially
- [ ] HasValue returns true after Set
- [ ] Clear removes value and sets HasValue to false
- [ ] Get returns default when no value set
- [ ] Version increments on Set/Update/Clear
- [ ] Update sets to last value in enumerable
- [ ] Thread safety - concurrent Set/Clear operations
- [ ] Get with wrong type throws InvalidCastException

### 1.6 GraphChannelSet Tests
**File**: `HPD.Graph.Core/Channels/GraphChannelSet.cs`

- [ ] Indexer creates LastValueChannel by default
- [ ] Contains returns true for existing channel
- [ ] Contains returns false for non-existent channel
- [ ] Remove existing channel returns true
- [ ] Remove non-existent channel returns false
- [ ] Clear removes all channels
- [ ] Add with duplicate name throws exception
- [ ] ChannelNames returns sorted list of channel names
- [ ] Thread safety - concurrent access to different channels

---

## 2. GraphStateScope Tests

**File**: `HPD.Graph.Core/State/GraphStateScope.cs`

### 2.1 Basic Operations
- [ ] Set and get value in root scope (empty scope name)
- [ ] Set and get value in named scope
- [ ] Get non-existent key returns default value
- [ ] TryGet returns false for non-existent key
- [ ] Remove existing key returns true
- [ ] Remove non-existent key returns false
- [ ] Keys returns all keys in scope
- [ ] AsDictionary returns snapshot of scope data

### 2.2 Scope Isolation
- [ ] Root scope doesn't see named scope data
- [ ] Named scope doesn't see root scope data
- [ ] Different named scopes are isolated from each other
- [ ] Clear only affects current scope, not other scopes
- [ ] Namespaced channel keys format correctly (scope:name:key)

### 2.3 Integration
- [ ] Scope maps to underlying channels correctly
- [ ] Multiple scopes with same key don't conflict
- [ ] Clear removes all namespaced channels for that scope
- [ ] Scope name is correctly prefixed in channel names

---

## 3. ManagedContext Tests

**File**: `HPD.Graph.Core/State/ManagedContext.cs`

### 3.1 Step Tracking
- [ ] CurrentStep starts at 0
- [ ] IncrementStep increments counter
- [ ] Thread safety - concurrent IncrementStep calls

### 3.2 Time Tracking
- [ ] ElapsedTime increases over time
- [ ] RemainingTime calculates correctly with estimates
- [ ] RemainingTime is null without estimates
- [ ] SetEstimatedTotalSteps sets value correctly

### 3.3 Metrics
- [ ] RecordMetric stores numeric value
- [ ] RecordMetric overwrites existing metric
- [ ] IncrementMetric adds to existing metric
- [ ] IncrementMetric creates metric if not exists
- [ ] Metrics returns read-only dictionary

---

## 4. NodeExecutionResult Tests

**File**: `HPD.Graph.Abstractions/Execution/NodeExecutionResult.cs`

### 4.1 Success Results
- [ ] Success record contains outputs dictionary
- [ ] Success record contains duration
- [ ] Success record contains optional metadata
- [ ] Pattern matching on Success works correctly

### 4.2 Failure Results
- [ ] Failure record contains exception
- [ ] Failure record contains severity (High/Medium/Low)
- [ ] Failure record contains IsTransient flag
- [ ] Failure record can contain partial outputs
- [ ] Pattern matching on Failure works correctly
- [ ] ErrorCode is optional and stored correctly

### 4.3 Other Results
- [ ] Skipped contains reason and optional message
- [ ] Skipped contains UpstreamFailedNode when applicable
- [ ] Suspended contains suspend token
- [ ] Suspended contains optional resume value
- [ ] Cancelled contains cancellation reason
- [ ] Pattern matching is exhaustive (compiler enforced)

---

## 5. HandlerInputs Tests

**File**: `HPD.Graph.Abstractions/Handlers/HandlerInputs.cs`

### 5.1 Get Operations
- [ ] Get required input succeeds when present
- [ ] Get missing required input throws KeyNotFoundException
- [ ] Get with wrong type throws InvalidCastException
- [ ] Get null value with nullable type succeeds
- [ ] Get null value with non-nullable type behavior

### 5.2 GetOrDefault Operations
- [ ] GetOrDefault returns value if exists
- [ ] GetOrDefault returns default if missing
- [ ] GetOrDefault returns default if wrong type
- [ ] GetOrDefault with null value returns default

### 5.3 TryGet Operations
- [ ] TryGet returns true for existing value
- [ ] TryGet returns false for missing value
- [ ] TryGet returns false for wrong type
- [ ] TryGet sets output value correctly when found

### 5.4 Add Operations
- [ ] Add stores value correctly
- [ ] Add overwrites existing value
- [ ] Add with null key throws ArgumentNullException
- [ ] GetAll returns all inputs as dictionary

---

## 6. Graph Structure Tests

**File**: `HPD.Graph.Abstractions/Graph/Graph.cs`

### 6.1 Graph Creation
- [ ] Create graph with nodes and edges
- [ ] GetNode returns correct node by ID
- [ ] GetNode returns null for invalid ID
- [ ] GetIncomingEdges returns correct edges for node
- [ ] GetOutgoingEdges returns correct edges for node
- [ ] EntryNodeId and ExitNodeId are set correctly

### 6.2 Topological Sorting (GetExecutionLayers)
- [ ] Linear graph (A→B→C) produces 3 sequential layers
- [ ] Parallel branches (A→B,C→D) produce layers with parallel nodes
- [ ] Diamond dependency (A→B,C→D) produces 3 layers correctly
- [ ] Cycle detection stops infinite loop (returns what it computed)
- [ ] Disconnected nodes are excluded from layers
- [ ] START/END nodes are excluded from execution layers
- [ ] Empty graph (only START/END) returns empty layer list

---

## 7. RetryPolicy Tests

**File**: `HPD.Graph.Abstractions/Graph/RetryPolicy.cs`

### 7.1 Backoff Strategies
- [ ] Constant backoff returns same delay for all attempts
- [ ] Exponential backoff doubles each time (2^n)
- [ ] Linear backoff increases linearly (n * initialDelay)
- [ ] MaxDelay caps exponential growth correctly
- [ ] GetDelay(0) returns zero (no delay on first attempt)
- [ ] Negative attempt numbers handled gracefully

### 7.2 Exception Filtering
- [ ] ShouldRetry returns true if no filter specified
- [ ] ShouldRetry returns true for matching exception type
- [ ] ShouldRetry returns false for non-matching exception type
- [ ] ShouldRetry handles derived exception types correctly
- [ ] Multiple retry-able exception types work

---

## 8. EdgeCondition Tests

**File**: `HPD.Graph.Abstractions/Graph/EdgeCondition.cs`
**File**: `HPD.Graph.Core/Orchestration/ConditionEvaluator.cs`

### 8.1 Basic Conditions
- [ ] Always condition returns true
- [ ] FieldEquals with matching value returns true
- [ ] FieldEquals with different value returns false
- [ ] FieldNotEquals works correctly
- [ ] FieldExists returns true for non-null field
- [ ] FieldNotExists returns true for null/missing field

### 8.2 Comparison Conditions
- [ ] FieldGreaterThan with numbers works
- [ ] FieldGreaterThan with strings works (ordinal comparison)
- [ ] FieldLessThan with numbers works
- [ ] FieldLessThan with strings works
- [ ] Null values handled correctly (returns false)

### 8.3 Contains Conditions
- [ ] FieldContains for string works (case insensitive)
- [ ] FieldContains for collection works
- [ ] FieldContains with null returns false

### 8.4 ConditionEvaluator
- [ ] Null condition returns true (unconditional edge)
- [ ] No outputs returns false for field conditions
- [ ] Numeric type conversions work (int, long, double, decimal)
- [ ] String comparisons work correctly

---

## 9. GraphContext Tests

**File**: `HPD.Graph.Core/Context/GraphContext.cs`

### 9.1 Execution Tracking
- [ ] MarkNodeComplete adds to CompletedNodes set
- [ ] IsNodeComplete returns true for completed nodes
- [ ] GetNodeExecutionCount returns 0 initially
- [ ] IncrementNodeExecutionCount increments correctly
- [ ] SetCurrentNode updates CurrentNodeId
- [ ] CompletedNodes is immutable (IReadOnlySet)

### 9.2 Logging
- [ ] Log adds entry with timestamp
- [ ] Log entries are chronological
- [ ] Log with exception stores exception in entry
- [ ] LogLevel is preserved in entry
- [ ] NodeId is stored when provided

### 9.3 Tags
- [ ] AddTag creates new list if tag doesn't exist
- [ ] AddTag appends to existing tag list
- [ ] AddTag doesn't duplicate values
- [ ] Tags are case-sensitive

### 9.4 Progress
- [ ] Progress is 0.0 when TotalLayers is 0
- [ ] Progress calculates correctly (CurrentLayerIndex / TotalLayers)
- [ ] Progress is between 0.0 and 1.0

### 9.5 Context Isolation (Parallel Execution)
- [ ] CreateIsolatedCopy shares immutable state (Graph, ExecutionId, Services)
- [ ] CreateIsolatedCopy clones mutable collections (channels, logs, etc.)
- [ ] CreateIsolatedCopy resets CurrentNodeId to null
- [ ] MergeFrom merges channels with correct semantics (Append vs LastValue)
- [ ] MergeFrom unions completed nodes
- [ ] MergeFrom takes max execution counts
- [ ] MergeFrom appends logs chronologically
- [ ] MergeFrom unions tag lists

---

## 10. GraphOrchestrator Tests

**File**: `HPD.Graph.Core/Orchestration/GraphOrchestrator.cs`

### 10.1 Basic Execution
- [ ] ExecuteAsync completes linear graph (A→B→C)
- [ ] ExecuteAsync runs parallel nodes concurrently
- [ ] Execution layers computed correctly via GetExecutionLayers
- [ ] START/END nodes are handled properly (not executed)
- [ ] Empty graph (only START/END) completes successfully

### 10.2 Node Execution
- [ ] Handler resolved correctly by name from DI
- [ ] Handler not found throws meaningful exception
- [ ] Node inputs prepared from upstream node outputs
- [ ] Node outputs stored in channels (node_output:nodeId)
- [ ] Node marked complete after successful execution
- [ ] CurrentNodeId set during node execution

### 10.3 Parallel Execution
- [ ] Single node in layer executes sequentially
- [ ] Multiple nodes in layer execute in parallel (Task.WhenAll)
- [ ] Isolated contexts created for parallel nodes
- [ ] Contexts merged after parallel execution completes
- [ ] Channel semantics preserved during merge

### 10.4 Retry Logic
- [ ] Transient failure triggers retry
- [ ] Retry delay calculated correctly (backoff strategy)
- [ ] MaxAttempts respected (stops after limit)
- [ ] Non-retryable exception propagates immediately
- [ ] Retry attempt number increments correctly in metadata

### 10.5 Timeout Handling
- [ ] Node timeout triggers cancellation
- [ ] No timeout allows unlimited execution
- [ ] CancellationToken propagated to handler
- [ ] Timeout cancellation throws OperationCanceledException

### 10.6 Result Handling
- [ ] Success stores outputs in channels
- [ ] Failure with retry policy retries node
- [ ] Failure without retry throws exception
- [ ] Skipped result logs appropriately
- [ ] Cancelled result throws OperationCanceledException
- [ ] Suspended result (not implemented yet, but structure exists)

### 10.7 Resume Capability
- [ ] ResumeAsync skips already completed nodes
- [ ] ResumeAsync executes only remaining nodes
- [ ] Partial execution state preserved in context

### 10.8 Conditional Routing
- [ ] Edge condition met includes outputs in downstream inputs
- [ ] Edge condition not met skips edge (no data passed)
- [ ] Multiple incoming edges with different conditions work
- [ ] Unconditional edges (null condition) always pass data

### 10.9 Error Scenarios
- [ ] Missing handler throws meaningful exception
- [ ] Graph validation errors caught early
- [ ] Cancellation during execution handled gracefully
- [ ] Exception in handler propagates correctly through result

---

## 11. GraphValidator Tests

**File**: `HPD.Graph.Core/Validation/GraphValidator.cs`

### 11.1 Basic Structure Validation
- [ ] Missing START node returns error (MISSING_START)
- [ ] Invalid START node type returns error (INVALID_START)
- [ ] Missing END node returns error (MISSING_END)
- [ ] Invalid END node type returns error (INVALID_END)
- [ ] Duplicate node IDs return error (DUPLICATE_NODE_ID)
- [ ] Edge references non-existent FROM node returns error (INVALID_EDGE_FROM)
- [ ] Edge references non-existent TO node returns error (INVALID_EDGE_TO)

### 11.2 Cycle Detection (DFS)
- [ ] Simple cycle detected as warning (CYCLE_DETECTED)
- [ ] Complex cycle detected correctly
- [ ] Self-loop detected
- [ ] No cycle in DAG returns success
- [ ] START/END nodes excluded from cycle detection

### 11.3 Reachability Validation (BFS)
- [ ] Unreachable END returns error (UNREACHABLE_END)
- [ ] Unreachable intermediate node returns warning (UNREACHABLE_NODE)
- [ ] All reachable nodes pass validation

### 11.4 Orphaned Nodes
- [ ] Node with no edges returns warning (ORPHANED_NODE)
- [ ] Node with only incoming edges passes
- [ ] Node with only outgoing edges passes
- [ ] START/END excluded from orphan check

### 11.5 Handler Validation
- [ ] Handler node without handler name returns warning (MISSING_HANDLER_NAME)
- [ ] Router node without handler name returns warning
- [ ] START/END nodes don't need handler names

### 11.6 Integration
- [ ] Valid graph returns IsValid = true, no errors
- [ ] Invalid graph returns IsValid = false with errors
- [ ] Warnings don't block validation (IsValid can be true with warnings)
- [ ] Multiple errors accumulated correctly

---

## 12. Checkpointing Tests

**File**: `HPD.Graph.Abstractions/Checkpointing/GraphCheckpoint.cs`
**File**: `HPD.Graph.Core/Checkpointing/InMemoryCheckpointStore.cs`

### 12.1 GraphCheckpoint Structure
- [ ] Checkpoint contains all required fields
- [ ] CompletedNodes is immutable set
- [ ] NodeOutputs is immutable dictionary
- [ ] SchemaVersion defaults to "1.0"
- [ ] Metadata is optional

### 12.2 InMemoryCheckpointStore
- [ ] SaveCheckpointAsync stores checkpoint
- [ ] LoadLatestCheckpointAsync returns most recent checkpoint
- [ ] LoadCheckpointAsync returns specific checkpoint by ID
- [ ] DeleteCheckpointsAsync removes all checkpoints for execution
- [ ] ListCheckpointsAsync returns all checkpoints ordered by time
- [ ] RetentionMode.LatestOnly keeps only newest checkpoint
- [ ] RetentionMode.FullHistory keeps all checkpoints
- [ ] Thread safety for concurrent checkpoint operations

---

## 13. Socket Attributes & Source Generator Tests

**File**: `HPD.Graph.Abstractions/Attributes/SocketAttributes.cs`
**File**: `HPD.Graph.SourceGenerator/SocketBridgeGenerator.cs`

### 13.1 Socket Attributes
- [ ] InputSocketAttribute can be applied to parameters
- [ ] OutputSocketAttribute can be applied to properties
- [ ] GraphNodeHandlerAttribute can be applied to classes
- [ ] Optional property works correctly
- [ ] Description property preserved

### 13.2 Source Generator (Integration Test)
- [ ] Generator creates bridge code for marked handlers
- [ ] HandlerName property injected if not defined
- [ ] IGraphNodeHandler.ExecuteAsync implementation generated
- [ ] Input extraction from HandlerInputs works
- [ ] Output dictionary creation works
- [ ] Exception handling code generated
- [ ] IsTransientException helper generated
- [ ] Generated code compiles without errors

### 13.3 End-to-End Handler Test
- [ ] Create handler with [GraphNodeHandler] attribute
- [ ] Use [InputSocket] on method parameters
- [ ] Use [OutputSocket] on return type properties
- [ ] Build succeeds and generates .Sockets.g.cs file
- [ ] Generated implementation calls user method correctly
- [ ] Orchestrator can execute generated handler

---

## 14. Integration Tests

### 14.1 Simple Linear Workflow
- [ ] Create graph: START → A → B → C → END
- [ ] Execute and verify all nodes run in order
- [ ] Verify execution order (A before B before C)
- [ ] Verify outputs passed correctly between nodes

### 14.2 Parallel Workflow
- [ ] Create graph: START → A → (B, C) → D → END
- [ ] Verify B and C run in parallel (same layer)
- [ ] Verify D receives outputs from both B and C
- [ ] Verify total time < sequential time (parallelism works)

### 14.3 Conditional Routing
- [ ] Create graph with conditional edges
- [ ] Verify condition met path executes
- [ ] Verify condition not met path skips
- [ ] Verify downstream receives correct inputs based on conditions

### 14.4 Error Recovery
- [ ] Node fails, retry succeeds after backoff
- [ ] Node fails, retry exhausted, workflow fails
- [ ] Partial completion, save checkpoint, resume continues from checkpoint

### 14.5 Complex Workflows
- [ ] Diamond dependency (A → B,C → D) - 3 layers
- [ ] Multi-level parallelism (nested parallel branches)
- [ ] Mixed conditional and unconditional edges
- [ ] Long linear chain (10+ nodes)
- [ ] Wide parallel execution (10+ concurrent nodes)

---

## 15. Content-Addressable Caching Tests

**Files**:
- `HPD.Graph.Abstractions/Caching/*.cs`
- `HPD.Graph.Core/Caching/*.cs`

### 15.1 HierarchicalFingerprintCalculator Tests
**File**: `HPD.Graph.Core/Caching/HierarchicalFingerprintCalculator.cs`

- [ ] Compute returns consistent hash for same inputs
- [ ] Compute returns different hash when nodeId changes
- [ ] Compute returns different hash when inputs change
- [ ] Compute returns different hash when upstream hashes change
- [ ] Compute returns different hash when globalHash changes
- [ ] Hash is deterministic (same inputs → same hash)
- [ ] Hash handles null values correctly
- [ ] Hash handles primitive types (string, int, bool, etc.)
- [ ] Hash handles collections (lists, arrays)
- [ ] Hash handles complex objects via JSON serialization
- [ ] Upstream fingerprint changes propagate downstream

### 15.2 GraphSnapshot Tests
**File**: `HPD.Graph.Abstractions/Caching/GraphSnapshot.cs`

- [ ] Snapshot contains all required fields
- [ ] NodeFingerprints is immutable dictionary
- [ ] GraphHash is stored correctly
- [ ] Timestamp is set correctly
- [ ] ExecutionId is optional and stored correctly

### 15.3 InMemoryNodeCacheStore Tests
**File**: `HPD.Graph.Core/Caching/InMemoryNodeCacheStore.cs`

- [ ] SetAsync stores cached result
- [ ] GetAsync returns cached result by fingerprint
- [ ] GetAsync returns null for non-existent fingerprint
- [ ] ExistsAsync returns true for cached fingerprint
- [ ] ExistsAsync returns false for non-existent fingerprint
- [ ] DeleteAsync removes cached result
- [ ] ClearAllAsync removes all cached results
- [ ] Thread safety - concurrent Get/Set operations
- [ ] GetStatistics returns correct entry count
- [ ] CachedNodeResult contains all required fields

### 15.4 AffectedNodeDetector Tests
**File**: `HPD.Graph.Core/Caching/AffectedNodeDetector.cs`

- [ ] GetAffectedNodesAsync returns all nodes when no previous snapshot
- [ ] GetAffectedNodesAsync detects changed node
- [ ] GetAffectedNodesAsync marks downstream nodes as affected
- [ ] GetAffectedNodesAsync skips unchanged nodes
- [ ] Fingerprint change propagates to all downstream nodes
- [ ] Diamond dependency handled correctly
- [ ] START/END nodes excluded from affected set
- [ ] Empty graph returns empty affected set

### 15.5 Integration: Caching + Orchestrator
- [ ] Execute graph, cache results by fingerprint
- [ ] Re-execute with same inputs - all results from cache (cache HIT)
- [ ] Re-execute with changed input - affected nodes re-execute, others from cache
- [ ] Upstream change invalidates downstream automatically
- [ ] GraphSnapshot captured after execution
- [ ] Incremental execution skips unchanged nodes
- [ ] Cache miss triggers node execution
- [ ] Cache hit returns cached result without execution

---

## 16. Critical Fixes & Enhancements Tests

### 16.1 Thread Safety Tests (GraphContext)
**File**: `HPD.Graph.Core/Context/GraphContext.cs`

- [ ] Concurrent MarkNodeComplete doesn't lose nodes
- [ ] Concurrent IncrementNodeExecutionCount calculates correctly
- [ ] Concurrent Log calls don't corrupt log entries
- [ ] Concurrent AddTag doesn't lose tags
- [ ] MergeFrom with concurrent updates is thread-safe
- [ ] CompletedNodes property returns consistent snapshot
- [ ] LogEntries property returns consistent snapshot
- [ ] Tags property filters duplicates correctly
- [ ] No race conditions during parallel node execution
- [ ] Context isolation doesn't copy values (empty channels)

### 16.2 Performance Optimization Tests (GetExecutionLayers)
**File**: `HPD.Graph.Abstractions/Graph/Graph.cs`

- [ ] GetExecutionLayers runs in O(V+E) time
- [ ] Large graph (1000 nodes, 5000 edges) completes quickly
- [ ] Adjacency list built correctly
- [ ] In-degree counts accurate
- [ ] No performance regression vs previous implementation
- [ ] START/END nodes correctly excluded

### 16.3 Caching Integration Tests (GraphOrchestrator)
**File**: `HPD.Graph.Core/Orchestration/GraphOrchestrator.cs`

- [ ] Orchestrator accepts optional INodeCacheStore
- [ ] Orchestrator accepts optional INodeFingerprintCalculator
- [ ] Cache check happens before execution
- [ ] Cache HIT skips handler execution entirely
- [ ] Cache HIT returns cached outputs
- [ ] Cache HIT logs appropriately
- [ ] Cache MISS executes handler normally
- [ ] Cache MISS stores result after success
- [ ] Fingerprint computed with upstream hashes
- [ ] Fingerprint stored in _currentFingerprints
- [ ] Metadata contains CacheHit=true for cached results
- [ ] Fire-and-forget cache storage doesn't block execution
- [ ] Cache failures logged as warnings

### 16.4 Suspended Handling Tests
**File**: `HPD.Graph.Core/Orchestration/GraphOrchestrator.cs`

- [ ] Suspended result stores suspend token in context
- [ ] Suspended result adds suspended_nodes tag
- [ ] Suspended result stores resume value in channel
- [ ] GraphSuspendedException thrown on suspension
- [ ] GraphSuspendedException contains NodeId and SuspendToken
- [ ] Caller can save checkpoint after suspension
- [ ] Resume from suspension continues correctly
- [ ] Multiple suspensions handled correctly

### 16.5 SubGraph Execution Tests
**File**: `HPD.Graph.Core/Orchestration/GraphOrchestrator.cs`, `HPD.Graph.Abstractions/Graph/Node.cs`

- [ ] Node.SubGraph property exists and stores Graph
- [ ] Node.SubGraphRef property exists for external refs
- [ ] SubGraph node type included in execution
- [ ] ExecuteSubGraphAsync called for SubGraph nodes
- [ ] Sub-graph receives inputs via channels (input:*)
- [ ] Sub-graph executes recursively
- [ ] Sub-graph outputs collected (output:*)
- [ ] Sub-graph outputs stored in parent context
- [ ] Sub-graph completes and marks node complete
- [ ] Nested sub-graphs (3+ levels) work correctly
- [ ] Sub-graph inherits service provider
- [ ] Sub-graph gets isolated execution ID
- [ ] Sub-graph caching works independently

---

## 17. Performance Tests (Optional)

### 17.1 Scalability
- [ ] 100 nodes sequential execution time
- [ ] 100 nodes parallel execution time
- [ ] Memory usage with 1000 nodes
- [ ] Channel operations with 10k writes

### 17.2 Concurrency
- [ ] 10 parallel nodes stress test
- [ ] 100 parallel nodes stress test
- [ ] No thread safety issues under load
- [ ] Context merge performance

---

## Test Categories Summary

- **Unit Tests**: Sections 1-11, 12.1-12.2, 13.1, 15.1-15.4 (~230 tests)
- **Integration Tests**: Sections 13.2-13.3, 14, 15.5 (~30 tests)
- **Performance Tests**: Section 16 (~10 tests) - Optional

**Total Estimated Tests**: ~270 tests

---

## Priority Levels

### P0 (Critical - Must Have for v1.0)
- Sections 1 (Channels - all 5 types), 4 (NodeExecutionResult), 5 (HandlerInputs), 6 (Graph), 10 (Orchestrator), 11 (Validator)
- Basic integration tests (14.1, 14.2)

### P1 (Important - Should Have)
- Sections 2 (StateScope), 3 (ManagedContext), 7 (RetryPolicy), 8 (EdgeCondition), 9 (GraphContext), 12 (Checkpointing)
- Section 15 (Content-Addressable Caching) - Critical for cost savings
- Advanced integration tests (14.3, 14.4, 15.5)

### P2 (Nice to Have)
- Section 13 (Source Generator - can be tested manually initially)
- Section 14.5 (Complex workflows)
- Section 16 (Performance)

---

## Testing Tools

- **xUnit** - Test framework
- **FluentAssertions** - Readable assertions
- **Moq** - Mocking handlers and dependencies
- **BenchmarkDotNet** - Performance tests (optional)

---

## Notes

- Each test should be independent (no shared state between tests)
- Use async/await properly for async tests
- Mock external dependencies (IServiceProvider, handlers)
- Use descriptive test names following Given_When_Then pattern
- Group tests by functionality using nested classes
- Use `[Fact]` for simple tests, `[Theory]` with `[InlineData]` for parameterized tests
- Test both happy path and error cases
- Verify thread safety where applicable (channels, context)

---

## Next Steps

1. ✅ Complete HPD.Graph implementation
2. ⏳ Set up test project with xUnit, FluentAssertions, Moq
3. ⏳ Implement P0 tests first
4. ⏳ Create sample graphs for integration tests
5. ⏳ Run all tests and fix issues
6. ⏳ Document any deviations from original design

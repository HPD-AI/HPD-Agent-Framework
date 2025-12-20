# HPD-Agent Tests

Comprehensive test suite for HPD-Agent implementing the **Test Pyramid** principle.

## Project Structure

```
HPD-Agent.Tests/
├── Infrastructure/              # Test infrastructure and helpers
│   ├── AgentTestBase.cs        # Base class with async cleanup
│   ├── FakeChatClient.cs       # Mock LLM for testing
│   └── TestBidirectionalCoordinator.cs  # Event capture helper
├── Phase0_Characterization/     # Regression tests (current behavior)
│   └── CharacterizationTests.cs
├── Phase1_Foundation/           # (To be created) State & decision tests
├── Phase2_ExecutionMethods/     # (To be created) Async execution tests
├── Phase3_Integration/          # (To be created) Component integration
└── Phase4_Components/           # (To be created) Individual components
```

## Test Pyramid

```
┌──────────────────────────────────────┐
│  E2E Tests (10 tests)                │  Slowest, highest confidence
│  Execution: 10s                      │
└──────────────────────────────────────┘
           ▲
┌──────────────────────────────────────┐
│  Integration Tests (61 tests)        │  Medium speed
│  Execution: 9.5s                     │
└──────────────────────────────────────┘
           ▲
┌──────────────────────────────────────┐
│  Component Tests (199 tests)         │  Fast
│  Execution: 6.5s                     │
└──────────────────────────────────────┘
           ▲
┌──────────────────────────────────────┐
│  Unit Tests (478 tests)              │  Fastest, most tests
│  Execution: 1.6s                     │
└──────────────────────────────────────┘
```

## Running Tests

### All Tests
```bash
dotnet test
```

### Specific Test Category
```bash
# Run only characterization tests
dotnet test --filter "FullyQualifiedName~Phase0_Characterization"

# Run only unit tests (when created)
dotnet test --filter "FullyQualifiedName~Phase1_Foundation"
```

### With Coverage
```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Phase 0: Characterization Tests (Current)

**Status**: Infrastructure setup complete ✅
**Tests**: 7 tests covering major scenarios
**Purpose**: Lock in current behavior before refactoring

### Test Coverage:
1.  Simple text response (no tools)
2.  Single tool call
3.  Multiple parallel tool calls
4.  Circuit breaker trigger
5.  Max iterations reached
6. ⏳ Permission denial flow (TODO)
7. ⏳ Container expansion (TODO)

## Infrastructure Components

### AgentTestBase
Base class for all tests providing:
- AsyncLocal cleanup (prevents test interference)
- Background task tracking
- Helper methods for creating messages and agents
- Assertion helpers for event sequences

### FakeChatClient
Mock LLM client that:
- Queues predefined responses
- Simulates streaming behavior
- Captures all requests for verification
- No actual network calls

### TestBidirectionalCoordinator
Event capture helper that:
- Captures all emitted events
- Allows programmatic responses (permissions, etc.)
- Provides queries for event verification
- Thread-safe

## Next Steps

1. **Complete Agent constructor integration** in `AgentTestBase.CreateAgent()`
2. **Unskip characterization tests** and verify they pass
3. **Add remaining tests**: Permission denial, Container expansion
4. **Move to Phase 1**: Foundation tests (AgentLoopState, AgentDecisionEngine)

## Design Principles

1. **Test at the lowest level possible** - Push tests down the pyramid
2. **Characterization before refactoring** - Lock in current behavior
3. **No implementation details** - Test behavior, not internals
4. **Fast feedback** - Unit tests run in <1 second
5. **Streaming preservation** - Verify events yield progressively

## Resources

- [Testability Refactoring Proposal](../../Proposals/Urgent/TESTABILITY_REFACTORING_PROPOSAL.md)
- [Tests Proposal](../../Proposals/Urgent/TestsProposal.md)
- [Appendix A: Execution Methods](../../Proposals/Urgent/APPENDIX_A_EXECUTION_METHODS.md)

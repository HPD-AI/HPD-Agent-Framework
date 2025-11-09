# LangChain v1 & LangGraph Runtime Integration Analysis

## Executive Summary

LangChain v1 is a complete architectural reimplementation that integrates LangGraph as its core runtime engine. This provides durable execution, state persistence, human-in-the-loop capabilities, and streaming support. The integration is transparent to end-users - they use the high-level `create_agent()` API while LangGraph manages the underlying state machine and execution.

---

## 1. Integration Layer Architecture

### 1.1 Core Dependencies

**File:** `/langchain/agents/factory.py` (1,605 lines)

```python
from langgraph._internal._runnable import RunnableCallable
from langgraph.constants import END, START
from langgraph.graph.state import StateGraph
from langgraph.prebuilt.tool_node import ToolCallWithContext, ToolNode
from langgraph.runtime import Runtime
from langgraph.types import Command, Send
from langgraph.typing import ContextT

# Checkpoint and persistence
from langgraph.types import Checkpointer
from langgraph.store.base import BaseStore
from langgraph.cache.base import BaseCache
```

**pyproject.toml Dependencies:**
```toml
dependencies = [
    "langchain-core>=1.0.0,<2.0.0",
    "langgraph>=1.0.2,<1.1.0",
    "pydantic>=2.7.4,<3.0.0",
]
```

### 1.2 High-Level Architecture

```
User-Facing API (create_agent)
        ↓
LangChain Abstraction Layer
  - AgentMiddleware system
  - AgentState management
  - ModelRequest/ModelResponse types
        ↓
StateGraph Construction
  - Node creation (model, tools, middleware hooks)
  - Conditional edge logic
  - Event routing
        ↓
LangGraph Compiled Graph
  - State management
  - Checkpoint persistence
  - Runtime execution
```

---

## 2. How LangChain v1 Integrates LangGraph

### 2.1 StateGraph-Based Agent Construction

The `create_agent()` factory function builds a LangGraph `StateGraph` with the following structure:

```python
def create_agent(
    model: str | BaseChatModel,
    tools: Sequence[BaseTool | Callable] | None = None,
    *,
    system_prompt: str | None = None,
    middleware: Sequence[AgentMiddleware] = (),
    checkpointer: Checkpointer | None = None,  # LangGraph persistence
    store: BaseStore | None = None,            # Cross-thread storage
    cache: BaseCache | None = None,            # Execution caching
    # ... more params
) -> CompiledStateGraph[AgentState[ResponseT], ContextT, ...]:
```

**Returns:** A compiled `CompiledStateGraph` from LangGraph (not a Runnable)

### 2.2 Agent State Schema

**File:** `/langchain/agents/middleware/types.py`

```python
class AgentState(TypedDict, Generic[ResponseT]):
    """State schema for the agent."""
    messages: Required[Annotated[list[AnyMessage], add_messages]]
    jump_to: NotRequired[Annotated[JumpTo | None, EphemeralValue, PrivateStateAttr]]
    structured_response: NotRequired[Annotated[ResponseT, OmitFromInput]]
```

**Key Features:**
- `messages` channel uses `add_messages` reducer (LangGraph pattern)
- `jump_to` is ephemeral (not persisted)
- `structured_response` omitted from input schema for clean API
- Middleware can extend via `state_schema` attribute

### 2.3 Graph Node Architecture

The factory creates nodes dynamically based on middleware hooks:

```
START
  ↓
[before_agent middleware chain]
  ↓
[before_model middleware chain]
  ↓
model_node (LLM execution)
  ↓
[after_model middleware chain]
  ↓
tools (conditional)
  ↓
[loop back to before_model] OR [after_agent chain] OR END
```

#### Model Node Structure (Sync & Async)

```python
def model_node(state: AgentState, runtime: Runtime[ContextT]) -> dict[str, Any]:
    """Sync model request handler with sequential middleware processing."""
    request = ModelRequest(
        model=model,
        tools=default_tools,
        system_prompt=system_prompt,
        response_format=initial_response_format,
        messages=state["messages"],
        state=state,
        runtime=runtime,
    )
    
    if wrap_model_call_handler is None:
        response = _execute_model_sync(request)
    else:
        response = wrap_model_call_handler(request, _execute_model_sync)
    
    state_updates = {"messages": response.result}
    if response.structured_response is not None:
        state_updates["structured_response"] = response.structured_response
    
    return state_updates

# Added to graph with dual sync/async support
graph.add_node("model", RunnableCallable(model_node, amodel_node, trace=False))
```

#### Tool Node Integration

LangGraph's prebuilt `ToolNode` is directly integrated:

```python
tool_node = ToolNode(
    tools=available_tools,
    wrap_tool_call=wrap_tool_call_wrapper,    # Middleware wrapper
    awrap_tool_call=awrap_tool_call_wrapper,  # Async middleware wrapper
)
if available_tools:
    graph.add_node("tools", tool_node)
```

### 2.4 Conditional Edge Logic

Three types of conditional edges handle agent loop:

```python
# 1. Model to Tools Edge (decides which tools to execute)
graph.add_conditional_edges(
    loop_exit_node,
    _make_model_to_tools_edge(
        model_destination=loop_entry_node,
        structured_output_tools=structured_output_tools,
        end_destination=exit_node,
    ),
    model_to_tools_destinations,
)

# 2. Tools to Model Edge (decides: continue loop or exit)
graph.add_conditional_edges(
    "tools",
    _make_tools_to_model_edge(
        tool_node=tool_node,
        model_destination=loop_entry_node,
        structured_output_tools=structured_output_tools,
        end_destination=exit_node,
    ),
    tools_to_model_destinations,
)

# 3. Middleware Jump Logic (allows middleware to redirect)
def _add_middleware_edge(
    graph,
    *,
    name: str,
    default_destination: str,
    model_destination: str,
    end_destination: str,
    can_jump_to: list[JumpTo] | None,
):
    if can_jump_to:
        graph.add_conditional_edges(name, jump_edge, destinations)
    else:
        graph.add_edge(name, default_destination)
```

---

## 3. Durable Execution & Persistence Benefits

### 3.1 Checkpoint System

LangGraph's checkpoint integration enables conversation memory:

```python
# In factory.py (line 1398-1406)
return graph.compile(
    checkpointer=checkpointer,      # Saves state snapshots
    store=store,                    # Cross-thread data store
    interrupt_before=interrupt_before,
    interrupt_after=interrupt_after,
    debug=debug,
    name=name,
    cache=cache,
)
```

**Supported Checkpointers:**
- `InMemorySaver` - development/testing
- `SqliteSaver` - single-machine persistence
- `PostgresSaver` - distributed systems
- Custom implementations via `BaseCheckpointSaver`

### 3.2 Persistence Benefits

1. **Conversation Memory**
   - Full message history saved between invocations
   - Resume from exact execution point
   - No need to replay history

2. **State Reconstruction**
   ```python
   # Usage pattern
   result = agent.invoke(
       {"messages": [HumanMessage("Hello")]},
       config={"configurable": {"thread_id": "user_123"}}  # Key per conversation
   )
   ```

3. **Cross-Thread Storage (BaseStore)**
   - Persistent data across multiple conversations
   - User profiles, knowledge bases, etc.
   - Accessed via `runtime.store` in middleware/tools

4. **Resumable Execution**
   - Interrupts at tool execution points
   - Human-in-the-loop workflows
   - Recovery from transient failures

### 3.3 Example: Using Checkpointing

```python
from langgraph.checkpoint.sqlite import SqliteSaver

agent = create_agent(
    model="anthropic:claude-3-5-sonnet",
    tools=[search_tool, calculator],
    checkpointer=SqliteSaver(db="conversations.db"),
)

# Session 1
config = {"configurable": {"thread_id": "user_123"}}
result = agent.invoke({"messages": [HumanMessage("What is 2+2?")]}, config)

# Session 2 - same conversation
result = agent.invoke(
    {"messages": [HumanMessage("What's the capital of France?")]},
    config  # Same thread_id - uses checkpoint!
)
```

---

## 4. LCEL Chains to LangGraph Node Mapping

### 4.1 Transformation Pattern

**Old LCEL Pattern (v0):**
```python
# Legacy
model | bind_tools(tools) | ...  # Chain composition
```

**New LangGraph Pattern (v1):**
```python
# v1 - Nodes in StateGraph
def model_node(state, runtime):
    # State becomes explicit input
    # Runtime provides access to graph features
    ...

graph.add_node("model", RunnableCallable(model_node, amodel_node))
```

### 4.2 Middleware Hooks as Nodes

LangChain middleware doesn't override the graph structure - it injects nodes at specific points:

```python
class AgentMiddleware:
    # These become nodes in the graph
    def before_agent(self, state, runtime) -> dict | None:
        """Injected as: before_agent_node"""
    
    def before_model(self, state, runtime) -> dict | None:
        """Injected as: before_model_node (runs before LLM in loop)"""
    
    def after_model(self, state, runtime) -> dict | None:
        """Injected as: after_model_node (runs after LLM)"""
    
    def after_agent(self, state, runtime) -> dict | None:
        """Injected as: after_agent_node (runs once at end)"""
    
    # These wrap execution at the model level
    def wrap_model_call(self, request, handler) -> ModelResponse:
        """Handler composition - no node, inline wrapper"""
    
    # These wrap execution at the tool level
    def wrap_tool_call(self, request, handler) -> ToolMessage:
        """Handler composition - no node, inline wrapper"""
```

### 4.3 Handler Composition Pattern

Middleware handlers use function composition (not nodes):

```python
def _chain_model_call_handlers(handlers):
    """Compose handlers: first = outermost layer"""
    # handlers = [auth, retry, cache]
    # Execution: auth -> retry -> cache -> base_handler
    # Return:    result <- base <- cache <- retry <- auth
    
    def compose_two(outer, inner):
        def composed(request, handler):
            def inner_handler(req):
                inner_result = inner(req, handler)
                return _normalize_to_model_response(inner_result)
            outer_result = outer(request, inner_handler)
            return _normalize_to_model_response(outer_result)
        return composed
    
    # Chain right-to-left
    result = handlers[-1]
    for handler in reversed(handlers[:-1]):
        result = compose_two(handler, result)
    return result
```

Example middleware stack execution:
```python
# Given middleware=[ToolRetryMiddleware(), ModelFallbackMiddleware()]

# Execution flow:
model_fallback.wrap_model_call(
    request,
    lambda req: tool_retry.wrap_model_call(
        req,
        lambda req2: _execute_model_sync(req2)  # Base handler
    )
)
```

---

## 5. Migration Path: Classic LangChain → v1

### 5.1 API Differences

| Feature | v0 (Classic) | v1 (LangGraph-based) |
|---------|--------------|----------------------|
| Entry point | `AgentExecutor` | `create_agent()` |
| Return type | `Runnable` | `CompiledStateGraph` |
| State management | Implicit | Explicit `AgentState` |
| Persistence | Via callbacks | Native `checkpointer` param |
| Middleware | `tools` parameter | `middleware` parameter |
| Streaming | `.stream()` on Runnable | `.stream()` on CompiledStateGraph |
| Tool calling | `Tool` protocol | `BaseTool` or `@tool` decorator |

### 5.2 Simple Migration Example

**v0 Pattern:**
```python
from langchain.agents import AgentExecutor, create_react_agent
from langchain_core.tools import tool

@tool
def search(query: str) -> str:
    return "results"

agent_executor = AgentExecutor(
    agent=create_react_agent(model, [search]),
    tools=[search],
)

result = agent_executor.invoke({"input": "Find info about AI"})
```

**v1 Pattern:**
```python
from langchain.agents import create_agent
from langchain_core.tools import tool

@tool
def search(query: str) -> str:
    return "results"

agent = create_agent(
    model="anthropic:claude-3-5-sonnet",
    tools=[search],
)

result = agent.invoke({"messages": [HumanMessage("Find info about AI")]})
```

### 5.3 Breaking Changes

1. **Input format**: `{"input": "..."}` → `{"messages": [...]}`
2. **Output format**: `{"output": "..."}` → `{"messages": [...]}`
3. **Message handling**: String prompts → LangChain Message objects
4. **Tool parameter**: `tools` in AgentExecutor → `tools` in create_agent
5. **No `agent_scratchpad`** - unnecessary with LangGraph state

### 5.4 Gradual Migration Strategy

```python
# Hybrid approach: wrap v0 agents as tools in v1
from langchain.agents import create_agent
from langchain_core.tools import tool

@tool
def legacy_agent_wrapper(query: str) -> str:
    """Wraps old agent for compatibility"""
    result = old_agent_executor.invoke({"input": query})
    return result["output"]

# Use in v1
new_agent = create_agent(
    model="anthropic:claude-3-5-sonnet",
    tools=[legacy_agent_wrapper, other_new_tools],
)
```

---

## 6. Checkpoint & Persistence Features

### 6.1 Checkpoint Interface

**File:** `/tests/unit_tests/agents/conftest.py`

```python
# Supported checkpointers (all implement BaseCheckpointSaver)
SYNC_CHECKPOINTER_PARAMS = [
    "memory",           # InMemorySaver
    "sqlite",           # SqliteSaver
    "postgres",         # PostgresSaver
    "postgres_pipe",    # PostgresSaver with connection pooling
    "postgres_pool",    # PostgresSaver with pool
]

ASYNC_CHECKPOINTER_PARAMS = [
    "memory",
    "sqlite_aio",
    "postgres_aio",
    "postgres_aio_pipe",
    "postgres_aio_pool",
]
```

### 6.2 Checkpoint Usage Pattern

```python
from langgraph.checkpoint.sqlite import SqliteSaver
from langchain.agents import create_agent

# Create with persistence
agent = create_agent(
    model="gpt-4",
    tools=[search, calculate],
    checkpointer=SqliteSaver(db_path="./agent_memory.db"),
    store=InMemoryStore(),
)

# Invoke with thread for conversation continuity
config = {"configurable": {"thread_id": "conversation_123"}}

# Turn 1
response = agent.invoke(
    {"messages": [HumanMessage("Hello")]},
    config
)

# Turn 2 - automatically loads from checkpoint
response = agent.invoke(
    {"messages": [HumanMessage("What did I say?")]},
    config  # Same thread_id
)
```

### 6.3 Checkpoint Content

Saved checkpoint includes:

```python
checkpoint = {
    "channel_values": {
        "messages": [
            HumanMessage(...),
            AIMessage(...),
            ToolMessage(...)
        ],
        # ... other state fields
    },
    "metadata": {
        "parents": {...},
        "source": "loop",  # or "input", "node_name"
        "step": 3,
        "writes": {...}
    }
}
```

---

## 7. Wrapping LangGraph's Checkpoint & Persistence

### 7.1 Checkpoint Exposure Pattern

LangChain doesn't hide LangGraph checkpoints - it exposes them directly:

```python
# Factory signature (lines 516-517 in factory.py)
checkpointer: Checkpointer | None = None,
store: BaseStore | None = None,

# Direct passthrough to graph.compile()
return graph.compile(
    checkpointer=checkpointer,
    store=store,
    interrupt_before=interrupt_before,
    interrupt_after=interrupt_after,
    debug=debug,
    name=name,
    cache=cache,
)
```

### 7.2 Runtime Access in Middleware

Tools and middleware access state via `runtime`:

```python
class MyMiddleware(AgentMiddleware):
    def before_model(self, state, runtime):
        # Access runtime features
        store_value = runtime.store.get("namespace", "key")
        
        # Stream custom output
        runtime.stream_writer("custom", {"data": value})
        
        return {"messages": [modified_message]}
```

### 7.3 Tool Runtime Injection

**File:** `/langchain/tools/tool_node.py`

```python
from langgraph.prebuilt import ToolRuntime, InjectedState, InjectedStore

# Tools can request injection
@tool
def my_tool(query: str, runtime: ToolRuntime) -> str:
    """Tool with runtime access."""
    # Access state
    current_messages = runtime.state["messages"]
    
    # Access store for persistence
    user_data = runtime.store.get("users", user_id)
    
    # Current tool call ID
    call_id = runtime.tool_call_id
    
    return result
```

---

## 8. Performance & Architectural Improvements

### 8.1 Key Architectural Improvements

1. **Explicit State Machine**
   - Before: Implicit via LCEL chain composition
   - Now: Explicit StateGraph with clear node flow
   - Benefit: Easier to understand, debug, visualize

2. **Efficient Tool Execution**
   - LangGraph's ToolNode handles tool execution
   - Parallel tool execution support via `Send`
   - Structured error handling

3. **Memory Efficiency**
   - `add_messages` reducer prevents duplicate messages
   - Ephemeral state (`jump_to`) not persisted
   - Selective state schema input/output filtering

4. **Concurrency**
   - Native async/await support
   - Parallel tool execution with `Send`
   - Non-blocking checkpoint operations

### 8.2 Streaming Performance

Supports multiple streaming modes:

```python
# stream_mode options
for chunk in agent.stream(
    {"messages": [HumanMessage("query")]},
    stream_mode="updates"  # Per-node updates
):
    # Efficient partial output streaming

for chunk in agent.stream(
    {"messages": [...]},
    stream_mode="values"   # Full state snapshots
):
    # Complete state after each step
```

### 8.3 Optimization Patterns

**Pattern 1: Handler Composition Over Node Chains**
```python
# Efficient: handlers are inline functions
wrap_model_call_handler = _chain_model_call_handlers([auth, retry, cache])
# vs. adding 3 nodes to graph

response = wrap_model_call_handler(request, _execute_model_sync)
```

**Pattern 2: Conditional Tools**
```python
# Dynamic tool binding (faster than rebuilding graph)
def _get_bound_model(request):
    # Auto-detect best response format strategy
    if _supports_provider_strategy(request.model):
        effective_response_format = ProviderStrategy(schema=...)
    else:
        effective_response_format = ToolStrategy(schema=...)
    
    return request.model.bind_tools(final_tools, ...)
```

**Pattern 3: Structured Output Tools**
```python
# Separate structured output tool schema
structured_output_tools: dict[str, OutputToolBinding] = {}

# Dynamically bound based on response_format
if isinstance(effective_response_format, ToolStrategy):
    final_tools.extend(structured_output_tools.values())
```

### 8.4 Benchmarking Considerations

No direct v0 vs v1 performance metrics in codebase, but improvements include:

1. **Reduced overhead**: Explicit state vs. implicit chain composition
2. **Better parallelization**: Send-based tool execution
3. **Checkpoint efficiency**: Delta-based updates (LangGraph feature)
4. **Memory usage**: Selective state schema application

---

## 9. Code Examples Showing Integration

### 9.1 Complete Agent with Middleware

**File:** `/tests/unit_tests/agents/test_middleware_agent.py`

```python
from langchain.agents import create_agent
from langchain.agents.middleware.types import AgentMiddleware
from langchain.agents.middleware.tool_retry import ToolRetryMiddleware
from langchain_core.tools import tool

@tool
def search(query: str) -> str:
    """Search the internet."""
    return f"Results for: {query}"

class LoggingMiddleware(AgentMiddleware):
    def before_model(self, state, runtime):
        print(f"Messages: {len(state['messages'])}")
        return None
    
    def after_model(self, state, runtime):
        return None

agent = create_agent(
    model="anthropic:claude-3-5-sonnet",
    tools=[search],
    system_prompt="You are a helpful assistant.",
    middleware=[
        LoggingMiddleware(),
        ToolRetryMiddleware(max_retries=2),
    ],
    checkpointer=SqliteSaver(db="agent.db"),
)

# Invoke
result = agent.invoke(
    {"messages": [HumanMessage("Search for AI news")]},
    {"configurable": {"thread_id": "user_1"}}
)
```

### 9.2 Human-in-the-Loop Example

**File:** `/langchain/agents/middleware/human_in_the_loop.py`

```python
from langchain.agents.middleware.human_in_the_loop import (
    HumanInTheLoopMiddleware,
)

agent = create_agent(
    model="gpt-4",
    tools=[delete_database, send_email],
    middleware=[
        HumanInTheLoopMiddleware(
            interrupt_on={
                "delete_database": True,  # Require approval
                "send_email": {           # Custom config
                    "allowed_decisions": ["approve", "edit", "reject"],
                    "description": "Confirm email before sending"
                }
            }
        )
    ],
)

# Execution will pause for human review
result = agent.invoke({"messages": [...]})
```

### 9.3 Runtime Injection Example

**File:** `/tests/unit_tests/agents/test_injected_runtime_create_agent.py`

```python
from langchain.tools import ToolRuntime

@tool
def state_aware_search(query: str, runtime: ToolRuntime) -> str:
    """Tool that accesses state and store."""
    # Access current messages
    messages = runtime.state.get("messages", [])
    
    # Access persistent store
    user_prefs = runtime.store.get("users", user_id)
    
    # Get tool call ID
    call_id = runtime.tool_call_id
    
    return results

agent = create_agent(
    model="gpt-4",
    tools=[state_aware_search],
    store=InMemoryStore(),  # Enables store access
)
```

---

## 10. Design Patterns & Best Practices

### 10.1 Middleware Hook Points

```
Graph Execution Flow:

START
  ↓
before_agent() ← Can observe/modify initial state
  ↓
[Agent Loop begins]
  ↓
before_model() ← Can modify messages before LLM
  ↓
wrap_model_call() ← Inline handler: retry, cache, auth
  ↓
model() ← LLM execution (core logic)
  ↓
after_model() ← Can inject tool messages, redirect
  ↓
[Tool execution if needed]
  ↓
[Loop back to before_model or continue]
  ↓
after_agent() ← Final processing before return
  ↓
END
```

### 10.2 Jump-To Pattern

Middleware can redirect execution:

```python
from langchain.agents.middleware.types import JumpTo

class ConditionalRedirectMiddleware(AgentMiddleware):
    def after_model(self, state, runtime):
        if should_end_early(state):
            return {"jump_to": "end"}  # Skip tools
        return None
```

### 10.3 Tool Wrapper Composition

Composable tool wrapping:

```python
def wrap_tool_call(self, request, handler):
    """request = ToolCallRequest, handler = execute_tool"""
    # Can call handler multiple times for retry
    for attempt in range(3):
        try:
            result = handler(request)
            return result
        except Exception as e:
            if attempt == 2:
                raise
            time.sleep(backoff_time)
```

---

## 11. Summary Table

| Aspect | Details |
|--------|---------|
| **Base Runtime** | LangGraph StateGraph + prebuilt ToolNode |
| **State Schema** | AgentState[ResponseT] TypedDict with add_messages reducer |
| **Nodes** | Model, Tools, Middleware hooks (before_agent, before_model, after_model, after_agent) |
| **Edges** | Conditional (model→tools, tools→model) + Sequential (middleware chains) |
| **Persistence** | LangGraph Checkpointer (Memory, SQLite, PostgreSQL) |
| **Handler Composition** | Right-to-left stacking of wrap_model_call/wrap_tool_call |
| **Runtime Access** | `Runtime[ContextT]` passed to all hooks + Tool injection |
| **Migration Path** | Input/output format changes, explicit message format |
| **Key Benefits** | Durable execution, resumable conversations, HITL, native streaming |
| **Performance** | Explicit state management, parallel tool execution, delta checkpoints |

---

## Files Referenced

1. `/langchain/agents/factory.py` - Core agent factory (1,605 lines)
2. `/langchain/agents/middleware/types.py` - Middleware base classes and types
3. `/langchain/agents/middleware/human_in_the_loop.py` - HITL example middleware
4. `/langchain/agents/middleware/tool_retry.py` - Tool retry middleware
5. `/langchain/tools/tool_node.py` - Tool execution wrapper
6. `/langchain/agents/structured_output.py` - Structured response handling
7. `/tests/unit_tests/agents/test_injected_runtime_create_agent.py` - Runtime injection examples


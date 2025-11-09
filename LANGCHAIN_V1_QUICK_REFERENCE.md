# LangChain v1 & LangGraph Integration - Quick Reference Guide

## At a Glance

LangChain v1 completely rebuilds agents on top of LangGraph's StateGraph runtime. The transformation is architectural but the user-facing API is simple: `create_agent()`.

## Core Dependencies

```python
# From pyproject.toml
langchain-core>=1.0.0,<2.0.0
langgraph>=1.0.2,<1.1.0
pydantic>=2.7.4,<3.0.0
```

## Basic Usage

```python
from langchain.agents import create_agent
from langchain_core.tools import tool
from langchain_core.messages import HumanMessage

@tool
def search(query: str) -> str:
    """Search the web."""
    return "results"

# Create agent (returns CompiledStateGraph, not Runnable)
agent = create_agent(
    model="anthropic:claude-3-5-sonnet",
    tools=[search],
    system_prompt="You are a helpful assistant.",
)

# Invoke
result = agent.invoke({"messages": [HumanMessage("Find AI news")]})
```

## Graph Architecture (What's Created Internally)

```
StateGraph Structure:
├── State: AgentState[ResponseT]
│   ├── messages (with add_messages reducer)
│   ├── jump_to (ephemeral)
│   └── structured_response (optional)
│
├── Nodes:
│   ├── before_agent (middleware hook)
│   ├── before_model (middleware hook)
│   ├── model (LLM execution)
│   ├── after_model (middleware hook)
│   ├── tools (LangGraph ToolNode)
│   └── after_agent (middleware hook)
│
└── Edges:
    ├── START → entry_node (before_agent or before_model or model)
    ├── model → tools (conditional: if tool calls)
    ├── tools → model or END (conditional: loop or exit)
    ├── Middleware chains (sequential edges)
    └── → END (after_agent or direct)
```

## Key Integration Points

### 1. StateGraph Base

File: `/langchain/agents/factory.py` (1,605 lines)

```python
from langgraph.graph.state import StateGraph

graph: StateGraph[AgentState[ResponseT], ContextT, ...] = StateGraph(
    state_schema=resolved_state_schema,
    input_schema=input_schema,
    output_schema=output_schema,
)
```

### 2. Node Creation

```python
# Model node (with sync/async)
graph.add_node("model", RunnableCallable(model_node, amodel_node, trace=False))

# Tools node (LangGraph prebuilt)
graph.add_node("tools", ToolNode(tools=available_tools))

# Middleware nodes
for middleware in middleware_list:
    graph.add_node(f"{m.name}.before_model", RunnableCallable(...))
```

### 3. Conditional Edges

```python
# Model → Tools
graph.add_conditional_edges(
    "model",
    _make_model_to_tools_edge(...),
    ["tools", "end"]
)

# Tools → Model or End
graph.add_conditional_edges(
    "tools",
    _make_tools_to_model_edge(...),
    ["model", "end"]
)
```

### 4. Compilation (Passthrough to LangGraph)

```python
return graph.compile(
    checkpointer=checkpointer,        # SqliteSaver, PostgresSaver, etc.
    store=store,                      # InMemoryStore, PostgresStore
    interrupt_before=interrupt_before,
    interrupt_after=interrupt_after,
    cache=cache,                      # BaseCache
)
```

## Middleware System

### Hook Points in Graph Flow

```python
class AgentMiddleware:
    # Graph hooks (become nodes)
    def before_agent(self, state, runtime): ...  # Once at start
    def before_model(self, state, runtime): ...  # Before LLM (looped)
    def after_model(self, state, runtime): ...   # After LLM (looped)
    def after_agent(self, state, runtime): ...   # Once at end
    
    # Inline handlers (not nodes, composed functions)
    def wrap_model_call(self, request, handler): ...  # Wraps LLM execution
    def wrap_tool_call(self, request, handler): ...   # Wraps tool execution
```

### Handler Composition (Right-to-Left)

```python
# Given: [middleware1, middleware2, middleware3]
# Execution order: middleware1 → middleware2 → middleware3 → base

def _chain_model_call_handlers(handlers):
    # Returns composed handler
    result = handlers[-1]  # Start with last
    for handler in reversed(handlers[:-1]):
        result = compose_two(handler, result)
    return result
```

## Checkpointing & Persistence

### Simple Setup

```python
from langgraph.checkpoint.sqlite import SqliteSaver

agent = create_agent(
    model="gpt-4",
    tools=[search],
    checkpointer=SqliteSaver(db="conversations.db"),
)

# Use with thread_id for conversation continuity
config = {"configurable": {"thread_id": "user_123"}}
result = agent.invoke({"messages": [HumanMessage("Hello")]}, config)
result = agent.invoke({"messages": [HumanMessage("What did I say?")]}, config)
```

### Checkpoint Backends

| Backend | Use Case |
|---------|----------|
| InMemorySaver | Development, testing |
| SqliteSaver | Single machine, file-based |
| PostgresSaver | Distributed, production |
| Custom | BaseCheckpointSaver implementation |

## Migration: v0 → v1

### Input/Output Changes

```python
# v0
agent_executor.invoke({"input": "query"})
# Returns: {"output": "response"}

# v1
agent.invoke({"messages": [HumanMessage("query")]})
# Returns: {"messages": [...AIMessage(...), ...]}
```

### API Changes

| v0 | v1 |
|----|-----|
| AgentExecutor | create_agent() |
| Runnable | CompiledStateGraph |
| tools=[] param | tools=[] param (same) |
| callbacks= | Not directly; use middleware |
| memory= | checkpointer= + thread_id |

## Runtime Injection in Tools

### Access Graph State & Store

```python
from langchain.tools import ToolRuntime

@tool
def my_tool(query: str, runtime: ToolRuntime) -> str:
    """Tool with graph access."""
    # Current state
    messages = runtime.state["messages"]
    
    # Persistent storage
    user_data = runtime.store.get("users", user_id)
    
    # Tool call ID
    call_id = runtime.tool_call_id
    
    return result
```

## LCEL to LangGraph Mapping

### State Becomes Explicit

```python
# v0: LCEL (implicit)
model | bind_tools(tools) | ...

# v1: StateGraph (explicit)
def model_node(state, runtime):
    # state explicitly contains: messages, jump_to, structured_response
    messages = state["messages"]
    # ... do work
    return {"messages": [...]}
```

### Middleware as Graph Components

```python
# v0: Callbacks or custom agent loop

# v1: Explicit graph nodes OR inline handlers
class MyMiddleware(AgentMiddleware):
    def before_model(self, state, runtime):
        # Added as graph node: "{name}.before_model"
        return {"messages": [modified_msg]}
    
    def wrap_model_call(self, request, handler):
        # Composed inline, not a node
        return handler(request)
```

## Performance Characteristics

### What's More Efficient

1. Handler composition vs. node chains
   - Inline functions faster than graph traversal
   - Use wrap_model_call/wrap_tool_call for retries, auth, caching

2. State reducers
   - add_messages prevents duplicates
   - Ephemeral state not persisted
   - Schema filtering (OmitFromInput/Output)

3. Parallel execution
   - Tools via Send (LangGraph feature)
   - Future enhancement: parallel tool execution

### Streaming Modes

```python
# Per-node updates (lower latency)
for chunk in agent.stream(
    input,
    stream_mode="updates"
):
    print(chunk)

# Full state snapshots
for chunk in agent.stream(
    input,
    stream_mode="values"
):
    print(chunk)
```

## Common Patterns

### Retry Middleware

```python
from langchain.agents.middleware.tool_retry import ToolRetryMiddleware

agent = create_agent(
    model="gpt-4",
    tools=[...],
    middleware=[
        ToolRetryMiddleware(
            max_retries=3,
            retry_on=(Exception,),
            backoff_factor=2.0,
        )
    ],
)
```

### Human-in-the-Loop

```python
from langchain.agents.middleware.human_in_the_loop import (
    HumanInTheLoopMiddleware,
)

agent = create_agent(
    model="gpt-4",
    tools=[delete, send_email],
    middleware=[
        HumanInTheLoopMiddleware(
            interrupt_on={
                "delete": True,
                "send_email": {
                    "allowed_decisions": ["approve", "edit", "reject"]
                }
            }
        )
    ],
)
```

### Conditional Flow Control

```python
class EarlyExitMiddleware(AgentMiddleware):
    def after_model(self, state, runtime):
        if condition_met(state):
            return {"jump_to": "end"}  # Skip tools
        return None
```

## Debugging Tips

### Enable Debug Output

```python
agent = create_agent(
    model="gpt-4",
    tools=[...],
    debug=True,  # Verbose logging of graph execution
)

result = agent.invoke({"messages": [...]}, debug=True)
```

### Visualize Graph

```python
# Get compiled graph
graph = agent.get_graph()

# Mermaid diagram
print(graph.draw_mermaid())

# Or ASCII visualization
print(graph.draw_ascii())
```

### Inspect Checkpoints

```python
from langgraph.checkpoint.sqlite import SqliteSaver

saver = SqliteSaver(db="conversations.db")
config = {"configurable": {"thread_id": "user_123"}}

# Get saved state
tuple_data = saver.get_tuple(config)
print(tuple_data.checkpoint["channel_values"])
```

## Key Files

- `/langchain/agents/factory.py` - Graph construction (1,605 lines)
- `/langchain/agents/middleware/types.py` - AgentMiddleware base classes
- `/langchain/agents/middleware/human_in_the_loop.py` - HITL example
- `/langchain/tools/tool_node.py` - Tool execution + runtime injection
- `/langchain/agents/structured_output.py` - Response format handling

## Resources

- Full analysis: `LANGCHAIN_V1_LANGGRAPH_INTEGRATION.md` (883 lines)
- Test examples: `/tests/unit_tests/agents/`
- Middleware reference: `/langchain/agents/middleware/`


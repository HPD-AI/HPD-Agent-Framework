# HPD-Agent Context Processing Flow - Documentation Index

Complete documentation of how context flows through the HPD-Agent system from user input to ChatClient invocation.

---

## Quick Start: Choose Your Entry Point

### I have 5 minutes - Executive Summary
Start here for a high-level overview:
- **File**: `HPD_AGENT_CONTEXT_FLOW_SUMMARY.md`
- **Contains**: 7-step flow, key mechanisms, filter details, context paths, troubleshooting
- **Lines**: 398
- **Best for**: Understanding the overall architecture quickly

### I have 15 minutes - Quick Reference
Visual diagrams and practical patterns:
- **File**: `HPD_AGENT_CONTEXT_QUICK_REFERENCE.md`
- **Contains**: Flow diagram, key concepts, context paths, filter order, patterns, troubleshooting
- **Lines**: 329
- **Best for**: Visual learners, quick lookups, implementation patterns

### I have 1 hour - Complete Technical Map
Detailed analysis with exact code locations:
- **File**: `HPD_AGENT_CONTEXT_FLOW_MAP.md`
- **Contains**: Full flow with line numbers, all classes, all methods, code snippets, architecture diagrams
- **Lines**: 694
- **Best for**: Deep understanding, implementation, debugging, extending

---

## Document Overview

### HPD_AGENT_CONTEXT_FLOW_MAP.md (694 lines)
**Most Detailed - Read This When:**
- Debugging context flow issues
- Implementing custom filters or memory systems
- Understanding exact code execution paths
- Need to trace context from specific line of code

**Contains:**
- Entry points (lines 1625, 1753)
- Core agentic loop details
- Message preparation with line numbers
- Complete filter pipeline breakdown
- Filter execution order and patterns
- All memory filter details (project, dynamic, static)
- Tool execution flow
- Post-invoke and turn filters
- Middleware pipeline ordering
- Complete context flow diagram
- Context passing mechanisms explained
- Critical file reference table
- Tracing examples (e.g., where project documents end up)

**Key Sections:**
1. Entry Points (2 options: RunAsync, RunStreamingAsync)
2. Core Agentic Loop
3. Message Preparation Phase
4. Prompt Filters (4 types detailed)
5. Message State After Filters
6. Decision Engine
7. LLM Call Execution
8. Streaming & Event Processing
9. Tool Execution Phase
10. Post-Invocation Filters
11. Message Turn Filters
12. History Reduction
13. Response Building
14. Middleware Pipeline
15. Complete Context Flow Diagram
16. Context Passing Mechanisms (3 main types)
17. Filter Registration Order
18. Error Handling & Reduction
19. Configuration & Customization
20. Context Flow Summary Table
21. Critical Files Reference
22. Tracing Context Examples

---

### HPD_AGENT_CONTEXT_QUICK_REFERENCE.md (329 lines)
**Visual & Practical - Read This When:**
- Need a quick visual reference
- Looking for implementation patterns
- Want decision trees for where to put context
- Searching for common implementation patterns
- Quick troubleshooting

**Contains:**
- ASCII flow diagram of complete context journey
- Visual filter pipeline breakdown
- Key concepts (one sentence each)
- 3 main context flow paths with examples
- Filter execution order (pre and post)
- Critical line numbers quick reference
- Decision tree: where should context go?
- Common implementation patterns (3 detailed examples)
- Troubleshooting checklist
- Quick statistics
- One-minute explanation

**Key Sections:**
1. Short Version - Context Journey (with ASCII diagram)
2. Key Concepts Table
3. Where Does Context Flow (3 path examples)
4. Filter Execution Order
5. Critical Line Numbers
6. Decision: Which Mechanism to Use
7. Common Implementation Patterns
8. Troubleshooting: Context Not Flowing
9. Quick Stats
10. One-Minute Explanation

---

### HPD_AGENT_CONTEXT_FLOW_SUMMARY.md (398 lines)
**Executive Summary - Read This When:**
- New to the codebase
- Need overview before deep dive
- Presenting to team
- Understanding at medium detail level

**Contains:**
- 7-step context flow explanation
- Key mechanisms (4 main ones explained)
- Filter details (ProjectInjected, Dynamic, Static, Custom)
- 3 context flow paths traced
- Critical code locations table
- Configuration points
- Common patterns
- Decision tree for context placement
- Performance considerations
- Troubleshooting checklist
- Summary of flow

**Key Sections:**
1. The 7-Step Context Flow (detailed)
2. Key Mechanisms (4 explained)
3. Filter Details (all types)
4. Context Flow Paths (3 traced)
5. Critical Code Locations
6. Configuration Points
7. Common Patterns
8. Decision Tree
9. Performance Considerations
10. Troubleshooting Checklist
11. Related Documentation

---

## The 7-Step Context Flow (Quick Preview)

```
1. Entry Point
   User calls Agent.RunAsync() or RunStreamingAsync()
   ↓
2. Context Extraction
   Project extracted from ConversationThread
   Placed in ChatOptions.AdditionalProperties
   ↓
3. Message Preparation
   System instructions prepended
   AdditionalProperties transferred to PromptFilterContext.Properties
   ↓
4. Prompt Filter Pipeline
   5 filters execute in order:
   - ProjectInjectedMemoryFilter (always)
   - DynamicMemoryFilter (optional)
   - StaticMemoryFilter (optional)
   - AgentPlanFilter (optional)
   - Custom Filters (optional)
   ↓
5. LLM Call
   Prepared messages sent to provider ChatClient
   Response streamed back with events
   ↓
6. Tool Execution (if needed)
   Function context available via AsyncLocal
   ScopedFilters, Permissions, Invocation
   ↓
7. Post-Processing
   PostInvokeFilters extract learned info
   MessageTurnFilters for observability
   Final response returned to user
```

---

## Quick Navigation by Topic

### I need to understand...

**How context enters the system**
- Summary: Step 1-2
- Quick Ref: Entry Point section
- Full Map: Entry Points (section 1)

**How filters work**
- Summary: Step 4, Filter Details section
- Quick Ref: Filter Execution Order, Implementation Patterns
- Full Map: Prompt Filters (section 4), Filter Pipeline Pattern (section 4.5)

**How project documents reach the LLM**
- Summary: Context Flow Paths → Path 1
- Quick Ref: Common Implementation Patterns → Pattern 1
- Full Map: Tracing Context Examples (section 22)

**How function context flows during tool execution**
- Summary: Step 6, Context Flow Paths → Path 2
- Quick Ref: Implementation Patterns → Pattern 2
- Full Map: Tool Execution Phase (section 9)

**How memory is extracted and stored**
- Summary: Step 7, Context Flow Paths → Path 3
- Quick Ref: Implementation Patterns → Pattern 3
- Full Map: Post-Invocation Filters (section 10)

**Where to put my custom context**
- Summary: Decision Tree
- Quick Ref: Decision: Which Mechanism to Use
- Full Map: Key Context Passing Mechanisms (section 16)

**How to register custom filters**
- Summary: Configuration Points
- Quick Ref: Decision Tree at bottom
- Full Map: Filter Registration Order (section 17)

**What happens with AsyncLocal**
- Summary: Key Mechanisms section 3
- Quick Ref: Key Concepts → AsyncLocal
- Full Map: Key Context Passing Mechanisms (section 16.2)

**How middleware wraps ChatClient**
- Summary: Mentioned in Step 5
- Quick Ref: Not detailed
- Full Map: Middleware Pipeline (section 14)

**Provider implementations (OpenRouter, OpenAI, etc.)**
- Summary: Step 5
- Quick Ref: Provider ChatClient
- Full Map: LLM Call Execution (section 7)

---

## Key Concepts Reference

| Concept | Location |
|---------|----------|
| ChatOptions.AdditionalProperties | Summary: Mechanisms 1, Full: Section 16.1 |
| Filter Pipeline Pattern | Summary: Mechanisms 2, Full: Section 4.5 |
| AsyncLocal Storage | Summary: Mechanisms 3, Full: Section 16.2 |
| PromptFilterContext.Properties | Summary: Step 3, Full: Section 3.2 |
| ProjectInjectedMemoryFilter | Summary: Filter Details, Full: Section 4.1 |
| DynamicMemoryFilter | Summary: Filter Details, Full: Section 4.2 |
| StaticMemoryFilter | Summary: Filter Details, Full: Section 4.3 |
| AgentDecisionEngine | Quick Ref: Key Concepts, Full: Section 6 |
| AgentTurn | Quick Ref: Key Concepts, Full: Section 7 |
| ToolScheduler | Summary: Step 6, Full: Section 9 |
| ScopedFilterManager | Summary: Step 6, Full: Section 9.2 |
| MessageProcessor | Summary: Step 3, Full: Section 3.1 |
| ConversationThread | Quick Ref: Key Concepts, Full: Section 1.1 |

---

## Code File References

| File | Primary Location |
|------|------------------|
| Agent.cs | All documents - multiple sections |
| AgentBuilder.cs | Summary: Config, Quick: Decision Tree, Full: Section 18-19 |
| ProjectInjectedMemoryFilter.cs | All docs describe it, Full Map: Section 4.1 |
| DynamicMemoryFilter.cs | All docs describe it, Full Map: Section 4.2 |
| StaticMemoryFilter.cs | All docs describe it, Full Map: Section 4.3 |
| IPromptFilter.cs | Full Map: Section 4 |
| PromptFilterContext.cs | Full Map: Section 3.2 |

---

## Line Number Index

For quick lookup of specific implementations in Agent.cs:

| Feature | Lines |
|---------|-------|
| RunAsync (entry) | 1625-1742 |
| RunStreamingAsync (entry) | 1753-1850 |
| Project context injection | 1772-1779 |
| MessageProcessor.PrepareMessagesAsync | 3456-3515 |
| Filter context creation | 3658-3667 |
| Filter pipeline execution | 3671-3672 |
| ApplyPromptFiltersAsync | 3646-3673 |
| ApplyPostInvokeFiltersAsync | 3684-3729 |
| AgentTurn.RunAsync | 3783-3801 |
| LLM call inline execution | 559-656 |
| Tool execution | 765-790 |
| FilterChain.BuildPromptPipeline | 5722-5739 |
| MessageProcessor class | 3420-3748 |
| AgentTurn class | 3752-3870 |

---

## Typical Reading Sequence

### For New Team Members
1. Start with Quick Reference (10 min)
2. Read Summary (15 min)
3. Look up specific sections in Full Map as needed (ongoing)

### For Implementers
1. Quick Reference for patterns (5 min)
2. Full Map section on their specific feature (20 min)
3. Cross-reference with actual code in Agent.cs

### For Debuggers
1. Troubleshooting checklist in Summary or Quick Ref (5 min)
2. Trace specific context path in Full Map (10 min)
3. Verify against actual code execution in debugger

### For Architects
1. Read Full Map section 15 (Context Flow Diagram) (5 min)
2. Read Summary sections on Mechanisms and Flow (10 min)
3. Review Configuration Points and related sections (10 min)

---

## How to Use These Documents

### If debugging context flow issues
1. Identify which step context breaks in (section 15 diagram in Full Map)
2. Find the specific component in that step
3. Check tracing example (section 22 in Full Map)
4. Verify with code

### If implementing a custom filter
1. Review Summary: Configuration Points
2. Review Quick Ref: Implementation Patterns → Pattern 1
3. Check Full Map: Section 4 for filter patterns
4. Implement following extension method pattern in Quick Ref

### If adding new context type
1. Check Summary: Decision Tree
2. Determine which mechanism (AdditionalProperties? AsyncLocal? Filter?)
3. Find that mechanism in Full Map: Section 16
4. Follow the pattern to implementation

### If understanding performance
1. Check Summary: Performance Considerations
2. Review caching details in Summary: Filter Details
3. Read full caching in Full Map: Section 4

---

## Document Quality

- **HPD_AGENT_CONTEXT_FLOW_MAP.md**: 20+ sections, 22 subsections, detailed code references
- **HPD_AGENT_CONTEXT_QUICK_REFERENCE.md**: 10+ sections, visual diagrams, practical patterns
- **HPD_AGENT_CONTEXT_FLOW_SUMMARY.md**: 10+ sections, executive overview, decision tree
- **Total**: 1421 lines of documentation, 50+ code references, 100+ key concepts

---

## When to Reference Each Document

| Situation | Document |
|-----------|----------|
| "What's the overall architecture?" | Summary |
| "I need a visual diagram" | Quick Reference |
| "Where is line X doing Y?" | Full Map |
| "How do I implement a filter?" | Quick Reference (Pattern 1) |
| "Why isn't my context reaching the LLM?" | Troubleshooting checklist |
| "How does tool execution work?" | Summary Step 6 + Full Map Section 9 |
| "What's the filter order?" | Quick Reference or Summary |
| "I need to trace where X ends up" | Full Map Section 22 |
| "Where do I put custom context?" | Decision Tree in Summary or Quick Ref |
| "How does post-invoke filtering work?" | Summary Step 7 + Full Map Section 10 |

---

## Related Source Files

All context in these documents comes from analysis of:

- `/HPD-Agent/Agent/Agent.cs` (main implementation)
- `/HPD-Agent/Agent/AgentBuilder.cs` (configuration)
- `/HPD-Agent/Filters/PromptFiltering/IPromptFilter.cs` (filter interface)
- `/HPD-Agent/Project/DocumentHandling/FullTextInjection/ProjectInjectedMemoryFilter.cs`
- `/HPD-Agent/Memory/Agent/DynamicMemory/DynamicMemoryFilter.cs`
- `/HPD-Agent/Memory/Agent/StaticMemory/StaticMemoryFilter.cs`
- `/HPD-Agent/Filters/PromptFiltering/PromptFilterContext.cs`
- `/HPD-Agent/Filters/PromptFiltering/PromptFilterContextExtensions.cs`

---

## Document Maintenance

These documents were generated by analyzing the HPD-Agent codebase on 2025-11-09.

They document:
- Agent.cs (3900+ lines of core logic)
- AgentBuilder.cs (2095 lines of configuration)
- All filter implementations (100+ lines each)
- Core architectural patterns

Updates recommended if:
- Filter pipeline architecture changes
- Entry point signatures change
- Filter registration mechanism changes
- Context passing mechanisms change
- Provider integration changes

---

## Questions?

If any documentation is unclear:
1. Check the "Quick Navigation by Topic" section above
2. Cross-reference between documents (summary has concept, full map has details)
3. Look at implementation patterns in Quick Reference
4. Review actual code against line references in Full Map

Each document is designed to be self-contained but cross-references the others for depth.

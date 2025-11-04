# AIContextProvider Architecture - Visual Documentation

## Architecture Overview

```ascii
═══════════════════════════════════════════════════════════════════════════════
         SIMPLE API: Microsoft-Compatible Context Provider Pattern
═══════════════════════════════════════════════════════════════════════════════

USER'S PERSPECTIVE (What they see):
┌─────────────────────────────────────────────────────────────────────────────┐
│                                                                               │
│  var agent = new AgentBuilder()                                              │
│      .WithName("MyAgent")                                                    │
│      .WithContextProvider(new MyMemoryProvider())  ← Simple, familiar        │
│      .Build();                                                               │
│                                                                               │
│  That's it! No filters, no pipelines, no complexity.                        │
│  100% Compatible with Microsoft.Agents.AI patterns                           │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

INTERNAL IMPLEMENTATION (What happens - internal to HPD-Agent):
┌─────────────────────────────────────────────────────────────────────────────┐
│                                                                               │
│  When .WithContextProvider(provider) is called:                              │
│                                                                               │
│  AgentBuilder internally creates TWO internal filter wrappers:               │
│                                                                               │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │ 1. AIContextProviderPreFilter (internal class)                  │         │
│  │    ├─ Calls provider.InvokingAsync()                            │         │
│  │    ├─ Gets AIContext (messages, tools, instructions)            │         │
│  │    └─ Merges into execution context                             │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                               │
│  ┌────────────────────────────────────────────────────────────────┐         │
│  │ 2. AIContextProviderPostFilter (internal class)                 │         │
│  │    ├─ No-op on forward pass                                     │         │
│  │    └─ Calls provider.InvokedAsync() on return                   │         │
│  └────────────────────────────────────────────────────────────────┘         │
│                                                                               │
│  Result: User gets Microsoft-compatible API, HPD-Agent handles the rest!    │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Execution Pipeline Flow

```ascii
═══════════════════════════════════════════════════════════════════════════════
                        COMPLETE REQUEST/RESPONSE FLOW
═══════════════════════════════════════════════════════════════════════════════

User Message: "What's my favorite color?"
     │
     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ FORWARD PASS (Building Context)                                              │
└─────────────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [AIContextProviderPreFilter] ← ALWAYS FIRST                        │
│                                                                     │
│  Calls: provider.InvokingAsync(requestMessages)                    │
│         ↓                                                           │
│  Returns: AIContext {                                               │
│    Messages: [System("User likes blue")],                          │
│    Tools: [get_preferences],                                        │
│    Instructions: "Be friendly and helpful"                          │
│  }                                                                  │
│         ↓                                                           │
│  Action: Merge into PromptFilterContext                            │
│    ├─ Prepend messages                                             │
│    ├─ Add tools to Options.Tools                                   │
│    ├─ Append to Options.Instructions                               │
│    └─ Store messages in Properties["__AIContextProviderMessages"]  │
│         ↓                                                           │
│  Calls: next(context)                                              │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [User's Custom Filter #1: SafetyFilter]                            │
│                                                                     │
│  - Scans for PII                                                   │
│  - Checks for inappropriate content                                │
│  - May modify or block messages                                    │
│         ↓                                                           │
│  Calls: next(context)                                              │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [User's Custom Filter #2: CustomRAGFilter]                         │
│                                                                     │
│  - Searches vector database                                        │
│  - Adds relevant documents as system messages                      │
│  - Example: Adds color psychology article                          │
│         ↓                                                           │
│  Calls: next(context)                                              │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [AIContextProviderPostFilter] ← ALWAYS LAST                        │
│                                                                     │
│  Forward pass: Just calls next(context) - no-op                    │
│         ↓                                                           │
│  Calls: next(context)                                              │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [ACTUAL LLM CALL]                                                  │
│                                                                     │
│  Final Messages Sent to LLM:                                       │
│    1. System: "Color psychology article..." ← CustomRAGFilter      │
│    2. System: "User likes blue" ← AIContextProvider                │
│    3. User: "What's my favorite color?"                            │
│                                                                     │
│  Tools Available:                                                  │
│    - get_preferences ← AIContextProvider                           │
│    - (any other tools...)                                          │
│                                                                     │
│  Instructions:                                                     │
│    "Be friendly and helpful" ← AIContextProvider                   │
│    (+ any system instructions)                                     │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
Response from LLM: "Blue is your favorite color! Would you like to 
                    know more about color psychology?"
     │
     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ BACKWARD PASS (Processing Response)                                          │
└─────────────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [AIContextProviderPostFilter.PostInvokeAsync()] ← FIRST to process │
│                                                                     │
│  Retrieves: Properties["__AIContextProviderMessages"]              │
│         ↓                                                           │
│  Calls: provider.InvokedAsync(                                     │
│           requestMessages,                                          │
│           aiContextProviderMessages,                                │
│           responseMessages,                                         │
│           exception: null                                           │
│         )                                                           │
│         ↓                                                           │
│  Provider Actions:                                                 │
│    - Extracts: User confirmed preference for blue                  │
│    - Stores: Memory updated with confirmation                      │
│    - Learns: Color preference is high confidence                   │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [User's Custom Filter #2: CustomRAGFilter.PostInvokeAsync()]       │
│                                                                     │
│  Optional post-processing:                                         │
│    - Update document relevance scores                              │
│    - Track which docs were useful                                  │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
┌────────────────────────────────────────────────────────────────────┐
│ [User's Custom Filter #1: SafetyFilter.PostInvokeAsync()]          │
│                                                                     │
│  Optional post-processing:                                         │
│    - Log interaction for compliance                                │
│    - Audit response for policy violations                          │
└────────────────────────────────────────────────────────────────────┘
     │
     ▼
Final Response to User: "Blue is your favorite color!..."
```

---

## Three User Profiles

```ascii
═══════════════════════════════════════════════════════════════════════════════
                    USER JOURNEY: SIMPLE → ADVANCED → EXPERT
═══════════════════════════════════════════════════════════════════════════════

┌─────────────────────────────────────────────────────────────────────────────┐
│ PROFILE 1: BEGINNER (95% of users)                                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  Needs: Just want memory/RAG to work                                         │
│                                                                               │
│  Code:                                                                        │
│    var agent = new AgentBuilder()                                            │
│        .WithContextProvider(new MyMemoryProvider())                          │
│        .Build();                                                             │
│                                                                               │
│  Pipeline: [PreFilter] → [PostFilter] → LLM                                 │
│                                                                               │
│  Knowledge Required:                                                         │
│    ✓ How to instantiate a provider                                          │
│    ✗ Filter architecture (hidden)                                           │
│    ✗ Message transformation (hidden)                                        │
│    ✗ Pipeline concepts (hidden)                                             │
│                                                                               │
│  Outcome: Working agent with memory in 5 minutes                             │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ PROFILE 2: INTERMEDIATE (4% of users)                                        │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  Needs: Memory + custom business logic (safety, personalization)            │
│                                                                               │
│  Code:                                                                        │
│    var agent = new AgentBuilder()                                            │
│        .WithContextProvider(new MyMemoryProvider())                          │
│        .WithPromptFilter(new SafetyFilter())                                 │
│        .WithPromptFilter(new PersonalizationFilter())                        │
│        .Build();                                                             │
│                                                                               │
│  Pipeline: [PreFilter] → [Safety] → [Personalization] → [PostFilter] → LLM  │
│                                                                               │
│  Knowledge Required:                                                         │
│    ✓ Context provider usage                                                 │
│    ✓ Basic filter concept                                                   │
│    ✓ Filter ordering                                                         │
│    ✗ Advanced pipeline control (not needed yet)                             │
│                                                                               │
│  Outcome: Powerful agent with custom logic, gradual learning curve           │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ PROFILE 3: EXPERT (1% of users - framework builders)                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  Needs: Complete control over message transformation pipeline                │
│                                                                               │
│  Code:                                                                        │
│    var agent = new AgentBuilder()                                            │
│        .WithPromptFilter(new MemoryFilter())                                 │
│        .WithPromptFilter(new RAGFilter())                                    │
│        .WithPromptFilter(new SafetyFilter())                                 │
│        .WithPromptFilter(new ToolSelectorFilter())                           │
│        .Build();                                                             │
│                                                                               │
│  Pipeline: [Memory] → [RAG] → [Safety] → [ToolSelector] → LLM              │
│                                                                               │
│  Knowledge Required:                                                         │
│    ✓ Full IPromptFilter interface                                           │
│    ✓ PromptFilterContext details                                            │
│    ✓ Pipeline execution model                                               │
│    ✓ Short-circuit patterns                                                 │
│    ✓ PostInvokeAsync lifecycle                                              │
│                                                                               │
│  Outcome: Maximum flexibility, building custom frameworks on HPD-Agent       │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Configuration Examples

```ascii
═══════════════════════════════════════════════════════════════════════════════
                          CONFIGURATION STRATEGIES
═══════════════════════════════════════════════════════════════════════════════

┌─────────────────────────────────────────────────────────────────────────────┐
│ STRATEGY 1: Simple (JSON Config)                                             │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  {                                                                            │
│    "name": "CustomerServiceAgent",                                           │
│    "provider": {                                                             │
│      "providerKey": "openai",                                                │
│      "modelName": "gpt-4"                                                    │
│    },                                                                         │
│    "contextProvider": {                                                       │
│      "type": "MyApp.MemoryProvider",                                         │
│      "assembly": "MyApp.dll"                                                 │
│    }                                                                          │
│  }                                                                            │
│                                                                               │
│  Result Pipeline:                                                            │
│    [ContextProviderPre] → [ContextProviderPost] → LLM                       │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ STRATEGY 2: Advanced (Context Provider + Filters)                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  {                                                                            │
│    "name": "CustomerServiceAgent",                                           │
│    "contextProvider": {                                                       │
│      "type": "MyApp.MemoryProvider",                                         │
│      "assembly": "MyApp.dll"                                                 │
│    },                                                                         │
│    "filters": [                                                               │
│      {                                                                        │
│        "type": "MyApp.SafetyFilter",                                         │
│        "priority": 100                                                       │
│      },                                                                       │
│      {                                                                        │
│        "type": "MyApp.PersonalizationFilter",                                │
│        "priority": 200                                                       │
│      }                                                                        │
│    ]                                                                          │
│  }                                                                            │
│                                                                               │
│  Result Pipeline:                                                            │
│    [ContextProviderPre] → [Safety] → [Personalization]                      │
│    → [ContextProviderPost] → LLM                                             │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ STRATEGY 3: Expert (Full Filter Control)                                     │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  {                                                                            │
│    "name": "CustomerServiceAgent",                                           │
│    "filters": [                                                               │
│      { "type": "MyApp.MemoryFilter", "priority": 10 },                       │
│      { "type": "MyApp.RAGFilter", "priority": 20 },                          │
│      { "type": "MyApp.SafetyFilter", "priority": 30 },                       │
│      { "type": "MyApp.ToolSelectorFilter", "priority": 40 }                  │
│    ]                                                                          │
│  }                                                                            │
│                                                                               │
│  Result Pipeline:                                                            │
│    [Memory] → [RAG] → [Safety] → [ToolSelector] → LLM                       │
│                                                                               │
│  Note: No context provider - full manual control                             │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Comparison with Microsoft's Implementation

```ascii
═══════════════════════════════════════════════════════════════════════════════
              HPD-AGENT vs MICROSOFT.AGENTS.AI IMPLEMENTATION
═══════════════════════════════════════════════════════════════════════════════

┌─────────────────────────────────────────────────────────────────────────────┐
│ MICROSOFT.AGENTS.AI (Parallel Merge)                                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  Multiple AIContextProviders run independently:                              │
│                                                                               │
│         ┌─────────────┐                                                      │
│  ┌─────→│  Provider1  │─────┐                                               │
│  │      └─────────────┘     │                                               │
│  │                           │                                               │
│  │      ┌─────────────┐     │                                               │
│  ├─────→│  Provider2  │─────┤                                               │
│  │      └─────────────┘     │                                               │
│  │                           ▼                                               │
│  │      ┌─────────────┐   ┌──────┐                                          │
│  └─────→│  Provider3  │───│MERGE │──→ LLM                                   │
│         └─────────────┘   └──────┘                                          │
│                                                                               │
│  Pros:                                                                       │
│    ✓ Providers are independent                                              │
│    ✓ Can run in parallel                                                    │
│                                                                               │
│  Cons:                                                                       │
│    ✗ Providers can't see each other's changes                               │
│    ✗ No transformation, only addition                                       │
│    ✗ No ordering guarantees                                                 │
│    ✗ Can't short-circuit                                                    │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ HPD-AGENT (Sequential Pipeline)                                              │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  Filters run sequentially, each sees previous changes:                       │
│                                                                               │
│  User Message                                                                │
│       ↓                                                                       │
│  ┌─────────────┐                                                             │
│  │ Provider    │  Wraps AIContextProvider                                    │
│  │ PreFilter   │  Adds memories, tools                                       │
│  └─────┬───────┘                                                             │
│        ↓                                                                      │
│  ┌─────────────┐                                                             │
│  │   Filter1   │  Sees provider's changes                                    │
│  │  (Safety)   │  Can transform or add                                       │
│  └─────┬───────┘                                                             │
│        ↓                                                                      │
│  ┌─────────────┐                                                             │
│  │   Filter2   │  Sees all previous changes                                  │
│  │   (RAG)     │  Can add documents                                          │
│  └─────┬───────┘                                                             │
│        ↓                                                                      │
│  ┌─────────────┐                                                             │
│  │ Provider    │  No-op on forward                                           │
│  │ PostFilter  │  Learns on return                                           │
│  └─────┬───────┘                                                             │
│        ↓                                                                      │
│       LLM                                                                     │
│                                                                               │
│  Pros:                                                                       │
│    ✓ Filters see each other's changes                                       │
│    ✓ Full transformation capability                                         │
│    ✓ Explicit ordering                                                      │
│    ✓ Can short-circuit                                                      │
│    ✓ Post-processing support                                                │
│                                                                               │
│  Tradeoff:                                                                   │
│    • Sequential execution (but more powerful)                                │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

VERDICT: HPD-Agent's approach is MORE POWERFUL while maintaining compatibility
```

---

## Migration Paths

```ascii
═══════════════════════════════════════════════════════════════════════════════
                    HOW USERS GROW WITH THE FRAMEWORK
═══════════════════════════════════════════════════════════════════════════════

┌─────────────────────────────────────────────────────────────────────────────┐
│ MONTH 1: Getting Started                                                     │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  User discovers HPD-Agent, reads getting started guide:                      │
│                                                                               │
│    var agent = new AgentBuilder()                                            │
│        .WithName("MyAgent")                                                  │
│        .WithContextProvider(new SimpleMemoryProvider())                      │
│        .Build();                                                             │
│                                                                               │
│  ✓ Working agent in minutes                                                 │
│  ✓ Understands basic concepts                                               │
│  ✓ Compatible with Microsoft docs                                           │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────────────┐
│ MONTH 3: Adding Business Logic                                               │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  User needs to add compliance/safety checks:                                 │
│                                                                               │
│    var agent = new AgentBuilder()                                            │
│        .WithName("MyAgent")                                                  │
│        .WithContextProvider(new SimpleMemoryProvider())                      │
│        .WithPromptFilter(new ComplianceFilter())  ← New!                     │
│        .Build();                                                             │
│                                                                               │
│  ✓ Learns about filters gradually                                           │
│  ✓ No need to rewrite existing code                                         │
│  ✓ Combines provider + filter easily                                        │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────────────┐
│ MONTH 6: Advanced Scenarios                                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  User needs custom message transformation:                                   │
│                                                                               │
│    var agent = new AgentBuilder()                                            │
│        .WithName("MyAgent")                                                  │
│        .WithContextProvider(new SimpleMemoryProvider())                      │
│        .WithPromptFilter(new ComplianceFilter())                             │
│        .WithPromptFilter(new CustomTransformFilter())  ← Custom logic        │
│        .Build();                                                             │
│                                                                               │
│  ✓ Building custom filters                                                  │
│  ✓ Understanding pipeline execution                                         │
│  ✓ Using PostInvokeAsync for learning                                       │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────────────┐
│ MONTH 12: Expert/Framework Builder                                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  User builds their own framework on top of HPD-Agent:                        │
│                                                                               │
│    // No context provider - full manual control                              │
│    var agent = new AgentBuilder()                                            │
│        .WithName("MyAgent")                                                  │
│        .WithPromptFilter(new CustomMemoryFilter())                           │
│        .WithPromptFilter(new AdvancedRAGFilter())                            │
│        .WithPromptFilter(new SecurityFilter())                               │
│        .WithPromptFilter(new ContextWindowOptimizer())                       │
│        .Build();                                                             │
│                                                                               │
│  ✓ Complete mastery of filter architecture                                  │
│  ✓ Building reusable components                                             │
│  ✓ Contributing back to ecosystem                                           │
│                                                                               │
└─────────────────────────────────────────────────────────────────────────────┘

KEY INSIGHT: Users never hit a "wall" where they need to rewrite everything.
            Each step builds on previous knowledge naturally.
```

---

## Benefits Summary

```ascii
═══════════════════════════════════════════════════════════════════════════════
                          WHY THIS ARCHITECTURE WINS
═══════════════════════════════════════════════════════════════════════════════

┌──────────────────────────┬──────────────────────────────────────────────────┐
│  Stakeholder             │  Benefit                                         │
├──────────────────────────┼──────────────────────────────────────────────────┤
│                          │                                                  │
│  Beginners               │  ✓ Simple API matches Microsoft docs            │
│  (95% of users)          │  ✓ Working agent in 5 minutes                    │
│                          │  ✓ No need to learn complex concepts             │
│                          │  ✓ Can share providers with other frameworks     │
│                          │                                                  │
├──────────────────────────┼──────────────────────────────────────────────────┤
│                          │                                                  │
│  Intermediate Users      │  ✓ Gradual learning curve                        │
│  (4% of users)           │  ✓ Mix simple + advanced features               │
│                          │  ✓ No need to rewrite existing code              │
│                          │  ✓ Clear documentation for each level            │
│                          │                                                  │
├──────────────────────────┼──────────────────────────────────────────────────┤
│                          │                                                  │
│  Expert Users            │  ✓ Full control via IPromptFilter                │
│  (1% of users)           │  ✓ No limitations or "walls"                     │
│                          │  ✓ Can build frameworks on HPD-Agent             │
│                          │  ✓ Performance optimization possible             │
│                          │                                                  │
├──────────────────────────┼──────────────────────────────────────────────────┤
│                          │                                                  │
│  HPD-Agent Project       │  ✓ Lowers barrier to adoption dramatically       │
│                          │  ✓ Microsoft ecosystem compatibility             │
│                          │  ✓ Professional, familiar patterns               │
│                          │  ✓ No breaking changes to existing code          │
│                          │  ✓ Positions as beginner-friendly + powerful     │
│                          │                                                  │
├──────────────────────────┼──────────────────────────────────────────────────┤
│                          │                                                  │
│  Ecosystem               │  ✓ Providers shareable across frameworks         │
│                          │  ✓ Reduces fragmentation                         │
│                          │  ✓ Community can build on standards              │
│                          │  ✓ Better interoperability                       │
│                          │                                                  │
└──────────────────────────┴──────────────────────────────────────────────────┘
```

---

**Document Version**: 1.0  
**Date**: November 4, 2025  
**Related Documents**: AICONTEXTPROVIDER_HYBRID_ARCHITECTURE_EVALUATION.md

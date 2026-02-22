# HPD Agent Headless UI - Development Guide

## Project Overview

**HPD Agent Headless UI** is a headless component library for AI chat interfaces built with Svelte. It provides reactive, protocol-first components that map directly to HPD Agent events.

**Key Characteristics:**
- 🚀 **Protocol-first**
- 💎 **Truly headless** (zero CSS, data attributes only)
- ⚡ **Fine-grained reactivity** (runes-based)
- 📦 **Bundle conscious** (< 20 KB target)

---

## Pre-Phase 1 Status 

### Completed Infrastructure

#### 1.  EventMapper - Core Reactive Layer
**Location:** `src/lib/internal/event-mapper.ts`

Maps 71 HPD protocol events to AgentState method calls:
- **Phase 1 Events (22 implemented):**
  - Text Content (3): `TEXT_MESSAGE_START`, `TEXT_DELTA`, `TEXT_MESSAGE_END`
  - Lifecycle (3): `MESSAGE_TURN_STARTED`, `MESSAGE_TURN_FINISHED`, `MESSAGE_TURN_ERROR`
  - Tools (4): `TOOL_CALL_START`, `TOOL_CALL_ARGS`, `TOOL_CALL_END`, `TOOL_CALL_RESULT`
  - Permissions (3): `PERMISSION_REQUEST`, `PERMISSION_APPROVED`, `PERMISSION_DENIED`
  - Clarifications (1): `CLARIFICATION_REQUEST`
  - Client Tools (2): `CLIENT_TOOL_INVOKE_REQUEST`, `CLIENT_TOOL_GROUPS_REGISTERED`
  - Reasoning (3): `REASONING_MESSAGE_START`, `REASONING_DELTA`, `REASONING_MESSAGE_END`

- **Deferred to Phase 2/3:**
  - 21 Observability events → Phase 2 (debugging)
  - 25 Audio events → Phase 3 (voice UI)
  - 2 Continuation events → Phase 2
  - 2 Middleware events → Phase 2

**Tests:** `src/lib/internal/__tests__/event-mapper.test.ts`

#### 2.  Bundle Analyzer
**Location:** `bundle-analyzer/bundle-analyzer.ts`

Per-component bundle size tracking:
- Monitors gzipped size of each component
- Enforces size limits (total < 20 KB)
- Run with: `bun run analyze`

**Size Targets:**
- createAgent: < 5 KB
- Message: < 2 KB
- ToolExecution: < 3 KB
- PermissionDialog: < 2 KB
- Input: < 1 KB

#### 3.  Testing Infrastructure
**Vitest Browser Mode** configured in `vite.config.ts`:
- Chromium + Playwright
- 80%+ coverage target
- Test pattern: `src/**/*.svelte.{test,spec}.{js,ts}`

---

## Architecture Patterns

### State Management
```typescript
// Component state class
export class ComponentState {
  // Mutable state
  #value = $state<Type>(default);

  // Derived state
  readonly computed = $derived(this.#value * 2);

  // Props for rendering
  readonly props = $derived({
    'data-component': '',
    'aria-label': '...'
  });
}
```

### Event Flow
```
HPD Backend → AgentClient → EventMapper → AgentState → Svelte Reactivity → UI Update
```

### File Structure
```
src/lib/bits/component-name/
├── component-name.svelte.ts    # State class
├── types.ts                    # TypeScript types
├── exports.ts                  # Public API
├── index.ts                    # Entry point
└── components/
    └── component-name.svelte   # Svelte component
```

---

## Next Steps (Phase 1)

### Ready to Build:
1. **AgentState class** (`src/lib/bits/agent/agent.svelte.ts`)
   - Reactive state with $state runes
   - Event handler methods (called by EventMapper)
   - Public API for components

2. **createAgent() factory** (`src/lib/bits/agent/index.ts`)
   - Instantiates AgentClient
   - Creates AgentState
   - Connects EventMapper
   - Returns reactive agent instance

3. **Message component** (`src/lib/bits/message/`)
   - Display individual messages
   - Streaming text support
   - Data attributes for styling

4. **Example app** (`src/routes/`)
   - Working chat UI
   - Demonstrates all patterns

---

## Development Commands

```bash
# Development
bun run dev                # Start dev server
bun run check              # Type checking

# Testing
bun run test:unit          # Unit tests
bun run test:e2e           # E2E tests
bun run test               # All tests

# Analysis
bun run analyze            # Bundle size analysis

# Building
bun run build              # Build library
bun run prepack            # Package for NPM

# Documentation
bun run storybook          # Start Storybook
```

---

## Svelte MCP Server Tools

You are able to use the Svelte MCP server, where you have access to comprehensive Svelte and SvelteKit documentation. Here's how to use the available tools effectively:

### Available MCP Tools:

#### 1. list-sections
Use this FIRST to discover all available documentation sections. Returns a structured list with titles, use_cases, and paths.
When asked about Svelte or SvelteKit topics, ALWAYS use this tool at the start of the chat to find relevant sections.

#### 2. get-documentation
Retrieves full documentation content for specific sections. Accepts single or multiple sections.
After calling the list-sections tool, you MUST analyze the returned documentation sections (especially the use_cases field) and then use the get-documentation tool to fetch ALL documentation sections that are relevant for the user's task.

#### 3. svelte-autofixer
Analyzes Svelte code and returns issues and suggestions.
You MUST use this tool whenever writing Svelte code before sending it to the user. Keep calling it until no issues or suggestions are returned.

#### 4. playground-link
Generates a Svelte Playground link with the provided code.
After completing the code, ask the user if they want a playground link. Only call this tool after user confirmation and NEVER if code was written to files in their project.

---

## Reference Materials

**HPD Agent Client:**
- Location: `../hpd-agent-client/`
- Event definitions: `src/types/events.ts`

**Proposal & Architecture:**
- Full proposal: `../InternalDocs/ShellOS/Proposal/hpd-agent-headless-ui/README.md`

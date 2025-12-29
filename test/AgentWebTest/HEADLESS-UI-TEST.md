# Testing HPD Agent Headless UI with Real Backend

This document explains how to test the Message component with the real HPD backend.

## Setup

### Option 1: Quick Test (Swap Files)

```bash
cd /Users/einsteinessibu/Documents/ShellOS/HPD-Agent-Framework/test/AgentWebTest/Frontend/frontend/src/routes

# Backup original
cp +page.svelte page-original.svelte

# Replace with headless UI version
cp page-headless.svelte +page.svelte
```

### Option 2: Side-by-Side Comparison

Keep both files and switch between them as needed.

## Running the Test

### 1. Start the Backend

```bash
cd /Users/einsteinessibu/Documents/ShellOS/HPD-Agent-Framework/test/AgentWebTest/AgentAPI
dotnet run
```

This starts the HPD Agent backend on `http://localhost:5135`

### 2. Install Dependencies (if needed)

```bash
cd /Users/einsteinessibu/Documents/ShellOS/HPD-Agent-Framework/test/AgentWebTest/Frontend/frontend
npm install
```

### 3. Start the Frontend

```bash
npm run dev
```

This starts the frontend on `http://localhost:5173` (or similar)

## What to Test

### 1. **Basic Streaming**

Send a simple message like:
```
Hello! Tell me a story.
```

**Expected Behavior:**
- User message appears immediately
- Assistant message shows `status: streaming` badge
- Text appears character by character
- Cursor (▊) blinks while streaming
- Status changes to `complete` when done

### 2. **Thinking/Reasoning**

Send a message that requires thinking:
```
What's the capital of France and why is it important?
```

**Expected Behavior:**
- Status shows `thinking` in purple
- Reasoning text appears in purple box: "Thinking: ..."
- Then switches to `streaming` when text starts
- Finally `complete` when done

### 3. **Tool Execution**

If the backend has tools configured:
```
Use the search tool to find information about X
```

**Expected Behavior:**
- Status shows `thinking` with "Using search..."
- Tool execution tracked in message state
- Results appear in content

### 4. **Permission Requests**

If tools require permissions:
```
Read my file system
```

**Expected Behavior:**
- Status shows `thinking` with "Waiting for permission..."
- Permission dialog appears
- After approval/denial, continues or stops

## What You're Validating

###   HPD Protocol Integration

1. **TEXT_DELTA events** → `streaming: true` → Shows cursor
2. **REASONING_DELTA events** → `thinking: true` → Shows reasoning
3. **TOOL_CALL_* events** → `status: executing` → Tracks tools
4. **Status Derivation** → Auto-calculated from protocol events

###   Message Component Features

1. **Truly Headless** - All styling via Tailwind classes in snippet
2. **AI-Specific States** - streaming, thinking, executing, complete
3. **Snippet Customization** - Full control over rendering
4. **Data Attributes** - Can be styled via CSS hooks
5. **ARIA Attributes** - Accessibility for screen readers

## Comparison

### Old Version (Original +page.svelte)

```svelte
<!-- Manual state management -->
<div class="message">
  {#if message.thinking}
    <div class="thinking">{message.thinking}</div>
  {/if}
  <div>{message.content}</div>
</div>
```

**Issues:**
- Manual thinking state tracking
- No status derivation
- Hardcoded HTML structure

### New Version (With Headless UI)

```svelte
<!-- Message component handles state -->
<Message {message}>
  {#snippet children({ content, thinking, reasoning, status })}
    <div>
      <span class="status">{status}</span>
      {#if hasReasoning}<div>{reasoning}</div>{/if}
      <div>{content}</div>
    </div>
  {/snippet}
</Message>
```

**Benefits:**
-   Automatic status derivation
-   Reasoning tracking built-in
-   Flexible HTML structure via snippet
-   Full HPD protocol support

## Debug Console

Open browser DevTools Console to see:

```
[v1.0] TEXT_DELTA: { text: "H", messageId: "..." }
Text delta: H

[v1.0] REASONING_DELTA: { text: "Analyzing...", messageId: "..." }
Reasoning: Analyzing...

[v1.0] TOOL_CALL_START: { callId: "...", name: "search" }
Tool call: search
```

This shows the **complete HPD protocol → Message component flow!**

## Expected Results

### Browser Display

```
Status: streaming
Hello! Let me tell you a st▊

Status: thinking
Thinking: Analyzing the user's request...

Status: complete
Hello! Let me tell you a story about...
```

### Console Logs

```
[SEND] Sending message to conversation: abc-123
[v1.0] TEXT_MESSAGE_START: { messageId: "...", role: "assistant" }
Text delta: H
Text delta: e
Text delta: l
[v1.0] TEXT_MESSAGE_END: { messageId: "..." }
Message complete
```

## Troubleshooting

### Message component not found

```bash
cd /Users/einsteinessibu/Documents/ShellOS/HPD-Agent-Framework/test/AgentWebTest/Frontend/frontend
npm install ../../../../hpd-agent-headless-ui
```

### Status not updating

Check that you're creating new message objects (not mutating):
```typescript
//   Correct
messages = [...messages.slice(0, -1), updatedMessage];

//   Wrong
messages[messages.length - 1].content = 'new'; // Mutation!
```

### Reasoning not showing

The backend needs to emit `REASONING_DELTA` events. Check backend logs.

## Success Criteria

You've successfully validated the integration when:

1.   Messages stream character by character with cursor
2.   Status badge shows: streaming → thinking → complete
3.   Reasoning appears in purple box when thinking
4.   Console shows HPD protocol events flowing through
5.   Component is truly headless (all styling in snippet)

## Next Steps

Once validated:

1. Document the integration pattern
2. Add tool execution visualization
3. Build PermissionDialog component
4. Add ClarificationDialog component
5. Create MessageList component with keyboard nav

---

**This test validates that your Message component correctly integrates with the full HPD protocol in a real application!** 🚀

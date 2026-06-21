# Externally Executed Client Tools

Client tools are model-visible tools whose implementation runs in a connected client runtime instead of inside the HPD server process.

The connected client can be a TypeScript UI, desktop app, mobile app, editor extension, local worker, or another SDK runtime. From the model's perspective, the tool is an ordinary function declaration. From HPD's perspective, the call is a bidirectional event exchange:

1. The client sends tool harness definitions in `runConfig.clientToolInput`.
2. HPD adds those tools to the model-visible tool list.
3. The model calls one of the tools.
4. HPD emits a `CLIENT_TOOL_INVOKE_REQUEST` event.
5. The connected client runs the local handler.
6. The client sends `CLIENT_TOOL_INVOKE_RESPONSE` back to HPD.
7. HPD resumes the tool call with the returned result.

Registering a handler tells the client how to execute a tool if HPD asks for it. Supplying `clientToolInput.clientToolHarnesses` tells HPD which external tools should be visible to the agent for that run.

## Register A UI Tool Harness

Use a client tool harness when the agent needs to invoke capabilities that only exist in the connected application. This example exposes two UI actions from a TypeScript app: opening a command menu and showing a notification.

```typescript
import {
  AgentClient,
  createCollapsedToolHarness,
  createErrorResponse,
  createTextResponse,
} from '@hpd/hpd-agent-client';

const client = new AgentClient({
  baseUrl: 'http://localhost:5135',
  transport: 'sse',
});

const uiHarness = createCollapsedToolHarness(
  'ui',
  'Use these tools when the agent needs to interact with the connected application UI.',
  [
    {
      name: 'open_command_menu',
      description: "Opens the connected application's command menu.",
      parametersSchema: {
        type: 'object',
        properties: {},
      },
    },
    {
      name: 'show_toast',
      description: 'Shows a short notification in the connected application.',
      parametersSchema: {
        type: 'object',
        properties: {
          message: { type: 'string' },
        },
        required: ['message'],
      },
    },
  ],
  {
    functionResult: 'The UI tools are now available.',
    systemPrompt: 'Use UI tools only when the user asks you to interact with the connected application.',
  }
);

client.tools.registerToolHarness(uiHarness, async (request) => {
  switch (request.toolName) {
    case 'open_command_menu':
      app.ui.openCommandMenu();
      return createTextResponse(request.requestId, 'Command menu opened.');

    case 'show_toast':
      app.ui.showToast(String(request.arguments.message ?? ''));
      return createTextResponse(request.requestId, 'Toast shown.');

    default:
      return createErrorResponse(request.requestId, `Unknown UI tool: ${request.toolName}`);
  }
});
```

The handler runs in the TypeScript client process. It can call application APIs, update UI state, read local state, or delegate to another process owned by the client application.

## Expose The Harness For A Run

Open the chat scope, subscribe to live events, then include the registered harnesses in the run config:

```typescript
const chat = await client.chat.open({
  agentId: 'assistant',
  threadId: 'main',
  session: {
    create: {
      metadata: { title: 'UI assistant' },
    },
  },
});

await chat.subscribeLive();

await chat.submitText('Open the command menu.', {
  runConfig: {
    clientToolInput: {
      clientToolHarnesses: client.tools.clientToolHarnesses,
    },
  },
});
```

`client.tools.registerToolHarness(...)` stores the local handler and the harness definition. `client.tools.clientToolHarnesses` is the schema surface sent to HPD for the run.

## Collapsed Client Harnesses

Client tool harnesses can start collapsed. A collapsed harness gives the model one container tool first; the underlying client tools become visible when the model expands the container.

```typescript
await chat.submitText('Help me navigate this UI.', {
  runConfig: {
    clientToolInput: {
      clientToolHarnesses: [uiHarness],
      expandedContainers: [],
      hiddenTools: ['show_toast'],
    },
  },
});
```

Use these fields to control visibility:

- `startCollapsed`: hides the harness behind a container when true.
- `description`: tells the model when to expand the container. It is required for collapsed harnesses.
- `functionResult`: returned once when the container is expanded.
- `systemPrompt`: injected persistently after expansion.
- `expandedContainers`: starts named harnesses expanded.
- `hiddenTools`: hides named tools for the run.

Tool responses can also return an augmentation that changes the client tool surface before the next iteration:

```typescript
return createTextResponse(request.requestId, 'Advanced UI tools are now visible.', {
  expandToolHarnesses: ['ui'],
  showTools: ['show_toast'],
});
```

Augmentation can inject or remove harnesses, expand or collapse harnesses, hide or show tools, add or remove context, and update client-owned state.

## Events And Transports

With SSE, the TypeScript client observes `CLIENT_TOOL_INVOKE_REQUEST` on the live event stream and posts the matching `CLIENT_TOOL_INVOKE_RESPONSE` to the hosted `/responses` route for the active agent, session, and thread.

With WebSocket, the same response event is sent over the socket after the connection is attached to a runtime scope.

Applications usually do not need to handle these events manually. If a matching handler is registered, the TypeScript client answers the request automatically. Use explicit event projection only when the UI needs to render, audit, or approve the external tool request.

## Boundary

Client tool harnesses use the portable harness shape: `description`, `startCollapsed`, `functionResult`, `systemPrompt`, `skills`, and runtime visibility controls. Server-only `[Collapse]` features such as collapse-scoped middleware types belong to C# tool harnesses, because those middleware instances run inside the HPD host process.

Use client tools when the executable capability belongs to the connected runtime. Use C# tool harnesses when the capability belongs to the agent host.

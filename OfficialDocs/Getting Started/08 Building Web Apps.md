# Building Web Apps

> Stream agent events to web, mobile, and desktop apps via Server-Sent Events (SSE)

HPD-Agent works in web applications through Server-Sent Events (SSE), enabling real-time streaming to React, Vue, Angular, mobile apps, and any HTTP client. The framework provides serialization tools for C# backends and a TypeScript client for frontends.

## Architecture Overview

```
┌─────────────────┐          ┌─────────────────┐
│   Frontend      │  SSE →   │   ASP.NET API   │
│   (TypeScript)  │  ← HTTP  │   (C# Backend)  │
└─────────────────┘          └─────────────────┘
         ↓                            ↓
   React/Vue/etc.              Agent.RunAsync()
```

**How it works:**
1. Frontend sends user messages via POST
2. Backend streams agent events via SSE (text/event-stream)
3. Frontend receives events in real-time using `hpd-agent-client`
4. Bidirectional events (permissions, clarifications) are sent back via POST

## Quick Start

### 1. Backend: ASP.NET Endpoint

Install the package:
```bash
dotnet add package HPD.Agent
```

Create a minimal SSE endpoint:

```csharp
using HPD.Agent;
using HPD.Agent.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();

app.MapPost("/agent/stream", async (
    HttpContext context,
    MessageRequest request) =>
{
    var agent = new AgentBuilder()
        .WithProvider("anthropic", "claude-sonnet-4-5")
        .WithSystemInstructions("You are a helpful assistant.")
        .Build();

    context.Response.ContentType = "text/event-stream";
    var writer = new StreamWriter(context.Response.Body);

    await foreach (var evt in agent.RunAsync(request.Messages))
    {
        // Serialize event to JSON
        var json = AgentEventSerializer.ToJson(evt);

        // Send as SSE
        await writer.WriteAsync($"data: {json}\n\n");
        await writer.FlushAsync();
    }
});

app.Run();

// Request model
record MessageRequest(List<ChatMessage> Messages);
```

### 2. Frontend: TypeScript Client

Install the client:
```bash
npm install hpd-agent-client
```

Create a React hook (or similar pattern for other frameworks):

```typescript
import { useAgent } from 'hpd-agent-client/react';

function ChatComponent() {
  const { messages, sendMessage, isStreaming } = useAgent({
    conversationId: 'my-conversation',
    baseUrl: 'http://localhost:5000'
  });

  const handleSubmit = (text: string) => {
    sendMessage({ role: 'user', content: text });
  };

  return (
    <div>
      {messages.map((msg, i) => (
        <div key={i}>{msg.content}</div>
      ))}
      {isStreaming && <div>Agent is typing...</div>}
      <input onSubmit={handleSubmit} />
    </div>
  );
}
```

That's it! The client handles event parsing, message accumulation, and state management automatically.

## What You Get

This minimal setup provides:
-   Real-time text streaming
-   Tool execution visibility
-   Automatic message accumulation
-   Turn lifecycle tracking
-   Error handling

## Production Setup

The quick start works for simple apps, but production deployments need:
- **Conversation persistence** - Save/restore conversations across sessions
- **Permission prompts** - UI dialogs for user approval
- **Client-side tools** - Execute tools in the browser (file pickers, geolocation, etc.)
- **Cancellation** - Stop button to interrupt long-running operations
- **Error recovery** - Retry logic, connection recovery

For complete production patterns, see:

- [**Web Quick Start**](../Platform%20Guides/Web%20Apps/Web%20Quick%20Start.md) - Full-featured web app setup
- [**Server Setup (SSE)**](../Platform%20Guides/Web%20Apps/Server%20Setup%20(SSE).md) - Production ASP.NET endpoints
- [**TypeScript Client**](../Platform%20Guides/Web%20Apps/TypeScript%20Client.md) - Complete client API reference
- [**User Prompts**](../Platform%20Guides/Web%20Apps/User%20Prompts.md) - React dialogs for permissions/clarifications
- [**Cancellation**](../Platform%20Guides/Web%20Apps/Cancellation.md) - Stop button implementation

## Client Installation Options

The TypeScript client supports multiple frameworks:

```bash
# React (with hooks)
npm install hpd-agent-client

# Vue/Angular/Vanilla JS (core only)
npm install hpd-agent-client
```

All frameworks use the same core client with framework-specific adapters.

## See Also

- [**Event Handling**](05%20Event%20Handling.md) - Understanding the event stream
- [**Building Console Apps**](07%20Building%20Console%20Apps.md) - Native .NET console patterns

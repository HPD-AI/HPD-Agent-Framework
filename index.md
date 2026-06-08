---
layout: home

hero:
  name: HPD Agent
  text: Agent runtime infrastructure for .NET.
  tagline: Tools, sessions, branches, events, middleware, providers, audio, bots, and hosted runtimes as explicit system surfaces.
  actions:
    - theme: brand
      text: Start Building
      link: /getting-started/
    - theme: alt
      text: Read The Concepts
      link: /concepts/agent-runtime-and-capabilities

features:
  - icon:
      src: /icons/zap.svg
    title: Start Small
    details: Begin with one builder and one run, then add streaming, tools, sessions, persistence, and hosting without changing the architecture.
    link: /getting-started/
    linkText: Follow the path

  - icon:
      src: /icons/git-branch.svg
    title: Durable State
    details: Keep session history, fork alternate branches, compact context, and preserve the runtime state a real agent app depends on.
    link: /concepts/sessions-branches-and-events
    linkText: Understand state

  - icon:
      src: /icons/radio.svg
    title: Event Native
    details: Stream text, tool calls, permissions, workflow traces, audio, custom events, and bidirectional host decisions through one event model.
    link: /guides/events/overview
    linkText: Explore events

  - icon:
      src: /icons/wrench.svg
    title: Composable Surfaces
    details: Register tools, harnesses, middleware, providers, subagents, bot adapters, and hosted clients with source-generation-friendly APIs.
    link: /guides/tools/author-a-tool-harness
    linkText: Add capabilities
---

<section class="hpd-home-lanes">
  <a class="hpd-lane hpd-lane-primary" href="/getting-started/">
    <span class="hpd-lane-kicker">First 30 minutes</span>
    <strong>Build one agent that streams, calls a tool, remembers context, and can be hosted.</strong>
    <span>Follow the shortest working path.</span>
  </a>
  <a class="hpd-lane" href="/guides/events/overview">
    <span class="hpd-lane-kicker">Runtime visibility</span>
    <strong>Render live turns, tool calls, permissions, audio, and workflows from the event stream.</strong>
    <span>Make the agent observable from day one.</span>
  </a>
  <a class="hpd-lane" href="/guides/middleware/overview">
    <span class="hpd-lane-kicker">Control plane</span>
    <strong>Add retrieval, policy, state, usage tracking, and custom behavior around each turn.</strong>
    <span>Shape the runtime without hiding it.</span>
  </a>
</section>

<section class="hpd-runtime-panel">
  <div>
    <p class="hpd-eyebrow">The core loop stays readable</p>
    <h2>One builder surface. Real production escape hatches.</h2>
    <p>
      HPD Agent keeps the beginner path small, then opens the runtime surfaces that usually get bolted on later:
      sessions, branches, event streams, middleware, tool harnesses, hosted APIs, and provider-specific clients.
    </p>
  </div>

```csharp
var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .WithInstructions("You are a concise product assistant.")
    .WithTool(new WeatherTools())
    .BuildAsync();

using var stream = agent.Subscribe<TextDeltaEvent>(e =>
    Console.Write(e.Delta));

var (sessionId, branchId) = await agent.CreateSessionAsync("demo");
await agent.RunAsync("What should I pack for Seattle?", sessionId, branchId);
```
</section>

<section class="hpd-capability-section">
  <p class="hpd-eyebrow">What you can build</p>
  <h2>Agents that are useful outside the demo.</h2>
  <p>
    HPD Agent gives .NET teams the runtime pieces around the model call: tools, state, events,
    channels, audio, hosting, evaluation, and orchestration.
  </p>

  <div class="hpd-capability-grid">
    <a class="hpd-capability-card" href="/guides/bots/overview">
      <span>Channels</span>
      <strong>Connect agents to Slack, Discord, Telegram, WhatsApp, and Teams.</strong>
      <p>Use platform adapters when you want the same agent to show up where users already work.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/tools/author-a-tool-harness">
      <span>Tools</span>
      <strong>Expose real C# capabilities as model-callable functions.</strong>
      <p>Register one method, a harness, MCP tools, OpenAPI tools, or externally executed client tools.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/sessions-and-streaming/render-an-event-stream">
      <span>Live UX</span>
      <strong>Render streaming text, tool calls, permissions, audio, and workflow traces.</strong>
      <p>Project the event stream into transcripts, timelines, dashboards, logs, or custom clients.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/sessions-and-streaming/branch-history-and-forking">
      <span>State</span>
      <strong>Keep sessions, fork branches, compact history, and resume work.</strong>
      <p>Build agents that remember enough to be useful without turning state into hidden magic.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/audio/overview">
      <span>Audio</span>
      <strong>Accept speech, produce voice, or run realtime audio experiences.</strong>
      <p>Use provider-specific audio clients while keeping audio events attached to the agent runtime.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/multi-agent/overview">
      <span>Orchestration</span>
      <strong>Compose subagents, handoffs, workflows, and conversation policies.</strong>
      <p>Move from one assistant to structured multi-agent systems when the problem calls for it.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/middleware/overview">
      <span>Control</span>
      <strong>Add middleware for permissions, retrieval, memory, usage, and policy.</strong>
      <p>Wrap the agent lifecycle without burying the behavior inside provider-specific code.</p>
    </a>
    <a class="hpd-capability-card" href="/guides/hosting/aspnet-core">
      <span>Hosting</span>
      <strong>Expose agents over HTTP with streaming, stored definitions, and clients.</strong>
      <p>Start locally, then turn the runtime into an application surface your frontend can call.</p>
    </a>
  </div>
</section>

<section class="hpd-home-path">
  <div>
    <p class="hpd-eyebrow">Start small</p>
    <h2>One agent first, then the rest of the system.</h2>
    <p>
      The beginner path gets you from a local console agent to tools, sessions, streaming, persistence,
      and hosting without forcing every advanced surface into the first page.
    </p>
  </div>
  <a class="hpd-path-button" href="/getting-started/">Open Getting Started</a>
</section>

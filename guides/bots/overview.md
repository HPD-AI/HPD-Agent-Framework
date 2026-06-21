# Bots Overview

HPD bot adapters connect platform messages to HPD agent sessions and threads. Use the bot packages when you want Slack, Discord, Telegram, WhatsApp, or Teams to drive the same hosted HPD agent runtime that your HTTP and UI surfaces use.

Start with [Platform Setup](platform-setup.md) when you are wiring a real bot. Use this page when you want to understand how all adapters map platform conversations into HPD sessions.

Provider pages:

- [Slack Bots](slack.md)
- [Discord Bots](discord.md)
- [Telegram Bots](telegram.md)
- [WhatsApp Bots](whatsapp.md)
- [Teams Bots](teams.md)

## Runtime Flow

```text
platform event, update, interaction, or activity
  -> platform-specific thread key
  -> PlatformSessionMapper.ResolveAsync(platformKey)
  -> HPD sessionId + threadId
  -> AgentManager builds or gets the configured agent
  -> Agent.RunAsync(...) in that session and thread scope
  -> platform-specific streaming/output callbacks
```

The platform adapter owns incoming transport details. HPD owns the agent run, session, thread, event stream, tools, and middleware behavior.

Most hosted examples use ASP.NET because webhooks are the common deployment shape. The adapter core is transport-neutral: generated adapters implement `IBotAdapter.HandleAsync(BotInboundEnvelope, ...)`, and the ASP.NET route is a generated bridge that converts `HttpContext` into that envelope. Polling, socket, worker, or custom hosts can dispatch the same adapter without making the bot logic depend on ASP.NET.

## Platform Keys

A platform key is an opaque string that identifies a conversation thread from the platform's point of view. Examples include a Slack channel and thread timestamp, a Discord interaction/thread identity, a Telegram chat/topic identity, a WhatsApp phone/user pair, or a Teams conversation identity.

`PlatformSessionMapper` maps that key to an HPD session and thread. On a miss, it creates a session through `SessionManager`, which also creates the default thread.

The mapper stores the primary key in session metadata as `platformKey`. It can also bind aliases through `platformKeyAliases` when a platform changes thread identity after the initial message.

## Scaling Caveat

The current mapper searches existing sessions by listing session ids and loading each session to compare metadata. That is O(n) over sessions. This is acceptable for low-volume deployments and early integrations, but high-volume bots should plan for a secondary index, cache, or store-level lookup before relying on this path at scale.

## Agent Selection

Platform configuration resolves an HPD agent id. That id selects the hosted or managed agent definition that handles the conversation.

Keep this separate from the platform thread key:

- the platform key identifies the user conversation
- the agent id identifies which HPD agent should answer
- the session id and thread id identify the persisted HPD conversation path

Most concrete adapters require `AgentId` in their config. That value should name the agent definition you want the platform to invoke, not the platform app id, bot user id, or channel id.

## Adapter Packages

The concrete packages provide hand-written setup extensions around the generated adapter registration:

- Slack: `AddSlackBot(...)`, `MapSlackWebhook(...)`, optional `AddSlackOAuth(...)`, optional Socket Mode with `AppToken`.
- Discord: `AddDiscordBot(...)`, `MapDiscordWebhook(...)`, optional Gateway forwarding.
- Telegram: `AddTelegramBot(...)` with `MapTelegramWebhook(...)`, or `AddTelegramBotWithPolling(...)` without a public webhook.
- WhatsApp: `AddWhatsappBot(...)`, `MapWhatsappWebhook(...)`.
- Teams: `builder.AddTeamsBot(...)`, `app.MapTeamsBot()`.

Prefer these package extensions over the bare generated overloads because they register platform HTTP clients, API clients, format converters, socket or polling services, registry providers, and other infrastructure.

## Streaming Runner

Adapters can use `BotStreamingRunner` to run an agent and send output back through platform callbacks. The runner obtains a thread operation lock, builds or gets the configured agent, subscribes to events, runs the scoped input, and releases the lock when the run finishes.

The shared output callbacks cover text deltas, final text, cards, and permission requests. Each platform still decides how those primitives become platform-native messages, edits, cards, buttons, or no-op behavior.

## Permission Caveats

Do not assume every platform has the same approval UX.

Current platform behavior is uneven. Some adapters may render interactive approval controls, while others may deny permission requests automatically or use platform-specific action handling. Platform button responses are only meaningful while the corresponding agent run is still waiting for the response.

Bot permission responses should not be described as hosted TUI response-route calls. They are handled through the bot adapter and active agent runtime path, not by posting to the hosted TUI's HTTP response route.

## Custom Adapters

When a platform package does not exist, use [Custom Bot Adapters And Source Generation](custom-adapters-and-source-generation.md). The generated source handles the repeated adapter dispatch shape and can also emit ASP.NET mapping helpers, but the adapter package still owns auth, signature verification, payload parsing, thread-key design, platform output, and any SDK-specific services.

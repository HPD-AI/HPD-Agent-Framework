# Slack Bots

Slack can reach HPD through the Events API, Interactivity, slash commands, OAuth install flow, or Socket Mode. The Slack adapter maps Slack threads to HPD sessions and threads, then runs the configured HPD agent for normal message and mention traffic.

## Quick Start

```csharp
using HPD.Agent.Bots.Slack;
using HPD.Agent.Bots.Slack.OAuth;

builder.Services.AddSlackBot(options =>
{
    options.SigningSecret = builder.Configuration["Slack:SigningSecret"]!;
    options.BotToken = builder.Configuration["Slack:BotToken"]!;
    options.BotUserId = builder.Configuration["Slack:BotUserId"];
    options.AppToken = builder.Configuration["Slack:AppToken"];
    options.AgentId = builder.Configuration["Agent:Id"] ?? "support-agent";
    options.UseNativeStreaming = false;
    options.StreamingDebounceMs = 500;
}, registerDefaultSecretResolver: true);

var app = builder.Build();

app.MapSlackWebhook("/slack/events");
```

`AgentId` is the HPD agent definition Slack should invoke. It is not the Slack app id or bot user id.

## Slack App Setup

Create a Slack app and configure these platform URLs to point at the route you mapped:

| Slack setting | Value |
| --- | --- |
| Events API request URL | `https://your-domain/slack/events` |
| Interactivity request URL | `https://your-domain/slack/events` |
| Slash command request URL | `https://your-domain/slack/events` |

Subscribe only to the events your app uses. Common events are `app_mention`, `message.channels`, `message.groups`, `message.im`, `message.mpim`, `reaction_added`, `reaction_removed`, `assistant_thread_started`, `assistant_thread_context_changed`, and `app_home_opened`.

Common scopes include `chat:write`, `app_mentions:read`, `channels:history`, `groups:history`, `im:history`, `mpim:history`, `users:read`, `im:write`, `reactions:read`, `reactions:write`, and `files:write`. Native assistant streaming also requires Slack's Assistants feature and the relevant assistant scope.

## Configuration

| Option | Required | Notes |
| --- | --- | --- |
| `SigningSecret` | Yes for HTTP | Verifies Slack request signatures. |
| `BotToken` | Usually | Bot user token for posting and updating messages. |
| `BotUserId` | Optional | Helps mention detection when known. |
| `AgentId` | Yes | HPD agent definition to run. |
| `AppToken` | Socket Mode only | `xapp-...` token with `connections:write`. |
| `UseNativeStreaming` | Optional | Uses Slack assistant streaming when assistant context is available. |
| `StreamingDebounceMs` | Optional | Controls edit/stream update cadence. |
| `PermissionTimeout` | Optional | How long Slack permission buttons can wait for a response. |

For single-workspace apps, set `Slack:BotToken`. For multi-workspace apps, the adapter can resolve `slack:BotToken:{teamId}` through the secret resolver. With the default resolver chain, `slack:BotToken` can resolve from environment-style keys such as `SLACK_BOT_TOKEN`.

## OAuth

Add Slack OAuth when the HPD host owns installation:

```csharp
builder.Services.AddSlackOAuth(options =>
{
    options.ClientId = builder.Configuration["Slack:OAuth:ClientId"]!;
    options.ClientSecret = builder.Configuration["Slack:OAuth:ClientSecret"]!;
    options.RedirectUri = builder.Configuration["Slack:OAuth:RedirectUri"]!;
});

app.MapSlackOAuth("/slack/install", "/slack/oauth/callback");
```

The default token store is in-memory. Register a durable `ISlackTokenStore` before relying on OAuth installs in production.

## Socket Mode

Set `Slack:AppToken` to enable Socket Mode. Socket Mode receives events over Slack's outbound WebSocket, which is useful for local development or private hosts without a public Events API URL.

Current confirmed Socket Mode handling covers Events API envelopes and block actions. Use the HTTP endpoint for slash commands and modal submissions unless your adapter package explicitly documents parity.

## Streaming And Permissions

The default output mode posts a placeholder and edits it as text arrives. Native Slack streaming uses Slack assistant stream APIs only when the request has assistant recipient context; otherwise the adapter falls back to post-and-edit.

Slack currently has the richest bot permission UX. Permission requests can render Approve/Deny Block Kit buttons and map the button action back into the active HPD run.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Slack rejects the request | `SigningSecret` is wrong, body was changed before verification, or timestamp is outside the replay window. |
| Bot does not answer in a channel | Bot is not invited, event subscriptions are missing, or the mapped URL is not public. |
| Native streaming is not used | Assistants feature/scope is missing, or the message is not in assistant thread context. |
| OAuth works then disappears after restart | The default install store is in-memory. |

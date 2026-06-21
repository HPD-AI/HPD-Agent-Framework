# Bot Platform Setup

Bot adapters bridge a platform conversation to an HPD agent run. The platform package receives the webhook, socket event, polling update, or Teams activity; maps it to a platform thread key; resolves an HPD session and thread; runs the configured agent; and sends output back to the platform.

The built-in host examples use ASP.NET for public webhook endpoints, but the generated adapter contract is not ASP.NET-only. An adapter can receive a `BotInboundEnvelope` from HTTP, polling, socket forwarding, or another host, then return a `BotAdapterResponse`. The `Map...Webhook(...)` helpers are the ASP.NET bridge around that neutral adapter surface.

## Common Host Shape

Every bot host needs the normal HPD runtime first:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHPDAgent(options =>
{
    options.ConfigureAgent = agent => agent
        .WithOpenAI(
            model: builder.Configuration["Agent:Model"]!,
            apiKey: builder.Configuration["OpenAI:ApiKey"]);

    options.PersistAfterTurn = true;
});

var agentId = builder.Configuration["Agent:Id"] ?? "support-agent";
```

Then add one or more platform adapters. The adapter `AgentId` is the HPD agent definition to invoke for inbound platform messages.

## Platform Matrix

| Platform | Register | Map | Transport | Required config |
| --- | --- | --- | --- | --- |
| Slack | `AddSlackBot(...)` | `MapSlackWebhook("/slack/events")` | HTTP Events API, or Socket Mode with `AppToken` | `SigningSecret`, `BotToken`, `AgentId` |
| Discord | `AddDiscordBot(...)` | `MapDiscordWebhook("/discord/interactions")` | Interaction webhook, optional Gateway forwarding | `PublicKey`, `BotToken`, `ApplicationId`, `AgentId` |
| Telegram | `AddTelegramBot(...)` or `AddTelegramBotWithPolling(...)` | `MapTelegramWebhook("/telegram/webhook")` in webhook mode | Webhook or long polling | `BotToken`, `AgentId`; optional `SecretToken` |
| WhatsApp | `AddWhatsappBot(...)` | `MapWhatsappWebhook("/whatsapp/webhook")` | Meta Cloud API webhook | `AccessToken`, `AppSecret`, `PhoneNumberId`, `VerifyToken`, `AgentId` |
| Teams | `builder.AddTeamsBot(...)` | `app.MapTeamsBot()` | Microsoft 365 Agents SDK endpoints | `AppId`, one auth method, `AgentId` |

You can also call `app.MapHPDBots()` for generated adapters that registered an `IBotRegistryProvider`. For first integrations, explicit `MapSlackWebhook(...)`, `MapDiscordWebhook(...)`, and similar calls are easier to inspect and easier to expose through platform dashboards.

The rest of this page shows the minimal host shape for each platform. Use the provider pages for dashboard setup, config tables, platform limits, and troubleshooting:

- [Slack Bots](slack.md)
- [Discord Bots](discord.md)
- [Telegram Bots](telegram.md)
- [WhatsApp Bots](whatsapp.md)
- [Teams Bots](teams.md)

## Slack

Slack supports HTTP Events API mode by default. Socket Mode is enabled when `AppToken` is configured, which lets the app receive events through an outbound WebSocket instead of a public inbound Events API URL.

```csharp
using HPD.Agent.Bots.Slack;
using HPD.Agent.Bots.Slack.OAuth;

builder.Services.AddSlackBot(options =>
{
    options.SigningSecret = builder.Configuration["Slack:SigningSecret"]!;
    options.BotToken = builder.Configuration["Slack:BotToken"]!;
    options.AppToken = builder.Configuration["Slack:AppToken"];
    options.AgentId = agentId;
    options.UseNativeStreaming = false;
    options.StreamingDebounceMs = 500;
}, registerDefaultSecretResolver: true);

var app = builder.Build();

app.MapSlackWebhook("/slack/events");
```

`SigningSecret` verifies inbound Slack requests. `BotToken` is the bot user token used to post replies. For multi-workspace apps, the adapter can resolve team-specific bot tokens through the secret resolver using keys such as `slack:BotToken:{teamId}`.

Slack has two output modes. `PostAndEdit` works broadly by posting a placeholder and editing it as text arrives. Native streaming uses Slack assistant stream APIs when an assistant thread provides the needed recipient context; it requires the Slack Assistants feature and the right OAuth scopes.

Slack OAuth is optional. Add it only when the host owns the install flow:

```csharp
builder.Services.AddSlackOAuth(options =>
{
    options.ClientId = builder.Configuration["Slack:ClientId"]!;
    options.ClientSecret = builder.Configuration["Slack:ClientSecret"]!;
    options.RedirectUri = builder.Configuration["Slack:OAuth:RedirectUri"]!;
});

app.MapSlackOAuth("/slack/install", "/slack/oauth/callback");
```

For Slack app scopes, Socket Mode, native streaming, OAuth storage, and troubleshooting, see [Slack Bots](slack.md).

## Discord

Discord webhook mode receives interaction payloads at the mapped interaction URL. Gateway mode is optional; when `GatewayToken` and `GatewayForwardUrl` are set, gateway events are forwarded into the same HTTP dispatch path.

```csharp
using HPD.Agent.Bots.Discord;

builder.Services.AddDiscordBot(options =>
{
    options.PublicKey = builder.Configuration["Discord:PublicKey"]!;
    options.BotToken = builder.Configuration["Discord:BotToken"]!;
    options.ApplicationId = builder.Configuration["Discord:ApplicationId"]!;
    options.AgentId = agentId;
    options.GatewayToken = builder.Configuration["Discord:GatewayToken"];
    options.GatewayForwardUrl = builder.Configuration["Discord:GatewayForwardUrl"];
}, registerInfrastructure: true);

var app = builder.Build();

app.MapDiscordWebhook("/discord/interactions");
```

`PublicKey` verifies Discord interaction signatures. `BotToken` is used for follow-up messages and platform API calls. `ApplicationId` identifies the Discord app for interaction responses.

Gateway mode is useful when you want message-create and reaction-style events that are not only slash-command interaction callbacks. The forward URL should point back to the hosted Discord webhook endpoint.

For Developer Portal setup, Gateway intents, role mentions, and troubleshooting, see [Discord Bots](discord.md).

## Telegram

Telegram can run with a public webhook or with long polling. Webhook mode is usually better for hosted production apps. Polling mode is convenient for local development or private hosts that cannot receive Telegram webhooks.

```csharp
using HPD.Agent.Bots.Telegram;

builder.Services.AddTelegramBot(options =>
{
    options.BotToken = builder.Configuration["Telegram:BotToken"]!;
    options.SecretToken = builder.Configuration["Telegram:SecretToken"];
    options.UserName = builder.Configuration["Telegram:UserName"];
    options.AgentId = agentId;
    options.Mode = TelegramBotMode.Webhook;
}, registerInfrastructure: true);

var app = builder.Build();

app.MapTelegramWebhook("/telegram/webhook");
```

For polling:

```csharp
builder.Services.AddTelegramBotWithPolling(options =>
{
    options.BotToken = builder.Configuration["Telegram:BotToken"]!;
    options.AgentId = agentId;
    options.Mode = TelegramBotMode.Polling;
});
```

In polling mode, do not map the webhook endpoint for Telegram. The hosted polling service receives updates and dispatches them inside the process.

`BotToken` can also come from `TELEGRAM_BOT_TOKEN`. `SecretToken` can come from `TELEGRAM_WEBHOOK_SECRET_TOKEN`. `ApiBaseUrl` can be used for Telegram-compatible gateways and otherwise defaults to `https://api.telegram.org`.

For `setWebhook`, polling options, group mention behavior, and Telegram limits, see [Telegram Bots](telegram.md).

## WhatsApp

WhatsApp uses Meta's Cloud API webhook. The mapped endpoint handles Meta's verification challenge and inbound message webhooks.

```csharp
using HPD.Agent.Bots.WhatsApp;

builder.Services.AddWhatsappBot(options =>
{
    options.AccessToken = builder.Configuration["WhatsApp:AccessToken"]!;
    options.AppSecret = builder.Configuration["WhatsApp:AppSecret"]!;
    options.PhoneNumberId = builder.Configuration["WhatsApp:PhoneNumberId"]!;
    options.VerifyToken = builder.Configuration["WhatsApp:VerifyToken"]!;
    options.AgentId = agentId;
}, registerInfrastructure: true);

var app = builder.Build();

app.MapWhatsappWebhook("/whatsapp/webhook");
```

The adapter can also read `WHATSAPP_ACCESS_TOKEN`, `WHATSAPP_APP_SECRET`, `WHATSAPP_PHONE_NUMBER_ID`, `WHATSAPP_VERIFY_TOKEN`, `WHATSAPP_API_URL`, and `WHATSAPP_BOT_USERNAME`.

WhatsApp Cloud API has a platform policy around the 24-hour customer service window. HPD does not enforce that locally; Meta rejects disallowed sends at the Graph API boundary. Plan template-message behavior separately when your bot must message users outside that window.

For Meta setup, webhook verification, signed POSTs, cards/buttons, and limits, see [WhatsApp Bots](whatsapp.md).

## Teams

Teams is different from the generated webhook adapters. It uses the Microsoft 365 Agents SDK for endpoint routing, authentication, activity deserialization, attachments, and proactive conversation endpoints. HPD takes over after the SDK has produced a turn.

```csharp
using HPD.Agent.Bots.Teams;

builder.AddTeamsBot(options =>
{
    options.AppId = builder.Configuration["Teams:AppId"]!;
    options.AppPassword = builder.Configuration["Teams:AppPassword"];
    options.AppTenantId = builder.Configuration["Teams:TenantId"];
    options.AppType = "SingleTenant";
    options.AgentId = agentId;
});

var app = builder.Build();

app.MapTeamsBot();
```

Exactly one Teams auth method must be configured:

- `AppPassword` for client-secret authentication.
- `Certificate` for certificate authentication.
- `Federated` for workload identity.

`AppTenantId` is required for single-tenant bots. The host also needs the Microsoft 365 Agents SDK configuration sections such as token validation and service connections. Teams file uploads are downloaded by the Agents SDK attachment downloader and surfaced as HPD input files before the agent run.

Graph-backed Teams history is opt-in:

```csharp
builder.Services.AddTeamsGraphHistory(graphClient);
```

Use that only when the host already has a configured `GraphServiceClient`.

For Agents SDK configuration, Teams auth modes, capabilities, and troubleshooting, see [Teams Bots](teams.md).

## Sessions And Thread Keys

Each adapter turns platform identity into a thread key:

- Slack: channel id plus thread timestamp.
- Discord: interaction, channel, or thread identity depending on event source.
- Telegram: chat id plus optional message thread id.
- WhatsApp: phone number id plus user WhatsApp id.
- Teams: conversation id plus service URL.

`PlatformSessionMapper` maps that key to an HPD session and thread. The first message creates the HPD session and thread; later messages in the same platform thread reuse them.

## Permissions And Streaming

Bot adapters project HPD runtime output into platform-native responses. Text deltas may become message edits, native streams, buffered posts, or final messages depending on the adapter. Cards and permission requests are also platform-specific.

Do not assume all platforms expose the same permission interaction. Slack currently has the richest button-oriented flow. Other adapters may render simpler controls, deny automatically, or require platform-specific action handling.

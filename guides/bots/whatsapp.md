# WhatsApp Bots

WhatsApp uses Meta's Cloud API webhook. HPD handles the verification challenge, verifies signed POST requests, maps each WhatsApp user conversation to an HPD session and thread, and sends replies through the Graph API.

## Quick Start

```csharp
using HPD.Agent.Bots.WhatsApp;

builder.Services.AddWhatsappBot(options =>
{
    options.AccessToken = builder.Configuration["WhatsApp:AccessToken"]!;
    options.AppSecret = builder.Configuration["WhatsApp:AppSecret"]!;
    options.PhoneNumberId = builder.Configuration["WhatsApp:PhoneNumberId"]!;
    options.VerifyToken = builder.Configuration["WhatsApp:VerifyToken"]!;
    options.AgentId = builder.Configuration["Agent:Id"] ?? "support-agent";
}, registerInfrastructure: true);

var app = builder.Build();

app.MapWhatsappWebhook("/whatsapp/webhook");
```

## Meta Setup

In Meta for Developers:

1. Create or select an app with the WhatsApp product.
2. Find the Phone Number ID and configure `WhatsApp:PhoneNumberId`.
3. Configure an access token for sends. Use a permanent system-user token for production.
4. Copy the App Secret into `WhatsApp:AppSecret`.
5. Set the webhook callback URL to `https://your-domain/whatsapp/webhook`.
6. Set the webhook verify token to the same value as `WhatsApp:VerifyToken`.
7. Subscribe the webhook to `messages`.

## Configuration

| Option | Required | Notes |
| --- | --- | --- |
| `AccessToken` | Yes | Graph API token. Can also come from `WHATSAPP_ACCESS_TOKEN`. |
| `AppSecret` | Yes | Verifies `x-hub-signature-256`. Can also come from `WHATSAPP_APP_SECRET`. |
| `PhoneNumberId` | Yes | WhatsApp Cloud API phone number id. Can also come from `WHATSAPP_PHONE_NUMBER_ID`. |
| `VerifyToken` | Yes | GET challenge token. Can also come from `WHATSAPP_VERIFY_TOKEN`. |
| `ApiVersion` | Optional | Defaults to `v25.0`. |
| `ApiUrl` | Optional | Defaults to `https://graph.facebook.com`; can also come from `WHATSAPP_API_URL`. |
| `UserName` | Optional | Defaults to `whatsapp-bot`; can also come from `WHATSAPP_BOT_USERNAME`. |
| `AgentId` | Yes | HPD agent definition to run. |

## Webhook Verification And Signing

The mapped route accepts GET and POST:

| Request | Behavior |
| --- | --- |
| GET | Expects `hub.mode=subscribe`, matching `hub.verify_token`, and responds with `hub.challenge`. |
| POST | Requires `x-hub-signature-256`, verified as HMAC-SHA256 over the raw body using `AppSecret`. |

Invalid verification returns `403`. Invalid signatures return `401`. Unsupported methods return `405`.

## Capabilities And Limits

| Area | HPD WhatsApp behavior |
| --- | --- |
| Inbound content | Text, image, document, audio, voice, video, sticker, and location. |
| Inbound actions | Interactive button replies, list replies, legacy button replies, and reactions. |
| Outbound content | Text, card fallback text, reply buttons, reactions, read receipts, and typing indicators. |
| Streaming | Buffer and post. WhatsApp does not support message edit streaming. |
| Text size | Messages are chunked around the 4096 character limit. |
| History | WhatsApp does not expose thread history to HPD. |
| Permissions | HPD permission requests are currently denied automatically in the WhatsApp streaming path. |

WhatsApp also enforces the 24-hour customer service window. HPD does not bypass that policy; Meta rejects sends that require approved templates.

## Cards And Buttons

HPD renders cards as WhatsApp reply buttons when possible:

- 1-3 non-link buttons become WhatsApp reply buttons.
- Button titles are limited to 20 characters.
- Headers are limited to 60 characters.
- Bodies are limited to 1024 characters.
- Link buttons or more than 3 buttons fall back to text.

Thread keys use `whatsapp:{PhoneNumberId}:{UserWaId}`. The channel id is `whatsapp:{PhoneNumberId}`.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Meta verification fails | Callback URL is wrong, route is not mapped, or `VerifyToken` does not match. |
| POSTs are rejected | `AppSecret` is wrong or the signed raw body changed before verification. |
| Messages do not arrive | The webhook is not subscribed to `messages`, or the phone number/business setup is incomplete. |
| Sends fail | Token expired, `PhoneNumberId` is wrong, or the 24-hour/template policy applies. |

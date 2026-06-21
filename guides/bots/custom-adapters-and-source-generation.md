# Custom Bot Adapters And Source Generation

HPD bot source generation can produce transport-neutral dispatch, dependency-injection registration, and ASP.NET webhook mapping code for custom adapters. This page documents the verified shape and caveats for adapter authors. It does not document platform-specific setup.

## Adapter Attribute

Mark an adapter with `[HpdBot("name")]`:

```csharp
using HPD.Agent.Bots;

[HpdBot("contoso")]
[HpdStreaming(StreamingStrategy.PostAndEdit)]
public sealed partial class ContosoBot
{
}
```

The source generator emits a registration class for the adapter name. For a bot named `contoso`, the generated API shape includes methods named:

```csharp
services.AddContosoBot(...);
endpoints.MapContosoWebhook(...);
```

Generated dispatch makes the partial adapter implement `IBotAdapter`:

```csharp
Task<BotAdapterResponse> HandleAsync(
    BotInboundEnvelope envelope,
    CancellationToken cancellationToken = default);
```

`MapContosoWebhook(...)` is the ASP.NET bridge around that method. Non-ASP.NET hosts can build a `BotInboundEnvelope` themselves and call `HandleAsync(...)` directly.

Generated registration configures adapter options, named streaming options when `[HpdStreaming]` is present, the bot singleton, and `PlatformSessionMapper`. Socket adapters can also add the socket hosted service when the expected socket configuration property is present.

## Bot Event Handlers

Use `[HpdBotEventHandler("event-type")]` to mark methods that handle extracted platform events:

```csharp
[HpdBotPayload]
public sealed record ContosoMessage(string Text);

[HpdBotEventHandler("message.created")]
public Task HandleMessageAsync(
    ContosoMessage payload,
    CancellationToken cancellationToken)
{
    return Task.CompletedTask;
}
```

`[HpdBotPayload]` currently applies to the payload type, not the handler parameter.

Use `[HpdHttpMethods(...)]` to declare allowed HTTP methods for the generated webhook route. Without it, the generated mapping defaults to POST.

```csharp
[HpdHttpMethods("GET", "POST")]
public sealed partial class ContosoBot
{
}
```

## Pre-Dispatch And Body Extraction

`[HpdBotPreDispatch]` marks adapter code that should run before handler dispatch. Platform adapters use this stage for work such as signature checks, verification challenges, or request normalization.

```csharp
[HpdBotPreDispatch]
private Task<BotAdapterResponse?> VerifyAsync(
    BotRequestContext ctx,
    byte[] bodyBytes)
{
    if (ctx.Header("x-contoso-signature") is null)
        return Task.FromResult<BotAdapterResponse?>(BotAdapterResponse.Status(401));

    return Task.FromResult<BotAdapterResponse?>(null);
}
```

`[HpdBotEnvelopeExtractor]` marks adapter code that extracts the payload or event envelope from the incoming request body.

```csharp
[HpdBotEnvelopeExtractor]
private (string? eventType, byte[] dispatchBytes) ExtractEnvelope(
    BotRequestContext ctx,
    byte[] bodyBytes)
{
    return (ctx.Header("x-contoso-event"), bodyBytes);
}
```

Keep platform-specific auth and verification code in the adapter package, not in generic app code.

Generated dispatch currently expects a hand-written JSON context named for the adapter, such as `ContosoBotJsonContext`, with payload types registered through `JsonSerializable` attributes.

`BotRequestContext` intentionally exposes request data through neutral helpers such as `Header(...)`, `QueryValue(...)`, `Method`, `Path`, and `CancellationToken`. Use those in generated hooks instead of `HttpContext` so the same adapter can be driven by webhooks, polling, socket workers, or tests.

## Thread Ids

Use `[ThreadId]` on the type that formats and parses platform thread keys:

```csharp
[ThreadId("contoso:{WorkspaceId}:{ConversationId}")]
public readonly record struct ContosoThreadId(
    string WorkspaceId,
    string ConversationId);
```

The platform key produced by this type is what `PlatformSessionMapper` uses to find or create the HPD session and thread.

## Generated Registry And Mapping

The generator emits `Add{Name}Bot(...)`, `Map{Name}Webhook(...)`, and neutral `IBotAdapter` dispatch for generated adapters. It can also emit assembly-local registry entries such as `BotRegistry.g.cs`.

`MapHPDBots()` is a convenience mapper that enumerates registered `IBotRegistryProvider` services. Generated `BotRegistry.g.cs` alone is not sufficient unless a generated or hand-written provider is also registered by `Add{Name}Bot(...)` or package infrastructure. For custom generated adapters, individual `Map{Name}Webhook(...)` remains the source-confirmed mapping path.

## Package-Level Extensions

Concrete platform packages often provide hand-written partial service extensions that call the generated overload and then register platform infrastructure such as:

- secret resolvers
- HTTP clients
- platform API clients
- formatting services
- gateway or socket services
- polling services
- SDK endpoint integration
- registry providers

For package consumers, prefer the package-provided `Add...Bot(...)` overload rather than calling a bare generated overload unless the package docs say otherwise.

## Diagnostics

The source generator currently reports diagnostics in the `HPDA001` through `HPDA011` range for adapter visibility, shape, and duplicate-handler errors. Treat diagnostics as source-generator feedback: fix the adapter shape, method visibility, attribute placement, or duplicate declaration before expecting generated registration to appear.

## Permission Handler Caveat

Do not document `[HpdPermissionHandler]` as an implemented generator-routed permission workflow. Current source inspection shows the generator recognizes duplicate permission-handler declarations, but the coordinator-style permission flow described by the attribute comments is not implemented in the inspected generator path.

Use platform adapter code and current runtime behavior for permission responses until that API is clarified.

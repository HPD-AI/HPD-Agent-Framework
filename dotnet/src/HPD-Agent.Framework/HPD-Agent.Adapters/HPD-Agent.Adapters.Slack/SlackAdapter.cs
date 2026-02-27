using System.Text;
using System.Text.Json;
using HPD.Agent;
using HPD.Agent.Adapters.Cards;
using HPD.Agent.Adapters.Session;
using HPD.Agent.Adapters.Slack.Payloads;
using HPD.Agent.Hosting.Lifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HPD.Agent.Adapters.Slack;

// ── Permission context ─────────────────────────────────────────────────────────

/// <summary>
/// Carries the Slack context needed to post a permission request message.
/// Passed to <see cref="SlackAdapter.RenderPermissionAsync"/> by <c>StreamToSlackAsync</c>.
/// </summary>
public record SlackPermissionContext(
    string Channel,
    string ThreadTs,
    string SessionId,
    CancellationToken RequestAborted
);

// ── Adapter event types ────────────────────────────────────────────────────────

/// <summary>Raised when a slash command is received.</summary>
public record SlackSlashCommandReceivedEvent(
    SlackSlashCommandPayload Payload,
    string UserName);

/// <summary>Raised when a Slack reaction is added or removed on any message.</summary>
public record SlackReactionReceivedEvent(
    SlackReactionEvent Payload,
    string? TeamId);

/// <summary>Raised when a Slack block action is NOT a permission response.</summary>
public record SlackBlockActionReceivedEvent(
    SlackAction Action,
    SlackBlockActionsPayload Payload);

/// <summary>Raised on view_submission.</summary>
public record SlackViewSubmittedEvent(
    string CallbackId,
    string ViewId,
    IReadOnlyDictionary<string, string> Values,
    string? PrivateMetadata,
    string? ContextId,
    SlackUser User);

/// <summary>Raised on view_closed.</summary>
public record SlackViewClosedEvent(
    string CallbackId,
    string ViewId,
    string? PrivateMetadata,
    string? ContextId,
    SlackUser User);

/// <summary>Raised when assistant thread context changes (user navigates channels).</summary>
public record SlackAssistantContextChangedReceivedEvent(
    SlackAssistantContextChangedEvent Payload);

/// <summary>Raised when user opens the bot's Home tab.</summary>
public record SlackAppHomeOpenedReceivedEvent(
    SlackAppHomeOpenedPayload Payload);

// ── Debounce timer ─────────────────────────────────────────────────────────────

/// <summary>
/// Schedules a callback after a debounce window. Cancels any pending scheduled call
/// when <see cref="Schedule"/> is called again before the window elapses.
/// </summary>
internal sealed class DebounceTimer(int debounceMs) : IDisposable
{
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public void Schedule(Func<Task> callback)
    {
        CancellationTokenSource? old;
        CancellationTokenSource next = new();
        lock (_lock)
        {
            old = _cts;
            _cts = next;
        }
        old?.Cancel();
        old?.Dispose();

        _ = Task.Delay(debounceMs, next.Token)
            .ContinueWith(t => { if (!t.IsCanceled) return callback(); return Task.CompletedTask; },
                next.Token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
    }

    public void Cancel()
    {
        CancellationTokenSource? old;
        lock (_lock) { old = _cts; _cts = null; }
        old?.Cancel();
        old?.Dispose();
    }

    public void Dispose() => Cancel();
}

// ── Main adapter class ─────────────────────────────────────────────────────────

/// <summary>
/// Connects an HPD agent to Slack via the Events API and Block Kit.
/// Receives inbound webhooks, routes them to the agent via <see cref="AgentSessionManager"/>,
/// consumes the <see cref="AgentEvent"/> stream, and posts responses back via <see cref="SlackApiClient"/>.
/// </summary>
[HpdAdapter("slack")]
[HpdWebhookSignature(HmacFormat.V0TimestampBody,
    SignatureHeader = "X-Slack-Signature",
    TimestampHeader = "X-Slack-Request-Timestamp",
    WindowSeconds   = 300)]
[HpdStreaming(StreamingStrategy.PostAndEdit, DebounceMs = 500)]
public partial class SlackAdapter(
    IOptions<SlackAdapterConfig> options,
    AgentSessionManager sessionManager,
    PlatformSessionMapper sessionMapper,
    SlackApiClient api,
    SlackFormatConverter formatter,
    SlackUserCache userCache)
{
    private readonly SlackAdapterConfig _config = options.Value;

    // ── Adapter events (user code subscribes to these) ─────────────────────────

    public event Action<SlackSlashCommandReceivedEvent>? OnSlashCommand;
    public event Action<SlackReactionReceivedEvent>? OnReaction;
    public event Action<SlackBlockActionReceivedEvent>? OnBlockAction;
    public event Action<SlackViewSubmittedEvent>? OnViewSubmission;
    public event Action<SlackViewClosedEvent>? OnViewClosed;
    public event Action<SlackAssistantContextChangedReceivedEvent>? OnAssistantContextChanged;
    public event Action<SlackAppHomeOpenedReceivedEvent>? OnAppHomeOpened;

    // ── Webhook handlers ───────────────────────────────────────────────────────

    [HpdWebhookHandler("url_verification")]
    private async Task<IResult> HandleUrlVerificationAsync(
        HttpContext ctx, SlackEventEnvelope envelope)
    {
        // Write directly — Results.Text appends "; charset=utf-8" causing challenge_failed,
        // and Results.Json with anonymous types serializes to empty in some ASP.NET configs.
        var body = System.Text.Encoding.UTF8.GetBytes($"{{\"challenge\":\"{envelope.Challenge}\"}}");
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = body.Length;
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
        return Results.Empty;
    }

    [HpdWebhookHandler("app_mention")]
    [HpdWebhookHandler("message")]
    private async Task<IResult> HandleMessageAsync(
        HttpContext ctx, SlackEventEnvelope envelope)
    {
        var ev = DeserializeEvent<SlackMessageEvent>(envelope.Event);
        if (ShouldSkip(ev)) return Results.Ok();

        var threadTs    = GetThreadTs(ev!);
        var platformKey = SlackThreadId.Format(ev!.Channel!, threadTs);
        var (sessionId, branchId) = await sessionMapper.ResolveAsync(platformKey, ctx.RequestAborted);
        var input = await BuildInputAsync(ev, ctx.RequestAborted);

        Console.WriteLine($"[SLACK] HandleMessageAsync: channel={ev.Channel} channelType={ev.ChannelType} user={ev.User} botId={ev.BotId} subtype={ev.Subtype} text={ev.Text} threadTs={threadTs} sessionId={sessionId} branchId={branchId}");

        // Fire-and-forget: Slack requires 200 within 3 seconds.
        _ = StreamToSlackAsync(sessionId, branchId, input, ev.Channel!, threadTs, ctx.RequestAborted);
        return Results.Ok();
    }

    [HpdWebhookHandler("reaction_added")]
    [HpdWebhookHandler("reaction_removed")]
    private Task<IResult> HandleReactionAsync(
        HttpContext ctx, SlackEventEnvelope envelope)
    {
        var ev = DeserializeEvent<SlackReactionEvent>(envelope.Event);
        OnReaction?.Invoke(new SlackReactionReceivedEvent(ev!, envelope.TeamId));
        return Task.FromResult(Results.Ok());
    }

    [HpdWebhookHandler("assistant_thread_started")]
    private async Task<IResult> HandleAssistantThreadStartedAsync(
        HttpContext ctx, SlackEventEnvelope envelope)
    {
        var ev     = DeserializeEvent<SlackAssistantThreadStartedEvent>(envelope.Event);
        var thread = ev!.AssistantThread;
        var platformKey = SlackThreadId.Format(thread.ChannelId, thread.ThreadTs);
        await sessionMapper.ResolveAsync(platformKey, ctx.RequestAborted); // ensure session exists
        await api.TrySetAssistantStatusAsync(thread.ChannelId, thread.ThreadTs, "Ready", ctx.RequestAborted);
        return Results.Ok();
    }

    [HpdWebhookHandler("assistant_thread_context_changed")]
    private Task<IResult> HandleAssistantContextChangedAsync(
        HttpContext ctx, SlackEventEnvelope envelope)
    {
        var ev = DeserializeEvent<SlackAssistantContextChangedEvent>(envelope.Event);
        OnAssistantContextChanged?.Invoke(new SlackAssistantContextChangedReceivedEvent(ev!));
        return Task.FromResult(Results.Ok());
    }

    [HpdWebhookHandler("app_home_opened")]
    private Task<IResult> HandleAppHomeOpenedAsync(
        HttpContext ctx, SlackEventEnvelope envelope)
    {
        var ev = DeserializeEvent<SlackAppHomeOpenedPayload>(envelope.Event);
        if (ev?.Tab == "home")
            OnAppHomeOpened?.Invoke(new SlackAppHomeOpenedReceivedEvent(ev));
        return Task.FromResult(Results.Ok());
    }

    // Slash commands arrive as form-urlencoded with a `command` field (no `payload` wrapper).
    // The generator detects form-urlencoded Content-Type + `command` field and routes here.
    // TriggerId (valid 3s) and ResponseUrl (valid 30min) are preserved in Extensions.

    [HpdWebhookHandler("slash_command")]
    private async Task<IResult> HandleSlashCommandAsync(
        HttpContext ctx, SlackSlashCommandPayload payload)
    {
        var userName    = await userCache.GetDisplayNameAsync(payload.UserId, ctx.RequestAborted);
        var platformKey = SlackThreadId.Format(payload.ChannelId, ""); // slash commands have no thread
        var (sessionId, branchId) = await sessionMapper.ResolveAsync(platformKey, ctx.RequestAborted);

        var input = new AgentInput(
            Text:      $"{payload.Command} {payload.Text}".Trim(),
            UserId:    payload.UserId,
            UserName:  userName,
            IsMention: true,
            Extensions: new Dictionary<string, string>
            {
                ["slack:triggerId"]   = payload.TriggerId,
                ["slack:responseUrl"] = payload.ResponseUrl
            });

        // Fire-and-forget: Slack requires 200 within 3 seconds.
        _ = StreamToSlackAsync(sessionId, branchId, input, payload.ChannelId, "", ctx.RequestAborted);
        return Results.Ok();
    }

    // Interactive payloads arrive as form-urlencoded with a `payload` JSON field.
    // The generator detects Content-Type and routes accordingly.

    [HpdWebhookHandler("block_actions")]
    private Task<IResult> HandleBlockActionsAsync(
        HttpContext ctx, SlackBlockActionsPayload payload)
    {
        foreach (var action in payload.Actions)
        {
            // Permission response: action IDs are GUIDs (the PermissionId).
            // block_id carries the sessionId set when BuildPermissionBlocks posted the message.
            if (IsPermissionAction(action.ActionId))
            {
                var agent = sessionManager.GetRunningAgent(action.BlockId);
                if (agent is not null)
                {
                    var approved = action.Value == "approve";
                    agent.SendMiddlewareResponse(action.ActionId, new PermissionResponseEvent(
                        PermissionId: action.ActionId,
                        SourceName:   "slack",
                        Approved:     approved));
                }
                continue;
            }

            OnBlockAction?.Invoke(new SlackBlockActionReceivedEvent(action, payload));
        }
        return Task.FromResult(Results.Ok());
    }

    [HpdWebhookHandler("view_submission")]
    private Task<IResult> HandleViewSubmissionAsync(
        HttpContext ctx, SlackViewSubmissionPayload payload)
    {
        var (contextId, privateMetadata) = SlackModalConverter.DecodeMetadata(payload.View.PrivateMetadata);
        var values = FlattenViewState(payload.View.State.Values);
        OnViewSubmission?.Invoke(new SlackViewSubmittedEvent(
            CallbackId:      payload.View.CallbackId ?? "",
            ViewId:          payload.View.Id,
            Values:          values,
            PrivateMetadata: privateMetadata,
            ContextId:       contextId,
            User:            payload.User));
        return Task.FromResult(Results.Ok());
    }

    [HpdWebhookHandler("view_closed")]
    private Task<IResult> HandleViewClosedAsync(
        HttpContext ctx, SlackViewClosedPayload payload)
    {
        var (contextId, privateMetadata) = SlackModalConverter.DecodeMetadata(payload.View.PrivateMetadata);
        OnViewClosed?.Invoke(new SlackViewClosedEvent(
            CallbackId:      payload.View.CallbackId ?? "",
            ViewId:          payload.View.Id,
            PrivateMetadata: privateMetadata,
            ContextId:       contextId,
            User:            payload.User));
        return Task.FromResult(Results.Ok());
    }

    // ── Permission handler ─────────────────────────────────────────────────────

    /// <summary>
    /// Posts Approve/Deny Block Kit buttons when the agent yields a
    /// <see cref="PermissionRequestEvent"/>. The block_id encodes the sessionId so
    /// <see cref="HandleBlockActionsAsync"/> can route the button click back to the
    /// waiting agent loop via <c>agent.SendMiddlewareResponse()</c>.
    /// </summary>
    [HpdPermissionHandler]
    private async Task RenderPermissionAsync(
        PermissionRequestEvent req,
        SlackPermissionContext ctx)
    {
        var blocks = BuildPermissionBlocks(req, blockId: ctx.SessionId);
        await api.PostMessageAsync(ctx.Channel, ctx.ThreadTs, blocks, ctx.RequestAborted);
    }

    // ── Streaming ──────────────────────────────────────────────────────────────

    private async Task StreamToSlackAsync(
        string sessionId, string branchId,
        AgentInput input,
        string channel, string threadTs,
        CancellationToken ct)
    {
        // Drop silently if another stream is already running on this branch.
        // Slack requires 200 within 3s — queueing is not viable.
        if (!sessionManager.TryAcquireStreamLock(sessionId, branchId))
        {
            Console.WriteLine($"[SLACK] StreamToSlackAsync: stream lock already held for session={sessionId} branch={branchId}, dropping");
            return;
        }

        try
        {
            Console.WriteLine($"[SLACK] StreamToSlackAsync: starting for session={sessionId} channel={channel} threadTs={threadTs}");

            // UseNativeStreaming: chat.startStream → chat.appendStream → chat.stopStream.
            // Only available when recipientUserId is known (Assistants threads only).
            // PostAndEdit: post placeholder → chat.update per debounce tick → final update.
            var useNative = _config.UseNativeStreaming
                && input.RecipientUserId is not null
                && input.RecipientTeamId is not null;

            string placeholderTs;
            if (useNative)
            {
                placeholderTs = await api.StartStreamAsync(
                    channel, threadTs, input.RecipientUserId, input.RecipientTeamId, ct);
            }
            else
            {
                Console.WriteLine($"[SLACK] StreamToSlackAsync: posting placeholder message...");
                placeholderTs = await api.PostMessageAsync(channel, threadTs, "...", ct);
                Console.WriteLine($"[SLACK] StreamToSlackAsync: placeholder posted ts={placeholderTs}");
                await api.TrySetAssistantStatusAsync(channel, threadTs, "Typing...", ct);
            }

            var agent = await sessionManager.GetOrCreateAgentAsync(sessionId, ct);
            sessionManager.SetStreaming(sessionId, true);

            var buffer  = new StringBuilder();
            var debounce = new DebounceTimer(_config.StreamingDebounceMs);

            Console.WriteLine($"[SLACK] StreamToSlackAsync: running agent with input={input.Text}");
            await foreach (var evt in agent.RunAsync(input.Text, sessionId, branchId, cancellationToken: ct))
            {
                Console.WriteLine($"[SLACK] AgentEvent: {evt.GetType().Name}");
                switch (evt)
                {
                    case TextDeltaEvent delta:
                        buffer.Append(delta.Text);
                        if (useNative)
                        {
                            debounce.Schedule(async () =>
                                await api.AppendStreamAsync(channel, placeholderTs,
                                    formatter.ToMrkdwn(buffer.ToString()), ct));
                        }
                        else
                        {
                            debounce.Schedule(async () =>
                                await api.UpdateMessageAsync(channel, placeholderTs,
                                    formatter.ToMrkdwn(buffer.ToString()), ct));
                        }
                        break;

                    case PermissionRequestEvent req:
                        // Post Block Kit approve/deny buttons. block_id = sessionId for routing.
                        var permCtx = new SlackPermissionContext(channel, threadTs, sessionId, ct);
                        await RenderPermissionAsync(req, permCtx);
                        // agent.SendMiddlewareResponse is called by HandleBlockActionsAsync
                        // when the user clicks a button — the agent loop is already waiting.
                        break;

                    case TextMessageEndEvent:
                        debounce.Cancel();
                        var finalMrkdwn = formatter.ToMrkdwn(buffer.ToString());
                        Console.WriteLine($"[SLACK] StreamToSlackAsync: TextMessageEnd, posting final update len={finalMrkdwn.Length}");
                        if (useNative)
                            await api.StopStreamAsync(channel, placeholderTs, finalMrkdwn, null, ct);
                        else
                        {
                            await api.UpdateMessageAsync(channel, placeholderTs, finalMrkdwn, ct);
                            await api.TryClearAssistantStatusAsync(channel, threadTs, ct);
                        }
                        buffer.Clear();
                        break;

                    case CardContentEvent card:
                        debounce.Cancel();
                        var cardBlocks  = new SlackCardRenderer().RenderCard(card.Card);
                        var cardFallback = CardFallbackText.From(card.Card);
                        if (useNative)
                            await api.StopStreamAsync(channel, placeholderTs, cardFallback, cardBlocks, ct);
                        else
                            await api.UpdateMessageAsync(channel, placeholderTs, cardFallback, cardBlocks, ct);
                        break;
                }
            }
            Console.WriteLine($"[SLACK] StreamToSlackAsync: agent stream complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SLACK] StreamToSlackAsync EXCEPTION: {ex}");
        }
        finally
        {
            sessionManager.SetStreaming(sessionId, false);
            sessionManager.ReleaseStreamLock(sessionId, branchId);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static bool ShouldSkip(SlackMessageEvent? ev)
    {
        if (ev is null) return true;
        if (ev.BotId is not null && ev.Type != "app_mention") return true; // suppress echo loops
        if (ev.Subtype is not null && ev.Subtype != "bot_message") return true; // skip edits/deletes
        return false;
    }

    private static string GetThreadTs(SlackMessageEvent ev)
        // DM top-level → empty (single conversation per DM).
        // DM thread reply / channel message → thread_ts ?? ts.
        => ev.ChannelType == "im" && ev.ThreadTs is null
            ? ""
            : ev.ThreadTs ?? ev.Ts ?? "";

    private async Task<AgentInput> BuildInputAsync(
        SlackMessageEvent ev, CancellationToken ct)
    {
        var text     = ev.Text ?? "";
        var userName = ev.Username ?? await userCache.GetDisplayNameAsync(ev.User ?? "unknown", ct);
        var resolved = await userCache.ResolveInlineMentionsAsync(text, ev.User, ct);
        var content  = formatter.ToPlainText(resolved);

        return new AgentInput(
            Text:        content,
            UserId:      ev.User ?? "unknown",
            UserName:    userName,
            Attachments: ev.Files?.Select(MapAttachment).ToArray() ?? [],
            IsMention:   ev.ChannelType == "im" || ev.Type == "app_mention");
    }

    private static SlackFileInfo MapAttachment(SlackFileInfo f) => f; // identity for now

    private static IReadOnlyList<SlackBlock> BuildPermissionBlocks(
        PermissionRequestEvent req, string blockId)
    {
        // block_id = sessionId so HandleBlockActionsAsync can route the button click
        // back to the waiting agent loop via GetRunningAgent(action.BlockId).
        // action_id = req.PermissionId (GUID) on both buttons so IsPermissionAction()
        // recognises them and the agent can match request to response.

        var blocks = new List<SlackBlock>
        {
            new SlackSectionBlock(
                Text: new SlackMrkdwn(
                    $"*{req.SourceName}* wants to call `{req.FunctionName}`"))
        };

        if (!string.IsNullOrWhiteSpace(req.Description))
            blocks.Add(new SlackSectionBlock(Text: new SlackMrkdwn(req.Description)));

        blocks.Add(new SlackActionsBlock(
            Elements: new[]
            {
                new SlackButton(
                    ActionId: req.PermissionId,
                    Text:     new SlackPlainText("Approve"),
                    Value:    "approve",
                    Style:    "primary"),
                new SlackButton(
                    ActionId: req.PermissionId,
                    Text:     new SlackPlainText("Deny"),
                    Value:    "deny",
                    Style:    "danger")
            },
            BlockId: blockId));

        return blocks;
    }

    private static bool IsPermissionAction(string actionId) =>
        Guid.TryParse(actionId, out _);

    private static TEvent? DeserializeEvent<TEvent>(JsonElement? element) where TEvent : class
        => element?.Deserialize<TEvent>(SlackAdapterJsonContext.Default.Options);

    private static Dictionary<string, string> FlattenViewState(
        Dictionary<string, Dictionary<string, SlackViewStateValue>> values)
    {
        var flat = new Dictionary<string, string>();
        foreach (var block in values.Values)
            foreach (var (actionId, input) in block)
                flat[actionId] = input.Value ?? input.SelectedOption?.Value ?? "";
        return flat;
    }

    // ── Adapter-internal input bag ─────────────────────────────────────────────
    // Does NOT cross the agent boundary — agent.RunAsync() takes a plain string.
    // RecipientUserId/RecipientTeamId drive native streaming eligibility.
    // Extensions carries Slack-specific values (triggerId, responseUrl) for
    // post-stream use by user code subscribing to adapter events.

    private record AgentInput(
        string Text,
        string UserId,
        string UserName,
        bool IsMention,
        SlackFileInfo[]? Attachments = null,
        string? RecipientUserId = null,
        string? RecipientTeamId = null,
        IReadOnlyDictionary<string, string>? Extensions = null);
}

// ── JSON serializer context ────────────────────────────────────────────────────

/// <summary>
/// AOT-safe source-generated JSON context for all Slack payload types.
/// The source generator populates this with <c>[JsonSerializable]</c> entries
/// for every <c>[WebhookPayload]</c> record in the assembly.
/// </summary>
// Inbound webhook payloads
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackEventEnvelope))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackMessageEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackReactionEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackBlockActionsPayload))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackViewSubmissionPayload))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackViewClosedPayload))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackAssistantThreadStartedEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackAssistantContextChangedEvent))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackAppHomeOpenedPayload))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackSlashCommandPayload))]
// Block Kit outbound types
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackBlock[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackSectionBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackActionsBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackHeaderBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackContextBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackImageBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackDividerBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackButton))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackPlainText))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackMrkdwn))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackConfirmationDialog))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackOption))]
// Modal view types
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackModalView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackModalInputBlock))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackPlainTextInput))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackStaticSelect))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlackRadioButtons))]
internal partial class SlackAdapterJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }

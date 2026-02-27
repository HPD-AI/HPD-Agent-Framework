namespace HPD.Agent.Adapters;

/// <summary>
/// Configures webhook signature verification for an <see cref="HpdAdapterAttribute"/> class.
/// The generator will call <see cref="HPD.Agent.Adapters.Verification.WebhookSignatureVerifier"/>
/// with these parameters before dispatching to any handler.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HpdWebhookSignatureAttribute(HmacFormat format) : Attribute
{
    /// <summary>HMAC signing format used by the platform.</summary>
    public HmacFormat Format => format;

    /// <summary>Name of the HTTP header carrying the signature (e.g. "X-Slack-Signature").</summary>
    public string SignatureHeader { get; init; } = string.Empty;

    /// <summary>
    /// Name of the HTTP header carrying the request timestamp (e.g. "X-Slack-Request-Timestamp").
    /// Used for replay-attack prevention. Empty string disables timestamp validation.
    /// </summary>
    public string TimestampHeader { get; init; } = string.Empty;

    /// <summary>
    /// Maximum age of a request in seconds before it is rejected as a replay.
    /// Default: 300 (5 minutes).
    /// </summary>
    public int WindowSeconds { get; init; } = 300;
}

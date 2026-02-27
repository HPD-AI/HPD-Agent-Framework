namespace HPD.Agent.Adapters;

/// <summary>
/// HMAC signing formats supported by <see cref="HPD.Agent.Adapters.Verification.WebhookSignatureVerifier"/>.
/// </summary>
public enum HmacFormat
{
    /// <summary>
    /// Slack-style V0 format: <c>HMAC-SHA256("v0:{timestamp}:{body}")</c>.
    /// Expected signature header value: <c>v0={hex}</c>.
    /// </summary>
    V0TimestampBody,
}

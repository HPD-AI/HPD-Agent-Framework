using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Auth;

/// <summary>
/// Represents stored authentication credentials.
/// Uses a discriminated union pattern for type safety.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OAuthEntry), "oauth")]
[JsonDerivedType(typeof(ApiKeyEntry), "api")]
[JsonDerivedType(typeof(WellKnownEntry), "wellknown")]
public abstract record AuthEntry
{
    /// <summary>
    /// Gets the credential value to use as the API key/token.
    /// </summary>
    public abstract string GetCredential();
}

/// <summary>
/// OAuth tokens with refresh capability.
/// Used for subscription-based services like ChatGPT Plus/Pro, GitHub Copilot.
/// </summary>
public sealed record OAuthEntry : AuthEntry
{
    [JsonPropertyName("access")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("expires")]
    public required long ExpiresAtUnixMs { get; init; }

    /// <summary>
    /// Optional account/organization ID for subscription services.
    /// Used by ChatGPT for org subscriptions, GitHub Copilot for enterprise.
    /// </summary>
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }

    /// <summary>
    /// Optional enterprise URL for GitHub Enterprise deployments.
    /// </summary>
    [JsonPropertyName("enterpriseUrl")]
    public string? EnterpriseUrl { get; init; }

    /// <summary>
    /// Checks if the access token has expired.
    /// </summary>
    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresAtUnixMs;

    /// <summary>
    /// Checks if the access token will expire within the specified duration.
    /// </summary>
    public bool ExpiresWithin(TimeSpan duration) =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)duration.TotalMilliseconds >= ExpiresAtUnixMs;

    /// <summary>
    /// Gets the expiration time as a DateTimeOffset.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtUnixMs);

    /// <summary>
    /// Gets the time remaining until expiration.
    /// </summary>
    [JsonIgnore]
    public TimeSpan TimeRemaining => ExpiresAt - DateTimeOffset.UtcNow;

    public override string GetCredential() => AccessToken;
}

/// <summary>
/// Static API key authentication.
/// Used for traditional API key-based services.
/// </summary>
public sealed record ApiKeyEntry : AuthEntry
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    public override string GetCredential() => Key;
}

/// <summary>
/// Reference to an environment variable containing the credential.
/// Allows storing a pointer to an env var rather than the actual secret.
/// </summary>
public sealed record WellKnownEntry : AuthEntry
{
    /// <summary>
    /// The name of the environment variable.
    /// </summary>
    [JsonPropertyName("envVar")]
    public required string EnvVarName { get; init; }

    /// <summary>
    /// Cached token value (optional, for validation).
    /// </summary>
    [JsonPropertyName("token")]
    public string? CachedToken { get; init; }

    public override string GetCredential() =>
        Environment.GetEnvironmentVariable(EnvVarName) ?? CachedToken ?? string.Empty;
}

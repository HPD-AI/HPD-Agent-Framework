namespace HPD_Agent.CLI.Auth;

/// <summary>
/// Defines an authentication provider that can authenticate users
/// via OAuth, API key, or other methods.
/// </summary>
public interface IAuthProvider
{
    /// <summary>
    /// Provider identifier (e.g., "openai", "github-copilot").
    /// Should be lowercase and URL-safe.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Display name for UI (e.g., "OpenAI (ChatGPT Plus/Pro)").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Available authentication methods for this provider.
    /// </summary>
    IReadOnlyList<AuthMethod> Methods { get; }

    /// <summary>
    /// Environment variable names that can provide credentials for this provider.
    /// Used for detecting existing credentials.
    /// </summary>
    IReadOnlyList<string> EnvironmentVariables { get; }

    /// <summary>
    /// Loads provider options after authentication.
    /// Returns the API key/token and any provider-specific configuration.
    /// </summary>
    Task<AuthLoadResult> LoadAsync(AuthEntry entry);

    /// <summary>
    /// Refreshes an OAuth token if needed.
    /// Returns the refreshed entry, or null if refresh not needed or not supported.
    /// </summary>
    Task<AuthEntry?> RefreshIfNeededAsync(AuthEntry entry);

    /// <summary>
    /// Validates that the credentials are still valid.
    /// </summary>
    Task<bool> ValidateAsync(AuthEntry entry);
}

/// <summary>
/// Represents an authentication method available for a provider.
/// </summary>
public record AuthMethod
{
    /// <summary>
    /// The type of authentication.
    /// </summary>
    public required AuthType Type { get; init; }

    /// <summary>
    /// Display label for the method (e.g., "ChatGPT Plus/Pro (browser)").
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Optional description providing more context.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether this is the recommended method.
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// Function to start the authentication flow.
    /// </summary>
    public required Func<CancellationToken, Task<AuthFlowResult>> StartFlow { get; init; }
}

/// <summary>
/// The type of authentication method.
/// </summary>
public enum AuthType
{
    /// <summary>Browser-based OAuth flow with local callback server.</summary>
    OAuthBrowser,

    /// <summary>Device code OAuth flow (headless, no browser needed).</summary>
    OAuthDeviceCode,

    /// <summary>Browser OAuth with manual code entry (no local callback server).</summary>
    OAuthManualCode,

    /// <summary>Manual API key entry.</summary>
    ApiKey,

    /// <summary>Reference to environment variable.</summary>
    WellKnown
}

/// <summary>
/// Result of starting an authentication flow.
/// </summary>
public abstract record AuthFlowResult
{
    /// <summary>Authentication completed successfully.</summary>
    public sealed record Success(AuthEntry Entry) : AuthFlowResult;

    /// <summary>Authentication was cancelled by user.</summary>
    public sealed record Cancelled : AuthFlowResult;

    /// <summary>Authentication failed with an error.</summary>
    public sealed record Failed(string Error, Exception? Exception = null) : AuthFlowResult;

    /// <summary>
    /// Requires user action (e.g., enter device code).
    /// Call WaitForCompletion to wait for user to complete the action.
    /// </summary>
    public sealed record PendingUserAction(
        string Message,
        string? Url,
        string? UserCode,
        Func<CancellationToken, Task<AuthFlowResult>> WaitForCompletion
    ) : AuthFlowResult;

    /// <summary>
    /// Requires user to enter a value (e.g., authorization code from browser).
    /// Call CompleteWithInput with the user's input to finish the flow.
    /// </summary>
    public sealed record NeedsUserInput(
        string Prompt,
        string InputLabel,
        Func<string, CancellationToken, Task<AuthFlowResult>> CompleteWithInput
    ) : AuthFlowResult;
}

/// <summary>
/// Result of loading credentials from an auth entry.
/// </summary>
public record AuthLoadResult
{
    /// <summary>
    /// The API key or access token to use.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Optional custom headers to include in requests.
    /// </summary>
    public Dictionary<string, string>? CustomHeaders { get; init; }

    /// <summary>
    /// Optional custom base URL for the provider.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Optional account ID for display purposes.
    /// </summary>
    public string? AccountId { get; init; }
}

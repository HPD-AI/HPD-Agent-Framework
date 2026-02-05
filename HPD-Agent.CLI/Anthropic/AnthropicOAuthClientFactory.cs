using System.Net.Http.Headers;
using HPD_Agent.CLI.Auth;
using HPD_Agent.CLI.Auth.Providers;
using Microsoft.Extensions.AI;

namespace HPD_Agent.CLI.Anthropic;

/// <summary>
/// Factory for creating Anthropic chat clients from authenticated OAuth credentials.
/// When OAuth is used, the client uses Bearer token authentication instead of x-api-key.
/// </summary>
public static class AnthropicOAuthClientFactory
{
    /// <summary>
    /// Creates an Anthropic chat client using OAuth credentials from the auth manager.
    /// </summary>
    /// <param name="authManager">The auth manager with stored credentials.</param>
    /// <param name="modelId">The model ID to use (e.g., "claude-sonnet-4-20250514").</param>
    /// <param name="options">Optional client configuration.</param>
    /// <returns>An IChatClient configured for the Anthropic API with OAuth.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no valid credentials are available.</exception>
    public static async Task<IChatClient> CreateAsync(
        AuthManager authManager,
        string modelId,
        AnthropicOAuthClientOptions? options = null)
    {
        var credentials = await authManager.ResolveCredentialsAsync("anthropic");

        if (credentials == null)
        {
            throw new InvalidOperationException(
                "No Anthropic credentials found. Please run '/auth login anthropic' to authenticate.");
        }

        return Create(credentials, modelId, options);
    }

    /// <summary>
    /// Creates an Anthropic chat client from resolved credentials.
    /// Supports both API keys (x-api-key) and OAuth tokens (Bearer).
    /// </summary>
    public static IChatClient Create(
        ResolvedCredentials credentials,
        string modelId,
        AnthropicOAuthClientOptions? options = null)
    {
        options ??= new AnthropicOAuthClientOptions();

        var httpClient = new HttpClient();

        // Detect if this is an API key (starts with "sk-") or an OAuth token (JWT)
        var isApiKey = credentials.ApiKey.StartsWith("sk-") ||
                       credentials.ApiKey.StartsWith("sk_") ||
                       !credentials.ApiKey.Contains(".");

        if (isApiKey)
        {
            // Use x-api-key header for API keys (standard Anthropic auth)
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "x-api-key",
                credentials.ApiKey);
        }
        else
        {
            // Use Bearer token for OAuth (JWT tokens contain periods)
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credentials.ApiKey);
        }

        // Add anthropic-beta header for Claude Code features
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "anthropic-beta",
            credentials.CustomHeaders?.GetValueOrDefault("anthropic-beta")
                ?? AnthropicOAuthAuthProvider.AnthropicBetaHeader);

        // Add anthropic-version header
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "anthropic-version",
            options.AnthropicVersion ?? "2023-06-01");

        // Add User-Agent header
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            credentials.CustomHeaders?.GetValueOrDefault("User-Agent")
                ?? AnthropicOAuthAuthProvider.ClaudeCodeUserAgent);

        // Add any additional custom headers
        if (credentials.CustomHeaders != null)
        {
            foreach (var header in credentials.CustomHeaders)
            {
                if (header.Key != "anthropic-beta" && header.Key != "User-Agent" && header.Key != "x-api-key")
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        // Add custom headers from options
        if (options.CustomHeaders != null)
        {
            foreach (var header in options.CustomHeaders)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Determine the base URL
        var baseUrl = credentials.BaseUrl ?? options.BaseUrl ?? "https://api.anthropic.com";

        // Create the Anthropic SDK client with custom HttpClient
        return new AnthropicOAuthChatClient(httpClient, modelId, baseUrl, options);
    }

    /// <summary>
    /// Checks if the Anthropic auth entry is an OAuth token (vs API key).
    /// </summary>
    public static async Task<bool> IsOAuthAsync(AuthManager authManager)
    {
        var entry = await authManager.Storage.GetAsync("anthropic");
        return entry is OAuthEntry;
    }

    /// <summary>
    /// Determines the appropriate authentication method for Anthropic.
    /// Checks for OAuth first, then falls back to API key.
    /// </summary>
    public static async Task<(bool IsOAuth, string? ProviderId)> GetAnthropicAuthTypeAsync(AuthManager authManager)
    {
        // Check for OAuth first
        var oauthEntry = await authManager.Storage.GetAsync("anthropic");
        if (oauthEntry is OAuthEntry)
        {
            return (true, "anthropic");
        }

        // Check for API key
        var apiKeyEntry = await authManager.Storage.GetAsync("anthropic");
        if (apiKeyEntry != null)
        {
            return (false, "anthropic");
        }

        // Check environment variable
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            return (false, "anthropic");
        }

        return (false, null);
    }

    /// <summary>
    /// Creates the appropriate chat client for Anthropic based on the auth type.
    /// - OAuth tokens → AnthropicOAuthChatClient (Bearer auth + beta headers)
    /// - API keys → Standard Anthropic SDK (via HPD-Agent.Providers.Anthropic)
    /// </summary>
    public static async Task<IChatClient?> CreateAnthropicClientAsync(
        AuthManager authManager,
        string modelId,
        Func<string, string, IChatClient>? standardClientFactory = null)
    {
        var (isOAuth, providerId) = await GetAnthropicAuthTypeAsync(authManager);

        if (providerId == null)
        {
            return null;
        }

        if (isOAuth)
        {
            // OAuth token - use Bearer auth with beta headers
            var credentials = await authManager.ResolveCredentialsAsync("anthropic");
            if (credentials == null) return null;

            return Create(credentials, modelId);
        }

        // API key - use standard Anthropic provider
        if (standardClientFactory != null)
        {
            var credentials = await authManager.ResolveCredentialsAsync("anthropic");
            if (credentials == null) return null;

            return standardClientFactory(modelId, credentials.ApiKey);
        }

        return null;
    }
}

/// <summary>
/// Options for configuring the Anthropic OAuth chat client.
/// </summary>
public class AnthropicOAuthClientOptions
{
    /// <summary>
    /// Base URL for the Anthropic API.
    /// Default is "https://api.anthropic.com".
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// Anthropic API version header value.
    /// Default is "2023-06-01".
    /// </summary>
    public string AnthropicVersion { get; set; } = "2023-06-01";

    /// <summary>
    /// Maximum number of tokens to generate.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Additional custom headers to include in requests.
    /// </summary>
    public Dictionary<string, string>? CustomHeaders { get; set; }

    /// <summary>
    /// Thinking budget tokens for extended thinking models.
    /// </summary>
    public long? ThinkingBudgetTokens { get; set; }
}

/// <summary>
/// Extension methods for integrating Anthropic OAuth with the CLI.
/// </summary>
public static class AnthropicOAuthExtensions
{
    /// <summary>
    /// Gets an IChatClient for Anthropic that automatically uses the correct API
    /// based on the authentication type (OAuth → Bearer auth, API Key → x-api-key).
    /// </summary>
    public static async Task<IChatClient?> GetAnthropicChatClientAsync(
        this AuthManager authManager,
        string modelId)
    {
        return await AnthropicOAuthClientFactory.CreateAnthropicClientAsync(authManager, modelId);
    }
}

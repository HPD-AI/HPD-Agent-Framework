using HPD_Agent.CLI.Auth.Providers;

namespace HPD_Agent.CLI.Auth;

/// <summary>
/// Manages authentication providers and credential resolution.
/// Central point for all auth operations in the CLI.
/// </summary>
public class AuthManager
{
    private readonly AuthStorage _storage;
    private readonly List<IAuthProvider> _providers;

    public AuthManager() : this(new AuthStorage()) { }

    public AuthManager(AuthStorage storage)
    {
        _storage = storage;
        _providers = new List<IAuthProvider>
        {
            // OAuth providers (subscription-based)
            new OpenAICodexAuthProvider(),
            new GitHubCopilotAuthProvider(),
            new OpenRouterAuthProvider(),
            new AnthropicOAuthAuthProvider(),

            // API key providers
            CommonAuthProviders.Anthropic,
            CommonAuthProviders.GoogleAI,
            CommonAuthProviders.Mistral,
            CommonAuthProviders.HuggingFace,
            CommonAuthProviders.AzureOpenAI,
            CommonAuthProviders.Bedrock
        };
    }

    /// <summary>
    /// Gets the auth storage instance.
    /// </summary>
    public AuthStorage Storage => _storage;

    /// <summary>
    /// Gets all registered auth providers.
    /// </summary>
    public IReadOnlyList<IAuthProvider> Providers => _providers;

    /// <summary>
    /// Gets an auth provider by ID.
    /// </summary>
    public IAuthProvider? GetProvider(string providerId)
    {
        return _providers.FirstOrDefault(p =>
            p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Registers a custom auth provider.
    /// </summary>
    public void RegisterProvider(IAuthProvider provider)
    {
        // Remove existing provider with same ID
        _providers.RemoveAll(p => p.ProviderId.Equals(provider.ProviderId, StringComparison.OrdinalIgnoreCase));
        _providers.Add(provider);
    }

    /// <summary>
    /// Resolves credentials for a provider, checking auth storage first, then environment variables.
    /// </summary>
    public async Task<ResolvedCredentials?> ResolveCredentialsAsync(string providerId)
    {
        var provider = GetProvider(providerId);

        // Check auth storage first
        var entry = await _storage.GetAsync(providerId);
        if (entry != null)
        {
            // Try to refresh if needed
            if (provider != null)
            {
                var refreshed = await provider.RefreshIfNeededAsync(entry);
                if (refreshed != null)
                {
                    await _storage.SetAsync(providerId, refreshed);
                    entry = refreshed;
                }

                var loadResult = await provider.LoadAsync(entry);
                return new ResolvedCredentials
                {
                    ApiKey = loadResult.ApiKey,
                    BaseUrl = loadResult.BaseUrl,
                    CustomHeaders = loadResult.CustomHeaders,
                    Source = GetCredentialSource(entry),
                    AccountId = loadResult.AccountId
                };
            }

            // No provider, just return the credential
            return new ResolvedCredentials
            {
                ApiKey = entry.GetCredential(),
                Source = GetCredentialSource(entry)
            };
        }

        // Check environment variables
        if (provider != null)
        {
            foreach (var envVar in provider.EnvironmentVariables)
            {
                var value = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(value))
                {
                    return new ResolvedCredentials
                    {
                        ApiKey = value,
                        Source = $"env:{envVar}"
                    };
                }
            }
        }

        // Check common env var pattern
        var defaultEnvVar = $"{providerId.ToUpperInvariant()}_API_KEY";
        var defaultValue = Environment.GetEnvironmentVariable(defaultEnvVar);
        if (!string.IsNullOrEmpty(defaultValue))
        {
            return new ResolvedCredentials
            {
                ApiKey = defaultValue,
                Source = $"env:{defaultEnvVar}"
            };
        }

        return null;
    }

    /// <summary>
    /// Gets a summary of all authenticated providers.
    /// </summary>
    public async Task<List<AuthSummary>> GetAuthSummaryAsync()
    {
        var result = new List<AuthSummary>();
        var storedAuth = await _storage.GetAllAsync();

        foreach (var provider in _providers)
        {
            var summary = new AuthSummary
            {
                ProviderId = provider.ProviderId,
                DisplayName = provider.DisplayName
            };

            // Check stored credentials
            if (storedAuth.TryGetValue(provider.ProviderId, out var entry))
            {
                summary.IsAuthenticated = true;
                summary.Source = GetCredentialSource(entry);

                if (entry is OAuthEntry oauth)
                {
                    summary.ExpiresAt = oauth.ExpiresAt;
                    summary.AccountId = oauth.AccountId;
                    summary.IsExpired = oauth.IsExpired;
                }
            }
            else
            {
                // Check environment variables
                foreach (var envVar in provider.EnvironmentVariables)
                {
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
                    {
                        summary.IsAuthenticated = true;
                        summary.Source = $"env:{envVar}";
                        break;
                    }
                }
            }

            result.Add(summary);
        }

        return result;
    }

    private static string GetCredentialSource(AuthEntry entry) => entry switch
    {
        OAuthEntry => "oauth",
        ApiKeyEntry => "api",
        WellKnownEntry wk => $"env:{wk.EnvVarName}",
        _ => "unknown"
    };
}

/// <summary>
/// Resolved credentials ready for use.
/// </summary>
public class ResolvedCredentials
{
    public required string ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public Dictionary<string, string>? CustomHeaders { get; init; }
    public required string Source { get; init; }
    public string? AccountId { get; init; }
}

/// <summary>
/// Summary of authentication status for a provider.
/// </summary>
public class AuthSummary
{
    public required string ProviderId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsAuthenticated { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? AccountId { get; set; }
    public bool IsExpired { get; set; }
}

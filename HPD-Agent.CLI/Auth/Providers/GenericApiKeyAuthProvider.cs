namespace HPD_Agent.CLI.Auth.Providers;

/// <summary>
/// Generic authentication provider for API key-based services.
/// Used as a fallback for providers that don't have OAuth support.
/// </summary>
public class GenericApiKeyAuthProvider : IAuthProvider
{
    private readonly string _providerId;
    private readonly string _displayName;
    private readonly string[] _environmentVariables;

    public GenericApiKeyAuthProvider(string providerId, string displayName, params string[] environmentVariables)
    {
        _providerId = providerId.ToLowerInvariant();
        _displayName = displayName;
        _environmentVariables = environmentVariables.Length > 0
            ? environmentVariables
            : new[] { $"{providerId.ToUpperInvariant()}_API_KEY" };
    }

    public string ProviderId => _providerId;
    public string DisplayName => _displayName;
    public IReadOnlyList<string> EnvironmentVariables => _environmentVariables;

    public IReadOnlyList<AuthMethod> Methods => new[]
    {
        new AuthMethod
        {
            Type = AuthType.ApiKey,
            Label = "API key",
            Description = $"Enter your {_displayName} API key",
            IsRecommended = true,
            StartFlow = StartApiKeyFlowAsync
        },
        new AuthMethod
        {
            Type = AuthType.WellKnown,
            Label = "Environment variable",
            Description = $"Use {string.Join(" or ", _environmentVariables)} environment variable",
            StartFlow = StartWellKnownFlowAsync
        }
    };

    public Task<AuthLoadResult> LoadAsync(AuthEntry entry)
    {
        var apiKey = entry switch
        {
            ApiKeyEntry ak => ak.Key,
            WellKnownEntry wk => wk.GetCredential(),
            OAuthEntry oauth => oauth.AccessToken,
            _ => throw new ArgumentException($"Unsupported auth entry type: {entry.GetType().Name}")
        };

        return Task.FromResult(new AuthLoadResult { ApiKey = apiKey });
    }

    public Task<AuthEntry?> RefreshIfNeededAsync(AuthEntry entry)
    {
        // API keys don't need refresh
        return Task.FromResult<AuthEntry?>(null);
    }

    public Task<bool> ValidateAsync(AuthEntry entry)
    {
        // Basic validation - just check it's not empty
        var credential = entry switch
        {
            ApiKeyEntry ak => ak.Key,
            WellKnownEntry wk => wk.GetCredential(),
            OAuthEntry oauth => oauth.AccessToken,
            _ => null
        };

        return Task.FromResult(!string.IsNullOrWhiteSpace(credential));
    }

    private Task<AuthFlowResult> StartApiKeyFlowAsync(CancellationToken cancellationToken)
    {
        // API key input is handled by the CLI
        return Task.FromResult<AuthFlowResult>(
            new AuthFlowResult.Failed("API key flow requires user input - handled by CLI"));
    }

    private Task<AuthFlowResult> StartWellKnownFlowAsync(CancellationToken cancellationToken)
    {
        // Check if any environment variable is set
        foreach (var envVar in _environmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                return Task.FromResult<AuthFlowResult>(
                    new AuthFlowResult.Success(new WellKnownEntry
                    {
                        EnvVarName = envVar,
                        CachedToken = value
                    }));
            }
        }

        return Task.FromResult<AuthFlowResult>(
            new AuthFlowResult.Failed($"No environment variable found. Set one of: {string.Join(", ", _environmentVariables)}"));
    }

    /// <summary>
    /// Creates an API key entry from user input.
    /// </summary>
    public static AuthEntry CreateApiKeyEntry(string apiKey) => new ApiKeyEntry { Key = apiKey };

    /// <summary>
    /// Creates a well-known entry from an environment variable name.
    /// </summary>
    public static AuthEntry CreateWellKnownEntry(string envVarName)
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        return new WellKnownEntry
        {
            EnvVarName = envVarName,
            CachedToken = value
        };
    }
}

/// <summary>
/// Pre-configured auth providers for common API key-based services.
/// </summary>
public static class CommonAuthProviders
{
    public static GenericApiKeyAuthProvider Anthropic => new(
        "anthropic",
        "Anthropic (Claude)",
        "ANTHROPIC_API_KEY");

    public static GenericApiKeyAuthProvider GoogleAI => new(
        "googleai",
        "Google AI (Gemini)",
        "GOOGLE_API_KEY", "GEMINI_API_KEY");

    public static GenericApiKeyAuthProvider Mistral => new(
        "mistral",
        "Mistral AI",
        "MISTRAL_API_KEY");

    public static GenericApiKeyAuthProvider OpenRouter => new(
        "openrouter",
        "OpenRouter",
        "OPENROUTER_API_KEY");

    public static GenericApiKeyAuthProvider HuggingFace => new(
        "huggingface",
        "HuggingFace",
        "HUGGINGFACE_API_KEY", "HF_TOKEN");

    public static GenericApiKeyAuthProvider AzureOpenAI => new(
        "azureopenai",
        "Azure OpenAI",
        "AZURE_OPENAI_API_KEY");

    public static GenericApiKeyAuthProvider Bedrock => new(
        "bedrock",
        "AWS Bedrock",
        "AWS_ACCESS_KEY_ID"); // Bedrock uses AWS credentials

    /// <summary>
    /// Gets all pre-configured auth providers.
    /// </summary>
    public static IEnumerable<IAuthProvider> GetAll()
    {
        yield return Anthropic;
        yield return GoogleAI;
        yield return Mistral;
        yield return OpenRouter;
        yield return HuggingFace;
        yield return AzureOpenAI;
        yield return Bedrock;
    }
}

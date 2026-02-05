using HPD_Agent.CLI.Auth;
using Microsoft.Extensions.AI;

namespace HPD_Agent.CLI.Codex;

/// <summary>
/// Factory for creating Codex chat clients from authenticated credentials.
/// </summary>
public static class CodexClientFactory
{
    /// <summary>
    /// Creates a Codex chat client using OAuth credentials from the auth manager.
    /// </summary>
    /// <param name="authManager">The auth manager with stored credentials.</param>
    /// <param name="modelId">The model ID to use (e.g., "gpt-5.2", "gpt-5.2-thinking").</param>
    /// <param name="options">Optional client configuration.</param>
    /// <returns>An IChatClient configured for the Codex API.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no valid credentials are available.</exception>
    public static async Task<IChatClient> CreateAsync(
        AuthManager authManager,
        string modelId,
        CodexClientOptions? options = null)
    {
        var credentials = await authManager.ResolveCredentialsAsync("openai");

        if (credentials == null)
        {
            throw new InvalidOperationException(
                "No OpenAI credentials found. Please run '/auth login openai' to authenticate.");
        }

        return Create(credentials, modelId, options);
    }

    /// <summary>
    /// Creates a Codex chat client from resolved credentials.
    /// </summary>
    public static IChatClient Create(
        ResolvedCredentials credentials,
        string modelId,
        CodexClientOptions? options = null)
    {
        options ??= new CodexClientOptions();

        // Merge custom headers from credentials
        if (credentials.CustomHeaders != null)
        {
            options.CustomHeaders ??= new Dictionary<string, string>();
            foreach (var header in credentials.CustomHeaders)
            {
                options.CustomHeaders[header.Key] = header.Value;
            }
        }

        // Add account ID header if available
        if (!string.IsNullOrEmpty(credentials.AccountId) &&
            (options.CustomHeaders == null || !options.CustomHeaders.ContainsKey("ChatGPT-Account-Id")))
        {
            options.CustomHeaders ??= new Dictionary<string, string>();
            options.CustomHeaders["ChatGPT-Account-Id"] = credentials.AccountId;
        }

        // Use the base URL from credentials or default to Codex API
        var baseUrl = credentials.BaseUrl ?? "https://chatgpt.com/backend-api/codex";

        return new CodexChatClient(modelId, credentials.ApiKey, baseUrl, options);
    }

    /// <summary>
    /// Checks if the OpenAI auth entry is an OAuth token (vs API key).
    /// OAuth tokens use the Codex API; API keys use the standard OpenAI API.
    /// </summary>
    public static async Task<bool> IsCodexAuthAsync(AuthManager authManager)
    {
        var entry = await authManager.Storage.GetAsync("openai");
        return entry is OAuthEntry;
    }

    /// <summary>
    /// Determines the appropriate base URL for OpenAI credentials.
    /// OAuth tokens should use the Codex API; API keys use standard OpenAI.
    /// </summary>
    public static async Task<(bool IsCodex, string BaseUrl)> GetOpenAIEndpointAsync(AuthManager authManager)
    {
        var entry = await authManager.Storage.GetAsync("openai");

        if (entry is OAuthEntry)
        {
            return (true, "https://chatgpt.com/backend-api/codex");
        }

        return (false, "https://api.openai.com/v1");
    }

    /// <summary>
    /// Creates the appropriate chat client for OpenAI based on the auth type.
    /// - OAuth tokens → CodexChatClient (Codex API)
    /// - API keys → Standard OpenAI SDK (via HPD-Agent.Providers.OpenAI)
    /// </summary>
    public static async Task<IChatClient?> CreateOpenAIClientAsync(
        AuthManager authManager,
        string modelId,
        Func<string, string, IChatClient>? standardClientFactory = null)
    {
        var credentials = await authManager.ResolveCredentialsAsync("openai");

        if (credentials == null)
        {
            return null;
        }

        var isCodex = await IsCodexAuthAsync(authManager);

        if (isCodex)
        {
            // OAuth token - use Codex API
            return Create(credentials, modelId, new CodexClientOptions
            {
                ReasoningEffort = CodexMessageConverter.IsReasoningModel(modelId) ? "medium" : null
            });
        }

        // API key - use standard OpenAI provider
        if (standardClientFactory != null)
        {
            return standardClientFactory(modelId, credentials.ApiKey);
        }

        // If no standard factory provided, fall back to Codex for now
        // (in production, you'd want to create the standard OpenAI client here)
        return null;
    }
}

/// <summary>
/// Extension methods for integrating Codex with the agent builder.
/// </summary>
public static class CodexAgentBuilderExtensions
{
    /// <summary>
    /// Gets an IChatClient for OpenAI that automatically uses the correct API
    /// based on the authentication type (OAuth → Codex, API Key → Standard).
    /// </summary>
    public static async Task<IChatClient?> GetOpenAIChatClientAsync(
        this AuthManager authManager,
        string modelId)
    {
        return await CodexClientFactory.CreateOpenAIClientAsync(authManager, modelId);
    }
}

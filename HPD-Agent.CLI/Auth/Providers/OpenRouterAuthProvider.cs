using System.Net.Http.Json;
using System.Text.Json;

namespace HPD_Agent.CLI.Auth.Providers;

/// <summary>
/// OAuth authentication provider for OpenRouter.
/// Supports both OAuth PKCE flow and API key authentication.
/// </summary>
public class OpenRouterAuthProvider : IAuthProvider
{
    private const string AuthBaseUrl = "https://openrouter.ai/auth";
    private const string ApiBaseUrl = "https://openrouter.ai/api/v1";

    // OpenRouter allows localhost:3000 for development
    private const int OAuthPort = 3000;

    public string ProviderId => "openrouter";
    public string DisplayName => "OpenRouter";
    public IReadOnlyList<string> EnvironmentVariables => new[] { "OPENROUTER_API_KEY" };

    public IReadOnlyList<AuthMethod> Methods => new[]
    {
        new AuthMethod
        {
            Type = AuthType.OAuthBrowser,
            Label = "Browser login",
            Description = "Sign in with your OpenRouter account",
            IsRecommended = true,
            StartFlow = StartBrowserFlowAsync
        },
        new AuthMethod
        {
            Type = AuthType.ApiKey,
            Label = "API key",
            Description = "Enter your OpenRouter API key manually",
            StartFlow = _ => Task.FromResult<AuthFlowResult>(
                new AuthFlowResult.Failed("API key flow requires user input - handled by CLI"))
        }
    };

    public Task<AuthLoadResult> LoadAsync(AuthEntry entry)
    {
        var apiKey = entry switch
        {
            ApiKeyEntry ak => ak.Key,
            OAuthEntry oauth => oauth.AccessToken,
            WellKnownEntry wk => wk.GetCredential(),
            _ => throw new ArgumentException($"Unsupported auth entry type: {entry.GetType().Name}")
        };

        return Task.FromResult(new AuthLoadResult
        {
            ApiKey = apiKey,
            BaseUrl = "https://openrouter.ai/api/v1"
        });
    }

    public Task<AuthEntry?> RefreshIfNeededAsync(AuthEntry entry)
    {
        // OpenRouter API keys don't expire unless explicitly set
        return Task.FromResult<AuthEntry?>(null);
    }

    public async Task<bool> ValidateAsync(AuthEntry entry)
    {
        var apiKey = entry switch
        {
            ApiKeyEntry ak => ak.Key,
            OAuthEntry oauth => oauth.AccessToken,
            WellKnownEntry wk => wk.GetCredential(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var response = await httpClient.GetAsync($"{ApiBaseUrl}/auth/key");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<AuthFlowResult> StartBrowserFlowAsync(CancellationToken cancellationToken)
    {
        var codeVerifier = OAuthHelpers.GenerateCodeVerifier();
        var codeChallenge = OAuthHelpers.GenerateCodeChallenge(codeVerifier);
        var state = OAuthHelpers.GenerateRandomString();

        // OpenRouter requires port 3000 for localhost callbacks
        var port = OAuthCallbackServer.FindAvailablePort(OAuthPort);
        if (port != OAuthPort)
        {
            return new AuthFlowResult.Failed(
                $"Port {OAuthPort} is required for OpenRouter OAuth but is in use. " +
                "Please close any applications using this port and try again.");
        }

        await using var callbackServer = new OAuthCallbackServer(port, state);

        // OpenRouter's auth URL format
        var authUrl = OAuthHelpers.BuildUrl(AuthBaseUrl, new Dictionary<string, string>
        {
            ["callback_url"] = callbackServer.CallbackUrl,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        });

        if (!OAuthHelpers.OpenBrowser(authUrl))
        {
            return new AuthFlowResult.Failed("Failed to open browser. Please use API key authentication instead.");
        }

        var callbackResult = await callbackServer.WaitForCallbackAsync(cancellationToken);

        return callbackResult switch
        {
            OAuthCallbackResult.Success success => await ExchangeCodeForKeyAsync(success.Code, codeVerifier),
            OAuthCallbackResult.Cancelled => new AuthFlowResult.Cancelled(),
            OAuthCallbackResult.Timeout => new AuthFlowResult.Failed("Authentication timed out. Please try again."),
            OAuthCallbackResult.Error error => new AuthFlowResult.Failed(error.Message),
            _ => new AuthFlowResult.Failed("Unknown callback result")
        };
    }

    private async Task<AuthFlowResult> ExchangeCodeForKeyAsync(string code, string codeVerifier)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "HPD-Agent-CLI");

            var requestBody = new
            {
                code,
                code_verifier = codeVerifier,
                code_challenge_method = "S256"
            };

            var response = await httpClient.PostAsJsonAsync(
                $"{ApiBaseUrl}/auth/keys",
                requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new AuthFlowResult.Failed($"Failed to exchange code for API key: {response.StatusCode} - {errorContent}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("key", out var keyElement))
            {
                return new AuthFlowResult.Failed("Response did not contain API key");
            }

            var apiKey = keyElement.GetString();
            if (string.IsNullOrEmpty(apiKey))
            {
                return new AuthFlowResult.Failed("Received empty API key");
            }

            // OpenRouter returns an API key, store it as an ApiKeyEntry
            // (it doesn't have refresh tokens or expiration like traditional OAuth)
            return new AuthFlowResult.Success(new ApiKeyEntry { Key = apiKey });
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Error exchanging code for API key: {ex.Message}", ex);
        }
    }
}

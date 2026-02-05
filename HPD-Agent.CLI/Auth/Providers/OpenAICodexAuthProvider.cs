using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Auth.Providers;

/// <summary>
/// Authentication provider for OpenAI ChatGPT Plus/Pro subscriptions via the Codex API.
/// Supports both browser-based OAuth and device code flows.
/// </summary>
public class OpenAICodexAuthProvider : IAuthProvider
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthBaseUrl = "https://auth.openai.com";
    private const string CodexApiBaseUrl = "https://chatgpt.com/backend-api/codex";

    private readonly HttpClient _httpClient;

    public OpenAICodexAuthProvider() : this(new HttpClient()) { }

    public OpenAICodexAuthProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderId => "openai";
    public string DisplayName => "OpenAI (ChatGPT Plus/Pro)";

    public IReadOnlyList<string> EnvironmentVariables => new[] { "OPENAI_API_KEY" };

    public IReadOnlyList<AuthMethod> Methods => new[]
    {
        new AuthMethod
        {
            Type = AuthType.OAuthBrowser,
            Label = "ChatGPT subscription (browser)",
            Description = "Opens browser for OAuth authentication",
            IsRecommended = true,
            StartFlow = StartBrowserFlowAsync
        },
        new AuthMethod
        {
            Type = AuthType.OAuthDeviceCode,
            Label = "ChatGPT subscription (device code)",
            Description = "Enter a code on openai.com - no browser popup needed",
            StartFlow = StartDeviceCodeFlowAsync
        },
        new AuthMethod
        {
            Type = AuthType.ApiKey,
            Label = "API key",
            Description = "Enter your OpenAI API key manually",
            StartFlow = StartApiKeyFlowAsync
        }
    };

    public Task<AuthLoadResult> LoadAsync(AuthEntry entry)
    {
        var result = entry switch
        {
            OAuthEntry oauth => new AuthLoadResult
            {
                ApiKey = oauth.AccessToken,
                // SDK appends /responses to the base URL, so just provide the codex base
                BaseUrl = CodexApiBaseUrl,
                CustomHeaders = oauth.AccountId != null
                    ? new Dictionary<string, string> { ["ChatGPT-Account-Id"] = oauth.AccountId }
                    : null,
                AccountId = oauth.AccountId
            },
            ApiKeyEntry apiKey => new AuthLoadResult
            {
                ApiKey = apiKey.Key
            },
            WellKnownEntry wellKnown => new AuthLoadResult
            {
                ApiKey = wellKnown.GetCredential()
            },
            _ => throw new ArgumentException($"Unsupported auth entry type: {entry.GetType().Name}")
        };

        return Task.FromResult(result);
    }

    public async Task<AuthEntry?> RefreshIfNeededAsync(AuthEntry entry)
    {
        if (entry is not OAuthEntry oauth)
        {
            return null;
        }

        // Refresh if expires within 5 minutes
        if (!oauth.ExpiresWithin(TimeSpan.FromMinutes(5)))
        {
            return null;
        }

        try
        {
            var tokenResponse = await _httpClient.RefreshTokenAsync(
                $"{AuthBaseUrl}/oauth/token",
                oauth.RefreshToken,
                ClientId);

            if (string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                return null;
            }

            // Extract account ID from new token
            var accountId = ExtractAccountId(tokenResponse.AccessToken) ?? oauth.AccountId;

            return new OAuthEntry
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? oauth.RefreshToken,
                ExpiresAtUnixMs = tokenResponse.GetExpiresAtUnixMs(),
                AccountId = accountId
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ValidateAsync(AuthEntry entry)
    {
        if (entry is OAuthEntry oauth && oauth.IsExpired)
        {
            return false;
        }

        // TODO: Could make a test API call to validate
        return await Task.FromResult(true);
    }

    private async Task<AuthFlowResult> StartBrowserFlowAsync(CancellationToken cancellationToken)
    {
        var state = OAuthHelpers.GenerateRandomString();
        var codeVerifier = OAuthHelpers.GenerateCodeVerifier();
        var codeChallenge = OAuthHelpers.GenerateCodeChallenge(codeVerifier);

        // OpenAI has http://localhost:1455/auth/callback registered for the  client_id
        const int oauthPort = 1455;
        var port = OAuthCallbackServer.FindAvailablePort(oauthPort);
        if (port != oauthPort)
        {
            return new AuthFlowResult.Failed($"Port {oauthPort} is in use. Please close any other OAuth flows and try again.");
        }
        await using var callbackServer = new OAuthCallbackServer(port, state);

        var authUrl = OAuthHelpers.BuildUrl($"{AuthBaseUrl}/oauth/authorize", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = callbackServer.CallbackUrl,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = ""  // Must use "" - it's whitelisted by OpenAI
        });

        if (!OAuthHelpers.OpenBrowser(authUrl))
        {
            return new AuthFlowResult.Failed("Failed to open browser. Please try device code flow instead.");
        }

        var callbackResult = await callbackServer.WaitForCallbackAsync(cancellationToken);

        return callbackResult switch
        {
            OAuthCallbackResult.Success success => await ExchangeCodeAsync(success.Code, callbackServer.CallbackUrl, codeVerifier, cancellationToken),
            OAuthCallbackResult.Cancelled => new AuthFlowResult.Cancelled(),
            OAuthCallbackResult.Timeout => new AuthFlowResult.Failed("Authentication timed out. Please try again."),
            OAuthCallbackResult.Error error => new AuthFlowResult.Failed(error.Message, error.Exception),
            _ => new AuthFlowResult.Failed("Unexpected callback result")
        };
    }

    private async Task<AuthFlowResult> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken)
    {
        try
        {
            var tokenResponse = await _httpClient.ExchangeCodeForTokensAsync(
                $"{AuthBaseUrl}/oauth/token",
                code,
                ClientId,
                redirectUri,
                codeVerifier,
                cancellationToken);

            if (string.IsNullOrEmpty(tokenResponse.AccessToken) || string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                return new AuthFlowResult.Failed("Invalid token response from OpenAI");
            }

            var accountId = ExtractAccountId(tokenResponse.AccessToken);

            var entry = new OAuthEntry
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiresAtUnixMs = tokenResponse.GetExpiresAtUnixMs(),
                AccountId = accountId
            };

            return new AuthFlowResult.Success(entry);
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Failed to exchange code for tokens: {ex.Message}", ex);
        }
    }

    private async Task<AuthFlowResult> StartDeviceCodeFlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Request device code
            var deviceCodeResponse = await RequestDeviceCodeAsync(cancellationToken);

            if (deviceCodeResponse == null)
            {
                return new AuthFlowResult.Failed("Failed to get device code from OpenAI");
            }

            // Return pending action - caller will display code and wait
            return new AuthFlowResult.PendingUserAction(
                $"Enter code: {deviceCodeResponse.UserCode}",
                "https://auth.openai.com/codex/device",
                deviceCodeResponse.UserCode,
                async ct => await PollForDeviceTokenAsync(deviceCodeResponse.DeviceAuthId, ct));
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Failed to start device code flow: {ex.Message}", ex);
        }
    }

    private async Task<DeviceCodeResponse?> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"{AuthBaseUrl}/api/accounts/deviceauth/usercode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: cancellationToken);
    }

    private async Task<AuthFlowResult> PollForDeviceTokenAsync(string deviceAuthId, CancellationToken cancellationToken)
    {
        const int maxAttempts = 60; // 5 minutes with 5 second intervals
        const int pollIntervalMs = 5000;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new AuthFlowResult.Cancelled();
            }

            try
            {
                var response = await _httpClient.PostAsync(
                    $"{AuthBaseUrl}/api/accounts/deviceauth/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["device_auth_id"] = deviceAuthId
                    }),
                    cancellationToken);

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var tokenResult = JsonSerializer.Deserialize<DeviceTokenResponse>(json);

                if (tokenResult?.Status == "complete" && !string.IsNullOrEmpty(tokenResult.Code))
                {
                    // Exchange the code for actual tokens
                    return await ExchangeDeviceCodeAsync(tokenResult.Code, cancellationToken);
                }

                if (tokenResult?.Status == "expired")
                {
                    return new AuthFlowResult.Failed("Device code expired. Please try again.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Continue polling on transient errors
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }

        return new AuthFlowResult.Failed("Device code flow timed out. Please try again.");
    }

    private async Task<AuthFlowResult> ExchangeDeviceCodeAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code
            });

            var response = await _httpClient.PostAsync($"{AuthBaseUrl}/oauth/token", content, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new AuthFlowResult.Failed($"Token exchange failed: {json}");
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);

            if (string.IsNullOrEmpty(tokenResponse?.AccessToken) || string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                return new AuthFlowResult.Failed("Invalid token response from OpenAI");
            }

            var accountId = ExtractAccountId(tokenResponse.AccessToken);

            var entry = new OAuthEntry
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiresAtUnixMs = tokenResponse.GetExpiresAtUnixMs(),
                AccountId = accountId
            };

            return new AuthFlowResult.Success(entry);
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Failed to exchange device code: {ex.Message}", ex);
        }
    }

    private Task<AuthFlowResult> StartApiKeyFlowAsync(CancellationToken cancellationToken)
    {
        // API key flow is handled by the CLI - just return a placeholder
        // The CLI will prompt for input and create the entry
        return Task.FromResult<AuthFlowResult>(
            new AuthFlowResult.Failed("API key flow requires user input - handled by CLI"));
    }

    private static string? ExtractAccountId(string accessToken)
    {
        var claims = OAuthHelpers.ParseJwtClaims(accessToken);
        if (claims == null) return null;

        // First try root-level chatgpt_account_id
        var accountId = OAuthHelpers.GetJwtClaim(claims, "chatgpt_account_id");
        if (!string.IsNullOrEmpty(accountId)) return accountId;

        // Try nested claim under https://api.openai.com/auth
        if (claims.TryGetValue("https://api.openai.com/auth", out var authClaim) &&
            authClaim.ValueKind == System.Text.Json.JsonValueKind.Object &&
            authClaim.TryGetProperty("chatgpt_account_id", out var nestedAccountId) &&
            nestedAccountId.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return nestedAccountId.GetString();
        }

        // Fallback to organizations array
        return OAuthHelpers.GetJwtClaim(claims, "organizations");
    }

    private class DeviceCodeResponse
    {
        [JsonPropertyName("device_auth_id")]
        public string DeviceAuthId { get; set; } = "";

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = "";

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int? Interval { get; set; }
    }

    private class DeviceTokenResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}

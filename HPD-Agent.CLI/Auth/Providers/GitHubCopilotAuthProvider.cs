using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Auth.Providers;

/// <summary>
/// Authentication provider for GitHub Copilot using device code OAuth flow.
/// Supports both GitHub.com and GitHub Enterprise.
/// </summary>
public class GitHubCopilotAuthProvider : IAuthProvider
{
    private const string ClientId = "Ov23li8tweQw6odWQebz";
    private const string DefaultGitHubUrl = "https://github.com";
    private const string CopilotApiUrl = "https://api.githubcopilot.com";

    private readonly HttpClient _httpClient;

    public GitHubCopilotAuthProvider() : this(new HttpClient()) { }

    public GitHubCopilotAuthProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ProviderId => "github-copilot";
    public string DisplayName => "GitHub Copilot";

    public IReadOnlyList<string> EnvironmentVariables => new[] { "GITHUB_TOKEN", "GH_TOKEN" };

    public IReadOnlyList<AuthMethod> Methods => new[]
    {
        new AuthMethod
        {
            Type = AuthType.OAuthDeviceCode,
            Label = "GitHub.com",
            Description = "Authenticate with your GitHub.com account",
            IsRecommended = true,
            StartFlow = ct => StartDeviceCodeFlowAsync(DefaultGitHubUrl, ct)
        },
        new AuthMethod
        {
            Type = AuthType.OAuthDeviceCode,
            Label = "GitHub Enterprise",
            Description = "Authenticate with a GitHub Enterprise instance",
            StartFlow = ct => StartEnterpriseFlowAsync(ct)
        },
        new AuthMethod
        {
            Type = AuthType.ApiKey,
            Label = "Personal Access Token",
            Description = "Enter a GitHub personal access token",
            StartFlow = StartApiKeyFlowAsync
        }
    };

    public Task<AuthLoadResult> LoadAsync(AuthEntry entry)
    {
        var result = entry switch
        {
            OAuthEntry oauth => new AuthLoadResult
            {
                ApiKey = oauth.RefreshToken, // GitHub uses the refresh token as the bearer token
                BaseUrl = CopilotApiUrl,
                CustomHeaders = new Dictionary<string, string>
                {
                    ["Copilot-Integration-Id"] = "hpd-agent-cli",
                    ["Openai-Intent"] = "conversation-edits"
                },
                AccountId = oauth.AccountId
            },
            ApiKeyEntry apiKey => new AuthLoadResult
            {
                ApiKey = apiKey.Key,
                BaseUrl = CopilotApiUrl,
                CustomHeaders = new Dictionary<string, string>
                {
                    ["Copilot-Integration-Id"] = "hpd-agent-cli"
                }
            },
            WellKnownEntry wellKnown => new AuthLoadResult
            {
                ApiKey = wellKnown.GetCredential(),
                BaseUrl = CopilotApiUrl
            },
            _ => throw new ArgumentException($"Unsupported auth entry type: {entry.GetType().Name}")
        };

        return Task.FromResult(result);
    }

    public async Task<AuthEntry?> RefreshIfNeededAsync(AuthEntry entry)
    {
        // GitHub Copilot tokens don't expire in the traditional sense
        // The "refresh token" IS the access token for Copilot
        // We could validate it's still working, but that requires an API call

        if (entry is not OAuthEntry oauth)
        {
            return null;
        }

        // If it has an expiry and is expired, try to get a new token
        // (This shouldn't happen with GitHub tokens, but handle it gracefully)
        if (oauth.IsExpired)
        {
            // Can't refresh GitHub tokens - user needs to re-authenticate
            return null;
        }

        return null;
    }

    public async Task<bool> ValidateAsync(AuthEntry entry)
    {
        try
        {
            var loadResult = await LoadAsync(entry);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{CopilotApiUrl}/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loadResult.ApiKey);

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private Task<AuthFlowResult> StartEnterpriseFlowAsync(CancellationToken cancellationToken)
    {
        // Return a pending action that asks for the enterprise URL
        // The CLI will handle prompting for the URL and then calling back
        return Task.FromResult<AuthFlowResult>(
            new AuthFlowResult.PendingUserAction(
                "Enter your GitHub Enterprise URL",
                null,
                null,
                async ct =>
                {
                    // This is a placeholder - the CLI will handle this
                    return new AuthFlowResult.Failed("Enterprise URL input required - handled by CLI");
                }));
    }

    internal async Task<AuthFlowResult> StartDeviceCodeFlowAsync(string githubUrl, CancellationToken cancellationToken)
    {
        try
        {
            var deviceCodeResponse = await RequestDeviceCodeAsync(githubUrl, cancellationToken);

            if (deviceCodeResponse == null)
            {
                return new AuthFlowResult.Failed("Failed to get device code from GitHub");
            }

            var verificationUrl = deviceCodeResponse.VerificationUri ?? $"{githubUrl}/login/device";

            return new AuthFlowResult.PendingUserAction(
                $"Enter code: {deviceCodeResponse.UserCode}",
                verificationUrl,
                deviceCodeResponse.UserCode,
                ct => PollForAccessTokenAsync(githubUrl, deviceCodeResponse.DeviceCode, deviceCodeResponse.Interval ?? 5, ct));
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Failed to start GitHub device code flow: {ex.Message}", ex);
        }
    }

    private async Task<DeviceCodeResponse?> RequestDeviceCodeAsync(string githubUrl, CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user"
        });

        var response = await _httpClient.PostAsync($"{githubUrl}/login/device/code", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: cancellationToken);
    }

    private async Task<AuthFlowResult> PollForAccessTokenAsync(
        string githubUrl,
        string deviceCode,
        int intervalSeconds,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 60; // 5 minutes with default 5 second intervals
        var pollInterval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, 5));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new AuthFlowResult.Cancelled();
            }

            await Task.Delay(pollInterval, cancellationToken);

            try
            {
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });

                var response = await _httpClient.PostAsync($"{githubUrl}/login/oauth/access_token", content, cancellationToken);
                var tokenResponse = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken: cancellationToken);

                if (!string.IsNullOrEmpty(tokenResponse?.AccessToken))
                {
                    // Get user info for account ID
                    var accountId = await GetUserLoginAsync(tokenResponse.AccessToken);
                    var isEnterprise = githubUrl != DefaultGitHubUrl;

                    var entry = new OAuthEntry
                    {
                        // GitHub uses the access token directly - no separate refresh token
                        AccessToken = tokenResponse.AccessToken,
                        RefreshToken = tokenResponse.AccessToken, // Same token used for API calls
                        ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds(), // GitHub tokens don't expire
                        AccountId = accountId,
                        EnterpriseUrl = isEnterprise ? githubUrl : null
                    };

                    return new AuthFlowResult.Success(entry);
                }

                // Check for specific error responses
                if (tokenResponse?.Error == "authorization_pending")
                {
                    // User hasn't authorized yet, keep polling
                    continue;
                }

                if (tokenResponse?.Error == "slow_down")
                {
                    // Increase poll interval
                    pollInterval = pollInterval.Add(TimeSpan.FromSeconds(5));
                    continue;
                }

                if (tokenResponse?.Error == "expired_token")
                {
                    return new AuthFlowResult.Failed("Device code expired. Please try again.");
                }

                if (tokenResponse?.Error == "access_denied")
                {
                    return new AuthFlowResult.Failed("Access denied. Please try again and approve the authorization.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Continue polling on transient errors
            }
        }

        return new AuthFlowResult.Failed("Authorization timed out. Please try again.");
    }

    private async Task<string?> GetUserLoginAsync(string accessToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HPD-Agent-CLI", "1.0"));

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
                return user?.Login;
            }
        }
        catch
        {
            // Ignore errors getting user info
        }

        return null;
    }

    private Task<AuthFlowResult> StartApiKeyFlowAsync(CancellationToken cancellationToken)
    {
        // API key/PAT flow is handled by the CLI
        return Task.FromResult<AuthFlowResult>(
            new AuthFlowResult.Failed("API key flow requires user input - handled by CLI"));
    }

    private class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = "";

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = "";

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int? Interval { get; set; }
    }

    private class AccessTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private class GitHubUser
    {
        [JsonPropertyName("login")]
        public string? Login { get; set; }

        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

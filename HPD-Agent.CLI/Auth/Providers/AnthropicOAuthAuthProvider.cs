using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Auth.Providers;

/// <summary>
/// Authentication provider for Anthropic Claude via OAuth.
/// Uses the same OAuth flow as Claude Code CLI to access Claude with subscription-based auth.
///
/// IMPORTANT: After OAuth authentication, this provider creates an API key using the OAuth token.
/// The API key is then used for all subsequent API calls (via x-api-key header).
/// This bypasses Anthropic's "OAuth authentication is currently not supported" restriction.
/// </summary>
public class AnthropicOAuthAuthProvider : IAuthProvider
{
    // Anthropic OAuth endpoints
    private const string AuthBaseUrl = "https://console.anthropic.com";
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";
    private const string ApiBaseUrl = "https://api.anthropic.com";

    // API key creation endpoint - uses OAuth token to create an API key
    // Note: This endpoint may not be publicly available - it's what we expect based on  behavior
    private const string CreateApiKeyEndpoint = "https://console.anthropic.com/api/keys";

    // Anthropic's server-side callback URL (user will copy the code from this page)
    private const string OAuthRedirectUri = "https://console.anthropic.com/oauth/code/callback";

    // OAuth client ID - same as Claude Code CLI
    // This is Anthropic's public OAuth client for CLI applications
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    // Beta header for Claude Code features
    public const string AnthropicBetaHeader = "claude-code-20250219,interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14";

    // User-Agent to match Claude Code CLI behavior
    public const string ClaudeCodeUserAgent = "claude-code/1.0.0";

    private readonly HttpClient _httpClient;

    public AnthropicOAuthAuthProvider() : this(new HttpClient()) { }

    public AnthropicOAuthAuthProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderId => "anthropic";
    public string DisplayName => "Anthropic (Claude Code OAuth)";

    public IReadOnlyList<string> EnvironmentVariables => new[] { "ANTHROPIC_API_KEY" };

    public IReadOnlyList<AuthMethod> Methods => new[]
    {
        new AuthMethod
        {
            Type = AuthType.OAuthManualCode,
            Label = "Claude subscription (browser)",
            Description = "Opens browser for OAuth - copy the authorization code back to CLI",
            IsRecommended = true,
            StartFlow = StartBrowserFlowAsync
        },
        new AuthMethod
        {
            Type = AuthType.ApiKey,
            Label = "API key",
            Description = "Enter your Anthropic API key manually",
            StartFlow = StartApiKeyFlowAsync
        }
    };

    public Task<AuthLoadResult> LoadAsync(AuthEntry entry)
    {
        // All entry types now use x-api-key header (not Bearer token)
        // OAuth flow creates an API key, so OAuthEntry.AccessToken actually contains the API key
        var result = entry switch
        {
            OAuthEntry oauth => new AuthLoadResult
            {
                ApiKey = oauth.AccessToken, // This is actually the created API key, not OAuth token
                BaseUrl = ApiBaseUrl,
                CustomHeaders = new Dictionary<string, string>
                {
                    ["anthropic-beta"] = AnthropicBetaHeader,
                    ["User-Agent"] = ClaudeCodeUserAgent
                },
                AccountId = oauth.AccountId
            },
            ApiKeyEntry apiKey => new AuthLoadResult
            {
                ApiKey = apiKey.Key,
                BaseUrl = ApiBaseUrl,
                CustomHeaders = new Dictionary<string, string>
                {
                    ["anthropic-beta"] = AnthropicBetaHeader
                }
            },
            WellKnownEntry wellKnown => new AuthLoadResult
            {
                ApiKey = wellKnown.GetCredential(),
                BaseUrl = ApiBaseUrl,
                CustomHeaders = new Dictionary<string, string>
                {
                    ["anthropic-beta"] = AnthropicBetaHeader
                }
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

        if (string.IsNullOrEmpty(oauth.RefreshToken))
        {
            return null;
        }

        try
        {
            var tokenResponse = await RefreshTokenAsync(oauth.RefreshToken);

            if (string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                return null;
            }

            return new OAuthEntry
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? oauth.RefreshToken,
                ExpiresAtUnixMs = tokenResponse.GetExpiresAtUnixMs(),
                AccountId = oauth.AccountId
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

    private Task<AuthFlowResult> StartBrowserFlowAsync(CancellationToken cancellationToken)
    {
        var state = OAuthHelpers.GenerateRandomString();
        var codeVerifier = OAuthHelpers.GenerateCodeVerifier();
        var codeChallenge = OAuthHelpers.GenerateCodeChallenge(codeVerifier);

        // Build Anthropic OAuth authorization URL using their server-side callback
        var authUrl = OAuthHelpers.BuildUrl($"{AuthBaseUrl}/oauth/authorize", new Dictionary<string, string>
        {
            ["client_id"] = OAuthClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = OAuthRedirectUri,
            ["scope"] = "org:create_api_key user:profile user:inference",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        });

        if (!OAuthHelpers.OpenBrowser(authUrl))
        {
            return Task.FromResult<AuthFlowResult>(
                new AuthFlowResult.Failed("Failed to open browser. Please try API key authentication instead."));
        }

        // Return NeedsUserInput - the CLI will prompt user for the authorization code
        // Store codeVerifier in context so we can use it when exchanging the code
        return Task.FromResult<AuthFlowResult>(
            new AuthFlowResult.NeedsUserInput(
                "Enter the authorization code from the browser",
                "Authorization Code",
                async (code, ct) => await ExchangeCodeAsync(code, codeVerifier, ct)));
    }

    private async Task<AuthFlowResult> ExchangeCodeAsync(string rawCode, string codeVerifier, CancellationToken cancellationToken)
    {
        try
        {
            // Anthropic returns authorization response as "code#state" format
            // Extract both the code and state parts
            var parts = rawCode.Split('#');
            var code = parts[0];
            var state = parts.Length > 1 ? parts[1] : null;

            // Step 1: Exchange authorization code for OAuth tokens
            var requestBody = new
            {
                grant_type = "authorization_code",
                client_id = OAuthClientId,
                code = code,
                state = state,
                redirect_uri = OAuthRedirectUri,
                code_verifier = codeVerifier
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(TokenEndpoint, jsonContent, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new AuthFlowResult.Failed($"Token exchange failed: {json}");
            }

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);

            if (string.IsNullOrEmpty(tokenResponse?.AccessToken))
            {
                return new AuthFlowResult.Failed("Invalid token response from Anthropic");
            }

            // Extract account/org info from JWT claims
            var accountId = ExtractAccountId(tokenResponse.AccessToken);
            var orgId = ExtractOrgId(tokenResponse.AccessToken);

            // Step 2: Try to create an API key using the OAuth token
            // This is the key bypass - we use OAuth to CREATE an API key, then use that API key for API calls
            var apiKeyResult = await CreateApiKeyAsync(tokenResponse.AccessToken, orgId, cancellationToken);

            OAuthEntry entry;
            if (apiKeyResult?.ApiKey != null)
            {
                // Success: Store the API key (not OAuth token) for future use
                entry = new OAuthEntry
                {
                    AccessToken = apiKeyResult.ApiKey, // This is the created API key, NOT the OAuth token
                    RefreshToken = tokenResponse.RefreshToken ?? string.Empty,
                    ExpiresAtUnixMs = 0, // API keys don't expire
                    AccountId = accountId
                };
            }
            else
            {
                // Fallback: Store the OAuth token directly
                // This may not work for all API calls, but we can try
                Console.WriteLine("Note: Could not create API key. Using OAuth token directly (may have limited functionality).");
                entry = new OAuthEntry
                {
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken ?? string.Empty,
                    ExpiresAtUnixMs = tokenResponse.GetExpiresAtUnixMs(),
                    AccountId = accountId
                };
            }

            return new AuthFlowResult.Success(entry);
        }
        catch (Exception ex)
        {
            return new AuthFlowResult.Failed($"Failed to exchange code for tokens: {ex.Message}", ex);
        }
    }

    private async Task<ApiKeyResponse?> CreateApiKeyAsync(string oauthToken, string? orgId, CancellationToken cancellationToken)
    {
        // Try multiple possible endpoints for API key creation
        var endpoints = new[]
        {
            CreateApiKeyEndpoint, // console.anthropic.com/api/keys
            "https://console.anthropic.com/v1/api_keys",
            "https://api.anthropic.com/v1/api_keys"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                var createKeyRequest = new
                {
                    name = $"HPD-Agent-CLI-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", oauthToken);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                request.Headers.TryAddWithoutValidation("anthropic-beta", AnthropicBetaHeader);
                request.Headers.TryAddWithoutValidation("User-Agent", ClaudeCodeUserAgent);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(createKeyRequest),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.SendAsync(request, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<ApiKeyResponse>(json);
                    if (!string.IsNullOrEmpty(result?.ApiKey))
                    {
                        Console.WriteLine($"Successfully created API key via {endpoint}");
                        return result;
                    }
                }
            }
            catch
            {
                // Try next endpoint
            }
        }

        // No endpoint worked
        Console.WriteLine("Could not create API key via known endpoints. OAuth token will be used directly.");
        return null;
    }

    private async Task<string?> GetDefaultOrgIdAsync(string oauthToken, CancellationToken cancellationToken)
    {
        try
        {
            // Try to get user's organizations
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/organizations");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", oauthToken);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.TryAddWithoutValidation("User-Agent", ClaudeCodeUserAgent);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var orgs = JsonSerializer.Deserialize<OrganizationsResponse>(json);
            return orgs?.Data?.FirstOrDefault()?.Id;
        }
        catch
        {
            return null;
        }
    }

    private async Task<TokenResponse> RefreshTokenAsync(string refreshToken)
    {
        // Anthropic requires JSON body for token requests
        var requestBody = new
        {
            grant_type = "refresh_token",
            client_id = OAuthClientId,
            refresh_token = refreshToken
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(TokenEndpoint, jsonContent);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TokenResponse>(json)
            ?? throw new InvalidOperationException("Failed to deserialize token response");
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

        // Try common claim names for account/user ID
        var accountId = OAuthHelpers.GetJwtClaim(claims, "sub");
        if (!string.IsNullOrEmpty(accountId)) return accountId;

        accountId = OAuthHelpers.GetJwtClaim(claims, "account_id");
        if (!string.IsNullOrEmpty(accountId)) return accountId;

        accountId = OAuthHelpers.GetJwtClaim(claims, "user_id");
        return accountId;
    }

    private static string? ExtractOrgId(string accessToken)
    {
        var claims = OAuthHelpers.ParseJwtClaims(accessToken);
        if (claims == null) return null;

        // Try to get organization ID from JWT claims
        var orgId = OAuthHelpers.GetJwtClaim(claims, "org_id");
        if (!string.IsNullOrEmpty(orgId)) return orgId;

        orgId = OAuthHelpers.GetJwtClaim(claims, "organization_id");
        if (!string.IsNullOrEmpty(orgId)) return orgId;

        // Some JWTs use "aud" (audience) for org context
        var aud = OAuthHelpers.GetJwtClaim(claims, "aud");
        if (!string.IsNullOrEmpty(aud) && aud.StartsWith("org_")) return aud;

        return null;
    }
}

/// <summary>
/// Response from API key creation endpoint.
/// </summary>
internal class ApiKeyResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}

/// <summary>
/// Response from organizations list endpoint.
/// </summary>
internal class OrganizationsResponse
{
    [JsonPropertyName("data")]
    public List<Organization>? Data { get; set; }
}

/// <summary>
/// Organization info.
/// </summary>
internal class Organization
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

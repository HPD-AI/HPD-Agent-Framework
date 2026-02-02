using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HPD_Agent.CLI.Auth;

/// <summary>
/// Helper utilities for OAuth flows including PKCE, state generation, and browser launching.
/// </summary>
public static class OAuthHelpers
{
    /// <summary>
    /// Generates a cryptographically secure random string for use as OAuth state or PKCE verifier.
    /// </summary>
    /// <param name="length">The length of the string (default 32).</param>
    /// <returns>A URL-safe random string.</returns>
    public static string GenerateRandomString(int length = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Generates a PKCE code verifier.
    /// </summary>
    /// <returns>A cryptographically random code verifier.</returns>
    public static string GenerateCodeVerifier()
    {
        // PKCE spec recommends 43-128 characters
        return GenerateRandomString(32);
    }

    /// <summary>
    /// Generates a PKCE code challenge from a code verifier using SHA256.
    /// </summary>
    /// <param name="codeVerifier">The code verifier.</param>
    /// <returns>The base64url-encoded SHA256 hash of the verifier.</returns>
    public static string GenerateCodeChallenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Base64URL encodes bytes (URL-safe base64 without padding).
    /// </summary>
    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Base64URL decodes a string.
    /// </summary>
    public static byte[] Base64UrlDecode(string input)
    {
        var output = input
            .Replace('-', '+')
            .Replace('_', '/');

        // Add padding if needed
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }

        return Convert.FromBase64String(output);
    }

    /// <summary>
    /// Opens a URL in the default browser.
    /// </summary>
    public static bool OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a URL with query parameters.
    /// </summary>
    public static string BuildUrl(string baseUrl, Dictionary<string, string> parameters)
    {
        if (parameters.Count == 0)
        {
            return baseUrl;
        }

        var query = string.Join("&", parameters
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return $"{baseUrl}?{query}";
    }

    /// <summary>
    /// Parses JWT claims without validation (for extracting account IDs, etc.).
    /// WARNING: This does not verify the JWT signature - only use for non-security-critical data extraction.
    /// </summary>
    public static Dictionary<string, JsonElement>? ParseJwtClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            var payload = Base64UrlDecode(parts[1]);
            var json = Encoding.UTF8.GetString(payload);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a string claim from JWT claims.
    /// </summary>
    public static string? GetJwtClaim(Dictionary<string, JsonElement>? claims, params string[] claimNames)
    {
        if (claims == null) return null;

        foreach (var name in claimNames)
        {
            if (claims.TryGetValue(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
                if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
                {
                    var first = value[0];
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        return first.GetString();
                    }
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var id))
                    {
                        return id.GetString();
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Masks a token/key for display purposes, showing only the last few characters.
    /// </summary>
    public static string MaskToken(string? token, int visibleChars = 8)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "(empty)";
        }

        if (token.Length <= visibleChars)
        {
            return new string('•', token.Length);
        }

        var masked = new string('•', Math.Min(8, token.Length - visibleChars));
        return masked + token[^visibleChars..];
    }

    /// <summary>
    /// Formats a time span as a human-readable string.
    /// </summary>
    public static string FormatTimeRemaining(TimeSpan timeSpan)
    {
        if (timeSpan <= TimeSpan.Zero)
        {
            return "expired";
        }

        if (timeSpan.TotalDays >= 1)
        {
            var days = (int)timeSpan.TotalDays;
            return days == 1 ? "1 day" : $"{days} days";
        }

        if (timeSpan.TotalHours >= 1)
        {
            var hours = (int)timeSpan.TotalHours;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        if (timeSpan.TotalMinutes >= 1)
        {
            var minutes = (int)timeSpan.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        return "less than a minute";
    }
}

/// <summary>
/// HTTP client extensions for OAuth token exchange.
/// </summary>
public static class OAuthHttpExtensions
{
    /// <summary>
    /// Exchanges an authorization code for tokens.
    /// </summary>
    public static async Task<TokenResponse> ExchangeCodeForTokensAsync(
        this HttpClient httpClient,
        string tokenEndpoint,
        string code,
        string clientId,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        });

        var response = await httpClient.PostAsync(tokenEndpoint, content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException($"Token exchange failed: {response.StatusCode} - {json}");
        }

        return JsonSerializer.Deserialize<TokenResponse>(json)
               ?? throw new OAuthException("Failed to parse token response");
    }

    /// <summary>
    /// Refreshes an access token using a refresh token.
    /// </summary>
    public static async Task<TokenResponse> RefreshTokenAsync(
        this HttpClient httpClient,
        string tokenEndpoint,
        string refreshToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        });

        var response = await httpClient.PostAsync(tokenEndpoint, content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException($"Token refresh failed: {response.StatusCode} - {json}");
        }

        return JsonSerializer.Deserialize<TokenResponse>(json)
               ?? throw new OAuthException("Failed to parse token response");
    }
}

/// <summary>
/// OAuth token response from a token endpoint.
/// </summary>
public class TokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    /// <summary>
    /// Calculates the expiration timestamp from expires_in.
    /// </summary>
    public long GetExpiresAtUnixMs(int? defaultExpiresIn = 3600)
    {
        var expiresIn = ExpiresIn ?? defaultExpiresIn ?? 3600;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn * 1000);
    }
}

/// <summary>
/// Exception thrown during OAuth operations.
/// </summary>
public class OAuthException : Exception
{
    public OAuthException(string message) : base(message) { }
    public OAuthException(string message, Exception innerException) : base(message, innerException) { }
}

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace HPD.Agent;

/// <summary>
/// Represents connection information parsed from a connection string.
/// Supports format: "Provider=openrouter;AccessKey=sk-xxx;Model=gpt-4;Endpoint=https://..."
/// </summary>
public class ProviderConnectionInfo
{
    /// <summary>
    /// The provider key (e.g., "openrouter", "openai", "azure", "ollama")
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// The API key or access token
    /// </summary>
    public string? AccessKey { get; init; }

    /// <summary>
    /// The model name (e.g., "gpt-4", "google/gemini-2.5-pro")
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The API endpoint (optional, uses provider default if not specified)
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Tries to parse a connection string into ProviderConnectionInfo
    /// </summary>
    /// <param name="connectionString">Connection string to parse</param>
    /// <param name="info">Parsed connection info if successful</param>
    /// <returns>True if parsing succeeded, false otherwise</returns>
    public static bool TryParse(string? connectionString, [NotNullWhen(true)] out ProviderConnectionInfo? info)
    {
        info = null;

        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            // Use DbConnectionStringBuilder for robust parsing
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            // Provider is required
            if (!builder.ContainsKey("Provider"))
                return false;

            var provider = builder["Provider"].ToString();
            if (string.IsNullOrWhiteSpace(provider))
                return false;

            // Parse optional fields
            string? accessKey = builder.ContainsKey("AccessKey")
                ? builder["AccessKey"].ToString()
                : null;

            string? model = builder.ContainsKey("Model")
                ? builder["Model"].ToString()
                : null;

            Uri? endpoint = null;
            if (builder.ContainsKey("Endpoint"))
            {
                var endpointStr = builder["Endpoint"].ToString();
                if (!string.IsNullOrWhiteSpace(endpointStr))
                {
                    if (!Uri.TryCreate(endpointStr, UriKind.Absolute, out endpoint))
                        return false; // Invalid endpoint URL
                }
            }

            info = new ProviderConnectionInfo
            {
                Provider = provider,
                AccessKey = accessKey,
                Model = model,
                Endpoint = endpoint
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a connection string, throwing an exception if invalid
    /// </summary>
    /// <param name="connectionString">Connection string to parse</param>
    /// <returns>Parsed connection info</returns>
    /// <exception cref="ArgumentException">Thrown if connection string is invalid</exception>
    public static ProviderConnectionInfo Parse(string connectionString)
    {
        if (TryParse(connectionString, out var info))
            return info;

        throw new ArgumentException(
            $"Invalid connection string: '{connectionString}'. " +
            "Expected format: 'Provider=openrouter;AccessKey=sk-xxx;Model=gpt-4;Endpoint=https://...' " +
            "(AccessKey, Model, and Endpoint are optional)",
            nameof(connectionString));
    }
}

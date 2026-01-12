using System;
using Microsoft.Extensions.Configuration;

namespace HPD.Agent.Providers;

/// <summary>
/// Helper utilities for provider configuration, shared across all provider extension methods and AgentBuilder.
/// </summary>
public static class ProviderConfigurationHelper
{
    /// <summary>
    /// Resolves an API key from multiple sources in priority order:
    /// 1. Explicitly provided value
    /// 2. Environment variable: {PROVIDER}_API_KEY (uppercase)
    /// 3. Environment variable: {Provider}_API_KEY (capitalized)
    /// 4. Configuration (appsettings.json): "{provider}:ApiKey" or "{Provider}:ApiKey"
    /// </summary>
    /// <param name="explicitApiKey">Explicitly provided API key (highest priority)</param>
    /// <param name="providerKey">Provider key (e.g., "anthropic", "openai")</param>
    /// <param name="configuration">Optional configuration instance for appsettings.json lookup</param>
    /// <returns>The resolved API key, or null if not found</returns>
    /// <example>
    /// <code>
    /// // Extension methods (no config access)
    /// var apiKey = ProviderConfigurationHelper.ResolveApiKey(userKey, "anthropic");
    ///
    /// // AgentBuilder (with config access)
    /// var apiKey = ProviderConfigurationHelper.ResolveApiKey(userKey, "anthropic", _configuration);
    /// </code>
    /// </example>
    public static string? ResolveApiKey(string? explicitApiKey, string providerKey, IConfiguration? configuration = null)
    {
        // Priority 1: Explicit API key provided by user
        if (!string.IsNullOrWhiteSpace(explicitApiKey))
            return explicitApiKey;

        // Priority 2: Environment variable with uppercase provider key
        // Example: ANTHROPIC_API_KEY, OPENAI_API_KEY
        var upperEnvVar = Environment.GetEnvironmentVariable($"{providerKey.ToUpperInvariant()}_API_KEY");
        if (!string.IsNullOrWhiteSpace(upperEnvVar))
            return upperEnvVar;

        // Priority 3: Environment variable with capitalized provider key
        // Example: Anthropic_API_KEY, OpenAI_API_KEY
        var capitalizedKey = Capitalize(providerKey);
        var capitalizedEnvVar = Environment.GetEnvironmentVariable($"{capitalizedKey}_API_KEY");
        if (!string.IsNullOrWhiteSpace(capitalizedEnvVar))
            return capitalizedEnvVar;

        // Priority 4: Configuration (appsettings.json)
        if (configuration != null)
        {
            // Try lowercase first: "anthropic:ApiKey"
            var configKey = configuration[$"{providerKey}:ApiKey"];
            if (!string.IsNullOrWhiteSpace(configKey))
                return configKey;

            // Try capitalized: "Anthropic:ApiKey"
            configKey = configuration[$"{capitalizedKey}:ApiKey"];
            if (!string.IsNullOrWhiteSpace(configKey))
                return configKey;
        }

        // Not found
        return null;
    }

    /// <summary>
    /// Resolves an endpoint from multiple sources in priority order:
    /// 1. Explicitly provided value
    /// 2. Environment variable: {PROVIDER}_ENDPOINT (uppercase)
    /// 3. Environment variable: {Provider}_ENDPOINT (capitalized)
    /// 4. Configuration (appsettings.json): "{provider}:Endpoint" or "{Provider}:Endpoint"
    /// </summary>
    /// <param name="explicitEndpoint">Explicitly provided endpoint (highest priority)</param>
    /// <param name="providerKey">Provider key (e.g., "anthropic", "openai", "azure-ai")</param>
    /// <param name="configuration">Optional configuration instance for appsettings.json lookup</param>
    /// <returns>The resolved endpoint, or null if not found</returns>
    /// <example>
    /// <code>
    /// // Extension methods (no config access)
    /// var endpoint = ProviderConfigurationHelper.ResolveEndpoint(userEndpoint, "azure-ai");
    ///
    /// // AgentBuilder (with config access)
    /// var endpoint = ProviderConfigurationHelper.ResolveEndpoint(userEndpoint, "azure-ai", _configuration);
    /// </code>
    /// </example>
    public static string? ResolveEndpoint(string? explicitEndpoint, string providerKey, IConfiguration? configuration = null)
    {
        // Priority 1: Explicit endpoint provided by user
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
            return explicitEndpoint;

        // Priority 2: Environment variable with uppercase provider key
        // Example: AZURE_AI_ENDPOINT, ANTHROPIC_ENDPOINT
        var upperEnvVar = Environment.GetEnvironmentVariable($"{providerKey.ToUpperInvariant().Replace("-", "_")}_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(upperEnvVar))
            return upperEnvVar;

        // Priority 3: Environment variable with capitalized provider key
        // Example: AzureAi_ENDPOINT, Anthropic_ENDPOINT
        var capitalizedKey = Capitalize(providerKey);
        var capitalizedEnvVar = Environment.GetEnvironmentVariable($"{capitalizedKey.Replace("-", "")}_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(capitalizedEnvVar))
            return capitalizedEnvVar;

        // Priority 4: Configuration (appsettings.json)
        if (configuration != null)
        {
            // Try lowercase: "azure-ai:Endpoint"
            var configEndpoint = configuration[$"{providerKey}:Endpoint"];
            if (!string.IsNullOrWhiteSpace(configEndpoint))
                return configEndpoint;

            // Try capitalized: "AzureAi:Endpoint"
            configEndpoint = configuration[$"{capitalizedKey.Replace("-", "")}:Endpoint"];
            if (!string.IsNullOrWhiteSpace(configEndpoint))
                return configEndpoint;
        }

        // Not found
        return null;
    }

    /// <summary>
    /// Gets a user-friendly error message when an API key cannot be resolved.
    /// Provides guidance on multiple ways to provide the API key.
    /// </summary>
    /// <param name="providerKey">Provider key (e.g., "anthropic", "openai")</param>
    /// <param name="providerDisplayName">Display name for the provider (e.g., "Anthropic", "OpenAI")</param>
    /// <returns>A detailed error message with instructions</returns>
    public static string GetApiKeyErrorMessage(string providerKey, string providerDisplayName)
    {
        var upperKey = providerKey.ToUpperInvariant().Replace("-", "_");
        var capitalizedKey = Capitalize(providerKey).Replace("-", "");

        return $"API key is required for {providerDisplayName}. You can provide it in several ways:\n" +
               $"1. Explicitly: .With{capitalizedKey}(apiKey: \"your-key-here\", ...)\n" +
               $"2. Environment variable: {upperKey}_API_KEY\n" +
               $"3. appsettings.json: {{ \"{providerKey}\": {{ \"ApiKey\": \"your-key-here\" }} }}";
    }

    /// <summary>
    /// Gets a user-friendly error message when an endpoint cannot be resolved.
    /// Provides guidance on multiple ways to provide the endpoint.
    /// </summary>
    /// <param name="providerKey">Provider key (e.g., "azure-ai", "openai")</param>
    /// <param name="providerDisplayName">Display name for the provider (e.g., "Azure AI", "OpenAI")</param>
    /// <returns>A detailed error message with instructions</returns>
    public static string GetEndpointErrorMessage(string providerKey, string providerDisplayName)
    {
        var upperKey = providerKey.ToUpperInvariant().Replace("-", "_");
        var capitalizedKey = Capitalize(providerKey).Replace("-", "");

        return $"Endpoint is required for {providerDisplayName}. You can provide it in several ways:\n" +
               $"1. Explicitly: .With{capitalizedKey}(endpoint: \"your-endpoint-here\", ...)\n" +
               $"2. Environment variable: {upperKey}_ENDPOINT\n" +
               $"3. appsettings.json: {{ \"{providerKey}\": {{ \"Endpoint\": \"your-endpoint-here\" }} }}";
    }

    /// <summary>
    /// Capitalizes the first letter of a string.
    /// </summary>
    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}

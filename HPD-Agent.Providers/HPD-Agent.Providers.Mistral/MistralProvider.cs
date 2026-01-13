using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using Mistral.SDK;
using HPD.Agent;
using HPD.Agent.Providers;
using HPD.Agent.ErrorHandling;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Providers.Mistral;

/// <summary>
/// Mistral AI provider implementation using Mistral.SDK.
/// Supports all Mistral models including open-source and commercial variants.
/// </summary>
/// <remarks>
/// <para>
/// This provider uses the unofficial but community-maintained Mistral.SDK:
/// - Mistral.SDK for chat completions and embeddings
/// - Microsoft.Extensions.AI for IChatClient integration
/// </para>
/// <para>
/// Supports Mistral AI API endpoint: https://api.mistral.ai/v1
/// </para>
/// <para>
/// Authentication:
/// - API Key authentication (required)
/// </para>
/// </remarks>
internal class MistralProvider : IProviderFeatures
{
    public string ProviderKey => "mistral";
    public string DisplayName => "Mistral";

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Provider properly registers AOT-compatible deserializer in provider module")]
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        // Resolve API key using the helper utility (handles env vars, config, etc.)
        string? apiKey = ProviderConfigurationHelper.ResolveApiKey(config.ApiKey, "mistral");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                ProviderConfigurationHelper.GetApiKeyErrorMessage("mistral", "Mistral"));
        }

        string? modelName = config.ModelName;
        if (string.IsNullOrEmpty(modelName))
        {
            throw new InvalidOperationException("For Mistral, the ModelName must be configured.");
        }

        // Get typed config (may be null if not configured)
        var mistralConfig = config.GetTypedProviderConfig<MistralProviderConfig>();

        // Create Mistral client
        var client = new MistralClient(apiKey);
        IChatClient chatClient = client.Completions;

        // Apply client factory middleware if provided
        if (config.AdditionalProperties?.TryGetValue("ClientFactory", out var factoryObj) == true &&
            factoryObj is Func<IChatClient, IChatClient> clientFactory)
        {
            chatClient = clientFactory(chatClient);
        }

        return chatClient;
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new MistralErrorHandler();
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = true,
            SupportsVision = false,
            DocumentationUrl = "https://docs.mistral.ai/"
        };
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Provider properly registers AOT-compatible deserializer in provider module")]
    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(config.ModelName))
            errors.Add("Model name is required for Mistral");

        // Resolve API key using the helper utility
        string? apiKey = ProviderConfigurationHelper.ResolveApiKey(config.ApiKey, "mistral");

        if (string.IsNullOrEmpty(apiKey))
            errors.Add(ProviderConfigurationHelper.GetApiKeyErrorMessage("mistral", "Mistral"));

        // Validate Mistral-specific config if present
        var mistralConfig = config.GetTypedProviderConfig<MistralProviderConfig>();
        if (mistralConfig != null)
        {
            // Validate Temperature range (0.0 - 1.0)
            if (mistralConfig.Temperature.HasValue &&
                (mistralConfig.Temperature.Value < 0 || mistralConfig.Temperature.Value > 1))
            {
                errors.Add("Temperature must be between 0.0 and 1.0");
            }

            // Validate TopP range (0.0 - 1.0)
            if (mistralConfig.TopP.HasValue &&
                (mistralConfig.TopP.Value < 0 || mistralConfig.TopP.Value > 1))
            {
                errors.Add("TopP must be between 0.0 and 1.0");
            }

            // Validate ResponseFormat
            if (!string.IsNullOrEmpty(mistralConfig.ResponseFormat))
            {
                var validFormats = new[] { "text", "json_object" };
                if (!validFormats.Contains(mistralConfig.ResponseFormat))
                {
                    errors.Add("ResponseFormat must be one of: text, json_object");
                }
            }

            // Validate ToolChoice
            if (!string.IsNullOrEmpty(mistralConfig.ToolChoice))
            {
                var validChoices = new[] { "auto", "any", "none" };
                if (!validChoices.Contains(mistralConfig.ToolChoice))
                {
                    errors.Add("ToolChoice must be one of: auto, any, none");
                }
            }
        }

        return errors.Count > 0
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}

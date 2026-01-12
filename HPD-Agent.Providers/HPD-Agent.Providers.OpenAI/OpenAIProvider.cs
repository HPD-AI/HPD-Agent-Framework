using System;
using System.Collections.Generic;
using System.Linq;
using Azure.AI.OpenAI;
using Azure;
using OpenAI.Chat;
using HPD.Agent;
using HPD.Agent.Providers;
using HPD.Agent.ErrorHandling;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Providers.OpenAI;

/// <summary>
/// OpenAI provider implementation using the official OpenAI .NET SDK.
/// Supports both OpenAI and Azure OpenAI endpoints.
/// </summary>
internal class OpenAIProvider : IProviderFeatures
{
    public string ProviderKey => "openai";
    public string DisplayName => "OpenAI";

    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        // Resolve API key using the helper utility
        string? apiKey = ProviderConfigurationHelper.ResolveApiKey(config.ApiKey, "openai");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                ProviderConfigurationHelper.GetApiKeyErrorMessage("openai", "OpenAI"));
        }

        string? modelName = config.ModelName;
        if (string.IsNullOrEmpty(modelName))
        {
            throw new InvalidOperationException("For OpenAI, the ModelName must be configured.");
        }

        // Create ChatClient
        var chatClient = new ChatClient(modelName, apiKey);
        IChatClient client = chatClient.AsIChatClient();

        // Apply client factory middleware if provided
        if (config.AdditionalProperties?.TryGetValue("ClientFactory", out var factoryObj) == true &&
            factoryObj is Func<IChatClient, IChatClient> clientFactory)
        {
            client = clientFactory(client);
        }

        return client;
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new OpenAIErrorHandler();
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = true,
            SupportsVision = true,
            SupportsAudio = true,
            DefaulTMetadataWindow = 128000, // GPT-4 Turbo
            DocumentationUrl = "https://platform.openai.com/docs"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();

        // Validate API key
        string? apiKey = ProviderConfigurationHelper.ResolveApiKey(config.ApiKey, "openai");
        if (string.IsNullOrEmpty(apiKey))
            errors.Add(ProviderConfigurationHelper.GetApiKeyErrorMessage("openai", "OpenAI"));

        // Validate model name
        if (string.IsNullOrEmpty(config.ModelName))
            errors.Add("Model name is required for OpenAI");

        // Validate OpenAI-specific config if present
        var openAIConfig = config.GetTypedProviderConfig<OpenAIProviderConfig>();
        if (openAIConfig != null)
        {
            // Validate Temperature range
            if (openAIConfig.Temperature.HasValue && (openAIConfig.Temperature.Value < 0 || openAIConfig.Temperature.Value > 2))
            {
                errors.Add("Temperature must be between 0 and 2");
            }

            // Validate TopP range
            if (openAIConfig.TopP.HasValue && (openAIConfig.TopP.Value < 0 || openAIConfig.TopP.Value > 1))
            {
                errors.Add("TopP must be between 0 and 1");
            }

            // Validate FrequencyPenalty range
            if (openAIConfig.FrequencyPenalty.HasValue && (openAIConfig.FrequencyPenalty.Value < -2 || openAIConfig.FrequencyPenalty.Value > 2))
            {
                errors.Add("FrequencyPenalty must be between -2 and 2");
            }

            // Validate PresencePenalty range
            if (openAIConfig.PresencePenalty.HasValue && (openAIConfig.PresencePenalty.Value < -2 || openAIConfig.PresencePenalty.Value > 2))
            {
                errors.Add("PresencePenalty must be between -2 and 2");
            }

            // Validate StopSequences count
            if (openAIConfig.StopSequences != null && openAIConfig.StopSequences.Count > 4)
            {
                errors.Add("Maximum of 4 stop sequences allowed");
            }

            // Validate TopLogProbabilityCount range
            if (openAIConfig.TopLogProbabilityCount.HasValue && (openAIConfig.TopLogProbabilityCount.Value < 0 || openAIConfig.TopLogProbabilityCount.Value > 20))
            {
                errors.Add("TopLogProbabilityCount must be between 0 and 20");
            }

            // Validate ResponseFormat
            if (!string.IsNullOrEmpty(openAIConfig.ResponseFormat))
            {
                var validFormats = new[] { "text", "json_object", "json_schema" };
                if (!validFormats.Contains(openAIConfig.ResponseFormat))
                {
                    errors.Add("ResponseFormat must be one of: text, json_object, json_schema");
                }

                // Validate json_schema requirements
                if (openAIConfig.ResponseFormat == "json_schema")
                {
                    if (string.IsNullOrEmpty(openAIConfig.JsonSchemaName))
                    {
                        errors.Add("JsonSchemaName is required when ResponseFormat is json_schema");
                    }
                    if (string.IsNullOrEmpty(openAIConfig.JsonSchema))
                    {
                        errors.Add("JsonSchema is required when ResponseFormat is json_schema");
                    }
                }
            }

            // Validate ToolChoice
            if (!string.IsNullOrEmpty(openAIConfig.ToolChoice))
            {
                var validChoices = new[] { "auto", "none", "required" };
                if (!validChoices.Contains(openAIConfig.ToolChoice))
                {
                    errors.Add("ToolChoice must be one of: auto, none, required");
                }
            }

            // Validate ReasoningEffortLevel
            if (!string.IsNullOrEmpty(openAIConfig.ReasoningEffortLevel))
            {
                var validLevels = new[] { "low", "medium", "high", "minimal" };
                if (!validLevels.Contains(openAIConfig.ReasoningEffortLevel))
                {
                    errors.Add("ReasoningEffortLevel must be one of: low, medium, high, minimal");
                }
            }

            // Validate AudioVoice
            if (!string.IsNullOrEmpty(openAIConfig.AudioVoice))
            {
                var validVoices = new[] { "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse" };
                if (!validVoices.Contains(openAIConfig.AudioVoice))
                {
                    errors.Add("AudioVoice must be one of: alloy, ash, ballad, coral, echo, sage, shimmer, verse");
                }
            }

            // Validate AudioFormat
            if (!string.IsNullOrEmpty(openAIConfig.AudioFormat))
            {
                var validFormats = new[] { "wav", "mp3", "flac", "opus", "pcm16" };
                if (!validFormats.Contains(openAIConfig.AudioFormat))
                {
                    errors.Add("AudioFormat must be one of: wav, mp3, flac, opus, pcm16");
                }
            }

            // Validate ServiceTier
            if (!string.IsNullOrEmpty(openAIConfig.ServiceTier))
            {
                var validTiers = new[] { "auto", "default" };
                if (!validTiers.Contains(openAIConfig.ServiceTier))
                {
                    errors.Add("ServiceTier must be one of: auto, default");
                }
            }
        }

        return errors.Count > 0
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}

/// <summary>
/// Azure OpenAI provider implementation (traditional API key-based endpoints).
/// For modern Azure AI Projects/Foundry, use the AzureAI provider instead.
/// </summary>
internal class AzureOpenAIProvider : IProviderFeatures
{
    public string ProviderKey => "azure-openai";
    public string DisplayName => "Azure OpenAI (Traditional)";

    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        // Resolve endpoint
        string? endpoint = ProviderConfigurationHelper.ResolveEndpoint(config.Endpoint, "azure-openai");

        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException(
                ProviderConfigurationHelper.GetEndpointErrorMessage("azure-openai", "Azure OpenAI"));
        }

        // Resolve API key
        string? apiKey = ProviderConfigurationHelper.ResolveApiKey(config.ApiKey, "azure-openai");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                ProviderConfigurationHelper.GetApiKeyErrorMessage("azure-openai", "Azure OpenAI"));
        }

        string? modelName = config.ModelName;
        if (string.IsNullOrEmpty(modelName))
        {
            throw new InvalidOperationException("For Azure OpenAI, the ModelName (deployment name) must be configured.");
        }

        // Create Azure OpenAI client
        var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey)
        );

        IChatClient client = azureClient.GetChatClient(modelName).AsIChatClient();

        // Apply client factory middleware if provided
        if (config.AdditionalProperties?.TryGetValue("ClientFactory", out var factoryObj) == true &&
            factoryObj is Func<IChatClient, IChatClient> clientFactory)
        {
            client = clientFactory(client);
        }

        return client;
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new OpenAIErrorHandler(); // Same error format as OpenAI
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = true,
            SupportsVision = true,
            DefaulTMetadataWindow = 128000,
            DocumentationUrl = "https://learn.microsoft.com/azure/ai-services/openai/"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();

        // Validate endpoint
        string? endpoint = ProviderConfigurationHelper.ResolveEndpoint(config.Endpoint, "azure-openai");
        if (string.IsNullOrEmpty(endpoint))
            errors.Add(ProviderConfigurationHelper.GetEndpointErrorMessage("azure-openai", "Azure OpenAI"));

        // Validate API key
        string? apiKey = ProviderConfigurationHelper.ResolveApiKey(config.ApiKey, "azure-openai");
        if (string.IsNullOrEmpty(apiKey))
            errors.Add(ProviderConfigurationHelper.GetApiKeyErrorMessage("azure-openai", "Azure OpenAI"));

        // Validate model name
        if (string.IsNullOrEmpty(config.ModelName))
            errors.Add("Model name (deployment name) is required for Azure OpenAI");

        // Validate OpenAI-specific config if present (same validation as OpenAI)
        var openAIConfig = config.GetTypedProviderConfig<OpenAIProviderConfig>();
        if (openAIConfig != null)
        {
            // Validate Temperature range
            if (openAIConfig.Temperature.HasValue && (openAIConfig.Temperature.Value < 0 || openAIConfig.Temperature.Value > 2))
            {
                errors.Add("Temperature must be between 0 and 2");
            }

            // Validate TopP range
            if (openAIConfig.TopP.HasValue && (openAIConfig.TopP.Value < 0 || openAIConfig.TopP.Value > 1))
            {
                errors.Add("TopP must be between 0 and 1");
            }

            // Validate FrequencyPenalty range
            if (openAIConfig.FrequencyPenalty.HasValue && (openAIConfig.FrequencyPenalty.Value < -2 || openAIConfig.FrequencyPenalty.Value > 2))
            {
                errors.Add("FrequencyPenalty must be between -2 and 2");
            }

            // Validate PresencePenalty range
            if (openAIConfig.PresencePenalty.HasValue && (openAIConfig.PresencePenalty.Value < -2 || openAIConfig.PresencePenalty.Value > 2))
            {
                errors.Add("PresencePenalty must be between -2 and 2");
            }

            // Add other validations as needed...
        }

        return errors.Count > 0
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}

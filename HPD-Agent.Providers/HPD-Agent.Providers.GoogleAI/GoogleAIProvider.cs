using System;
using System.Collections.Generic;
using GenerativeAI;
using GenerativeAI.Microsoft;
using HPD.Agent;
using HPD.Agent.Providers;
using HPD.Agent.ErrorHandling;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Providers.GoogleAI;

internal class GoogleAIProvider : IProviderFeatures
{
    public string ProviderKey => "google-ai";
    public string DisplayName => "Google AI (Gemini)";

    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrEmpty(config.ApiKey))
        {
            throw new InvalidOperationException("API Key is required for GoogleAI provider.");
        }

        return new GenerativeAIChatClient(config.ApiKey, config.ModelName);
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new GoogleAIErrorHandler();
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
            DocumentationUrl = "https://ai.google.dev/docs"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            return ProviderValidationResult.Failure("API key is required for Google AI");

        if (string.IsNullOrEmpty(config.ModelName))
            return ProviderValidationResult.Failure("Model name is required");

        return ProviderValidationResult.Success();
    }
}

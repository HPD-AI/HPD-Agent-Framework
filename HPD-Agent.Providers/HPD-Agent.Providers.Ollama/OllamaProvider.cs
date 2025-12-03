using System;
using System.Collections.Generic;
using OllamaSharp;
using HPD.Agent;
using HPD.Agent.Providers;
using HPD.Agent.ErrorHandling;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Providers.Ollama;

internal class OllamaProvider : IProviderFeatures
{
    public string ProviderKey => "ollama";
    public string DisplayName => "Ollama";

    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        var endpoint = string.IsNullOrEmpty(config.Endpoint) ? new Uri("http://localhost:11434") : new Uri(config.Endpoint);
        return new OllamaApiClient(endpoint, config.ModelName);
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new OllamaErrorHandler();
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = false, // Ollama function calling is model-dependent and not standardized
            SupportsVision = true,
            DocumentationUrl = "https://ollama.com/"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        if (string.IsNullOrEmpty(config.ModelName))
            return ProviderValidationResult.Failure("Model name is required for Ollama");

        if (!string.IsNullOrEmpty(config.Endpoint) && !Uri.IsWellFormedUriString(config.Endpoint, UriKind.Absolute))
            return ProviderValidationResult.Failure("Endpoint must be a valid, absolute URI.");

        return ProviderValidationResult.Success();
    }
}

using System;
using System.Collections.Generic;
using Mistral.SDK;
using HPD.Providers.Core;
using HPD.Providers.Core;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.Mistral;

internal class MistralProvider : IProviderFeatures
{
    public string ProviderKey => "mistral";
    public string DisplayName => "Mistral";


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling;
    /// <summary>
    /// Creates an IChatClient that sends chat completions to the Mistral service using the provided configuration.
    /// </summary>
    /// <param name="config">Provider configuration containing the Mistral API key and model selection; the API key must be set.</param>
    /// <param name="services">Optional service provider for dependency resolution (may be null).</param>
    /// <returns>An IChatClient configured to use Mistral completions.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="config"/> does not contain an API key.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("Mistral requires an API key");

        return new MistralClient(config.ApiKey).Completions;
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

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            return ProviderValidationResult.Failure("API key is required for Mistral");

        if (string.IsNullOrEmpty(config.ModelName))
            return ProviderValidationResult.Failure("Model name is required");

        return ProviderValidationResult.Success();
    }
}
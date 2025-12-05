using System;
using System.Collections.Generic;
using GenerativeAI;
using GenerativeAI.Microsoft;
using HPD.Providers.Core;
using HPD.Providers.Core;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.GoogleAI;

internal class GoogleAIProvider : IProviderFeatures
{
    public string ProviderKey => "google-ai";
    public string DisplayName => "Google AI (Gemini)";


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling;
    /// <summary>
    /// Creates an IChatClient configured for Google AI (Gemini).
    /// </summary>
    /// <param name="config">Provider configuration; must include a non-empty <c>ApiKey</c> and a <c>ModelName</c>.</param>
    /// <returns>An <see cref="IChatClient"/> configured with the provided API key and model name.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <c>config.ApiKey</c> is null or empty.</exception>
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
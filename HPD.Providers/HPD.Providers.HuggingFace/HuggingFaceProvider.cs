using System;
using System.Collections.Generic;
using HuggingFace;
using HPD.Providers.Core;
using HPD.Providers.Core;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.HuggingFace;

internal class HuggingFaceProvider : IProviderFeatures
{
    public string ProviderKey => "huggingface";
    public string DisplayName => "Hugging Face";


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling;
    /// <summary>
    /// Creates a Hugging Face chat client configured from the given provider settings.
    /// </summary>
    /// <param name="config">Provider configuration containing the API key and model settings; <see cref="ProviderConfig.ApiKey"/> must be non-empty.</param>
    /// <returns>An <see cref="IChatClient"/> instance configured to use the Hugging Face API.</returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="ProviderConfig.ApiKey"/> is null or empty.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("Hugging Face requires an API key (HF_TOKEN)");

        return new HuggingFaceClient(config.ApiKey);
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new HuggingFaceErrorHandler();
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = false, // Generally not supported by HF Inference API
            SupportsVision = false,
            DocumentationUrl = "https://huggingface.co/docs/api-inference/index"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            return ProviderValidationResult.Failure("API key (HF_TOKEN) is required for Hugging Face");

        if (string.IsNullOrEmpty(config.ModelName))
            return ProviderValidationResult.Failure("Model name (repository ID) is required");

        return ProviderValidationResult.Success();
    }
}
using System;
using System.Collections.Generic;
using HuggingFace;
using HPD.Agent;
using HPD.Agent.Providers;
using HPD.Agent.ErrorHandling;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Providers.HuggingFace;

internal class HuggingFaceProvider : IProviderFeatures
{
    public string ProviderKey => "huggingface";
    public string DisplayName => "Hugging Face";

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

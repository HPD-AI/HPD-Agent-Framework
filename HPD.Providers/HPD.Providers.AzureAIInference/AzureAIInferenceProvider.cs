using System;
using System.Collections.Generic;
using Azure;
using Azure.AI.Inference;
using HPD.Providers.Core;
using HPD.Providers.Core;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.AzureAIInference;

internal class AzureAIInferenceProvider : IProviderFeatures
{
    public string ProviderKey => "azure-ai-inference";
    public string DisplayName => "Azure AI Inference";


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling;
    /// <summary>
    /// Create an <see cref="IChatClient"/> configured to use Azure AI Inference.
    /// </summary>
    /// <param name="config">Provider configuration used to resolve model name, endpoint, and API key. Endpoint and API key are taken from <paramref name="config"/> first, then from <c>config.AdditionalProperties</c>, and finally from the environment variables <c>AZURE_AI_INFERENCE_ENDPOINT</c> and <c>AZURE_AI_INFERENCE_API_KEY</c>.</param>
    /// <param name="services">Optional service provider (not required by this implementation).</param>
    /// <returns>An <see cref="IChatClient"/> bound to the resolved endpoint and model.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the endpoint or API key cannot be resolved from configuration or environment.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        string? endpoint = config.Endpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            if (config.AdditionalProperties?.TryGetValue("Endpoint", out var endpointObj) == true)
            {
                endpoint = endpointObj?.ToString();
            }
        }
        endpoint ??= Environment.GetEnvironmentVariable("AZURE_AI_INFERENCE_ENDPOINT");

        string? apiKey = config.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            if (config.AdditionalProperties?.TryGetValue("ApiKey", out var apiKeyObj) == true)
            {
                apiKey = apiKeyObj?.ToString();
            }
        }
        apiKey ??= Environment.GetEnvironmentVariable("AZURE_AI_INFERENCE_API_KEY");

        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException("For AzureAIInference, the Endpoint must be configured.");
        }
        
        if (string.IsNullOrEmpty(apiKey))
        {
             throw new InvalidOperationException("For AzureAIInference, the ApiKey must be configured.");
        }

        var client = new ChatCompletionsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        return client.AsIChatClient(config.ModelName);
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new AzureAIInferenceErrorHandler();
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
            DocumentationUrl = "https://learn.microsoft.com/en-us/azure/ai-studio/how-to/deploy-models-inference"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(config.ModelName))
            errors.Add("Model name is required for Azure AI Inference");

        string? endpoint = config.Endpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            if (config.AdditionalProperties?.TryGetValue("Endpoint", out var endpointObj) == true)
            {
                endpoint = endpointObj?.ToString();
            }
        }
        endpoint ??= Environment.GetEnvironmentVariable("AZURE_AI_INFERENCE_ENDPOINT");

        string? apiKey = config.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            if (config.AdditionalProperties?.TryGetValue("ApiKey", out var apiKeyObj) == true)
            {
                apiKey = apiKeyObj?.ToString();
            }
        }
        apiKey ??= Environment.GetEnvironmentVariable("AZURE_AI_INFERENCE_API_KEY");

        if (string.IsNullOrEmpty(endpoint))
            errors.Add("Endpoint is required. Configure it in ProviderConfig, AdditionalProperties, or via the AZURE_AI_INFERENCE_ENDPOINT environment variable.");
        
        if (string.IsNullOrEmpty(apiKey))
            errors.Add("API Key is required. Configure it in ProviderConfig, AdditionalProperties, or via the AZURE_AI_INFERENCE_API_KEY environment variable.");

        return errors.Count > 0 
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}
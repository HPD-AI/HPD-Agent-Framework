using System;
using System.Collections.Generic;
using System.Linq;
using Azure.AI.OpenAI;
using Azure;
using OpenAI.Chat;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.OpenAI;

internal class OpenAIProvider : IProviderFeatures
{
    public string ProviderKey => "openai";
    public string DisplayName => "OpenAI";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling |
        ProviderCapabilities.Vision |
        ProviderCapabilities.Audio |
        ProviderCapabilities.Embeddings;

    /// <summary>
    /// Creates an IChatClient configured for the specified OpenAI model and API key.
    /// </summary>
    /// <param name="config">Provider configuration containing the API key and model/deployment name; the API key must be provided and the model name selects the target model or deployment.</param>
    /// <param name="services">Optional service provider for additional dependencies (may be null).</param>
    /// <returns>An IChatClient instance configured to communicate with OpenAI using the provided model and API key.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="config"/>.ApiKey is null or empty.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("OpenAI requires an API key");

        // ChatClient from OpenAI.Chat can be cast to IChatClient via extension method
        var chatClient = new ChatClient(config.ModelName, config.ApiKey);
        return chatClient.AsIChatClient();
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
            DefaultContextWindow = 128000, // GPT-4 Turbo
            DocumentationUrl = "https://platform.openai.com/docs"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            return ProviderValidationResult.Failure("API key is required for OpenAI");

        if (string.IsNullOrEmpty(config.ModelName))
            return ProviderValidationResult.Failure("Model name is required");

        return ProviderValidationResult.Success();
    }
}

// Azure OpenAI variant
internal class AzureOpenAIProvider : IProviderFeatures
{
    public string ProviderKey => "azure-openai";
    public string DisplayName => "Azure OpenAI";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling |
        ProviderCapabilities.Vision |
        ProviderCapabilities.Embeddings;

    /// <summary>
    /// Creates an IChatClient connected to the specified Azure OpenAI deployment.
    /// </summary>
    /// <param name="config">Provider configuration containing the Azure endpoint (Endpoint), API key (ApiKey), and model/deployment name (ModelName).</param>
    /// <returns>An IChatClient for the configured Azure OpenAI model/deployment.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="config"/>.Endpoint is null or empty ("Azure OpenAI requires an endpoint") or when <paramref name="config"/>.ApiKey is null or empty ("Azure OpenAI requires an API key").</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        if (string.IsNullOrEmpty(config.Endpoint))
            throw new ArgumentException("Azure OpenAI requires an endpoint");

        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("Azure OpenAI requires an API key");

        var azureClient = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(config.Endpoint),
            new AzureKeyCredential(config.ApiKey)
        );

        return azureClient.GetChatClient(config.ModelName).AsIChatClient();
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new OpenAIErrorHandler(); // Same error format
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
            DefaultContextWindow = 128000,
            DocumentationUrl = "https://learn.microsoft.com/azure/ai-services/openai/"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(config.Endpoint))
            errors.Add("Endpoint is required for Azure OpenAI");

        if (string.IsNullOrEmpty(config.ApiKey))
            errors.Add("API key is required for Azure OpenAI");

        if (string.IsNullOrEmpty(config.ModelName))
            errors.Add("Model name (deployment name) is required");

        return errors.Any() 
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}
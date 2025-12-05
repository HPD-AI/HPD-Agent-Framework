using System;
using System.Collections.Generic;
using Amazon;
using Amazon.BedrockRuntime;
using HPD.Providers.Core;
using HPD.Providers.Core;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.Bedrock;

internal class BedrockProvider : IProviderFeatures
{
    public string ProviderKey => "bedrock";
    public string DisplayName => "AWS Bedrock";


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling;
    /// <summary>
    /// Create an IChatClient for AWS Bedrock based on the supplied provider configuration.
    /// </summary>
    /// <param name="config">Provider configuration. Requires <c>ModelName</c>; may include AdditionalProperties keys: <c>Region</c>, <c>AccessKeyId</c>, and <c>SecretAccessKey</c> which override the corresponding environment variables.</param>
    /// <param name="services">Optional service provider (not used by this implementation).</param>
    /// <returns>An <see cref="IChatClient"/> configured to communicate with the specified Bedrock model.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the AWS region is not provided via AdditionalProperties["Region"] or the <c>AWS_REGION</c> environment variable.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        string region = null;
        if (config.AdditionalProperties?.TryGetValue("Region", out var regionObj) == true)
        {
            region = regionObj?.ToString();
        }
        region ??= Environment.GetEnvironmentVariable("AWS_REGION");

        string accessKey = null;
        if (config.AdditionalProperties?.TryGetValue("AccessKeyId", out var accessKeyObj) == true)
        {
            accessKey = accessKeyObj?.ToString();
        }
        accessKey ??= Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");

        string secretKey = null;
        if (config.AdditionalProperties?.TryGetValue("SecretAccessKey", out var secretKeyObj) == true)
        {
            secretKey = secretKeyObj?.ToString();
        }
        secretKey ??= Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            
        if (string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException(
                "For the Bedrock provider, the AWS Region must be configured via AdditionalProperties or the AWS_REGION environment variable.");
        }

        IAmazonBedrockRuntime bedrockRuntime;
        var regionEndpoint = RegionEndpoint.GetBySystemName(region);
        
        if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
        {
            bedrockRuntime = new AmazonBedrockRuntimeClient(accessKey, secretKey, regionEndpoint);
        }
        else
        {
            bedrockRuntime = new AmazonBedrockRuntimeClient(regionEndpoint);
        }

        return bedrockRuntime.AsIChatClient(config.ModelName);
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new BedrockErrorHandler();
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = true, // Bedrock supports tool use
            SupportsVision = true,
            DocumentationUrl = "https://aws.amazon.com/bedrock/"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(config.ModelName))
            errors.Add("Model name is required for AWS Bedrock");

        string region = null;
        if (config.AdditionalProperties?.TryGetValue("Region", out var regionObj) == true)
        {
            region = regionObj?.ToString();
        }
        region ??= Environment.GetEnvironmentVariable("AWS_REGION");

        if (string.IsNullOrEmpty(region))
            errors.Add("AWS Region is required for Bedrock. Configure it in AdditionalProperties or via the AWS_REGION environment variable.");

        return errors.Count > 0 
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}
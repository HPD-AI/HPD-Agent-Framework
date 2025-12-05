using System;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntimeGenAI;
using HPD.Providers.Core;
using HPD.Providers.Core;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

namespace HPD.Providers.OnnxRuntime;

internal class OnnxRuntimeProvider : IProviderFeatures
{
    public string ProviderKey => "onnx-runtime";
    public string DisplayName => "ONNX Runtime";


    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling;
    /// <summary>
    /// Creates an IChatClient configured to use an ONNX Runtime model.
    /// </summary>
    /// <param name="config">Provider configuration. Must provide the model path via AdditionalProperties["ModelPath"] or the ONNX_MODEL_PATH environment variable. Optional AdditionalProperties keys: "StopSequences" (IList&lt;string&gt;), "EnableCaching" (bool), and "PromptFormatter" (Func&lt;IEnumerable&lt;ChatMessage&gt;, ChatOptions?, string&gt;).</param>
    /// <param name="services">Optional service provider for dependency resolution (may be null).</param>
    /// <returns>An IChatClient instance configured to use the specified ONNX model and options.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no model path is configured.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        string modelPath = null;
        if (config.AdditionalProperties?.TryGetValue("ModelPath", out var modelPathObj) == true)
        {
            modelPath = modelPathObj?.ToString();
        }
        modelPath ??= Environment.GetEnvironmentVariable("ONNX_MODEL_PATH");

        if (string.IsNullOrEmpty(modelPath))
        {
            throw new InvalidOperationException("For the OnnxRuntime provider, the ModelPath must be configured.");
        }

        IList<string> stopSequences = null;
        if (config.AdditionalProperties?.TryGetValue("StopSequences", out var stopSequencesObj) == true && stopSequencesObj is IList<string> sequences)
        {
            stopSequences = sequences;
        }

        bool enableCaching = false;
        if (config.AdditionalProperties?.TryGetValue("EnableCaching", out var enableCachingObj) == true && enableCachingObj is bool caching)
        {
            enableCaching = caching;
        }

        Func<IEnumerable<ChatMessage>, ChatOptions?, string> promptFormatter = null;
        if (config.AdditionalProperties?.TryGetValue("PromptFormatter", out var promptFormatterObj) == true && promptFormatterObj is Func<IEnumerable<ChatMessage>, ChatOptions?, string> formatter)
        {
            promptFormatter = formatter;
        }

        var options = new OnnxRuntimeGenAIChatClientOptions
        {
            StopSequences = stopSequences,
            EnableCaching = enableCaching,
            PromptFormatter = promptFormatter
        };
        
        return new OnnxRuntimeGenAIChatClient(modelPath, options);
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new OnnxRuntimeErrorHandler();
    }

    public ProviderMetadata GetMetadata()
    {
        return new ProviderMetadata
        {
            ProviderKey = ProviderKey,
            DisplayName = DisplayName,
            SupportsStreaming = true,
            SupportsFunctionCalling = false,
            SupportsVision = false,
            DocumentationUrl = "https://onnxruntime.ai/docs/genai/"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        var errors = new List<string>();
        string modelPath = null;
        if (config.AdditionalProperties?.TryGetValue("ModelPath", out var modelPathObj) == true)
        {
            modelPath = modelPathObj?.ToString();
        }
        modelPath ??= Environment.GetEnvironmentVariable("ONNX_MODEL_PATH");

        if (string.IsNullOrEmpty(modelPath))
            errors.Add("ModelPath is required. Configure it in AdditionalProperties or via the ONNX_MODEL_PATH environment variable.");

        return errors.Count > 0 
            ? ProviderValidationResult.Failure(errors.ToArray())
            : ProviderValidationResult.Success();
    }
}
using FluentValidation;
using System;
using System.Linq;

public class AgentConfigValidator : AbstractValidator<AgentConfig>
{
    public AgentConfigValidator()
    {
        // Basic configuration validation
        RuleFor(config => config.Name)
            .NotEmpty()
            .WithMessage("Agent name must not be empty.")
            .Length(1, 100)
            .WithMessage("Agent name must be between 1 and 100 characters.");

        RuleFor(config => config.MaxFunctionCallTurns)
            .GreaterThan(0)
            .LessThanOrEqualTo(50)
            .WithMessage("MaxFunctionCallTurns must be between 1 and 50.");

        // Provider validation - ensure a provider is configured
        RuleFor(config => config.Provider)
            .NotNull()
            .WithMessage("A provider must be configured for the agent.")
            .DependentRules(() =>
            {
                RuleFor(config => config.Provider!.ModelName)
                    .NotEmpty()
                    .WithMessage("Provider model name must be specified.");

                // Provider-specific validation
                When(config => config.Provider!.Provider == ChatProvider.AzureOpenAI, () =>
                {
                    RuleFor(config => config.Provider!.Endpoint)
                        .NotEmpty()
                        .WithMessage("Azure OpenAI requires an endpoint URL.")
                        .Must(BeValidUri)
                        .WithMessage("Azure OpenAI endpoint must be a valid URI.");
                });

                When(config => config.Provider!.Provider == ChatProvider.Ollama, () =>
                {
                    RuleFor(config => config.Provider!.ModelName)
                        .Must(modelName => !string.IsNullOrEmpty(modelName) && !modelName.Contains("/"))
                        .WithMessage("Ollama model name should not contain '/' characters.");
                });

                When(config => config.Provider!.Endpoint != null, () =>
                {
                    RuleFor(config => config.Provider!.Endpoint)
                        .Must(BeValidUri)
                        .WithMessage("Provider endpoint must be a valid URI.");
                });
            });

        // Memory configuration validation
        When(config => config.InjectedMemory != null, () =>
        {
            RuleFor(config => config.InjectedMemory!.MaxTokens)
                .GreaterThan(0)
                .LessThanOrEqualTo(100000)
                .WithMessage("InjectedMemory MaxTokens must be between 1 and 100,000.");

            RuleFor(config => config.InjectedMemory!.AutoEvictionThreshold)
                .InclusiveBetween(50, 95)
                .WithMessage("AutoEvictionThreshold must be between 50 and 95 percent.");

            RuleFor(config => config.InjectedMemory!.StorageDirectory)
                .NotEmpty()
                .WithMessage("InjectedMemory StorageDirectory must be specified.")
                .Must(BeValidPath)
                .WithMessage("InjectedMemory StorageDirectory must be a valid path.");
        });

        // MCP configuration validation
        When(config => config.Mcp != null && !string.IsNullOrEmpty(config.Mcp.ManifestPath), () =>
        {
            RuleFor(config => config.Mcp!.ManifestPath)
                .Must(BeValidPath)
                .WithMessage("MCP ManifestPath must be a valid file path.");
        });

        // Web search configuration validation
        When(config => config.WebSearch != null, () =>
        {
            When(config => config.WebSearch!.Tavily != null, () =>
            {
                RuleFor(config => config.WebSearch!.Tavily!.ApiKey)
                    .NotEmpty()
                    .WithMessage("Tavily API key is required when Tavily is configured.");
            });

            When(config => config.WebSearch!.Brave != null, () =>
            {
                RuleFor(config => config.WebSearch!.Brave!.ApiKey)
                    .NotEmpty()
                    .WithMessage("Brave API key is required when Brave is configured.");
            });

            When(config => config.WebSearch!.Bing != null, () =>
            {
                RuleFor(config => config.WebSearch!.Bing!.ApiKey)
                    .NotEmpty()
                    .WithMessage("Bing API key is required when Bing is configured.");
            });
        });

        // Error handling configuration validation
        When(config => config.ErrorHandling != null, () =>
        {
            RuleFor(config => config.ErrorHandling!.MaxRetries)
                .InclusiveBetween(0, 10)
                .WithMessage("ErrorHandling MaxRetries must be between 0 and 10.");
        });

        // Cross-configuration validation rules
        RuleFor(config => config)
            .Must(HaveValidProviderModelCombination)
            .WithMessage("The specified model is not supported by the selected provider.")
            .Must(HaveReasonableResourceLimits)
            .WithMessage("Resource limits (MaxTokens, MaxFunctionCallTurns) may be too high for stable operation.");
    }

    private static bool BeValidUri(string? uri)
    {
        return !string.IsNullOrEmpty(uri) && Uri.TryCreate(uri, UriKind.Absolute, out _);
    }

    private static bool BeValidPath(string path)
    {
        try
        {
            // Basic path validation - avoid path traversal and null characters
            return !string.IsNullOrWhiteSpace(path) &&
                   path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) == -1 &&
                   !path.Contains("..") &&
                   path.Length < 260; // Windows path limit
        }
        catch
        {
            return false;
        }
    }

    private static bool HaveValidProviderModelCombination(AgentConfig config)
    {
        if (config.Provider == null) return true; // Already validated above

        return config.Provider.Provider switch
        {
            ChatProvider.OpenAI => IsValidOpenAIModel(config.Provider.ModelName),
            ChatProvider.OpenRouter => IsValidOpenRouterModel(config.Provider.ModelName),
            ChatProvider.AzureOpenAI => IsValidAzureModel(config.Provider.ModelName),
            ChatProvider.Ollama => IsValidOllamaModel(config.Provider.ModelName),
            _ => true // Unknown providers are allowed
        };
    }

    private static bool IsValidOpenAIModel(string modelName)
    {
    // OpenAI now has many models; accept any non-empty model name
    return !string.IsNullOrEmpty(modelName);
    }

    private static bool IsValidOpenRouterModel(string modelName)
    {
    // OpenRouter accepts many model formats, be more lenient
    return !string.IsNullOrEmpty(modelName) && modelName.Length > 3;
    }

    private static bool IsValidAzureModel(string modelName)
    {
    // Azure model names are deployment names, can be anything
    return !string.IsNullOrEmpty(modelName);
    }

    private static bool IsValidOllamaModel(string modelName)
    {
    // Ollama models typically don't have slashes in local names
    return !string.IsNullOrEmpty(modelName) && modelName.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.');
    }

    private static bool HaveReasonableResourceLimits(AgentConfig config)
    {
        // Check if the combination of settings might cause issues
        var maxTokens = config.InjectedMemory?.MaxTokens ?? 0;
        var maxFunctionCalls = config.MaxFunctionCallTurns;
        var maxHistory = config.HistoryReduction?.TargetMessageCount ?? 20;

        // Warn if total potential token usage is very high
        var estimatedMaxTokens = maxTokens + (maxHistory * 500) + (maxFunctionCalls * 200);
        return estimatedMaxTokens < 200000; // Reasonable upper limit
    }
}
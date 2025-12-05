using System;
using System.Collections.Generic;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using HPD.Providers.Core;
using Microsoft.Extensions.AI;

// Aliases to avoid namespace conflicts
using AnthropicChatOptionsExtensions = Anthropic.SDK.Extensions.ChatOptionsExtensions;
using AnthropicSkill = Anthropic.SDK.Messaging.Skill;

namespace HPD.Providers.Anthropic;

internal class AnthropicProvider : IProviderFeatures
{
    public string ProviderKey => "anthropic";
    public string DisplayName => "Anthropic (Claude)";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Chat |
        ProviderCapabilities.Streaming |
        ProviderCapabilities.FunctionCalling |
        ProviderCapabilities.Vision;

    /// <summary>
    /// Creates and returns an Anthropic chat client configured from the given provider config.
    /// </summary>
    /// <param name="config">Provider configuration containing the Anthropic API key and optional provider-specific settings.</param>
    /// <param name="services">Optional service provider for resolving dependencies (not used by this implementation).</param>
    /// <returns>The configured IChatClient for Anthropic Messages.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="config"/> does not contain an API key.</exception>
    public IChatClient CreateChatClient(ProviderConfig config, IServiceProvider? services = null)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new ArgumentException("Anthropic requires an API key");

        // Apply provider-specific configuration if available
        var anthropicConfig = config.GetTypedProviderConfig<AnthropicProviderConfig>();
        if (anthropicConfig != null)
        {
            ApplyProviderConfig(config, anthropicConfig);
        }

        var anthropicClient = new AnthropicClient(config.ApiKey);
        var chatClient = anthropicClient.Messages;

        return chatClient;
    }

    /// <summary>
    /// Applies AnthropicProviderConfig to the ProviderConfig's DefaultChatOptions.
    /// This is called during CreateChatClient to configure Anthropic-specific features.
    /// </summary>
    private void ApplyProviderConfig(ProviderConfig config, AnthropicProviderConfig anthropicConfig)
    {
        var chatOptions = config.DefaultChatOptions ?? new ChatOptions();

        // Apply sampling parameters
        if (anthropicConfig.MaxTokens > 0)
            chatOptions.MaxOutputTokens = anthropicConfig.MaxTokens;

        if (anthropicConfig.Temperature.HasValue)
            chatOptions.Temperature = anthropicConfig.Temperature.Value;

        if (anthropicConfig.TopP.HasValue)
            chatOptions.TopP = anthropicConfig.TopP.Value;

        if (anthropicConfig.StopSequences is { Count: > 0 })
            chatOptions.StopSequences = anthropicConfig.StopSequences;

        // Apply extended thinking if configured
        if (anthropicConfig.ThinkingBudgetTokens.HasValue)
        {
            var thinkingParams = new ThinkingParameters
            {
                BudgetTokens = anthropicConfig.ThinkingBudgetTokens.Value,
                UseInterleavedThinking = anthropicConfig.UseInterleavedThinking
            };

            chatOptions = anthropicConfig.UseInterleavedThinking
                ? AnthropicChatOptionsExtensions.WithInterleavedThinking(chatOptions, thinkingParams)
                : AnthropicChatOptionsExtensions.WithThinking(chatOptions, thinkingParams);
        }

        // Build additional properties for Anthropic-specific features
        var additionalProps = config.AdditionalProperties ?? new Dictionary<string, object>();

        // Prompt caching
        if (anthropicConfig.EnablePromptCaching)
        {
            additionalProps["PromptCachingType"] = anthropicConfig.PromptCacheType;
        }

        // Top-K (Anthropic-specific)
        if (anthropicConfig.TopK.HasValue)
        {
            additionalProps["TopK"] = anthropicConfig.TopK.Value;
        }

        // Service tier
        if (!string.IsNullOrEmpty(anthropicConfig.ServiceTier))
        {
            additionalProps["ServiceTier"] = anthropicConfig.ServiceTier;
        }

        // Claude Skills (Anthropic's document processing)
        if (anthropicConfig.ClaudeSkills is { Count: > 0 })
        {
            var container = new Container
            {
                Id = anthropicConfig.ContainerId,
                Skills = new List<AnthropicSkill>()
            };

            foreach (var skillId in anthropicConfig.ClaudeSkills)
            {
                container.Skills.Add(new AnthropicSkill
                {
                    Type = "anthropic",
                    SkillId = skillId,
                    Version = "latest"
                });
            }

            additionalProps["Container"] = container;
        }
        else if (!string.IsNullOrEmpty(anthropicConfig.ContainerId))
        {
            // Reuse existing container without skills
            additionalProps["Container"] = new Container { Id = anthropicConfig.ContainerId };
        }

        // MCP Servers
        if (anthropicConfig.MCPServers is { Count: > 0 })
        {
            var mcpServers = new List<MCPServer>();
            foreach (var serverConfig in anthropicConfig.MCPServers)
            {
                mcpServers.Add(new MCPServer
                {
                    Url = serverConfig.Url,
                    Name = serverConfig.Name,
                    AuthorizationToken = serverConfig.AuthorizationToken,
                    ToolConfiguration = serverConfig.AllowedTools is { Count: > 0 }
                        ? new MCPToolConfiguration { Enabled = true, AllowedTools = serverConfig.AllowedTools }
                        : null
                });
            }
            additionalProps["MCPServers"] = mcpServers;
        }

        // Update the config
        config.DefaultChatOptions = chatOptions;
        if (additionalProps.Count > 0)
        {
            config.AdditionalProperties = additionalProps;
        }
    }

    public IProviderErrorHandler CreateErrorHandler()
    {
        return new AnthropicErrorHandler();
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
            DefaultContextWindow = 200000, // Claude 3.5 Sonnet
            DocumentationUrl = "https://docs.anthropic.com/"
        };
    }

    public ProviderValidationResult ValidateConfiguration(ProviderConfig config)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            return ProviderValidationResult.Failure("API key is required for Anthropic");

        if (string.IsNullOrEmpty(config.ModelName))
            return ProviderValidationResult.Failure("Model name is required");

        return ProviderValidationResult.Success();
    }
}
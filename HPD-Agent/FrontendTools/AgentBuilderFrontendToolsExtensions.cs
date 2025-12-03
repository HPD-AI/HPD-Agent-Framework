// Copyright (c) 2025 Einstein Essibu. All rights reserved.

namespace HPD.Agent.FrontendTools;

/// <summary>
/// Extension methods for configuring frontend tools on AgentBuilder.
/// </summary>
public static class AgentBuilderFrontendToolsExtensions
{
    /// <summary>
    /// Adds frontend tool support to the agent with default configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithFrontendTools()
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFrontendTools(this AgentBuilder builder)
    {
        return builder.WithFrontendTools(new FrontendToolConfig());
    }

    /// <summary>
    /// Adds frontend tool support to the agent with custom configuration.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="config">Frontend tool configuration</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithFrontendTools(new FrontendToolConfig
    ///     {
    ///         InvokeTimeout = TimeSpan.FromSeconds(60),
    ///         ValidateSchemaOnRegistration = true
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFrontendTools(this AgentBuilder builder, FrontendToolConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        var middleware = new FrontendToolMiddleware(config);
        builder.WithMiddleware(middleware);
        return builder;
    }

    /// <summary>
    /// Adds frontend tool support to the agent with configuration action.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="configure">Action to configure frontend tools</param>
    /// <returns>The builder for chaining</returns>
    /// <example>
    /// <code>
    /// var agent = new AgentBuilder()
    ///     .WithFrontendTools(config =>
    ///     {
    ///         config.InvokeTimeout = TimeSpan.FromSeconds(60);
    ///         config.DisconnectionStrategy = FrontendDisconnectionStrategy.RetryWithBackoff;
    ///     })
    ///     .Build();
    /// </code>
    /// </example>
    public static AgentBuilder WithFrontendTools(
        this AgentBuilder builder,
        Action<FrontendToolConfig> configure)
    {
        if (configure == null)
            throw new ArgumentNullException(nameof(configure));

        var config = new FrontendToolConfig();
        configure(config);
        return builder.WithFrontendTools(config);
    }
}

using Microsoft.Extensions.Logging;

/// <summary>
/// Extension methods for configuring agent-specific memory capabilities.
/// </summary>
public static class AgentBuilderMemoryExtensions
{
    /// <summary>
    /// Configures the agent's deep, static, read-only knowledge base.
    /// This utilizes an Indexed Retrieval (RAG) system for the agent's core expertise.
    /// </summary>
    public static AgentBuilder WithKnowledgeBase(this AgentBuilder builder, Action<AgentMemoryBuilder> configure)
    {
        var agentMemoryBuilder = new AgentMemoryBuilder(builder.AgentName);
        configure(agentMemoryBuilder);
        builder.SetMemoryBuilder(agentMemoryBuilder); // Assumes internal method on AgentBuilder
        return builder;
    }

    /// <summary>
    /// Configures the agent's dynamic, editable working memory.
    /// This enables a Full Text Injection (formerly CAG) system and provides the agent
    /// with tools to manage its own persistent facts.
    /// </summary>
    public static AgentBuilder WithInjectedMemory(this AgentBuilder builder, Action<AgentInjectedMemoryOptions> configure)
    {
        var options = new AgentInjectedMemoryOptions();
        configure(options);

        var manager = new AgentInjectedMemoryManager(options.StorageDirectory);
        var plugin = new AgentInjectedMemoryPlugin(manager, builder.AgentName);
        var filter = new AgentInjectedMemoryFilter(options);

        builder.WithPlugin(plugin);
        builder.WithPromptFilter(filter);
        return builder;
    }
}

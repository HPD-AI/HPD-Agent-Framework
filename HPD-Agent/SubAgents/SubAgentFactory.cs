using HPD.Agent;

namespace HPD_Agent;

/// <summary>
/// Factory for creating SubAgent objects with AgentConfig
/// </summary>
public static class SubAgentFactory
{
    /// <summary>
    /// Creates a sub-agent that can be invoked as a tool/function by parent agents.
    /// The sub-agent is configured using AgentConfig which defines its behavior, provider, plugins, etc.
    /// </summary>
    /// <param name="name">Sub-agent name (REQUIRED - becomes AIFunction name shown to parent agent)</param>
    /// <param name="description">Description shown in tool list (REQUIRED - becomes AIFunction description)</param>
    /// <param name="agentConfig">Agent configuration defining the sub-agent's behavior</param>
    /// <param name="pluginTypes">Optional plugin types to register with the sub-agent (e.g., typeof(FileSystemPlugin))</param>
    /// <returns>SubAgent object processed by source generator and converted to AIFunction at runtime</returns>
    public static SubAgent Create(
        string name,
        string description,
        AgentConfig agentConfig,
        params Type[] pluginTypes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sub-agent name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Sub-agent description cannot be empty", nameof(description));

        if (agentConfig == null)
            throw new ArgumentNullException(nameof(agentConfig), "AgentConfig cannot be null");

        return new SubAgent
        {
            Name = name,
            Description = description,
            AgentConfig = agentConfig,
            ThreadMode = SubAgentThreadMode.Stateless,  // Default to stateless
            PluginTypes = pluginTypes ?? Array.Empty<Type>()
        };
    }

    /// <summary>
    /// Creates a sub-agent with a shared thread for stateful multi-turn conversations.
    /// A new shared thread will be created automatically for maintaining context.
    /// WARNING: Shared threads are not thread-safe - avoid concurrent usage!
    /// </summary>
    /// <param name="name">Sub-agent name (REQUIRED - becomes AIFunction name shown to parent agent)</param>
    /// <param name="description">Description shown in tool list (REQUIRED - becomes AIFunction description)</param>
    /// <param name="agentConfig">Agent configuration defining the sub-agent's behavior</param>
    /// <param name="pluginTypes">Optional plugin types to register with the sub-agent (e.g., typeof(FileSystemPlugin))</param>
    /// <returns>SubAgent object configured with shared thread</returns>
    public static SubAgent CreateStateful(
        string name,
        string description,
        AgentConfig agentConfig,
        params Type[] pluginTypes)
    {
        var subAgent = Create(name, description, agentConfig, pluginTypes);
        subAgent.ThreadMode = SubAgentThreadMode.SharedThread;
        subAgent.SharedThread = new ConversationThread();
        return subAgent;
    }

    /// <summary>
    /// Creates a sub-agent with per-session thread management.
    /// Thread is provided at invocation time by the user.
    /// </summary>
    /// <param name="name">Sub-agent name (REQUIRED - becomes AIFunction name shown to parent agent)</param>
    /// <param name="description">Description shown in tool list (REQUIRED - becomes AIFunction description)</param>
    /// <param name="agentConfig">Agent configuration defining the sub-agent's behavior</param>
    /// <param name="pluginTypes">Optional plugin types to register with the sub-agent (e.g., typeof(FileSystemPlugin))</param>
    /// <returns>SubAgent object configured for per-session thread management</returns>
    public static SubAgent CreatePerSession(
        string name,
        string description,
        AgentConfig agentConfig,
        params Type[] pluginTypes)
    {
        var subAgent = Create(name, description, agentConfig, pluginTypes);
        subAgent.ThreadMode = SubAgentThreadMode.PerSession;
        return subAgent;
    }
}

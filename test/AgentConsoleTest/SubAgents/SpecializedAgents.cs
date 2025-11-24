using HPD.Agent;
using HPD_Agent;

/// <summary>
/// Example specialized agents that can be called as tools by parent agents
/// Demonstrates Microsoft's AsAIFunction() pattern with HPD-Agent's compile-time validation
/// </summary>
public class SpecializedAgents
{
    /// <summary>
    /// Weather specialist sub-agent - handles all weather-related queries
    /// Demonstrates stateless sub-agent (new thread per invocation)
    /// </summary>
    [SubAgent(Category = "Domain Experts", Priority = 1)]
    public SubAgent WeatherExpert()
    {
        return SubAgentFactory.Create(
            name: "WeatherExpert",
            description: "Specialized agent for weather forecasts, climate data, and meteorological analysis",
            agentConfig: new AgentConfig
            {
                Name = "Weather Expert",
                SystemInstructions = @"You are a meteorology expert specializing in weather forecasts and climate analysis.

When asked about weather:
1. Provide clear and concise weather information
2. Explain weather patterns in accessible terms
3. Give temperature in both Celsius and Fahrenheit
4. Include relevant safety warnings if applicable",
                MaxAgenticIterations = 10,
                Provider = new ProviderConfig
                {
                    ProviderKey = "openrouter",
                    ModelName = "google/gemini-2.0-flash-exp:free" // Fast, free model for specialized task
                }
            });
    }

    /// <summary>
    /// Math specialist sub-agent - handles complex calculations
    /// Demonstrates stateful sub-agent (maintains conversation context)
    /// </summary>
    [SubAgent(Category = "Domain Experts", Priority = 2)]
    public SubAgent MathExpert()
    {
        return SubAgentFactory.CreateStateful(
            name: "MathExpert",
            description: "Specialized agent for mathematical calculations, problem-solving, and explanations",
            agentConfig: new AgentConfig
            {
                Name = "Math Expert",
                SystemInstructions = @"You are a mathematics expert specializing in calculations and problem-solving.

                When solving math problems:
                1. Show step-by-step work
                2. Explain the reasoning behind each step
                3. Verify your calculations
                4. Provide the final answer clearly",
                MaxAgenticIterations = 15,
                Provider = new ProviderConfig
                {
                    ProviderKey = "openrouter",
                    ModelName = "google/gemini-2.0-flash-exp:free"
                }
            });
    }

    /// <summary>
    /// Code review specialist sub-agent - analyzes code quality
    /// Demonstrates sub-agent with plugins registered (FileSystemPlugin for reading code files)
    /// </summary>
    [SubAgent(Category = "Engineering", Priority = 1)]
    public SubAgent CodeReviewer()
    {
        return SubAgentFactory.Create(
            name: "CodeReviewer",
            description: "Specialized agent for code review, security analysis, and best practices enforcement. Can read code files from the filesystem.",
            agentConfig: new AgentConfig
            {
                Name = "Code Reviewer",
                SystemInstructions = @"You are a senior software engineer specializing in code review.

When reviewing code:
1. Check for security vulnerabilities
2. Verify error handling
3. Assess code maintainability
4. Suggest improvements with examples
5. Be constructive and specific

You have access to FileSystemPlugin tools to read code files when needed.",
                MaxAgenticIterations = 20,
                Provider = new ProviderConfig
                {
                    ProviderKey = "openrouter",
                    ModelName = "google/gemini-2.0-flash-exp:free"
                }
            },
            pluginTypes: typeof(HPD.Agent.Plugins.FileSystem.FileSystemPlugin));
    }
}

using HPD.Agent;
using HPD.Agent;

namespace HPD.Agent.Tests.TestPlugins;

/// <summary>
/// Test plugin that combines all three capability types: AIFunctions, Skills, and SubAgents.
/// Used to verify the source generator correctly handles plugins with mixed capabilities.
/// </summary>
public class CombinedCapabilitiesPlugin
{
    //     
    // AI FUNCTIONS
    //     

    [AIFunction, AIDescription("Analyze data and return insights")]
    public string AnalyzeData(string data) => $"Analysis of: {data}";

    [AIFunction, AIDescription("Transform data into a different format")]
    public string TransformData(string data, string format) => $"Transformed {data} to {format}";

    [AIFunction, AIDescription("Validate data against rules")]
    public bool ValidateData(string data) => !string.IsNullOrEmpty(data);

    //     
    // SKILLS
    //     

    [Skill]
    public static Skill DataAnalysisSkill() => SkillFactory.Create(
        "DataAnalysis",
        "Comprehensive data analysis workflow",
        "Use AnalyzeData and ValidateData to perform thorough data analysis",
        "CombinedCapabilitiesPlugin.AnalyzeData",
        "CombinedCapabilitiesPlugin.ValidateData"
    );

    [Skill]
    public static Skill DataTransformationSkill() => SkillFactory.Create(
        "DataTransformation",
        "Data transformation and conversion workflow",
        "Use TransformData to convert data between formats",
        "CombinedCapabilitiesPlugin.TransformData"
    );

    //     
    // SUB-AGENTS
    //     

    [SubAgent]
    public SubAgent DataExpertAgent()
    {
        return SubAgentFactory.Create(
            "DataExpert",
            "Expert sub-agent specialized in data analysis tasks",
            new AgentConfig
            {
                Name = "Data Expert",
                SystemInstructions = "You are an expert in data analysis. Help users understand their data.",
                MaxAgenticIterations = 10,
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",
                    ModelName = "test-model"
                }
            });
    }

    [SubAgent]
    public SubAgent DataProcessorAgent()
    {
        return SubAgentFactory.Create(
            "DataProcessor",
            "Sub-agent for batch data processing tasks",
            new AgentConfig
            {
                Name = "Data Processor",
                SystemInstructions = "You process large amounts of data efficiently.",
                MaxAgenticIterations = 20,
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",
                    ModelName = "test-model"
                }
            });
    }
}

/// <summary>
/// Plugin with only AIFunctions and SubAgents (no Skills)
/// </summary>
public class FunctionsAndSubAgentsPlugin
{
    // AI Functions
    [AIFunction, AIDescription("Search for items")]
    public string Search(string query) => $"Results for: {query}";

    [AIFunction, AIDescription("Filter results")]
    public string Filter(string results, string criteria) => $"Filtered by {criteria}";

    // Sub-Agent
    [SubAgent]
    public SubAgent SearchExpertAgent()
    {
        return SubAgentFactory.Create(
            "SearchExpert",
            "Expert in search and discovery",
            new AgentConfig
            {
                Name = "Search Expert",
                SystemInstructions = "You help users find information efficiently.",
                MaxAgenticIterations = 5,
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",
                    ModelName = "test-model"
                }
            });
    }
}

/// <summary>
/// Plugin with only Skills and SubAgents (no direct AIFunctions)
/// Note: Skills reference functions from other plugins
/// </summary>
public class SkillsAndSubAgentsPlugin
{
    // Skill that references functions from MockFileSystemPlugin
    [Skill]
    public static Skill FileOperationsSkill() => SkillFactory.Create(
        "FileOps",
        "File operation workflows",
        "Use file operations for reading and writing",
        "MockFileSystemPlugin.ReadFile",
        "MockFileSystemPlugin.WriteFile"
    );

    // Sub-Agent
    [SubAgent]
    public SubAgent FileAssistantAgent()
    {
        return SubAgentFactory.Create(
            "FileAssistant",
            "Assistant for file management tasks",
            new AgentConfig
            {
                Name = "File Assistant",
                SystemInstructions = "You help users manage their files.",
                MaxAgenticIterations = 8,
                Provider = new ProviderConfig
                {
                    ProviderKey = "test",
                    ModelName = "test-model"
                }
            });
    }
}

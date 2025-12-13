namespace AgentConsoleTest;

public class TestSkillSimple
{
    [Skill]
    public Skill SimpleTest(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "Test",
            description: "Test skill",
            functionResult: "Test instructions",
            systemPrompt: null,
            "TestPlugin.TestFunction"
        );
    }
}

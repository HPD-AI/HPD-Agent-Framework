using HPD_Agent.Skills;

namespace AgentConsoleTest;

public class TestSkillSimple
{
    [Skill]
    public Skill SimpleTest(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            "Test",
            "Test skill",
            "Test instructions",
            "TestPlugin.TestFunction"
        );
    }
}

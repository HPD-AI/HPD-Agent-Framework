using HPD_Agent.Skills;

namespace HPD_Agent.Tests.TestPlugins;

/// <summary>
/// Mock plugin with multiple functions for testing selective registration
/// This plugin must be in its own file so the source generator can process it
/// </summary>
public class MockFileSystemPlugin
{
    [AIFunction, AIDescription("Read a file")]
    public string ReadFile(string path) => "file content";

    [AIFunction, AIDescription("Write a file")]
    public void WriteFile(string path, string content) { }

    [AIFunction, AIDescription("Delete a file")]
    public void DeleteFile(string path) { }

    [AIFunction, AIDescription("List files")]
    public string[] ListFiles(string path) => Array.Empty<string>();

    [AIFunction, AIDescription("Get file info")]
    public string GetFileInfo(string path) => "info";
}

/// <summary>
/// Mock debugging plugin with skills that reference MockFileSystemPlugin functions
/// </summary>
public class MockDebuggingPlugin
{
    [Skill]
    public static Skill FileDebugging() => SkillFactory.Create(
        "FileDebugging",
        "Debug file system issues",
        "Use file operations to debug issues",
        "MockFileSystemPlugin.ReadFile",
        "MockFileSystemPlugin.WriteFile"
    );
}

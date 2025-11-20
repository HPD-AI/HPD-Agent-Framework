
/// <summary>
/// Example skill definitions using the new type-safe skill architecture.
/// This file demonstrates the new SkillFactory.Create() pattern with compile-time safety.
///
/// NOTE: These are template examples showing the pattern.
/// Actual skills will be created in Phase 2 after source generator implementation.
///
/// IMPORTANT: Skill methods can be:
/// - Static: For shared, reusable skill definitions (recommended)
/// - Instance: For contextual skills with dependency injection
/// 
/// Both patterns require: [Skill] attribute, public, and return type Skill
/// Static is NOT required - use whichever fits your use case.
/// </summary>
public static class Example_TypeSafeSkills
{
    /*
    /// <summary>
    /// Example of a simple skill with type-safe function references
    /// </summary>
    public static Skill FileOperationsExample(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "FileOperations",
            description: "Basic file operations for reading and writing files",
            instructions: @"
Use this skill to perform basic file operations:
1. Use ReadFile to read file contents
2. Use WriteFile to create or update files
3. Always validate paths are within the workspace
",
            // Type-safe method references - compiler validates these exist!
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile
        );
    }

    /// <summary>
    /// Example of a skill with options
    /// Note: Skills are always scoped - functions are hidden until skill is activated
    /// </summary>
    public static Skill FileDiscoveryExample(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "FileDiscovery",
            description: "Discover and search for files in the workspace",
            instructions: @"
Use this skill to discover files:
1. Use SearchFiles to find files matching patterns
2. Use ListDirectory to explore directory contents
3. Respect .gitignore rules when searching
",
            options: options ?? new SkillOptions(),
            FileSystemPlugin.SearchFiles,
            FileSystemPlugin.ListDirectory
        );
    }

    /// <summary>
    /// Example of a skill with document references
    /// </summary>
    public static Skill AdvancedFileEditingExample(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "AdvancedFileEditing",
            description: "Advanced file editing with diff and patch capabilities",
            instructions: "See instruction documents for detailed editing workflows",
            options: new SkillOptions()
                .AddDocument("advanced_editing", "Advanced editing techniques and workflows"),
            FileSystemPlugin.ReadFile,
            FileSystemPlugin.WriteFile,
            FileSystemPlugin.EditFile
        );
    }

    /// <summary>
    /// Example of nested skills (skills referencing other skills)
    /// Available in Phase 2 after source generator supports skill-to-skill references
    /// </summary>
    public static Skill NestedSkillExample(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "ComprehensiveFileManagement",
            description: "Complete file management with all capabilities",
            instructions: "Combines file operations, discovery, and editing skills",
            // Skill references (will be resolved by source generator)
            Example_TypeSafeSkills.FileOperationsExample,
            Example_TypeSafeSkills.FileDiscoveryExample,
            Example_TypeSafeSkills.AdvancedFileEditingExample
        );
    }
    */
}

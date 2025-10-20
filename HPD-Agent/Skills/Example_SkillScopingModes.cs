using HPD_Agent.Skills;

/// <summary>
/// Example demonstrating the new Skill Scoping Modes.
/// This shows how to use ScopingMode and SuppressPluginContainers properties.
/// </summary>
public static class SkillScopingModesExample
{
    /// <summary>
    /// Example 1: InstructionOnly mode (Default) - Functions stay visible
    /// Use case: General-purpose skills where discoverability is important
    /// </summary>
    public static SkillDefinition InstructionOnlyExample()
    {
        return new SkillDefinition
        {
            Name = "DebuggingBasics",
            Description = "Basic debugging techniques for common issues",
            ScopingMode = SkillScopingMode.InstructionOnly,  // Default - functions stay visible
            FunctionReferences = new[]
            {
                "ReadFile",
                "WriteFile",
                "GetStackTrace"
            },
            PostExpansionInstructions = @"
                Debugging workflow:
                1. Use ReadFile to examine log files
                2. Use GetStackTrace to identify error locations
                3. Use WriteFile to save debug notes
            "
        };

        // What agent sees BEFORE expansion:
        //   ✅ DebuggingBasics (skill container)
        //   ✅ ReadFile (already visible)
        //   ✅ WriteFile (already visible)
        //   ✅ GetStackTrace (already visible)
        //
        // What agent sees AFTER expanding DebuggingBasics:
        //   ❌ DebuggingBasics (container hidden)
        //   ✅ ReadFile (still visible - no change)
        //   ✅ WriteFile (still visible - no change)
        //   ✅ GetStackTrace (still visible - no change)
        //   + Receives PostExpansionInstructions
        //
        // Benefit: Functions always discoverable, skill provides guidance when invoked
    }

    /// <summary>
    /// Example 2: Scoped mode - Functions hidden until skill expanded
    /// Use case: Highly specialized workflows, token efficiency
    /// </summary>
    public static SkillDefinition ScopedExample()
    {
        return new SkillDefinition
        {
            Name = "AdvancedDatabaseMigration",
            Description = "Complex database migration workflow with safety checks",
            ScopingMode = SkillScopingMode.Scoped,  // Hide functions until expanded
            FunctionReferences = new[]
            {
                "ExecuteSQL",
                "BackupDatabase",
                "RollbackDatabase",
                "ValidateSchema"
            },
            PostExpansionInstructions = @"
                CRITICAL - Follow this exact sequence:
                1. BackupDatabase FIRST
                2. ValidateSchema on backup
                3. Execute migration with ExecuteSQL
                4. If errors: RollbackDatabase immediately
            "
        };

        // What agent sees BEFORE expansion:
        //   ✅ AdvancedDatabaseMigration (skill container)
        //   ❌ ExecuteSQL (HIDDEN)
        //   ❌ BackupDatabase (HIDDEN)
        //   ❌ RollbackDatabase (HIDDEN)
        //   ❌ ValidateSchema (HIDDEN)
        //
        // What agent sees AFTER expanding AdvancedDatabaseMigration:
        //   ❌ AdvancedDatabaseMigration (container hidden)
        //   ✅ ExecuteSQL (NOW VISIBLE)
        //   ✅ BackupDatabase (NOW VISIBLE)
        //   ✅ RollbackDatabase (NOW VISIBLE)
        //   ✅ ValidateSchema (NOW VISIBLE)
        //   + Receives PostExpansionInstructions
        //
        // Benefit: Token efficient, agent only sees these dangerous functions when explicitly needed
    }

    /// <summary>
    /// Example 3: PluginReferences with Scoped mode
    /// Solves Scenario 4 - Skill can scope non-[PluginScope] plugins!
    /// </summary>
    public static SkillDefinition ScopedPluginReferenceExample()
    {
        return new SkillDefinition
        {
            Name = "FileManagement",
            Description = "File operations with best practices",
            ScopingMode = SkillScopingMode.Scoped,  // Hide ALL plugin functions
            PluginReferences = new[] { "FileSystemPlugin" },  // References entire plugin
            PostExpansionInstructions = @"
                File operation best practices:
                - Always validate paths before operations
                - Check file existence before reading
                - Handle exceptions gracefully
            "
        };

        // Assumes FileSystemPlugin has NO [PluginScope] attribute
        // Before this feature, PluginReferences didn't work on non-scoped plugins!
        //
        // What agent sees BEFORE expansion:
        //   ✅ FileManagement (skill container)
        //   ❌ ReadFile (HIDDEN by skill's Scoped mode)
        //   ❌ WriteFile (HIDDEN by skill's Scoped mode)
        //   ❌ DeleteFile (HIDDEN by skill's Scoped mode)
        //
        // What agent sees AFTER expanding FileManagement:
        //   ❌ FileManagement (container hidden)
        //   ✅ ReadFile (NOW VISIBLE)
        //   ✅ WriteFile (NOW VISIBLE)
        //   ✅ DeleteFile (NOW VISIBLE)
        //
        // ✅ THIS NOW WORKS! (Previously would fail at Build() time)
    }

    /// <summary>
    /// Example 4: SuppressPluginContainers with scoped plugin
    /// Skill "takes ownership" and hides the plugin container
    /// </summary>
    public static SkillDefinition SuppressPluginContainerExample()
    {
        return new SkillDefinition
        {
            Name = "WebDebugging",
            Description = "Specialized web application debugging workflow",
            ScopingMode = SkillScopingMode.InstructionOnly,  // Functions visible
            PluginReferences = new[] { "FileSystemPlugin" },  // Assume HAS [PluginScope]
            SuppressPluginContainers = true,  // Hide the FileSystemPlugin container!
            PostExpansionInstructions = @"
                Web debugging workflow:
                - ReadFile to examine server logs
                - WriteFile to save findings
                Focus on HTTP errors and stack traces
            "
        };

        // Assumes FileSystemPlugin HAS [PluginScope] attribute
        //
        // What agent sees (Turn 1):
        //   ✅ WebDebugging (skill container)
        //   ❌ FileSystemPlugin (SUPPRESSED by skill!)
        //   ✅ ReadFile (VISIBLE - InstructionOnly mode + suppression)
        //   ✅ WriteFile (VISIBLE - InstructionOnly mode + suppression)
        //
        // What happens:
        // 1. Skill suppresses the FileSystemPlugin container
        // 2. Skill's InstructionOnly mode keeps functions visible
        // 3. Agent can use functions directly without expanding plugin
        // 4. Only ONE expansion path exists (the skill)
        //
        // Without SuppressPluginContainers:
        //   ✅ WebDebugging (skill)
        //   ✅ FileSystemPlugin (plugin container - confusing!)
        //   ❌ ReadFile (HIDDEN by plugin scoping)
        //   ❌ WriteFile (HIDDEN by plugin scoping)
    }

    /// <summary>
    /// Example 5: Combining everything - Complex scenario
    /// </summary>
    public static SkillDefinition ComplexExample()
    {
        return new SkillDefinition
        {
            Name = "SecureFileOperations",
            Description = "File operations with security validation",
            ScopingMode = SkillScopingMode.Scoped,  // Hide until expanded
            PluginReferences = new[] { "FileSystemPlugin" },  // Entire plugin
            FunctionReferences = new[]  // PLUS additional functions
            {
                "SecurityPlugin.ValidatePath",
                "SecurityPlugin.CheckPermissions"
            },
            SuppressPluginContainers = true,  // Take full ownership
            PostExpansionInstructions = @"
                Secure file operations workflow:
                1. ValidatePath BEFORE any file operation
                2. CheckPermissions BEFORE write/delete operations
                3. Use ReadFile/WriteFile only after validation passes
            "
        };

        // Combines:
        // - PluginReferences (entire FileSystemPlugin)
        // - FunctionReferences (specific security functions)
        // - Scoped mode (hide everything)
        // - SuppressPluginContainers (hide FileSystemPlugin container)
        //
        // Result: Skill is the ONLY way to access these functions
        // Perfect for dangerous operations requiring strict workflow
    }
}

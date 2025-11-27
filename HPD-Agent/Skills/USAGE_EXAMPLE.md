# Skills System - Complete User Example

This document shows how users interact with the Skills system through AgentBuilder.

## Complete Example

```csharp
using HPD_Agent.Skills;

// Build an agent with skills
var agent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithName("DebugAgent")

    // Register plugins (existing API)
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DebugPlugin>()
    .WithPlugin<DataAnalysisPlugin>()
    .WithPlugin<MathPlugin>()

    // Configure skills (NEW API)
    .WithSkills(skills => {
        // Define Debugging skill
        skills.DefineSkill(
            name: "Debugging",
            description: "Debugging and troubleshooting capabilities",
            functionRefs: new[] {
                "FileSystemPlugin.ReadFile",
                "FileSystemPlugin.ListDirectory",
                "DebugPlugin.GetStackTrace",
                "DebugPlugin.ListProcesses"
            },
            instructions: "When debugging, always read error logs first.",
            instructionDocuments: new[] {
                "debugging-protocol.md",
                "troubleshooting-checklist.md"
            }
        );

        // Define FileManagement skill
        skills.DefineSkill(
            name: "FileManagement",
            description: "File and directory management operations",
            functionRefs: new[] {
                "FileSystemPlugin.ReadFile",      // Same function in multiple skills!
                "FileSystemPlugin.WriteFile",
                "FileSystemPlugin.DeleteFile",
                "FileSystemPlugin.ListDirectory"
            },
            instructionDocuments: new[] {
                "file-safety-protocol.md"
            }
        );

        // Define DataAnalysis skill
        skills.DefineSkill(
            name: "DataAnalysis",
            description: "Data analysis and processing capabilities",
            functionRefs: new[] {
                "FileSystemPlugin.ReadFile",      // Same function again!
                "DataAnalysisPlugin.ParseCSV",
                "DataAnalysisPlugin.GenerateChart",
                "MathPlugin.Statistics"
            },
            instructions: "Always validate data before analysis."
        );
    })

    // Build the agent
    .Build();

// Use the agent normally
await foreach (var ev in agent.CompleteStreamingAsync("Debug the authentication error"))
{
    // Agent will see skill containers and can activate them as needed
}
```

## Alternative: Pre-Configured Skills

```csharp
// Load skills from a configuration file or database
var skillDefinitions = new SkillDefinition[]
{
    new()
    {
        Name = "Debugging",
        Description = "Debugging capabilities",
        FunctionReferences = new[] { "FileSystemPlugin.ReadFile", "DebugPlugin.GetStackTrace" },
        PostExpansionInstructionDocuments = new[] { "debugging-protocol.md" }
    },
    new()
    {
        Name = "FileManagement",
        Description = "File operations",
        FunctionReferences = new[] { "FileSystemPlugin.ReadFile", "FileSystemPlugin.WriteFile" },
        PostExpansionInstructionDocuments = new[] { "file-safety.md" }
    }
};

var agent = AgentBuilder.Create()
    .WithOpenAI(apiKey, "gpt-4")
    .WithPlugin<FileSystemPlugin>()
    .WithPlugin<DebugPlugin>()
    .WithSkills(skillDefinitions)  // Pass array directly
    .Build();
```

## Instruction Documents

Create markdown files in `skills/documents/`:

**skills/documents/debugging-protocol.md:**
```markdown
# Debugging Protocol

## Step-by-Step Process:
1. Read error logs first using ReadFile
2. Analyze stack traces with GetStackTrace
3. Check running processes with ListProcesses
4. Verify file permissions
5. Test fixes incrementally

## Common Issues:
- Missing files: Use ListDirectory to verify paths
- Permission errors: Check file access rights
- Process conflicts: Check running processes first
```

**skills/documents/file-safety-protocol.md:**
```markdown
# File Safety Protocol

## Before Any File Operation:
1. Verify file paths are valid
2. Check if file exists (use ReadFile first)
3. For destructive operations (delete, overwrite):
   - Confirm with user
   - Create backup if needed
4. Validate file permissions

## Never:
- Delete system files
- Overwrite without confirmation
- Assume paths are safe
```

## How It Works at Runtime

**User:** "Debug the authentication error"

**Agent sees:**
- Debugging (skill container)
- FileManagement (skill container)
- DataAnalysis (skill container)

**Agent invokes:** `Debugging`

**Agent receives:**
```
Debugging expanded. Available functions: ReadFile, ListDirectory, GetStackTrace, ListProcesses

# Debugging Protocol
## Step-by-Step Process:
1. Read error logs first using ReadFile
...
[Full content from debugging-protocol.md]

# Troubleshooting Checklist
...
[Full content from troubleshooting-checklist.md]
```

**Agent now sees:**
- FileManagement (still Collapse)
- DataAnalysis (still Collapse)
- ReadFile (from Debugging)
- ListDirectory (from Debugging)
- GetStackTrace (from Debugging)
- ListProcesses (from Debugging)

**Agent uses:** `ReadFile("/var/log/auth.log")`

## Key Points

1. **Plugin Scoping Required**: Skills only work when plugin scoping is enabled
2. **Fluent API**: Uses familiar AgentBuilder pattern
3. **Document-Based**: Instructions loaded from markdown files
4. **M:N Relationships**: Same function in multiple skills
5. **Automatic Deduplication**: If multiple skills expanded, functions appear once
6. **Token Efficient**: Instructions shown once, then filtered from history

## No Configuration Needed Beyond This

Users don't interact with:
- SkillManager
- SkillScopingManager
- SkillDefinition internals

Everything is handled by `.WithSkills()` and `AgentBuilder.Build()`.

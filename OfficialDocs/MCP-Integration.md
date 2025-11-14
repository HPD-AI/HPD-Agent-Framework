# MCP Integration

## What is MCP?

The **Model Context Protocol (MCP)** is an open standard for connecting AI agents to external systems and data sources. MCP servers expose tools, resources, and prompts that agents can use at runtime.

**Key benefit**: Connect to thousands of community-built MCP servers without custom integration work.

---

## Quick Start

### 1. Create MCP Manifest File

Create `MCP.json` in your application root:

```json
{
  "servers": [
    {
      "name": "filesystem",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/Users/yourname/Documents"],
      "description": "Read and write files in Documents folder",
      "enabled": true,
      "enableScoping": true,
      "requiresPermission": true
    },
    {
      "name": "github",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-github"],
      "description": "Search repositories, read files, create issues",
      "enabled": true,
      "requiresPermission": true,
      "environment": {
        "GITHUB_TOKEN": "your-token-here"
      }
    },
    {
      "name": "context7",
      "command": "npx",
      "arguments": ["-y", "@upstash/context7-mcp"],
      "description": "Read-only documentation and examples",
      "enabled": true,
      "enableScoping": true,
      "requiresPermission": false
    }
  ]
}
```

### 2. Configure Agent with MCP

```csharp
var agent = new AgentBuilder()
    .WithMCP("MCP.json")  // Load MCP servers from manifest
    .Build();
```

**That's it!** The agent automatically loads and registers all enabled MCP servers.

---

## Configuration Reference

### Server Configuration

```json
{
  "name": "server-name",           // Required: Unique identifier
  "command": "npx",                // Required: Command to run
  "arguments": ["-y", "package"],  // Required: Command arguments
  "description": "Optional description for agent",  // See description priority below
  "enabled": true,                 // Default: true
  "enableScoping": null,           // Default: null (uses global setting), true = force scoping, false = force direct
  "requiresPermission": true,      // Default: true - See Permission Control section below
  "timeout": 30000,                // Default: 30000ms
  "retryAttempts": 3,              // Default: 3
  "environment": {                 // Optional environment variables
    "API_KEY": "value"
  }
}
```

### Description Priority

Descriptions determine how MCP servers appear to the agent. The function list is **always appended** to show available tools:

1. **User-provided (JSON config)** - Highest priority
   ```json
   "description": "File operations for Documents folder"
   ```
   **Result**: `"File operations for Documents folder. Contains 10 functions: read_file, write_file, ..."`

2. **MCP server metadata** - Extracted from `serverInfo.Description` during initialization
   ```
   Automatically extracted if available
   ```
   **Result**: `"[Server description]. Contains 10 functions: read_file, write_file, ..."`

3. **Auto-generated** - Fallback when no description provided
   ```
   "MCP Server 'filesystem'. Contains 10 functions: read_file, write_file, ..."
   ```

---

## Scoping MCP Tools

By default, MCP tools are loaded **without scoping** (all tools visible). To enable scoping, use the per-server `enableScoping` property in your MCP.json:

### Without Scoping (Default)

All MCP tools are exposed directly:

```
Available Functions:
- filesystem_read_file
- filesystem_write_file
- filesystem_list_directory
- github_search_repositories
- github_create_issue
- github_get_file
... (hundreds of functions)
```

**Problem**: Token bloat, overwhelming tool list.

---

### With Scoping (Recommended)

Enable scoping per-server in your MCP.json:

```json
{
  "servers": [
    {
      "name": "filesystem",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/path"],
      "enableScoping": true,
      "description": "File operations"
    }
  ]
}
```

Tools are grouped behind server containers:

```
Available Functions:
- MCP_filesystem: Read and write files in Documents folder
- MCP_github: Search repositories, read files, create issues
```

**Agent workflow:**

```
User: "Read my config file"

Agent: Calls MCP_filesystem container
→ Result: "filesystem server expanded. Available functions: read_file, write_file, list_directory, ..."

Agent: Calls read_file(path: "config.json")
→ Result: "{ 'setting': 'value' }"
```

**Benefits:**
- ✅ **Token efficiency**: Only load tools when needed
- ✅ **Clear organization**: Tools grouped by purpose
- ✅ **Automatic filtering**: Container activations filtered from history (see [Scoping.md](Scoping.md))

---

### Per-Server Scoping Control

You can control scoping on a per-server basis using the `enableScoping` property in your MCP.json:

```json
{
  "servers": [
    {
      "name": "filesystem",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/path"],
      "enableScoping": true,  // Force scoping for this server
      "description": "File operations"
    },
    {
      "name": "github",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-github"],
      "enableScoping": false,  // Force direct exposure (no scoping)
      "description": "GitHub integration"
    },
    {
      "name": "postgres",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-postgres"],
      // enableScoping not specified - defaults to false (no scoping)
      "description": "Database access"
    }
  ]
}
```

**How it works:**
- `"enableScoping": true` - Server tools are always scoped behind a container (e.g., `MCP_filesystem`)
- `"enableScoping": false` - Server tools are always exposed directly (e.g., `filesystem_read_file`)
- `enableScoping` omitted or `null` - Defaults to `false` (no scoping)

**Example result** (with per-server overrides):

```
Available Functions:
- MCP_filesystem (scoped - enableScoping: true)
- github_search_repositories (direct - enableScoping: false)
- github_create_issue (direct - enableScoping: false)
- postgres_query (direct - enableScoping not set, defaults to false)
- postgres_execute (direct - enableScoping not set, defaults to false)
```

**Use cases:**
- **High-cardinality servers** (many tools): Enable scoping to reduce token usage
- **Frequently-used servers** (few tools): Disable scoping for faster access
- **Mixed environments**: Fine-tune per server without affecting all MCP tools globally

---

## Permission Control

By default, **all MCP tools require user permission** before execution for security. You can configure this per-server based on the risk profile of the MCP server's operations.

### Default Behavior (Requires Permission)

```json
{
  "servers": [
    {
      "name": "filesystem",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/path"],
      "requiresPermission": true  // Default - user must approve each action
    }
  ]
}
```

**Agent workflow:**
```
User: "Delete temp.txt"

Agent: Calls delete_file("temp.txt")
→ ⚠️ Permission request triggered

User: Approve? (y/n): y
→ ✅ Permission granted, file deleted

Agent: "File deleted successfully"
```

---

### Disabling Permissions for Read-Only Servers

For **read-only or low-risk MCP servers** (documentation, weather, search), you can disable permission requirements:

```json
{
  "servers": [
    {
      "name": "context7",
      "command": "npx",
      "arguments": ["-y", "@upstash/context7-mcp"],
      "requiresPermission": false,  // No permission needed - read-only docs
      "enableScoping": true
    },
    {
      "name": "weather",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-weather"],
      "requiresPermission": false  // No permission needed - external read-only API
    }
  ]
}
```

**Agent workflow:**
```
User: "Search for React documentation"

Agent: Calls search_docs("React")
→ ✅ Executes immediately (no permission prompt)

Agent: "Here's the React documentation..."
```

---

### Per-Server Permission Strategy

Configure permissions based on server risk profile:

```json
{
  "servers": [
    {
      "name": "filesystem",
      "requiresPermission": true,   // ⚠️ Destructive - delete, write files
      "enableScoping": true
    },
    {
      "name": "github",
      "requiresPermission": true,   // ⚠️ Modifying - create issues, PRs
      "enableScoping": true
    },
    {
      "name": "postgres",
      "requiresPermission": true,   // ⚠️ Database - write, delete data
      "enableScoping": true
    },
    {
      "name": "context7",
      "requiresPermission": false,  // ✅ Read-only - documentation search
      "enableScoping": true
    },
    {
      "name": "weather",
      "requiresPermission": false,  // ✅ Read-only - weather data
      "enableScoping": false
    },
    {
      "name": "calculator",
      "requiresPermission": false   // ✅ Safe - math operations
    }
  ]
}
```

---

### Permission Decision Guide

Use this guide to determine the `requiresPermission` setting for your MCP servers:

| Server Type | Example | requiresPermission | Reason |
|-------------|---------|-------------------|---------|
| **File System** | filesystem, file-operations | `true` | Can delete, modify files |
| **Database** | postgres, sqlite, mongodb | `true` | Can write, delete data |
| **Version Control** | github, gitlab | `true` | Can create commits, issues, PRs |
| **Communication** | slack, email, sms | `true` | Sends messages, costs money |
| **Cloud Services** | aws, gcp, azure | `true` | Can create/delete resources, costs money |
| **Documentation** | context7, devdocs | `false` | Read-only, no side effects |
| **Search** | web-search, google | `false` | Read-only queries |
| **Weather/News** | weather, news-api | `false` | External read-only data |
| **Math/Utils** | calculator, uuid-gen | `false` | Pure functions, no side effects |

**General Rule:**
- `requiresPermission: true` → Can modify state, cost money, or send data externally
- `requiresPermission: false` → Read-only, idempotent, no side effects

---

### Important Notes

1. **Server-Level Granularity**: Permission settings apply to **all tools** from a server. You cannot configure permissions per-function within an MCP server (function discovery happens at runtime).

2. **Safe Default**: If omitted, `requiresPermission` defaults to `true` for safety. Always explicitly set to `false` only for trusted, read-only servers.

3. **Permission Handler Required**: If any MCP server has `requiresPermission: true`, you must configure a permission handler:
   ```csharp
   var agent = new AgentBuilder()
       .WithMCP("MCP.json")
       .WithPermissions()  // Required for permission prompts
       .Build();
   ```
   See [Permission-System.md](Permission-System.md) for details on implementing permission handlers.

4. **Testing Override**: During development, you can auto-approve all permissions:
   ```csharp
   var agent = new AgentBuilder()
       .WithMCP("MCP.json")
       .WithAutoApprovePermissions()  // ⚠️ Testing only!
       .Build();
   ```

---

## Usage Patterns

### Pattern 1: From File (Recommended)

```csharp
var agent = new AgentBuilder()
    .WithMCP("MCP.json")
    .Build();
```

### Pattern 2: From JSON Content

```csharp
var manifestJson = await File.ReadAllTextAsync("MCP.json");

var agent = new AgentBuilder()
    .WithMCPContent(manifestJson)
    .Build();
```

### Pattern 3: With Options

```csharp
var agent = new AgentBuilder()
    .WithMCP("MCP.json", options =>
    {
        options.FailOnServerError = true;  // Throw if any server fails
        options.ConnectionTimeout = TimeSpan.FromSeconds(60);
        options.MaxConcurrentServers = 5;
    })
    .Build();
```

### Pattern 4: Via AgentConfig

```csharp
var config = new AgentConfig
{
    Mcp = new McpConfig
    {
        ManifestPath = "MCP.json",
        Options = new MCPOptions
        {
            FailOnServerError = false
        }
    },
    Scoping = new ScopingConfig
    {
        Enabled = true,  // Enable scoping for C# plugins
        MaxFunctionNamesInDescription = 10
    }
};

var agent = new AgentBuilder()
    .WithConfig(config)
    .Build();
```

---

## Popular MCP Servers

### Official Servers

```json
{
  "name": "filesystem",
  "command": "npx",
  "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/path/to/directory"],
  "description": "File operations"
}
```

```json
{
  "name": "github",
  "command": "npx",
  "arguments": ["-y", "@modelcontextprotocol/server-github"],
  "description": "GitHub integration",
  "environment": {
    "GITHUB_TOKEN": "${GITHUB_TOKEN}"
  }
}
```

```json
{
  "name": "postgres",
  "command": "npx",
  "arguments": ["-y", "@modelcontextprotocol/server-postgres", "postgresql://localhost/mydb"],
  "description": "PostgreSQL database access"
}
```

### Community Servers

Browse the full list at: https://github.com/modelcontextprotocol/servers

---

## Environment Variables

Use environment variables for sensitive data:

```json
{
  "environment": {
    "API_KEY": "${MY_API_KEY}",
    "DATABASE_URL": "${DATABASE_URL}"
  }
}
```

Set environment variables before running:

```bash
export MY_API_KEY="secret-key"
export DATABASE_URL="postgresql://localhost/mydb"
dotnet run
```

---

## Health Checks

Monitor MCP server health:

```csharp
var healthStatus = await mcpManager.HealthCheckAsync();

foreach (var (serverName, isHealthy) in healthStatus)
{
    Console.WriteLine($"{serverName}: {(isHealthy ? "✓ Healthy" : "✗ Unhealthy")}");
}
```

---

## Advanced Configuration

### Post-Expansion Instructions

Provide server-specific guidance after container expansion:

```csharp
var config = new AgentConfig
{
    Scoping = new ScopingConfig
    {
        Enabled = true,
        MCPServerInstructions = new Dictionary<string, string>
        {
            ["filesystem"] = "IMPORTANT: Always use absolute paths. Check file exists before operations.",
            ["github"] = "Rate limit: 5000 requests/hour. Use pagination for large result sets."
        }
    }
};
```

**Agent sees after expanding `MCP_filesystem`:**
```
filesystem server expanded. Available functions: read_file, write_file, ...

IMPORTANT: Always use absolute paths. Check file exists before operations.
```

---

## Error Handling

### Fail on Server Error (Default: False)

```csharp
var agent = new AgentBuilder()
    .WithMCP("MCP.json", options =>
    {
        options.FailOnServerError = true;  // Throw if any server fails
    })
    .Build();
```

**Default behavior**: Continue loading other servers if one fails.

### Retry Configuration

Configure per-server:

```json
{
  "name": "unreliable-server",
  "retryAttempts": 5,  // Default: 3
  "timeout": 60000      // Default: 30000ms
}
```

---

## Advanced Scenarios

### Max Function Names in Description

Control how many function names appear in container descriptions:

```csharp
var config = new AgentConfig
{
    Scoping = new ScopingConfig
    {
        Enabled = true,
        MaxFunctionNamesInDescription = 5  // Default: 10
    }
};
```

**Result:**
```
MCP_filesystem: Read and write files. Contains 20 functions: read_file, write_file, list_directory, delete_file, copy_file and 15 more
```

### Programmatic Configuration

Build manifest dynamically:

```csharp
var manifestJson = JsonSerializer.Serialize(new MCPManifest
{
    Servers = new List<MCPServerConfig>
    {
        new MCPServerConfig
        {
            Name = "dynamic-server",
            Command = "node",
            Arguments = new List<string> { "server.js" },
            Description = "Dynamically configured server",
            Enabled = true
        }
    }
}, MCPJsonSerializerContext.Default.MCPManifest);

var agent = new AgentBuilder()
    .WithMCPContent(manifestJson)
    .Build();
```

---

## Comparison: MCP vs. Native Plugins vs. Skills

| Feature | MCP Servers | Native Plugins | Skills |
|---------|-------------|----------------|--------|
| **Implementation** | External processes | C# classes | Capability bundles |
| **Discovery** | Runtime (from manifest) | Compile-time | Compile-time |
| **Scoping** | Optional (via containers) | Optional (`[Scope]` attribute) | Always scoped |
| **Type Safety** | ❌ Runtime only | ✅ Compile-time | ✅ Compile-time (for native functions) |
| **Ecosystem** | ✅ Thousands of servers | Limited to your codebase | Limited to your codebase |
| **Permissions** | Configurable per-server (default: required) | Configurable per-function (`[RequiresPermission]`) | Configurable per-function |
| **Activation** | Container expansion | Container expansion | Skill activation |
| **Can Claim Functions** | ❌ No | ✅ Yes (via `[Scope]`) | ✅ Yes (native plugins only) |
| **Use with MCP** | Provides tools | Provides tools | ✅ Provides workflows/instructions |
| **Best For** | External integrations | Core functionality | Workflows + MCP orchestration |

---

## Integration with Skills

Skills and MCP servers complement each other perfectly:
- **MCP servers provide the tools** (external integrations)
- **Skills provide the workflows** (instructions on how to use those tools)

### Pattern 1: Skills as Instruction Containers (Recommended)

Even though Skills cannot formally reference MCP tools (compile-time vs. runtime), you can create Skills that provide **workflow instructions** for using MCP server functions:

```csharp
[Skill]
public Skill DebugConfigFile()
{
    return SkillFactory.Create(
        "DebugConfigFile",
        "Debug configuration file issues systematically",
        @"Follow these steps to debug config files:

        1. Use read_file to read the configuration file
        2. Analyze the contents for common issues:
           - Missing required keys
           - Malformed JSON/YAML syntax
           - Invalid values or types
        3. If fixes are needed, use write_file to save the corrected version
        4. Use read_file again to verify the fix was applied

        IMPORTANT: Always validate file structure before writing changes.
        For JSON files, ensure valid JSON syntax. For YAML, check indentation."
    );
}
```

**How it works:**
1. Developer knows MCP server functions from documentation (e.g., `read_file`, `write_file` from filesystem server)
2. Skill provides step-by-step instructions mentioning those function names
3. At runtime, agent reads Skill instructions, sees it needs `read_file`, finds it in available MCP tools
4. No compile-time dependency needed - instructions are plain text

**Benefits:**
- ✅ **Workflow guidance** - Skill provides the "how" (process/best practices)
- ✅ **MCP provides the "what"** - MCP server provides actual tool implementations
- ✅ **No compile-time coupling** - Function names mentioned in instructions only
- ✅ **Reusable patterns** - Create Skills for common MCP workflows

**Example with filesystem MCP server:**

```csharp
// MCP.json - Load filesystem server
{
  "servers": [
    {
      "name": "filesystem",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "/app/config"],
      "enableScoping": true
    }
  ]
}

// Skills.cs - Provide workflows for using filesystem tools
[Skill]
public Skill BackupConfig()
{
    return SkillFactory.Create(
        "BackupConfig",
        "Create backup of configuration files",
        @"To backup config files:
        1. Use read_file to read the original config
        2. Use write_file to save a copy with .backup extension
        3. Verify backup was created using read_file"
    );
}

[Skill]
public Skill RestoreConfig()
{
    return SkillFactory.Create(
        "RestoreConfig",
        "Restore configuration from backup",
        @"To restore config:
        1. Use read_file to verify backup file exists
        2. Use read_file to read backup contents
        3. Use write_file to restore to original location
        4. Verify restoration using read_file"
    );
}

// Agent setup
var agent = new AgentBuilder()
    .WithMCP("MCP.json")           // MCP filesystem tools
    .WithPlugin<ConfigSkills>()    // Workflow skills
    .Build();
```

**Agent workflow:**
```
User: "Backup my app config"

Agent: Calls BackupConfig skill
→ Instructions: "Use read_file... then write_file..."

Agent: Expands MCP_filesystem container
→ Available: read_file, write_file, list_directory...

Agent: Calls read_file("app.config")
→ Result: "{ 'setting': 'value' }"

Agent: Calls write_file("app.config.backup", "{ 'setting': 'value' }")
→ Result: "File written successfully"

Agent: Responds "Backup created at app.config.backup"
```

---

### Pattern 2: Mixed Native + MCP Workflows

Skills can reference **native plugins** while providing instructions for **MCP tools**:

```csharp
[Skill]
public Skill FullSystemDebug()
{
    return SkillFactory.Create(
        "FullSystemDebug",
        "Complete system diagnostic workflow",
        @"Comprehensive debugging workflow:

        1. Check application logs (use native logging functions)
        2. Verify config files (use read_file from filesystem MCP)
        3. Test database connection (use query from postgres MCP)
        4. Check API health (use fetch from web MCP)

        Report all findings in structured format.",

        // Formally reference native plugins
        "LoggingPlugin.GetRecentErrors",
        "LoggingPlugin.GetWarnings"

        // MCP tools mentioned in instructions only (read_file, query, fetch)
    );
}

var agent = new AgentBuilder()
    .WithMCP("MCP.json")              // MCP servers (filesystem, postgres, web)
    .WithPlugin<LoggingPlugin>()      // Native plugin (referenced in skill)
    .WithPlugin<DebugSkills>()        // Skills
    .Build();
```

**Agent sees:**
```
Available Functions:
- FullSystemDebug (Skill - provides workflow)
- MCP_filesystem (MCP server - file operations)
- MCP_postgres (MCP server - database access)
- MCP_web (MCP server - HTTP requests)
```

When `FullSystemDebug` is activated:
- `LoggingPlugin.GetRecentErrors` becomes visible (formally claimed)
- Instructions guide agent to use `read_file`, `query`, `fetch` from MCP servers

---

### Key Differences: Native vs. MCP in Skills

| Aspect | Native Plugins | MCP Tools |
|--------|---------------|-----------|
| **In Skills** | Formal references (`"Plugin.Function"`) | Instructions only (text mentions) |
| **Validation** | ✅ Compile-time (source generator) | ❌ Runtime only (no validation) |
| **Claiming** | ✅ Functions hidden until skill activates | ❌ Not claimed (always available when container expanded) |
| **Safety** | ✅ Guaranteed to exist | ⚠️ Error if MCP server doesn't have the function |
| **Use Case** | Core application logic | External integrations, workflows |

---

### Best Practices

✅ **DO: Use Skills for MCP Workflows**
```csharp
// Good: Skill provides structured workflow for MCP tools
[Skill]
public Skill AnalyzeLogs()
{
    return SkillFactory.Create(
        "AnalyzeLogs",
        "Analyze log files for errors",
        @"1. Use list_directory to find .log files
        2. Use read_file for each log
        3. Look for ERROR, WARN patterns
        4. Summarize findings"
    );
}
```

✅ **DO: Document MCP Requirements**
```csharp
// Good: Clear documentation of required MCP server
/// <summary>
/// Requires filesystem MCP server with: read_file, write_file, list_directory
/// </summary>
[Skill]
public Skill FileAnalysis() { ... }
```

❌ **DON'T: Try to Formally Reference MCP Tools**
```csharp
// Bad: Won't compile - MCP tools don't exist at compile-time
[Skill]
public Skill BadSkill()
{
    return SkillFactory.Create(
        "BadSkill",
        "...",
        "...",
        "MCP_filesystem.read_file"  // ❌ Error: Cannot resolve
    );
}
```

❌ **DON'T: Assume MCP Functions Exist**
```csharp
// Bad: No validation that MCP server has these functions
[Skill]
public Skill RiskySkill()
{
    return SkillFactory.Create(
        "RiskySkill",
        "...",
        "Use nonexistent_function..."  // ⚠️ Runtime error if not available
    );
}
```

---

## Troubleshooting

### Server Not Starting

Check server command is valid:
```bash
# Test manually
npx -y @modelcontextprotocol/server-filesystem /path/to/directory
```

### Tools Not Loading

Enable debug logging:
```csharp
var logger = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Debug)
).CreateLogger<MCPClientManager>();
```

### Permission Errors

**Problem**: MCP tools require permission by default but no handler is configured.

**Solution**: Enable permission handling:
```csharp
var agent = new AgentBuilder()
    .WithMCP("MCP.json")
    .WithPermissions()  // Add permission handler
    .Build();
```

**Alternative**: Disable permissions for read-only servers in `MCP.json`:
```json
{
  "name": "context7",
  "requiresPermission": false  // Only for trusted, read-only servers
}
```

**Testing**: Auto-approve all permissions during development:
```csharp
var agent = new AgentBuilder()
    .WithMCP("MCP.json")
    .WithAutoApprovePermissions()  // ⚠️ Testing only!
    .Build();
```

### Timeout Issues

Increase timeout for slow servers:
```json
{
  "timeout": 60000  // 60 seconds
}
```

---

## Best Practices

### ✅ DO: Use Scoping for Production

```csharp
enableScoping: true  // Groups tools, saves tokens
```

### ✅ DO: Provide Meaningful Descriptions

```json
{
  "description": "Read and analyze application logs from /var/log"
}
```

### ✅ DO: Use Environment Variables for Secrets

```json
{
  "environment": {
    "API_KEY": "${API_KEY}"
  }
}
```

### ✅ DO: Disable Unused Servers

```json
{
  "enabled": false  // Skip loading
}
```

### ❌ DON'T: Load All Community Servers

Only load what you need - each server adds startup overhead.

### ❌ DON'T: Hardcode Secrets

Never commit API keys or tokens to version control.

### ❌ DON'T: Skip Error Handling

Always handle server connection failures gracefully.

---

## Summary

- **MCP = External tool ecosystem** for agents
- **Load via manifest** at build time or runtime
- **Scoping recommended** for token efficiency
- **Description priority**: User override → Server metadata → Auto-generated (function list always appended)
- **Per-server scoping control** via `enableScoping` in JSON config
- **Works alongside** native plugins and skills
- **Skills + MCP pattern**: Skills provide workflow instructions, MCP provides tools
- **Cannot formally reference MCP in skills** (runtime vs. compile-time), but can mention function names in instructions
- **Container pattern** provides same UX as native scoped plugins

For more on scoping behavior, see [Scoping.md](Scoping.md).

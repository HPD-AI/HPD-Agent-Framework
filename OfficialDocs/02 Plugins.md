## Your First Plugin

Give your agent capabilities with function calling.

## Table of Contents
- [Your First Plugin](#your-first-plugin)
- [Fundamental Function Attributes](#fundamental-function-attributes)
- [Advanced Attributes](#advanced-attributes)
- [Dependency Injection](#dependency-injection)
- [Best Practices](#best-practices)

---

## Your First Plugin

A plugin is just a C# class with methods marked with `[AIFunction]`:

```csharp
public class CalculatorPlugin
{
    [AIFunction]
    [AIDescription("Add two numbers together")]
    public int Add(
        [AIDescription("First addend")] int a,
        [AIDescription("Second addend")] int b)
    {
        return a + b;
    }
}
```
Register it with your agent:

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a helpful math assistant")
    .WithPlugin<CalculatorPlugin>()
    .Build();
```

Now the agent can do math:

```csharp
await foreach (var evt in agent.RunAsync("What's 125 + 847?", thread))
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Text);
}
```

**Output:**
```
125 + 847 equals 972.
```

**What happened:**
1. Agent received "What's 125 + 847?"
2. Agent decided to call `Add(125, 847)`
3. Your function returned `972`
4. Agent used the result to answer naturally

---

## Fundamental Function Attributes

The `[AIFunction]` attribute enables the LLM to be able to call the function.
The `[AIDescription]` attribte tells the LLM what the function does.

### Basic Usage

```csharp
[AIFunction]
[AIDescription("Get the current weather for a city")]
public string GetWeather([AIDescription("City name")] string city)
{
    return $"The weather in {city} is sunny, 72°F";
}
```

### Parameter Descriptions

```csharp
[AIFunction]
[AIDescription("Search for products in the catalog")]
public List<Product> SearchProducts(
    [AIDescription("Search query (product name, category, or keywords)")]
    string query,

    [AIDescription("Maximum number of results to return (default: 10)")]
    int limit = 10)
{
    // Implementation
}
```
**Why this matters:** Parameter descriptions help the LLM pass the right values.

---

## Advanced Attributes

#### `CollapseAttribute`

Use `[Collapse]` on plugin classes to group related functions behind a single expandable container.
Collapsed containers keep your agent's tool surface small; the agent expands a container only when it decides the contained functions are relevant.

Constructor parameters:
- `description` (required): Short explanation of what the container provides and when to expand it.
- `FunctionResult` (optional): One-time message returned when the container first expands. Use this for activation confirmations or to list available capabilities.
- `SystemPrompt` (optional): Persistent instructions injected into the system prompt on every iteration after expansion. Use this for behavioral rules, safety guidelines, or workflow requirements that must persist throughout the conversation.

**Important**: Both instruction contexts can be string literals or calls to methods/properties (static or instance) that return strings. Instance methods can access plugin state for dynamic instructions.

> **Deprecated**: `postExpansionInstructions` is deprecated in favor of the dual-context approach. For backward compatibility, it maps to `FunctionResult`.

##### When to Use Each Context

- **`FunctionResult`**: For one-time activation messages
  - "Search plugin activated. Available functions: WebSearch, CodeSearch, DocumentSearch"
  - "Financial analysis tools now available. Run GetStockPrice to start."
  - Shown once in the function result when the container expands

- **`SystemPrompt`**: For persistent behavioral rules
  - "Always paginate large datasets. Prefer provider-specific searches for accuracy."
  - "CRITICAL: Verify all trades before execution. Never auto-execute without confirmation."
  - Injected into system prompt on every iteration after activation

##### Example 1: Activation Message Only

```csharp
[Collapse("Search operations across web, code, and documentation",
    FunctionResult: "Search plugin activated. Available: WebSearch, CodeSearch, DocumentSearch.")]
public class SearchPlugin
{
    [AIFunction]
    [AIDescription("Search the web for a query")]
    public Task<string> WebSearch(string query) => ...;

    [AIFunction]
    public Task<string> CodeSearch(string query) => ...;

    [AIFunction]
    public Task<string> DocumentSearch(string query) => ...;
}
```

##### Example 2: Persistent Rules Only

```csharp
[Collapse("Financial trading operations",
   SystemPrompt: @"CRITICAL TRADING RULES:
- ALWAYS verify trade parameters before execution
- NEVER auto-execute trades without explicit user confirmation
- Check account balance before placing orders
- Log all trade attempts for audit compliance")]
public class TradingPlugin
{
    [AIFunction]
    public Task<TradeResult> ExecuteTrade(string symbol, decimal amount) => ...;
}
```

##### Example 3: Both Contexts (Recommended)

```csharp
[Collapse("Database operations for user and order management",
    FunctionResult: "Database plugin activated. Available: QueryUsers, QueryOrders, UpdateUser.",
   SystemPrompt: @"DATABASE SAFETY PROTOCOLS:
- Always use parameterized queries (never string concatenation)
- Limit query results to 100 rows by default unless user specifies otherwise
- Log all write operations for audit trail")]
public class DatabasePlugin
{
    [AIFunction]
    public Task<List<User>> QueryUsers(string filter) => ...;

    [AIFunction]
    public Task<List<Order>> QueryOrders(string userId) => ...;

    [AIFunction]
    public Task UpdateUser(string userId, UserUpdate update) => ...;
}
```

##### Example 4: Dynamic Instructions from Method Calls

```csharp
public static class SearchInstructionBuilder
{
    public static string GetActivationMessage()
    {
        return $"Search plugin v{GetVersion()} activated. Available: WebSearch, CodeSearch, DocumentSearch.";
    }

    public static string GetSearchRules()
    {
        // Instructions could be loaded from a file, database, or built dynamically
        return $@"SEARCH PROTOCOL (v{GetVersion()}):
- Prefer provider-specific searches for better accuracy
- Use pagination for large datasets (max 50 results per page)
- Cache results for 5 minutes to reduce API calls";
    }

    private static string GetVersion() => "2.1";
}

[Collapse("Search operations across web, code, and documentation",
    FunctionResult: SearchInstructionBuilder.GetActivationMessage(),
   SystemPrompt: SearchInstructionBuilder.GetSearchRules())]
public class DynamicSearchPlugin
{
    [AIFunction]
    public Task<string> WebSearch(string query) => ...;

    [AIFunction]
    public Task<string> CodeSearch(string query) => ...;
}
```

##### Example 5: Instance Methods for State-Based Instructions

Instance methods can access plugin state to generate dynamic instructions based on configuration, environment, or runtime values:

```csharp
[Collapse("Environment-aware configuration plugin",
    FunctionResult: GetActivationMessage(),
   SystemPrompt: GetEnvironmentRules())]
public class ConfigurationPlugin
{
    private readonly string _environment;
    private readonly string _version;
    private readonly DateTime _activatedAt;

    public ConfigurationPlugin(IConfiguration config)
    {
        _environment = config["Environment"] ?? "Development";
        _version = config["Version"] ?? "1.0";
        _activatedAt = DateTime.UtcNow;
    }

    public string GetActivationMessage()
    {
        return $"Configuration plugin v{_version} activated in {_environment} environment at {_activatedAt:HH:mm:ss} UTC";
    }

    public string GetEnvironmentRules()
    {
        return _environment switch
        {
            "Production" => @"PRODUCTION RULES:
- ALWAYS validate configuration changes before applying
- NEVER expose sensitive configuration values
- Log all configuration reads for audit compliance",
            "Staging" => @"STAGING RULES:
- Validate configuration changes before applying
- Allow reading sensitive values for debugging
- Log configuration changes",
            _ => @"DEVELOPMENT RULES:
- Allow direct configuration changes
- Allow reading all configuration values
- Minimal logging"
        };
    }

    [AIFunction]
    [AIDescription("Get a configuration value")]
    public Task<string> GetConfig(string key) => ...;

    [AIFunction]
    [AIDescription("Update a configuration value")]
    public Task UpdateConfig(string key, string value) => ...;
}
```

**Benefits of instance methods:**
- Access plugin fields and properties for runtime-specific instructions
- Adapt instructions based on constructor-injected dependencies
- Generate dynamic content based on environment, configuration, or state

##### Instruction Persistence

By default, `SystemPrompt` instructions are cleared at the end of each message turn to keep prompts clean. To make instructions persist across multiple turns, configure:

```csharp
agent.Config.Collapsing.PersistSystemPromptInjections = true;
```

**Warning**: Persistent injections can cause prompt bloat in long conversations. Only enable if your workflow requires container rules to remain active across multiple user messages.
---
## Dependency Injection

Plugins can use constructor injection:

```csharp
public class DatabasePlugin
{
    private readonly IDbContext _db;
    private readonly ILogger<DatabasePlugin> _logger;

    public DatabasePlugin(IDbContext db, ILogger<DatabasePlugin> logger)
    {
        _db = db;
        _logger = logger;
    }

    [Function(Description = "Query users from database")]
    public async Task<List<User>> GetUsersAsync(string filter)
    {
        _logger.LogInformation("Querying users with filter: {Filter}", filter);
        return await _db.Users.Where(u => u.Name.Contains(filter)).ToListAsync();
    }
}
```

Register with a service provider:

```csharp
var services = new ServiceCollection()
    .AddDbContext<IDbContext, MyDbContext>()
    .AddLogging()
    .BuildServiceProvider();

var agent = new AgentBuilder()
    .WithServiceProvider(services)  // Provide DI container
    .WithPlugin<DatabasePlugin>()   // Plugin resolved from DI
    .Build();
```

**Note:** For plugins with dependencies, you must provide the service provider.

---
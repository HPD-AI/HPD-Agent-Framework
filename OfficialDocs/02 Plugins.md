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
- `postExpansionInstructions` (optional): Guidance shown after expansion (best practices, safety notes, or workflow tips). These instructions only consume tokens when the container is expanded.

Example:

```csharp
[Collapse("Search operations across web, code, and documentation",
    postExpansionInstructions: @"When expanded, prefer provider-specific searches for accuracy. Use pagination when querying large datasets.")]
public class SearchPlugin
{
    [AIFunction]
    [AIDescription("Search the web for a query")]
    public Task<string> WebSearch(string query) => ...;

    [AIFunction]
    public Task<string> CodeSearch(string query) => ...;
}
```
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
# Dynamic Metadata & Context System

## Overview

HPD-Agent functions can **adapt at runtime** based on configuration. Function descriptions, parameter lists, and even entire functions can change based on the runtime context.

This allows you to build **adaptive agents** where the tool surface automatically matches available capabilities.

---

## The Problem: Static Functions Don't Fit Dynamic Systems

### Traditional Approach (Static)

```csharp
[AIFunction]
[AIDescription("Search the web using Tavily")]
public async Task<string> TavilySearch(string query) { ... }

[AIFunction]
[AIDescription("Search the web using Brave")]
public async Task<string> BraveSearch(string query) { ... }

[AIFunction]
[AIDescription("Search the web using Bing")]
public async Task<string> BingSearch(string query) { ... }
```

**Problems:**
- Hardcoded provider names in descriptions
- Agent sees 3 functions even if user only configured Tavily
- No way to indicate which provider is default
- Descriptions don't reflect actual configuration

---

## The Solution: Dynamic Metadata with Context

### Step 1: Define a Context Class

```csharp
public class WebSearchContext : IPluginMetadataContext
{
    // Strongly-typed properties for compile-time validation
    public bool HasTavilyProvider { get; private set; }
    public bool HasBraveProvider { get; private set; }
    public bool HasBingProvider { get; private set; }
    public string DefaultProvider { get; private set; }
    public string ConfiguredProviders { get; private set; }

    public WebSearchContext(IEnumerable<IWebSearchConnector> connectors, string? defaultProvider = null)
    {
        var connectorList = connectors.ToList();

        // Initialize based on actual configuration
        HasTavilyProvider = connectorList.Any(c => c.ProviderName == "tavily");
        HasBraveProvider = connectorList.Any(c => c.ProviderName == "brave");
        HasBingProvider = connectorList.Any(c => c.ProviderName == "bing");

        DefaultProvider = defaultProvider ?? connectorList.FirstOrDefault()?.ProviderName ?? "none";
        ConfiguredProviders = string.Join(", ", connectorList.Select(c => c.ProviderName));
    }

    // IPluginMetadataContext implementation
    public T? GetProperty<T>(string propertyName, T? defaultValue = default)
    {
        return propertyName.ToLowerInvariant() switch
        {
            "hastavilyprovider" => (T)(object)HasTavilyProvider,
            "hasbraveprovider" => (T)(object)HasBraveProvider,
            "hasbingprovider" => (T)(object)HasBingProvider,
            "defaultprovider" => (T)(object)DefaultProvider,
            "configuredproviders" => (T)(object)ConfiguredProviders,
            _ => defaultValue
        };
    }

    public bool HasProperty(string propertyName) => propertyName.ToLowerInvariant() switch
    {
        "hastavilyprovider" or "hasbraveprovider" or "hasbingprovider"
            or "defaultprovider" or "configuredproviders" => true,
        _ => false
    };

    public IEnumerable<string> GetPropertyNames() =>
        new[] { "HasTavilyProvider", "HasBraveProvider", "HasBingProvider",
                "DefaultProvider", "ConfiguredProviders" };
}
```

**Key Points:**
- Implements `IPluginMetadataContext`
- Has strongly-typed properties (e.g., `HasTavilyProvider`, `DefaultProvider`)
- Properties are populated based on actual runtime configuration
- Implements dictionary-like access for source generator

---

### Step 2: Use `[AIFunction<TContext>]` with Dynamic Descriptions

```csharp
public class WebSearchPlugin
{
    [AIFunction<WebSearchContext>]
    [AIDescription("Search the web using {context.DefaultProvider} (configured: {context.ConfiguredProviders})")]
    public async Task<string> WebSearch(
        [AIDescription("Search query to execute")] string query,
        [AIDescription("Provider to use (available: {context.ConfiguredProviders})")] string? provider = null)
    {
        // Implementation uses context to route to correct provider
    }
}
```

**Template Syntax:**
- `{context.PropertyName}` → Replaced with actual value at runtime
- Example: `{context.DefaultProvider}` → `"tavily"`
- Example: `{context.ConfiguredProviders}` → `"tavily, brave, bing"`

---

### Step 3: Register with Context

```csharp
// Create context based on actual configuration
var connectors = new List<IWebSearchConnector>
{
    new TavilyConnector(tavilyApiKey),
    new BraveConnector(braveApiKey)
};

var context = new WebSearchContext(connectors, defaultProvider: "tavily");

// Register plugin with context
var agent = new AgentBuilder()
    .WithPlugin<WebSearchPlugin>(context)
    .Build();
```

---

### What the Agent Sees

**Configuration: Tavily + Brave, Default = Tavily**

```
Available Functions:
- WebSearch: Search the web using tavily (configured: tavily, brave)

Parameters:
- query (string): Search query to execute
- provider (string, optional): Provider to use (available: tavily, brave)
```

**Configuration: Only Bing, Default = Bing**

```
Available Functions:
- WebSearch: Search the web using bing (configured: bing)

Parameters:
- query (string): Search query to execute
- provider (string, optional): Provider to use (available: bing)
```

**The description adapts to actual configuration!**

---

## IPluginMetadataContext Interface

All context classes must implement this interface:

```csharp
public interface IPluginMetadataContext
{
    /// <summary>
    /// Gets a property value by name with optional default
    /// </summary>
    T? GetProperty<T>(string propertyName, T? defaultValue = default);

    /// <summary>
    /// Checks if a property exists
    /// </summary>
    bool HasProperty(string propertyName);

    /// <summary>
    /// Gets all available property names (for validation)
    /// </summary>
    IEnumerable<string> GetPropertyNames();
}
```

**Why this interface?**
- Allows source generator to validate property references at compile-time
- Enables runtime template resolution
- Provides type-safe access to context properties

---

## Context Property Types

### Boolean Properties
```csharp
public class MyContext : IPluginMetadataContext
{
    public bool HasFeatureX { get; }
    public bool IsEnabled { get; }
}

// Usage in descriptions
[AIDescription("Advanced search (requires premium: {context.IsEnabled})")]
```

### String Properties
```csharp
public class MyContext : IPluginMetadataContext
{
    public string DefaultDatabase { get; }
    public string AvailableProviders { get; }
}

// Usage in descriptions
[AIDescription("Query {context.DefaultDatabase} database (available: {context.AvailableProviders})")]
```

### Numeric Properties
```csharp
public class MyContext : IPluginMetadataContext
{
    public int MaxRetries { get; }
    public double Version { get; }
}

// Usage in descriptions
[AIDescription("Upload with max {context.MaxRetries} retries (API v{context.Version})")]
```

---

## Advanced Context Example

```csharp
public class DatabaseContext : IPluginMetadataContext
{
    // Connection info
    public string DefaultDatabase { get; }
    public string AvailableDatabases { get; }

    // Capabilities
    public bool HasReadAccess { get; }
    public bool HasWriteAccess { get; }
    public bool HasTransactionSupport { get; }

    // Configuration
    public int MaxConnections { get; }
    public int TimeoutSeconds { get; }
    public string DatabaseEngine { get; }  // e.g., "PostgreSQL", "MySQL", "SQL Server"

    public DatabaseContext(
        IReadOnlyList<string> databases,
        string defaultDb,
        DatabasePermissions permissions,
        DatabaseCapabilities capabilities)
    {
        DefaultDatabase = defaultDb;
        AvailableDatabases = string.Join(", ", databases);

        HasReadAccess = permissions.CanRead;
        HasWriteAccess = permissions.CanWrite;
        HasTransactionSupport = capabilities.SupportsTransactions;

        MaxConnections = capabilities.MaxConnections;
        TimeoutSeconds = capabilities.TimeoutSeconds;
        DatabaseEngine = capabilities.Engine;
    }

    // IPluginMetadataContext implementation...
}
```

**Usage:**
```csharp
[AIFunction<DatabaseContext>]
[AIDescription("Query {context.DefaultDatabase} using {context.DatabaseEngine} (timeout: {context.TimeoutSeconds}s)")]
public async Task<string> QueryDatabase(
    [AIDescription("SQL query to execute")] string query,
    [AIDescription("Database to query (available: {context.AvailableDatabases})")] string? database = null)
{
    // Implementation
}
```

**Agent sees (PostgreSQL, ReadOnly, timeout=30):**
```
QueryDatabase: Query mydb using PostgreSQL (timeout: 30s)
Parameters:
- query (string): SQL query to execute
- database (string, optional): Database to query (available: mydb, testdb, analytics)
```

---

## Benefits of Dynamic Metadata

### 1. Self-Documenting Configuration
Agent descriptions automatically reflect what's actually available:
- ✅ "Search using tavily (configured: tavily, brave)"
- ❌ "Search the web" (doesn't tell agent what's configured)

### 2. Reduced Confusion
Agent doesn't see references to unavailable features:
- ✅ If only Bing configured → description mentions only Bing
- ❌ Static description mentions all providers even if not configured

### 3. Better Agent Decisions
Agent can make informed choices:
- Sees default provider upfront
- Knows which alternatives are available
- Understands capabilities/limitations

### 4. Single Source of Truth
Configuration flows from runtime → context → descriptions:
```
User Config → Context Properties → Template Resolution → Agent Sees
```

No hardcoded descriptions that go stale.

---

## When to Use Dynamic Metadata

### ✅ Use Dynamic Metadata When:
- Functions depend on runtime configuration (API keys, databases, providers)
- You have multiple implementations of same functionality (search providers, LLM models)
- Capabilities vary based on deployment environment
- You want descriptions to reflect actual available features

### ❌ Use Static Descriptions When:
- Function behavior is fixed and never changes
- No configuration or runtime dependencies
- Simple utility functions (timestamp, GUID generation)

---

## Complete Example

```csharp
// Context
public class FinancialAnalysisContext : IPluginMetadataContext
{
    public bool HasRealTimeData { get; }
    public bool HasHistoricalData { get; }
    public string DataSources { get; }
    public int YearsOfHistory { get; }

    public FinancialAnalysisContext(
        bool hasRealTime,
        bool hasHistorical,
        int yearsOfHistory)
    {
        HasRealTimeData = hasRealTime;
        HasHistoricalData = hasHistorical;
        YearsOfHistory = yearsOfHistory;

        var sources = new List<string>();
        if (hasRealTime) sources.Add("Real-time");
        if (hasHistorical) sources.Add("Historical");
        DataSources = string.Join(", ", sources);
    }

    // IPluginMetadataContext implementation...
}

// Plugin with dynamic descriptions
public class FinancialAnalysisPlugin
{
    [AIFunction<FinancialAnalysisContext>]
    [AIDescription("Analyze stock trends over {context.YearsOfHistory} years using {context.DataSources}")]
    public async Task<string> AnalyzeTrends(
        [AIDescription("Company ticker symbol")] string ticker,
        [AIDescription("Include comparison with sector average")] bool includeSectorComparison = false)
    {
        // Implementation
    }
}

// Registration
var context = new FinancialAnalysisContext(
    hasRealTime: true,
    hasHistorical: true,
    yearsOfHistory: 10
);

var agent = new AgentBuilder()
    .WithPlugin<FinancialAnalysisPlugin>(context)
    .Build();
```

**Agent sees:**
```
AnalyzeTrends: Analyze stock trends over 10 years using Real-time, Historical
```

**If configuration changes (only 5 years historical data):**
```csharp
var context = new FinancialAnalysisContext(
    hasRealTime: false,
    hasHistorical: true,
    yearsOfHistory: 5
);
```

**Agent sees:**
```
AnalyzeTrends: Analyze stock trends over 5 years using Historical
```

**Same code, different descriptions based on actual capabilities!**

---

## Best Practices

### ✅ DO: Use strongly-typed properties
```csharp
public class MyContext : IPluginMetadataContext
{
    public bool HasFeature { get; }  // ✅ Compile-time validation
    public string Provider { get; }
}
```

### ✅ DO: Compute derived properties
```csharp
public string ConfiguredProviders =>
    string.Join(", ", _connectors.Select(c => c.Name));
```

### ✅ DO: Make context immutable
```csharp
public bool HasTavilyProvider { get; private set; }  // Set in constructor only
```

### ❌ DON'T: Use mutable context
```csharp
public bool HasFeature { get; set; }  // ❌ Don't allow changes after creation
```

### ❌ DON'T: Return null from GetProperty for existing properties
```csharp
public T? GetProperty<T>(string propertyName, T? defaultValue = default)
{
    return propertyName.ToLowerInvariant() switch
    {
        "hasprovider" => (T)(object)HasProvider,
        _ => defaultValue  // ✅ Return default for unknown properties
    };
}
```

---

## Next Steps

- Learn about [Conditional Functions](Conditional-Functions.md) to hide/show entire functions based on context
- Explore [Conditional Parameters](Conditional-Parameters.md) to dynamically adjust function schemas
- See [Complete Examples](Examples.md) for real-world context implementations

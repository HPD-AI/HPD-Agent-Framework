# Conditional Functions and Parameters

## Overview

HPD-Agent allows functions and parameters to **appear or disappear** based on runtime context. This creates truly adaptive AI agents where the tool surface automatically matches available capabilities.

---

## Conditional Functions

### The Problem

```csharp
// Traditional: All functions always visible
[AIFunction]
public async Task<string> TavilySearch(string query) { ... }

[AIFunction]
public async Task<string> BraveSearch(string query) { ... }

[AIFunction]
public async Task<string> BingSearch(string query) { ... }
```

**Agent sees:**
```
- TavilySearch
- BraveSearch
- BingSearch
```

**Even if user only configured Tavily!** Agent might try to call unavailable functions.

---

### The Solution: `[ConditionalFunction]`

```csharp
public class WebSearchPlugin
{
    [AIFunction<WebSearchContext>]
    [ConditionalFunction("HasTavilyProvider")]
    [AIDescription("Search using Tavily")]
    public async Task<string> TavilySearch(string query) { ... }

    [AIFunction<WebSearchContext>]
    [ConditionalFunction("HasBraveProvider")]
    [AIDescription("Search using Brave")]
    public async Task<string> BraveSearch(string query) { ... }

    [AIFunction<WebSearchContext>]
    [ConditionalFunction("HasBingProvider")]
    [AIDescription("Search using Bing")]
    public async Task<string> BingSearch(string query) { ... }
}
```

**Configuration: Only Tavily + Brave**

```csharp
var context = new WebSearchContext(
    connectors: new[] { tavilyConnector, braveConnector },
    defaultProvider: "tavily"
);
// context.HasTavilyProvider = true
// context.HasBraveProvider = true
// context.HasBingProvider = false
```

**Agent sees:**
```
- TavilySearch: Search using Tavily  ✅
- BraveSearch: Search using Brave    ✅
(BingSearch is completely hidden - doesn't exist for this agent)
```

**Function only appears when condition is true!**

---

## How It Works

1. **Define context properties** (compile-time)
```csharp
public class WebSearchContext : IPluginMetadataContext
{
    public bool HasTavilyProvider { get; }  // Strongly-typed property
}
```

2. **Reference in attribute** (compile-time validation)
```csharp
[ConditionalFunction("HasTavilyProvider")]  // ✅ Validated at build time
```

3. **Evaluate at runtime** (when agent loads)
```csharp
var context = new WebSearchContext(...);
// context.HasTavilyProvider = true → Function included
// context.HasTavilyProvider = false → Function excluded
```

---

## Conditional Expression Syntax

### Simple Boolean Property
```csharp
[ConditionalFunction("HasTavilyProvider")]
```
Function appears when `HasTavilyProvider == true`

---

### Comparison Operators
```csharp
[ConditionalFunction("MaxValue > 1000")]
[ConditionalFunction("YearsOfHistory >= 5")]
[ConditionalFunction("ProviderCount == 2")]
```

Supported: `>`, `<`, `>=`, `<=`, `==`, `!=`

---

### Boolean Logic
```csharp
[ConditionalFunction("HasTavilyProvider && HasBraveProvider")]  // Both required
[ConditionalFunction("HasTavilyProvider || HasBraveProvider")]  // At least one
[ConditionalFunction("AllowNegative == false")]                 // Explicit comparison
```

Supported: `&&` (AND), `||` (OR), `==`, `!=`

---

### Complex Expressions
```csharp
[ConditionalFunction("HasRealTimeData && YearsOfHistory >= 5")]
[ConditionalFunction("ProviderCount >= 2 && IsEnabled")]
[ConditionalFunction("(HasFeatureA || HasFeatureB) && IsEnabled")]
```

---

## Complete Example

```csharp
// Context
public class MathPluginContext : IPluginMetadataContext
{
    public long MaxValue { get; }
    public bool AllowNegative { get; }

    public MathPluginContext(long maxValue = 1000, bool allowNegative = true)
    {
        MaxValue = maxValue;
        AllowNegative = allowNegative;
    }

    // IPluginMetadataContext implementation...
}

// Plugin
public class MathPlugin
{
    // Always available
    [AIFunction<MathPluginContext>]
    [AIDescription("Add two numbers")]
    public long Add(long a, long b) => a + b;

    // Only if negatives allowed
    [AIFunction<MathPluginContext>]
    [ConditionalFunction("AllowNegative == true")]
    [AIDescription("Subtract b from a. Only available if negatives are allowed.")]
    public long Subtract(long a, long b) => a - b;

    // Only if negatives NOT allowed
    [AIFunction<MathPluginContext>]
    [ConditionalFunction("AllowNegative == false")]
    [AIDescription("Return absolute value. Only available if negatives are not allowed.")]
    public long Abs(long value) => Math.Abs(value);

    // Only if maxValue > 1000
    [AIFunction<MathPluginContext>]
    [ConditionalFunction("MaxValue > 1000")]
    [AIDescription("Square a number. Only available if maxValue > 1000.")]
    public long Square(long value) => value * value;

    // Only if maxValue < 500
    [AIFunction<MathPluginContext>]
    [ConditionalFunction("MaxValue < 500")]
    [AIDescription("Return minimum of two numbers. Only available if maxValue < 500.")]
    public long Min(long a, long b) => Math.Min(a, b);
}
```

### Configuration 1: AllowNegative=true, MaxValue=2000
```csharp
var context = new MathPluginContext(maxValue: 2000, allowNegative: true);
```

**Agent sees:**
```
- Add: Add two numbers                        ✅ Always available
- Subtract: Subtract b from a                 ✅ AllowNegative == true
- Square: Square a number                     ✅ MaxValue > 1000
(Abs hidden - AllowNegative must be false)
(Min hidden - MaxValue must be < 500)
```

### Configuration 2: AllowNegative=false, MaxValue=300
```csharp
var context = new MathPluginContext(maxValue: 300, allowNegative: false);
```

**Agent sees:**
```
- Add: Add two numbers                        ✅ Always available
- Abs: Return absolute value                  ✅ AllowNegative == false
- Min: Return minimum of two numbers          ✅ MaxValue < 500
(Subtract hidden - AllowNegative must be true)
(Square hidden - MaxValue must be > 1000)
```

**Same plugin, different function surfaces based on configuration!**

---

## Conditional Parameters

Parameters can also be conditionally visible within a function's schema.

### Basic Example

```csharp
[AIFunction<WebSearchContext>]
[AIDescription("Search with optional advanced features")]
public async Task<string> AdvancedSearch(
    [AIDescription("Search query")] string query,

    [ConditionalParameter("HasTavilyProvider")]
    [AIDescription("Use Tavily's advanced search")]
    bool useTavilyAdvanced = false,

    [ConditionalParameter("ProviderCount > 1")]
    [AIDescription("Aggregate results from all configured providers")]
    bool aggregateResults = false
)
{
    // Implementation
}
```

### Configuration 1: Only Bing
```csharp
var context = new WebSearchContext(
    connectors: new[] { bingConnector }
);
// context.HasTavilyProvider = false
// context.ProviderCount = 1
```

**Function schema:**
```json
{
  "name": "AdvancedSearch",
  "parameters": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "description": "Search query" }
    },
    "required": ["query"]
  }
}
```

Both `useTavilyAdvanced` and `aggregateResults` are **hidden** - conditions not met.

---

### Configuration 2: Tavily + Brave
```csharp
var context = new WebSearchContext(
    connectors: new[] { tavilyConnector, braveConnector }
);
// context.HasTavilyProvider = true
// context.ProviderCount = 2
```

**Function schema:**
```json
{
  "name": "AdvancedSearch",
  "parameters": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "description": "Search query" },
      "useTavilyAdvanced": {
        "type": "boolean",
        "description": "Use Tavily's advanced search"
      },
      "aggregateResults": {
        "type": "boolean",
        "description": "Aggregate results from all configured providers"
      }
    },
    "required": ["query"]
  }
}
```

Both conditional parameters are **visible** - conditions met!

---

## Financial Analysis Example

```csharp
public class FinancialAnalysisContext : IPluginMetadataContext
{
    public bool HasRealTimeData { get; }
    public bool HasHistoricalData { get; }
    public int YearsOfHistory { get; }
}

public class FinancialAnalysisPlugin
{
    // Only available if real-time data configured
    [AIFunction<FinancialAnalysisContext>]
    [ConditionalFunction("HasRealTimeData")]
    [AIDescription("Get real-time stock price")]
    public async Task<decimal> GetRealTimePrice(string ticker) { ... }

    // Only available if historical data configured
    [AIFunction<FinancialAnalysisContext>]
    [ConditionalFunction("HasHistoricalData")]
    [AIDescription("Analyze historical trends")]
    public async Task<string> AnalyzeHistoricalTrends(
        [AIDescription("Company ticker")] string ticker,

        // 5-year comparison only if we have 5+ years of data
        [ConditionalParameter("YearsOfHistory >= 5")]
        [AIDescription("Include 5-year comparison")]
        bool include5YearComparison = false,

        // 10-year comparison only if we have 10+ years
        [ConditionalParameter("YearsOfHistory >= 10")]
        [AIDescription("Include 10-year comparison")]
        bool include10YearComparison = false
    ) { ... }
}
```

### Configuration: Real-time + 10 years historical
```csharp
var context = new FinancialAnalysisContext(
    hasRealTime: true,
    hasHistorical: true,
    yearsOfHistory: 10
);
```

**Agent sees:**
```
Functions:
- GetRealTimePrice: Get real-time stock price               ✅
- AnalyzeHistoricalTrends: Analyze historical trends        ✅

AnalyzeHistoricalTrends parameters:
- ticker (string, required)
- include5YearComparison (bool, optional)                   ✅ >= 5 years
- include10YearComparison (bool, optional)                  ✅ >= 10 years
```

---

### Configuration: Only 3 years historical
```csharp
var context = new FinancialAnalysisContext(
    hasRealTime: false,
    hasHistorical: true,
    yearsOfHistory: 3
);
```

**Agent sees:**
```
Functions:
- AnalyzeHistoricalTrends: Analyze historical trends        ✅
(GetRealTimePrice hidden - no real-time data)

AnalyzeHistoricalTrends parameters:
- ticker (string, required)
(Both comparison parameters hidden - insufficient history)
```

---

## Benefits

### 1. Prevents Errors
Agent can't call functions that would fail due to missing dependencies:
```
❌ Without: Agent calls TavilySearch → Runtime error (Tavily not configured)
✅ With: TavilySearch function doesn't exist → Agent uses available provider
```

### 2. Cleaner Tool List
Agent only sees what's actually available:
```
❌ Without: 20 functions (10 require features not configured)
✅ With: 10 functions (only what's actually usable)
```

### 3. Self-Documenting Capabilities
Function presence indicates capability:
```
GetRealTimePrice exists → Agent knows real-time data is available
GetRealTimePrice absent → Agent knows only historical data available
```

### 4. Flexible Deployment
Same codebase, different function surfaces:
```
Production: HasWriteAccess=true → DeleteFile function available
Read-Only: HasWriteAccess=false → DeleteFile function hidden
```

---

## When to Use

### ✅ Use Conditional Functions When:
- Functions depend on API keys, credentials, or external services
- You have optional features based on subscription tier
- Functions require specific hardware/environment capabilities
- Deployment environments vary (dev vs prod)

### ✅ Use Conditional Parameters When:
- Function has optional features based on configuration
- Parameter only makes sense with certain capabilities
- You want to keep one function instead of multiple variants

### ❌ Don't Use When:
- Function behavior is always the same
- All configurations support the function
- Simple utility functions with no dependencies

---

## Best Practices

### ✅ DO: Explain why function is conditional
```csharp
[ConditionalFunction("HasTavilyProvider")]
[AIDescription("Search using Tavily. Only available if Tavily is configured.")]
```

### ✅ DO: Use meaningful property names
```csharp
public bool HasRealTimeData { get; }  // ✅ Clear intent
```

### ✅ DO: Group related conditional functions
```csharp
// All write operations conditional on same property
[ConditionalFunction("HasWriteAccess")]
public void InsertRecord(...) { ... }

[ConditionalFunction("HasWriteAccess")]
public void UpdateRecord(...) { ... }

[ConditionalFunction("HasWriteAccess")]
public void DeleteRecord(...) { ... }
```

### ❌ DON'T: Over-complicate conditions
```csharp
// ❌ Too complex
[ConditionalFunction("(HasFeatureA && (ProviderCount > 2 || IsEnabled)) && !IsDisabled")]

// ✅ Better: Compute in context
public bool CanUseAdvancedFeatures =>
    HasFeatureA && (ProviderCount > 2 || IsEnabled) && !IsDisabled;

[ConditionalFunction("CanUseAdvancedFeatures")]
```

### ❌ DON'T: Hide critical always-needed functions
```csharp
// ❌ Bad - agent needs this immediately
[ConditionalFunction("HasBasicAccess")]
public void RespondToUser(string message) { ... }
```

---

## Combining with Dynamic Descriptions

You can use both dynamic descriptions AND conditional logic:

```csharp
[AIFunction<WebSearchContext>]
[ConditionalFunction("HasTavilyProvider && HasBraveProvider")]
[AIDescription("Compare search results from {context.DefaultProvider} and other providers")]
public async Task<string> CompareSearchResults(
    string query,

    [ConditionalParameter("ProviderCount > 2")]
    [AIDescription("Include third provider ({context.ConfiguredProviders})")]
    bool includeThirdProvider = false
)
{
    // Implementation
}
```

**Configuration: Tavily + Brave + Bing, default=Tavily**

```
Function exists: ✅ (HasTavilyProvider && HasBraveProvider = true)

Description: "Compare search results from tavily and other providers"

Parameters:
- query (string)
- includeThirdProvider (bool): "Include third provider (tavily, brave, bing)"  ✅ (ProviderCount=3)
```

**Configuration: Only Tavily**

```
Function exists: ❌ (HasBraveProvider = false)
```

---

## Summary

- **`[ConditionalFunction]`** - Entire function appears/disappears based on context
- **`[ConditionalParameter]`** - Parameter appears/disappears in function schema
- **Context properties** drive conditions (strongly-typed, validated at compile-time)
- **Expressions** support boolean logic, comparisons, and complex conditions
- **Result:** Agent tool surface automatically matches available capabilities
- **Benefits:** Prevents errors, cleaner tool list, self-documenting, flexible deployment

**Next:** See [Permission System](Permission-System.md) for human-in-the-loop approval

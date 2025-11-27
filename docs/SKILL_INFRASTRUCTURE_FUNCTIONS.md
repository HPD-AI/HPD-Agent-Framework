# Skill Infrastructure Functions - Implementation Guide

## Overview

This document describes the standardized pattern for adding infrastructure functions that are conditionally visible based on skill state. This pattern was established with `read_skill_document()` and should be followed for all similar functions.

---

## When to Use This Pattern

Use this pattern when adding a function that:
- ✅ Is **NOT** part of a user-defined plugin
- ✅ Is **only useful when skills are active/expanded**
- ✅ Should be **shared across all skills** (not skill-specific)
- ✅ Needs **conditional visibility** based on skill metadata/state

**Examples:** `read_skill_document`, `list_skill_documents`, `get_skill_metadata`

**Do NOT use for:**
- ❌ Regular plugin functions
- ❌ Skill-specific functions
- ❌ Always-visible utilities
- ❌ User-facing plugin methods

---

## Architecture Overview

The pattern has three key layers:

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Plugin Layer (WHAT)                                      │
│    Defines the function implementation                      │
│    Location: HPD-Agent/Skills/[Category]/[Name]Plugin.cs   │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│ 2. Registration Layer (WHEN to register)                    │
│    Conditionally registers plugin in function pool          │
│    Location: HPD-Agent/Agent/AgentBuilder.cs                │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│ 3. Visibility Layer (WHEN to show)                          │
│    Conditionally shows function based on skill state        │
│    Location: HPD-Agent/Scoping/ToolVisibilityManager.cs     │
└─────────────────────────────────────────────────────────────┘
```

---

## Step-by-Step Implementation

### **Step 1: Create the Infrastructure Plugin**

**Location:** `HPD-Agent/Skills/[Category]/[FunctionName]Plugin.cs`

**Reference Example:** [DocumentRetrievalPlugin.cs](../HPD-Agent/Skills/DocumentStore/DocumentRetrievalPlugin.cs)

```csharp
namespace HPD_Agent.Skills.[Category];

/// <summary>
/// Infrastructure functions for [purpose].
/// These functions are conditionally visible based on skill state.
/// </summary>
public class [FunctionName]Plugin
{
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Parameterless constructor required for plugin registration.
    /// </summary>
    public [FunctionName]Plugin() { }

    /// <summary>
    /// Constructor with logger (optional).
    /// </summary>
    public [FunctionName]Plugin(ILogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// [Function description]
    /// This function is conditionally visible based on [condition].
    /// </summary>
    [AIFunction(
        Name = "[function_name]",
        Description = "[Clear description for LLM - explain when and how to use]")]
    public async Task<string> [FunctionName](
        [Description("Parameter description")] string param,
        [DependencyInjected] SomeService service,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Implementation
            var result = await service.DoSomethingAsync(param, cancellationToken);

            if (result == null)
            {
                return $"⚠️ [Resource] '{param}' not found.";
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to [action] {Param}", param);
            return $"⚠️ Error [action]: {ex.Message}\n" +
                   $"The [resource] may be temporarily unavailable. Please try again.";
        }
    }
}
```

**Best Practices:**

1. **Function Naming:**
   - Use **snake_case** for AIFunction Name (e.g., `read_skill_document`)
   - Use **PascalCase** for C# method name (e.g., `ReadSkillDocument`)
   - Make names descriptive and action-oriented

2. **Error Handling:**
   - Always wrap in try-catch
   - Return user-friendly error messages with ⚠️ emoji
   - Log errors for debugging
   - Never throw exceptions to LLM

3. **Dependency Injection:**
   - Use constructor injection for services
   - Support parameterless constructor for registration
   - Services are resolved automatically by the framework

4. **Return Types:**
   - Prefer `Task<string>` for LLM-facing functions
   - Format output clearly (markdown, structured text)
   - Include helpful context in responses

---

### **Step 2: Register Plugin Conditionally in AgentBuilder**

**Location:** `HPD-Agent/Agent/AgentBuilder.cs` in `BuildDependenciesAsync()` method

**Line Reference:** ~Line 900, after `ProcessSkillDocumentsAsync()`

**Pattern:**
```csharp
// Auto-register [function_name] plugin if [condition]
if ([condition_check])
{
    _pluginManager.RegisterPlugin<HPD_Agent.Skills.[Category].[FunctionName]Plugin>();
}
```

**Example from `read_skill_document`:**
```csharp
// Auto-register document retrieval plugin if document store is present
if (_documentStore != null)
{
    _pluginManager.RegisterPlugin<HPD_Agent.Skills.DocumentStore.DocumentRetrievalPlugin>();
}
```

**Common Conditions:**

| Condition | Check | Use Case |
|-----------|-------|----------|
| Document store exists | `_documentStore != null` | Document-related functions |
| Any skills registered | `_pluginManager.GetPluginRegistrations().Any(HasSkills)` | Skill utility functions |
| Specific service enabled | `_someService != null` | Service-dependent functions |
| Configuration flag | `_options.EnableFeature` | Feature-gated functions |

**⚠️ Important Notes:**
- This only registers the plugin in the **function pool**
- It does **NOT** make the function visible yet
- Visibility is controlled by Step 3
- Registration happens during agent build, before any turns

---

### **Step 3: Add Conditional Visibility Logic to ToolVisibilityManager**

**Location:** `HPD-Agent/Scoping/ToolVisibilityManager.cs` in `GetToolsForAgentTurn()` method

**Line Reference:** ~Line 210, in the function classification section, after the `functionsReferencedBySkills` check

**Pattern:**
```csharp
// Special handling for [function_name] - only visible when [condition]
else if (functionName.Equals("[function_name]", StringComparison.OrdinalIgnoreCase) ||
         functionName.Equals("[FunctionName]", StringComparison.OrdinalIgnoreCase))
{
    // This function should only be visible when [condition description]
    bool conditionMet = expandedSkills.Any(skillName =>
    {
        // Find the skill container
        var skillContainer = allTools.FirstOrDefault(t =>
            IsSkillContainer(t) &&
            GetSkillName(t).Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skillContainer == null) return false;

        // Check your custom condition using helper method
        return [YourConditionCheckMethod](skillContainer);
    });

    if (conditionMet)
    {
        expandedSkillFunctions.Add(tool);
    }
    // If condition not met, function is hidden
}
```

**Example from `read_skill_document`:**
```csharp
// Special handling for read_skill_document - only visible when a skill with documents is expanded
else if (functionName.Equals("read_skill_document", StringComparison.OrdinalIgnoreCase) ||
         functionName.Equals("ReadSkillDocument", StringComparison.OrdinalIgnoreCase))
{
    // This function should only be visible when a skill with documents is expanded
    bool anySkillWithDocumentsExpanded = expandedSkills.Any(skillName =>
    {
        // Find the skill container
        var skillContainer = allTools.FirstOrDefault(t =>
            IsSkillContainer(t) &&
            GetSkillName(t).Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skillContainer == null) return false;

        // Check if skill has documents
        return HasDocuments(skillContainer);
    });

    if (anySkillWithDocumentsExpanded)
    {
        expandedSkillFunctions.Add(tool);
    }
    // If no skill with documents is expanded, function is hidden
}
```

**Key Points:**
- Check **both** snake_case and PascalCase names (framework might use either)
- Use `StringComparison.OrdinalIgnoreCase` for case-insensitive matching
- Add to `expandedSkillFunctions` list (this ensures proper ordering and deduplication)
- Comment clearly what condition makes function visible
- The `expandedSkills` parameter contains names of currently expanded skills

---

### **Step 4: Add Helper Methods (if needed)**

**Location:** `HPD-Agent/Scoping/ToolVisibilityManager.cs` in the helper methods section

**Line Reference:** ~Line 320, after `GetReferencedPlugins()` method

**Pattern:**
```csharp
/// <summary>
/// Checks if a skill container meets [condition].
/// </summary>
private bool [ConditionCheckName](AIFunction skillContainer)
{
    if (skillContainer.AdditionalProperties == null)
        return false;

    // Check metadata from source generator or skill definition
    if (skillContainer.AdditionalProperties.TryGetValue("[MetadataKey]", out var value) &&
        [conditionCheck])
    {
        return true;
    }

    return false;
}
```

**Example from `read_skill_document`:**
```csharp
/// <summary>
/// Checks if a skill container has documents attached.
/// </summary>
private bool HasDocuments(AIFunction skillContainer)
{
    if (skillContainer.AdditionalProperties == null)
        return false;

    // Check for DocumentUploads
    if (skillContainer.AdditionalProperties.TryGetValue("DocumentUploads", out var uploadsObj) &&
        uploadsObj is Array uploadsArray && uploadsArray.Length > 0)
    {
        return true;
    }

    // Check for DocumentReferences
    if (skillContainer.AdditionalProperties.TryGetValue("DocumentReferences", out var refsObj) &&
        refsObj is Array refsArray && refsArray.Length > 0)
    {
        return true;
    }

    return false;
}
```

**Common Helper Patterns:**

#### Check for Metadata Key
```csharp
private bool HasMetadata(AIFunction skillContainer, string key)
{
    return skillContainer.AdditionalProperties?.ContainsKey(key) == true;
}
```

#### Check Array Property
```csharp
private bool HasArrayProperty(AIFunction skillContainer, string key)
{
    return skillContainer.AdditionalProperties?.TryGetValue(key, out var value) == true &&
           value is Array arr && arr.Length > 0;
}
```

#### Check for Specific Plugin Reference
```csharp
private bool ReferencesPlugin(AIFunction skillContainer, string pluginName)
{
    if (skillContainer.AdditionalProperties?.TryGetValue("ReferencedPlugins", out var plugins) != true)
        return false;

    return plugins is string[] pluginArray &&
           pluginArray.Contains(pluginName, StringComparer.OrdinalIgnoreCase);
}
```

#### Check Minimum Function Count
```csharp
private bool HasMinimumFunctions(AIFunction skillContainer, int minCount)
{
    if (skillContainer.AdditionalProperties?.TryGetValue("ReferencedFunctions", out var funcs) != true)
        return false;

    return funcs is string[] funcArray && funcArray.Length >= minCount;
}
```

**Available Metadata Keys from Source Generator:**
- `"IsSkill"` - bool, marks as skill container
- `"IsContainer"` - bool, marks as container
- `"ReferencedFunctions"` - string[], functions referenced by skill
- `"ReferencedPlugins"` - string[], plugins referenced by skill
- `"DocumentUploads"` - Dictionary<string,string>[], files to upload
- `"DocumentReferences"` - Dictionary<string,string>[], existing document refs
- `"ParentSkillContainer"` - string, parent scope if any

---

### **Step 5: Add Comprehensive Tests**

**Location:** `test/HPD-Agent.Tests/Scoping/ToolVisibilityManagerTests.cs`

**Line Reference:** Add in new test region at end of class, before closing brace

**Template:**
```csharp
#region [FunctionName] Conditional Visibility Tests

[Fact]
public void [FunctionName]_NotVisible_When[ConditionNotMet]()
{
    // Arrange: Set up scenario where condition is false
    var tools = new List<AIFunction>();
    tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: false)); // No docs
    tools.Add(Create[FunctionName]Function());
    tools.AddRange(CreatePluginFunctions("SomePlugin"));

    var explicitPlugins = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "SomePlugin");

    var manager = new ToolVisibilityManager(tools, explicitPlugins);

    // Act: Expand skill but condition not met
    var expandedSkills = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "SomeSkill");

    var visibleTools = manager.GetToolsForAgentTurn(
        tools.ToList(),
        ImmutableHashSet<string>.Empty,
        expandedSkills);

    // Assert: Function should NOT be visible
    visibleTools.Should().NotContain(t =>
        t.Name.Equals("[function_name]", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void [FunctionName]_Visible_When[ConditionMet]()
{
    // Arrange: Set up scenario where condition is true
    var tools = new List<AIFunction>();
    tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: true)); // Has docs
    tools.Add(Create[FunctionName]Function());
    tools.AddRange(CreatePluginFunctions("SomePlugin"));

    var explicitPlugins = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "SomePlugin");

    var manager = new ToolVisibilityManager(tools, explicitPlugins);

    // Act: Expand skill with condition met
    var expandedSkills = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "SkillWithCondition");

    var visibleTools = manager.GetToolsForAgentTurn(
        tools.ToList(),
        ImmutableHashSet<string>.Empty,
        expandedSkills);

    // Assert: Function SHOULD be visible
    visibleTools.Should().Contain(t =>
        t.Name.Equals("[function_name]", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void [FunctionName]_NotVisible_WhenNoSkillsExpanded()
{
    // Arrange: Skills exist but none expanded
    var tools = new List<AIFunction>();
    tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: true));
    tools.Add(Create[FunctionName]Function());

    var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

    // Act: No skills expanded
    var visibleTools = manager.GetToolsForAgentTurn(
        tools.ToList(),
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty);

    // Assert: Function should NOT be visible
    visibleTools.Should().NotContain(t =>
        t.Name.Equals("[function_name]", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void [FunctionName]_VisibleOnce_WhenMultipleConditionsMet()
{
    // Arrange: Multiple skills that meet condition
    var tools = new List<AIFunction>();
    tools.AddRange(CreateSkillsWithDocuments(parentScope: null, withDocuments: true));
    tools.Add(Create[FunctionName]Function());

    var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

    // Act: Expand multiple skills with condition met
    var expandedSkills = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Skill1",
        "Skill2");

    var visibleTools = manager.GetToolsForAgentTurn(
        tools.ToList(),
        ImmutableHashSet<string>.Empty,
        expandedSkills);

    // Assert: Function appears exactly once (deduplication)
    var count = visibleTools.Count(t =>
        t.Name.Equals("[function_name]", StringComparison.OrdinalIgnoreCase));
    count.Should().Be(1);
}

[Fact]
public void [FunctionName]_Visible_When[MixedScenario]()
{
    // Arrange: Mixed conditions - some meet, some don't
    var tools = new List<AIFunction>();
    var skillsWithCondition = CreateSkillsWithDocuments(parentScope: null, withDocuments: true).ToList();
    var skillsWithoutCondition = CreateSkillsWithDocuments(parentScope: null, withDocuments: false)
        .Select(s =>
        {
            var name = s.Name + "_NoCondition";
            return AIFunctionFactory.Create(
                (object? args, CancellationToken ct) => Task.FromResult<object?>($"{name} executed"),
                new AIFunctionFactoryOptions
                {
                    Name = name,
                    Description = $"{name} skill",
                    AdditionalProperties = s.AdditionalProperties
                });
        }).ToList();

    tools.AddRange(skillsWithCondition);
    tools.AddRange(skillsWithoutCondition);
    tools.Add(Create[FunctionName]Function());

    var manager = new ToolVisibilityManager(tools, ImmutableHashSet<string>.Empty);

    // Act: Expand mixed skills
    var expandedSkills = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "SkillWithCondition",
        "SkillWithCondition_NoCondition");

    var visibleTools = manager.GetToolsForAgentTurn(
        tools.ToList(),
        ImmutableHashSet<string>.Empty,
        expandedSkills);

    // Assert: Function visible because at least one skill meets condition
    visibleTools.Should().Contain(t =>
        t.Name.Equals("[function_name]", StringComparison.OrdinalIgnoreCase));
}

#endregion
```

**Helper Method for Test Setup:**
```csharp
private AIFunction Create[FunctionName]Function()
{
    return AIFunctionFactory.Create(
        (object? args, CancellationToken ct) => Task.FromResult<object?>("[Function result]"),
        new AIFunctionFactoryOptions
        {
            Name = "[function_name]",
            Description = "[Function description]",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["ParentPlugin"] = "[PluginName]"
            }
        });
}
```

**Required Test Coverage (Minimum 5 tests):**
1. ✅ Function NOT visible when condition not met
2. ✅ Function IS visible when condition met
3. ✅ Function NOT visible when no skills expanded
4. ✅ Function visible only once when multiple conditions met (deduplication)
5. ✅ Function visible in mixed scenarios (some conditions met, some not)

**Reference Example:** Lines 556-724 in ToolVisibilityManagerTests.cs for `read_skill_document` tests

---

### **Step 6: Update Existing Tests (if needed)**

Some existing tests may assume functions are always visible. Search and update:

**Find tests that:**
- Use `CreateTestTools()` helper
- Assert specific `.HaveCount()` values
- Contain assertions about `ReadSkillDocument` or similar always-visible functions

**Update pattern:**
```csharp
// OLD (incorrect after adding conditional visibility)
visibleTools.Should().HaveCount(3); // Containers + ReadSkillDocument
visibleTools.Should().Contain(t => t.Name == "ReadSkillDocument");

// NEW (correct)
visibleTools.Should().HaveCount(2); // Only containers
visibleTools.Should().NotContain(t => t.Name == "ReadSkillDocument"); // Not visible until skill expanded
```

**Tests that typically need updates:**
- `Scenario1_BothScoped_BothExplicit_ShowsOnlyContainers`
- `Scenario2_PluginNotScoped_SkillsScoped_ShowsAllPluginFunctions`
- `Scenario5_OnlySkillsExplicit_WithScope_ShowsOnlyScopeContainer`
- `Scenario6_ScopedPluginExplicit_NoSkills_HidesFunctions`

**Reference:** See git diff for lines 42-54, 84-96, 126-139, 208-221, 250-266 for update examples

---

## Step 7: Build and Test

### Build
```bash
dotnet build HPD-Agent/HPD-Agent.csproj
dotnet build test/HPD-Agent.Tests/HPD-Agent.Tests.csproj
```

### Run Tests
```bash
# Run only ToolVisibilityManager tests
dotnet test test/HPD-Agent.Tests/HPD-Agent.Tests.csproj \
    --filter "FullyQualifiedName~ToolVisibilityManagerTests"

# Run all tests
dotnet test
```

### Verify
- ✅ All tests pass
- ✅ No compilation errors
- ✅ Function appears when condition met
- ✅ Function hidden when condition not met
- ✅ Proper deduplication (appears once when multiple conditions met)

---

## Quick Reference Checklist

When adding a new skill-infrastructure function, complete these steps in order:

- [ ] **Step 1:** Create plugin class in `HPD-Agent/Skills/[Category]/[Name]Plugin.cs`
  - [ ] Add parameterless constructor
  - [ ] Add logger support
  - [ ] Implement function with `[AIFunction]` attribute
  - [ ] Use snake_case for function name
  - [ ] Add error handling with user-friendly messages

- [ ] **Step 2:** Register conditionally in `AgentBuilder.BuildDependenciesAsync()` (~line 900)
  - [ ] Add condition check (e.g., `if (_documentStore != null)`)
  - [ ] Call `_pluginManager.RegisterPlugin<YourPlugin>()`
  - [ ] Add comment explaining condition

- [ ] **Step 3:** Add visibility logic in `ToolVisibilityManager.GetToolsForAgentTurn()` (~line 210)
  - [ ] Add else-if clause for your function name (both cases)
  - [ ] Check `expandedSkills` for condition
  - [ ] Use helper method for condition check
  - [ ] Add to `expandedSkillFunctions` if condition met

- [ ] **Step 4:** Add helper method in `ToolVisibilityManager` (~line 320)
  - [ ] Check `AdditionalProperties` is not null
  - [ ] Extract relevant metadata
  - [ ] Return boolean indicating condition
  - [ ] Add XML doc comment

- [ ] **Step 5:** Add 5+ tests in `ToolVisibilityManagerTests.cs`
  - [ ] Test: Not visible when condition not met
  - [ ] Test: Visible when condition met
  - [ ] Test: Not visible when no skills expanded
  - [ ] Test: Deduplication (visible once)
  - [ ] Test: Mixed scenario
  - [ ] Add helper method to create test function

- [ ] **Step 6:** Update existing tests
  - [ ] Search for count assertions
  - [ ] Remove expectations of your function being always visible
  - [ ] Update counts accordingly

- [ ] **Step 7:** Build and test
  - [ ] `dotnet build` succeeds
  - [ ] `dotnet test --filter ToolVisibilityManagerTests` passes
  - [ ] All 13+ tests pass

---

## Common Visibility Conditions

### 1. Has Documents
**When to use:** Function needs to interact with skill documents

```csharp
private bool HasDocuments(AIFunction skillContainer)
{
    if (skillContainer.AdditionalProperties == null) return false;

    return (skillContainer.AdditionalProperties.TryGetValue("DocumentUploads", out var uploads) &&
            uploads is Array uploadsArr && uploadsArr.Length > 0) ||
           (skillContainer.AdditionalProperties.TryGetValue("DocumentReferences", out var refs) &&
            refs is Array refsArr && refsArr.Length > 0);
}
```

**Example usage:** `read_skill_document`, `list_skill_documents`

---

### 2. References Specific Plugin
**When to use:** Function only useful when skill uses certain plugin

```csharp
private bool ReferencesPlugin(AIFunction skillContainer, string pluginName)
{
    if (skillContainer.AdditionalProperties?.TryGetValue("ReferencedPlugins", out var plugins) != true)
        return false;

    return plugins is string[] pluginArray &&
           pluginArray.Contains(pluginName, StringComparer.OrdinalIgnoreCase);
}
```

**Example usage:** `validate_financial_data` (only for skills using FinancialAnalysisPlugin)

---

### 3. Has Metadata Key
**When to use:** Function depends on specific skill configuration

```csharp
private bool HasMetadata(AIFunction skillContainer, string metadataKey)
{
    return skillContainer.AdditionalProperties?.ContainsKey(metadataKey) == true;
}
```

**Example usage:** `get_skill_examples` (only for skills with "Examples" metadata)

---

### 4. Minimum Function Count
**When to use:** Function only makes sense for complex skills

```csharp
private bool HasMinimumFunctions(AIFunction skillContainer, int minCount)
{
    if (skillContainer.AdditionalProperties?.TryGetValue("ReferencedFunctions", out var funcs) != true)
        return false;

    return funcs is string[] funcArray && funcArray.Length >= minCount;
}
```

**Example usage:** `optimize_skill_execution` (only for skills with 5+ functions)

---

### 5. Always Visible (Any Skill Expanded)
**When to use:** Function useful for all skills, but not needed when no skills active

```csharp
// In visibility logic:
bool anySkillExpanded = expandedSkills.Any();

if (anySkillExpanded)
{
    expandedSkillFunctions.Add(tool);
}
```

**Example usage:** `get_current_skill_name`, `log_skill_action`

---

### 6. Multiple Conditions (AND)
**When to use:** Function requires several conditions to be met

```csharp
private bool MeetsComplexCondition(AIFunction skillContainer)
{
    return HasDocuments(skillContainer) &&
           ReferencesPlugin(skillContainer, "AnalysisPlugin") &&
           HasMinimumFunctions(skillContainer, 3);
}
```

**Example usage:** `run_advanced_analysis` (needs docs, specific plugin, multiple functions)

---

### 7. Multiple Conditions (OR)
**When to use:** Function useful in several different scenarios

```csharp
private bool MeetsAnyCondition(AIFunction skillContainer)
{
    return HasDocuments(skillContainer) ||
           HasMetadata(skillContainer, "RequiresContext") ||
           HasMinimumFunctions(skillContainer, 10);
}
```

**Example usage:** `get_skill_help` (useful for documented skills OR complex skills OR skills with special metadata)

---

## Future Function Examples

Here are potential infrastructure functions you might add using this pattern:

| Function Name | Visibility Condition | Purpose | Plugin Name |
|---------------|---------------------|---------|-------------|
| `list_skill_documents` | Has documents | List all documents for active skill | DocumentRetrievalPlugin |
| `get_skill_metadata` | Any skill expanded | Retrieve skill configuration/info | SkillMetadataPlugin |
| `validate_against_schema` | Has validation schema metadata | Validate inputs against skill schema | ValidationPlugin |
| `get_skill_examples` | Has examples metadata | Show usage examples for skill | ExamplesPlugin |
| `switch_skill_mode` | Has multiple modes metadata | Change skill behavior mode | SkillModesPlugin |
| `get_skill_history` | Skill tracking enabled | Show previous invocations | HistoryPlugin |
| `cache_skill_result` | Caching enabled | Cache intermediate results | CachingPlugin |
| `explain_skill_logic` | Has explanation metadata | Explain how skill works | ExplanationPlugin |
| `benchmark_skill` | Performance tracking enabled | Get performance metrics | BenchmarkPlugin |
| `export_skill_config` | Any skill expanded | Export skill configuration | ConfigPlugin |

---

## Architecture Rationale

### Why Three Layers?

**1. Plugin Layer (WHAT)**
- Defines function implementation
- No awareness of visibility rules
- Reusable, testable in isolation
- Can be unit tested independently

**2. Registration Layer (WHEN to register)**
- Decides if function should exist in pool
- Based on build-time conditions (services, configuration)
- Prevents unnecessary plugin instantiation
- Reduces memory footprint

**3. Visibility Layer (WHEN to show)**
- Decides if function appears in tool list
- Based on runtime conditions (expanded skills, skill state)
- Token-efficient (only show when useful)
- LLM sees clean, contextual tool lists

### Benefits

1. **Separation of Concerns**
   - Each layer has single responsibility
   - Changes isolated to appropriate layer
   - Easy to reason about

2. **Testability**
   - Plugin logic tested independently
   - Visibility rules tested in isolation
   - Integration tests verify end-to-end

3. **Maintainability**
   - Clear pattern to follow
   - Easy to add new functions
   - Consistent across codebase

4. **Performance**
   - Don't register unused plugins
   - Don't show irrelevant functions
   - Reduce token waste

5. **Scalability**
   - Pattern handles arbitrary conditions
   - Supports complex visibility rules
   - Easy to extend with new metadata

---

## Troubleshooting

### Function Not Appearing

**Check Registration Layer:**
```csharp
// In AgentBuilder.BuildDependenciesAsync(), verify:
if (_yourCondition != null) // Is this condition true?
{
    _pluginManager.RegisterPlugin<YourPlugin>(); // Is this being called?
}
```

**Check Visibility Layer:**
```csharp
// In ToolVisibilityManager.GetToolsForAgentTurn(), verify:
else if (functionName.Equals("your_function", ...)) // Is name matching?
{
    bool conditionMet = ...; // Is this true?
    if (conditionMet) // Is function being added?
    {
        expandedSkillFunctions.Add(tool);
    }
}
```

**Debug Tips:**
- Add breakpoints in visibility logic
- Log when function added to `expandedSkillFunctions`
- Check `expandedSkills` parameter - is your skill name there?
- Verify `AdditionalProperties` has expected metadata

---

### Function Appearing When It Shouldn't

**Check Condition Logic:**
- Is helper method returning true unexpectedly?
- Are you checking the right metadata keys?
- Is condition too broad?

**Check for Multiple Paths:**
- Is function being added by different visibility rule?
- Check if function matches other `else if` clauses
- Verify `ParentPlugin` metadata is correct

---

### Deduplication Not Working

**Verify Function Added to Correct List:**
```csharp
// CORRECT - enables deduplication
expandedSkillFunctions.Add(tool);

// WRONG - bypasses deduplication
nonScopedFunctions.Add(tool);
```

**Check Function Name Consistency:**
- Plugin uses `read_skill_document`
- Visibility checks both `read_skill_document` AND `ReadSkillDocument`
- Tests check case-insensitively

---

### Tests Failing After Adding Function

**Common Issues:**

1. **Count Assertions:** Other tests may expect different counts
   ```csharp
   // Update from:
   visibleTools.Should().HaveCount(3);
   // To:
   visibleTools.Should().HaveCount(2); // If your function now hidden
   ```

2. **Always-Visible Assumptions:** Tests may assume function always present
   ```csharp
   // Remove or update:
   visibleTools.Should().Contain(t => t.Name == "your_function");
   ```

3. **CreateTestTools:** May need to update helper to support your function
   ```csharp
   private IEnumerable<AIFunction> CreateTestTools(...)
   {
       // ... existing code ...

       // Add your function conditionally
       if (includeYourFunction)
       {
           tools.Add(CreateYourFunction());
       }
   }
   ```

---

## Advanced Patterns

### Condition Based on Multiple Expanded Skills

```csharp
// Function visible only when BOTH skill types expanded
bool bothConditionsMet = expandedSkills.Any(skillName =>
{
    var container = FindSkillContainer(allTools, skillName);
    return container != null && HasDocuments(container);
}) && expandedSkills.Any(skillName =>
{
    var container = FindSkillContainer(allTools, skillName);
    return container != null && ReferencesPlugin(container, "AnalysisPlugin");
});
```

### Condition Based on Skill Count

```csharp
// Function visible only when 3+ skills expanded
bool threeOrMoreExpanded = expandedSkills.Count >= 3;
```

### Condition Based on Specific Skill Combination

```csharp
// Function visible only when DataAnalysis AND Visualization skills both expanded
bool specificCombination =
    expandedSkills.Contains("DataAnalysis", StringComparer.OrdinalIgnoreCase) &&
    expandedSkills.Contains("Visualization", StringComparer.OrdinalIgnoreCase);
```

### Condition Based on External State

```csharp
// In AgentBuilder, store state needed for visibility check
private readonly Dictionary<string, object> _runtimeState = new();

// In visibility logic, access via closure or injected service
bool externalConditionMet = _someService.CheckCondition();
```

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2025-01-XX | Initial documentation based on `read_skill_document` implementation |

---

## Related Documentation

- [Skills Architecture](./SKILLS_ARCHITECTURE.md) - Overall skills system design
- [Scoping System](./SCOPING_SYSTEM.md) - How plugin/skill scoping works
- [Plugin Skills Integration](./PLUGIN_SKILLS_INTEGRATION.md) - How plugins and skills integrate
- [Source Generator Guide](./SOURCE_GENERATOR_GUIDE.md) - How skill metadata is generated

---

## Questions or Issues?

If you encounter issues or have questions about implementing skill infrastructure functions:

1. Review existing implementation: `DocumentRetrievalPlugin.cs` and related tests
2. Check this document for troubleshooting tips
3. Ensure all 7 steps completed
4. Verify tests pass with `dotnet test --filter ToolVisibilityManagerTests`

**Remember:** This pattern is complex because the scoping architecture is complex. Take time to understand each layer before implementing.

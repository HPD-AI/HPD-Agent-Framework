using Microsoft.Extensions.AI;

namespace HPD_Agent.Tests.Infrastructure;

/// <summary>
/// Helper for creating scoped plugins and testing container expansion.
/// Provides utilities for building test plugins with container functions using HPDAIFunctionFactory.
/// </summary>
public static class ScopedPluginTestHelper
{
    /// <summary>
    /// Creates a container function for a scoped plugin.
    /// This simulates what the source generator creates for [PluginScope] attributes.
    /// </summary>
    public static AIFunction CreateContainerFunction(
        string pluginName,
        string description,
        IReadOnlyList<AIFunction> containedFunctions,
        string? postExpansionInstructions = null)
    {
        var functionNames = containedFunctions.Select(f => f.Name ?? "").ToArray();
        var functionCount = containedFunctions.Count;

        var options = new HPDAIFunctionFactoryOptions
        {
            Name = pluginName,
            Description = description,
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["IsContainer"] = true,
                ["PluginName"] = pluginName,
                ["FunctionNames"] = functionNames,
                ["FunctionCount"] = functionCount,
                ["SourceType"] = "CSharp"
            }
        };

        return HPDAIFunctionFactory.Create(
            async (args, ct) =>
            {
                var funcs = string.Join(", ", functionNames);
                var instructions = postExpansionInstructions ?? "";
                return $"Plugin '{pluginName}' expanded. Available functions: {funcs}. {instructions}";
            },
            options);
    }

    /// <summary>
    /// Creates a function that belongs to a scoped plugin.
    /// Adds the necessary metadata to mark it as a plugin member.
    /// </summary>
    public static AIFunction CreatePluginMemberFunction(
        string name,
        string description,
        Func<AIFunctionArguments, CancellationToken, Task<object?>> invocation,
        string pluginName)
    {
        var options = new HPDAIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["ParentPlugin"] = pluginName,
                ["PluginName"] = pluginName
            }
        };

        return HPDAIFunctionFactory.Create(invocation, options);
    }

    /// <summary>
    /// Creates a complete scoped plugin with container and member functions.
    /// Returns (containerFunction, memberFunctions[]).
    /// </summary>
    public static (AIFunction Container, AIFunction[] Members) CreateScopedPlugin(
        string pluginName,
        string description,
        params (string name, string desc, Func<AIFunctionArguments, CancellationToken, Task<object?>> func)[] functions)
    {
        // Create member functions
        var memberFunctions = functions.Select(f =>
            CreatePluginMemberFunction(f.name, f.desc, f.func, pluginName)
        ).ToArray();

        // Create container
        var container = CreateContainerFunction(pluginName, description, memberFunctions);

        return (container, memberFunctions);
    }

    /// <summary>
    /// Helper to create simple member functions from synchronous delegates.
    /// </summary>
    public static (string name, string desc, Func<AIFunctionArguments, CancellationToken, Task<object?>> func)
        MemberFunc(string name, string description, Func<object?> syncFunc)
    {
        return (name, description, (args, ct) => Task.FromResult(syncFunc()));
    }

    /// <summary>
    /// Extracts tool names from a list of AIFunctions.
    /// Useful for assertions in container expansion tests.
    /// </summary>
    public static List<string> GetToolNames(IEnumerable<AIFunction> tools)
    {
        return tools.Select(t => t.Name ?? "").ToList();
    }

    /// <summary>
    /// Checks if a function is a container function.
    /// </summary>
    public static bool IsContainer(AIFunction function)
    {
        return function.AdditionalProperties?.TryGetValue("IsContainer", out var value) == true
            && value is bool isContainer
            && isContainer;
    }

    /// <summary>
    /// Gets the plugin name from a function's metadata.
    /// </summary>
    public static string? GetPluginName(AIFunction function)
    {
        if (function.AdditionalProperties?.TryGetValue("PluginName", out var value) == true)
        {
            return value as string;
        }
        return null;
    }

    /// <summary>
    /// Creates a non-scoped function (regular function without plugin scoping).
    /// </summary>
    public static AIFunction CreateNonScopedFunction(
        string name,
        string description,
        Func<AIFunctionArguments, CancellationToken, Task<object?>> invocation)
    {
        var options = new HPDAIFunctionFactoryOptions
        {
            Name = name,
            Description = description
        };

        return HPDAIFunctionFactory.Create(invocation, options);
    }

    /// <summary>
    /// Helper to create simple functions from synchronous delegates.
    /// </summary>
    public static AIFunction CreateSimpleFunction(string name, string description, Func<object?> syncFunc)
    {
        return CreateNonScopedFunction(name, description, (args, ct) => Task.FromResult(syncFunc()));
    }

    /// <summary>
    /// Common test functions for quick test setup.
    /// </summary>
    public static class CommonFunctions
    {
        public static AIFunction Add() =>
            CreateSimpleFunction("Add", "Adds two numbers", () => "Result: sum");

        public static AIFunction Multiply() =>
            CreateSimpleFunction("Multiply", "Multiplies two numbers", () => "Result: product");

        public static AIFunction Subtract() =>
            CreateSimpleFunction("Subtract", "Subtracts two numbers", () => "Result: difference");

        public static AIFunction Echo() =>
            CreateSimpleFunction("Echo", "Echoes input text", () => "Echo result");

        public static AIFunction GetTime() =>
            CreateSimpleFunction("GetTime", "Gets current time", () => DateTime.UtcNow.ToString("O"));
    }
}

/// <summary>
/// Extension methods for working with scoped plugins in tests.
/// </summary>
public static class ScopedPluginExtensions
{
    /// <summary>
    /// Converts a list of AIFunctions to their names for easy assertion.
    /// </summary>
    public static List<string> ToNames(this IEnumerable<AIFunction> functions)
    {
        return functions.Select(f => f.Name ?? "").ToList();
    }

    /// <summary>
    /// Filters functions by whether they're containers.
    /// </summary>
    public static IEnumerable<AIFunction> OnlyContainers(this IEnumerable<AIFunction> functions)
    {
        return functions.Where(ScopedPluginTestHelper.IsContainer);
    }

    /// <summary>
    /// Filters functions by whether they're NOT containers.
    /// </summary>
    public static IEnumerable<AIFunction> ExcludeContainers(this IEnumerable<AIFunction> functions)
    {
        return functions.Where(f => !ScopedPluginTestHelper.IsContainer(f));
    }

    /// <summary>
    /// Filters functions by plugin name.
    /// </summary>
    public static IEnumerable<AIFunction> FromPlugin(this IEnumerable<AIFunction> functions, string pluginName)
    {
        return functions.Where(f => ScopedPluginTestHelper.GetPluginName(f) == pluginName);
    }
}

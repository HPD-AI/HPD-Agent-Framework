using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using System.Reflection;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

/// <summary>
/// Extended AIFunctionFactory that supports parameter descriptions, invocation filters, and enhanced JSON schema generation.
/// </summary>
public class HPDAIFunctionFactory
{
    private static readonly HPDAIFunctionFactoryOptions _defaultOptions = new();

    /// <summary>
    /// Creates an AIFunction with rich parameter descriptions and invocation filters.
    /// </summary>
    public static AIFunction Create(Delegate method, HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(method.Method, method.Target, options ?? _defaultOptions);
    }

    /// <summary>
    /// Creates an AIFunction with rich parameter descriptions and invocation filters.
    /// </summary>
    public static AIFunction Create(MethodInfo method, object? target, HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(method, target, options ?? _defaultOptions);
    }

    /// <summary>
    /// AIFunction implementation that includes parameter descriptions in its JSON schema and supports invocation filters.
    /// </summary>
    public class HPDAIFunction : AIFunction
    {
        private readonly MethodInfo _method;
        private readonly object? _target;
        private readonly HPDAIFunctionFactoryOptions _options;

        public HPDAIFunction(MethodInfo method, object? target, HPDAIFunctionFactoryOptions options)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _target = target;
            _options = options;
            HPDOptions = options;

            JsonSchema = options.SchemaProvider?.Invoke() ?? default;

            Name = options.Name ?? method.Name;
            Description = options.Description ??
                method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "";
        }

        public HPDAIFunctionFactoryOptions HPDOptions { get; }
        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }
        public override MethodInfo? UnderlyingMethod => _method;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // The actual invocation logic is now passed in via a delegate from the source generator,
            // but we keep this override as part of the class structure.
            // A more advanced implementation could use the provided delegate directly.
            
            var parameters = _method.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                if (param.ParameterType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                }
                else if (param.ParameterType == typeof(AIFunctionArguments))
                {
                    args[i] = arguments;
                }
                else if (param.ParameterType == typeof(IServiceProvider))
                {
                    args[i] = arguments.Services;
                }
                else
                {
                    if (!arguments.TryGetValue(param.Name!, out var value))
                    {
                        if (param.HasDefaultValue)
                        {
                            args[i] = param.DefaultValue;
                            continue;
                        }
                        throw new ArgumentException($"Required parameter '{param.Name}' was not provided.");
                    }
                    args[i] = value; // Direct assignment is sufficient now
                }
            }

            var result = _method.Invoke(_target, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(true);
                // Extract result from Task<T> if it's a generic Task
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    return taskType.GetProperty("Result")?.GetValue(task);
                }
                return null;
            }
            return result;
        }
    }
}

/// <summary>
/// Options for HPDAIFunctionFactory with enhanced metadata support.
/// </summary>
public class HPDAIFunctionFactoryOptions
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? ParameterDescriptions { get; set; }
    public bool RequiresPermission { get; set; }
    
    // A delegate for the generated validation logic.
    public Func<string, (bool IsValid, string ErrorMessage)>? Validator { get; set; }
    
    // A delegate to provide the generated JSON schema.
    public Func<JsonElement>? SchemaProvider { get; set; }
}

// This context can be simplified if not used elsewhere, but is harmless to keep.
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<Dictionary<string, object>>))]
[JsonSerializable(typeof(List<Dictionary<string, object?>>))]
internal partial class AOTJsonContext : JsonSerializerContext
{
}
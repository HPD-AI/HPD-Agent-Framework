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
/// A modern, unified AIFunctionFactory that prioritizes delegate-based invocation 
/// for performance and AOT-compatibility, with a reflection-based fallback.
/// </summary>
public class HPDAIFunctionFactory
{
    private static readonly HPDAIFunctionFactoryOptions _defaultOptions = new();

    /// <summary>
    /// Creates an AIFunction using a pre-compiled invocation delegate.
    /// This is the preferred method for source-generated plugins and adapters.
    /// </summary>
    public static AIFunction Create(
        Func<AIFunctionArguments, CancellationToken, Task<object?>> invocation, 
        HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(invocation, options ?? _defaultOptions);
    }

    /// <summary>
    /// [Legacy] Creates an AIFunction using reflection.
    /// </summary>
    public static AIFunction Create(Delegate method, HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(method.Method, method.Target, options ?? _defaultOptions);
    }

    /// <summary>
    /// [Legacy] Creates an AIFunction using reflection.
    /// </summary>
    public static AIFunction Create(MethodInfo method, object? target, HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(method, target, options ?? _defaultOptions);
    }

    /// <summary>
    /// A unified AIFunction that supports both delegate and reflection-based invocation.
    /// </summary>
    public class HPDAIFunction : AIFunction
    {
        private readonly Func<AIFunctionArguments, CancellationToken, Task<object?>>? _invocationHandler;
        private readonly MethodInfo? _method;
        private readonly object? _target;

        // Constructor for the modern, delegate-based approach
        public HPDAIFunction(Func<AIFunctionArguments, CancellationToken, Task<object?>> invocationHandler, HPDAIFunctionFactoryOptions options)
        {
            _invocationHandler = invocationHandler ?? throw new ArgumentNullException(nameof(invocationHandler));
            _method = invocationHandler.Method; // For metadata
            _target = invocationHandler.Target;
            HPDOptions = options;

            JsonSchema = options.SchemaProvider?.Invoke() ?? default;
            Name = options.Name ?? _method.Name;
            Description = options.Description ?? "";
        }

        // Constructor for the legacy, reflection-based approach
        public HPDAIFunction(MethodInfo method, object? target, HPDAIFunctionFactoryOptions options)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _target = target;
            HPDOptions = options;

            JsonSchema = options.SchemaProvider?.Invoke() ?? default;
            Name = options.Name ?? method.Name;
            Description = options.Description ?? "";
        }

        public HPDAIFunctionFactoryOptions HPDOptions { get; }
        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }
        public override MethodInfo? UnderlyingMethod => _method;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // 1. Prioritize the fast, pre-compiled delegate if it exists.
            if (_invocationHandler != null)
            {
                return await _invocationHandler(arguments, cancellationToken).ConfigureAwait(false);
            }

            // 2. Fallback to the slower, reflection-based invocation if no delegate was provided.
            if (_method == null)
            {
                throw new InvalidOperationException("AIFunction is not configured for invocation.");
            }
            
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
                    args[i] = value;
                }
            }

            var result = _method.Invoke(_target, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(true);
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

// Options class remains the same
public class HPDAIFunctionFactoryOptions
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? ParameterDescriptions { get; set; }
    public bool RequiresPermission { get; set; }
    public Func<string, (bool IsValid, string ErrorMessage)>? Validator { get; set; }
    public Func<JsonElement>? SchemaProvider { get; set; }
}

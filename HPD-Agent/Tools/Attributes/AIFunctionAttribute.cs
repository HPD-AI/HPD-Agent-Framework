using System;
using HPD.Agent;

/// <summary>
/// Marks a method as an AI function with a specific context type.
/// The generic version enables compile-time validation and is required for conditional logic or dynamic descriptions.
/// </summary>
/// <typeparam name="TMetadata">The context type providing properties for conditions and templates</typeparam>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIFunctionAttribute<TMetadata> : Attribute where TMetadata : IToolMetadata
{
    /// <summary>
    /// The context type used by this function for compile-time validation.
    /// </summary>
    public Type ContextType => typeof(TMetadata);
    
    /// <summary>
    /// Custom name for the function. If not specified, uses the method name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Non-generic version for simple functions without conditional logic or dynamic descriptions.
/// Use AIFunction&lt;TMetadata&gt; for advanced features.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIFunctionAttribute : Attribute
{
    /// <summary>
    /// Custom name for the function. If not specified, uses the method name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Static description of the function. For dynamic descriptions, use AIDescription with AIFunction&lt;TMetadata&gt;.
    /// </summary>
    public string? Description { get; set; }
}


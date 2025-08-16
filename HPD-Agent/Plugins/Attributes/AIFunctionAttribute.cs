using System;

/// <summary>
/// Marks a method as an AI function with a specific context type.
/// The generic version enables compile-time validation and is required for conditional logic or dynamic descriptions.
/// </summary>
/// <typeparam name="TContext">The context type providing properties for conditions and templates</typeparam>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIFunctionAttribute<TContext> : Attribute where TContext : IPluginMetadataContext
{
    /// <summary>
    /// The context type used by this function for compile-time validation.
    /// </summary>
    public Type ContextType => typeof(TContext);
    
    /// <summary>
    /// Custom name for the function. If not specified, uses the method name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Non-generic version for simple functions without conditional logic or dynamic descriptions.
/// Use AIFunction&lt;TContext&gt; for advanced features.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIFunctionAttribute : Attribute
{
    /// <summary>
    /// Custom name for the function. If not specified, uses the method name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Static description of the function. For dynamic descriptions, use AIDescription with AIFunction&lt;TContext&gt;.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Specifies that a function requires specific permissions to be executed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresPermissionAttribute : Attribute
{
    /// <summary>
    /// The required permission string.
    /// </summary>
    public string Permission { get; }
    
    /// <summary>
    /// Initializes a new instance of the RequiresPermissionAttribute.
    /// </summary>
    /// <param name="permission">The required permission</param>
    public RequiresPermissionAttribute(string permission)
    {
        Permission = permission ?? throw new ArgumentNullException(nameof(permission));
    }
}

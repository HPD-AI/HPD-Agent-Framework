using System;

/// <summary>
/// Provides dynamic descriptions for AI functions and parameters with context template support.
/// Replaces System.ComponentModel.DescriptionAttribute for all AI-facing metadata.
/// Supports templates like "Search using {context.ProviderName}".
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class AIDescriptionAttribute : Attribute
{
    /// <summary>
    /// The description template, which may contain context expressions like {context.PropertyName}.
    /// </summary>
    public string Description { get; }
    
    /// <summary>
    /// Initializes a new instance of the AIDescriptionAttribute.
    /// </summary>
    /// <param name="description">The description template</param>
    public AIDescriptionAttribute(string description)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }
}

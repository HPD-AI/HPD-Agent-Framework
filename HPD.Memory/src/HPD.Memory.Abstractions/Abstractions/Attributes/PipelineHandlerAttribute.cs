// Copyright (c) Einstein Essibu. All rights reserved.
// Marker attribute for automatic pipeline handler registration via source generation.

namespace HPDAgent.Memory.Abstractions.Attributes;

/// <summary>
/// Marks a class as a pipeline handler for automatic registration via source generation.
/// This enables Native AOT-compatible handler discovery without runtime reflection.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// [PipelineHandler(StepName = "extract-text")]
/// public class TextExtractionHandler : IPipelineHandler&lt;DocumentIngestionContext&gt;
/// {
///     // Implementation...
/// }
/// </code>
/// The source generator will automatically create extension methods to register all marked handlers.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PipelineHandlerAttribute : Attribute
{
    /// <summary>
    /// The step name for this handler in the pipeline.
    /// If not specified, defaults to the class name without "Handler" suffix.
    /// </summary>
    public string? StepName { get; set; }
}

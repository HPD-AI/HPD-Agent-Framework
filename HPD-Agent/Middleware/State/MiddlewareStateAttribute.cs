// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System;

namespace HPD.Agent;

/// <summary>
/// Marks a record as middleware state, triggering source generation
/// of properties on MiddlewareState.
/// </summary>
/// <remarks>
/// <para><b>Requirements:</b></para>
/// <list type="bullet">
/// <item>Must be applied to a record type (not class)</item>
/// <item>Record should be sealed for performance</item>
/// <item>All members must be JSON-serializable</item>
/// </list>
///
/// <para><b>Example:</b></para>
/// <code>
/// [MiddlewareState(Version = 1)]
/// public sealed record MyMiddlewareState
/// {
///     public int Count { get; init; }
/// }
/// </code>
///
/// <para><b>Versioning:</b></para>
/// <para>
/// Increment the version when making breaking changes to the state schema:
/// </para>
/// <list type="bullet">
/// <item>Removing or renaming properties</item>
/// <item>Changing property types</item>
/// <item>Changing collection types (e.g., List → ImmutableList)</item>
/// </list>
/// <para>
/// Non-breaking changes (no version bump needed):
/// </para>
/// <list type="bullet">
/// <item>Adding new optional properties with default values</item>
/// <item>Adding helper methods</item>
/// <item>Updating documentation</item>
/// </list>
///
/// <para><b>Generated Code:</b></para>
/// <para>
/// The source generator will create a property on MiddlewareState:
/// </para>
/// <code>
/// public sealed partial class MiddlewareState
/// {
///     public MyMiddlewareState? MyMiddleware
///     {
///         get => GetState&lt;MyMiddlewareState&gt;("YourNamespace.MyMiddlewareState");
///     }
///
///     public MiddlewareState WithMyMiddleware(MyMiddlewareState? value)
///     {
///         return value == null ? this : SetState("YourNamespace.MyMiddlewareState", value);
///     }
/// }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MiddlewareStateAttribute : Attribute
{
    /// <summary>
    /// Version of this middleware state schema. Defaults to 1.
    /// Increment when making breaking changes to the state record.
    /// </summary>
    /// <remarks>
    /// <para><b>When to Bump Version:</b></para>
    /// <list type="bullet">
    /// <item>Removing or renaming properties</item>
    /// <item>Changing property types</item>
    /// <item>Changing collection types (e.g., List → ImmutableList)</item>
    /// </list>
    ///
    /// <para><b>No Version Bump Needed:</b></para>
    /// <list type="bullet">
    /// <item>Adding new optional properties with default values</item>
    /// <item>Adding helper methods</item>
    /// <item>Updating documentation</item>
    /// </list>
    /// </remarks>
    public int Version { get; set; } = 1;
}

// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Container for all middleware state.
/// Properties are source-generated from [MiddlewareState] types.
/// </summary>
/// <remarks>
/// <para><b>Internal Storage:</b></para>
/// <para>
/// Uses ImmutableDictionary&lt;string, object?&gt; as backing storage.
/// This pattern is proven by Microsoft.Extensions.AI (see AIJsonUtilities.Defaults.cs).
/// During deserialization from checkpoints, values become JsonElement which are
/// transparently converted to concrete types by the smart accessor.
/// </para>
///
/// <para><b>Performance:</b></para>
/// <list type="bullet">
/// <item>Runtime reads: ~20-25ns (dictionary lookup + pattern match)</item>
/// <item>Post-checkpoint first read: ~150ns (JsonElement deserialize)</item>
/// <item>Post-checkpoint cached reads: ~20ns (from cache)</item>
/// <item>Immutable updates: ~30ns (ImmutableDictionary.SetItem)</item>
/// </list>
///
/// <para><b>Usage:</b></para>
/// <code>
/// // Access middleware state
/// var state = context.State.MiddlewareState.CircuitBreaker ?? new();
///
/// // Update middleware state (immutable)
/// context.UpdateState(s => s with
/// {
///     MiddlewareState = s.MiddlewareState.WithCircuitBreaker(newState)
/// });
/// </code>
/// </remarks>
public sealed partial class MiddlewareStateContainer
{
    // ═══════════════════════════════════════════════════════
    // BACKING STORAGE (Internal)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Internal storage for middleware states.
    /// Keys: Fully-qualified type names (e.g., "HPD.Agent.CircuitBreakerStateData")
    /// Values: State instances (runtime) or JsonElement (deserialized)
    /// </summary>
    /// <remarks>
    /// <para>⚠️ <b>Internal API - Do not use directly!</b></para>
    /// <para>
    /// This property is public only for JSON serialization compatibility.
    /// Always use the generated properties (e.g., <c>CircuitBreaker</c>, <c>ErrorTracking</c>)
    /// instead of accessing this dictionary directly.
    /// </para>
    /// <para>
    /// JSON serialization: This gets serialized as a dictionary.
    /// On deserialization, values become JsonElement which are converted by smart accessor.
    /// </para>
    /// </remarks>
    [JsonPropertyName("states")]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public ImmutableDictionary<string, object?> States { get; init; }

    // ═══════════════════════════════════════════════════════
    // SCHEMA METADATA (Runtime Fields)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Schema signature of the code that created this checkpoint.
    /// Comma-separated list of middleware state FQNs in alphabetical order.
    /// Null for checkpoints created before schema versioning was added.
    /// </summary>
    [JsonPropertyName("schemaSignature")]
    public string? SchemaSignature { get; init; }

    /// <summary>
    /// Container schema version (for future container-level migrations).
    /// Always 1 in this version of HPD-Agent.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Per-state version mapping (type FQN → version).
    /// Used for detecting individual state schema evolution.
    /// Null for checkpoints created before schema versioning was added.
    /// </summary>
    [JsonPropertyName("stateVersions")]
    public ImmutableDictionary<string, int>? StateVersions { get; init; }

    /// <summary>
    /// Lazy cache for deserialized JsonElement states.
    /// Each container instance gets its own cache to maintain immutability.
    /// Only initialized when deserialization occurs (zero overhead for runtime-only scenarios).
    /// </summary>
    [JsonIgnore]
    private readonly Lazy<ConcurrentDictionary<string, object?>> _deserializedCache;

    // ═══════════════════════════════════════════════════════
    // CONSTRUCTORS
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Creates an empty middleware state container.
    /// Auto-populates schema metadata from source-generated constants.
    /// </summary>
    public MiddlewareStateContainer()
    {
        States = ImmutableDictionary<string, object?>.Empty;
        _deserializedCache = new Lazy<ConcurrentDictionary<string, object?>>(
            () => new ConcurrentDictionary<string, object?>());

        // Auto-populate schema metadata from compiled constants
        SchemaSignature = CompiledSchemaSignature;
        SchemaVersion = CompiledSchemaVersion;
        StateVersions = CompiledStateVersions;
    }

    // ═══════════════════════════════════════════════════════
    // SMART ACCESSOR (Protected - Used by Generated Code)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Smart accessor that handles both runtime and deserialized states.
    /// </summary>
    /// <typeparam name="TState">The middleware state type</typeparam>
    /// <param name="key">Fully-qualified type name (e.g., "HPD.Agent.CircuitBreakerStateData")</param>
    /// <returns>The state instance, or null if not present</returns>
    /// <remarks>
    /// <para><b>State Transitions:</b></para>
    /// <list type="bullet">
    /// <item>Runtime: value is TState (direct cast, ~20-25ns)</item>
    /// <item>Deserialized: value is JsonElement (deserialize first access ~150ns, cached ~20ns)</item>
    /// </list>
    ///
    /// <para><b>Timeline of State Transitions:</b></para>
    /// <para>
    /// T0: Runtime - Store concrete type in _states[key] = TState instance
    /// T1: Serialize to JSON (checkpoint)
    /// T2: Deserialize from JSON (restore) - _states[key] = JsonElement
    /// T3: Access via property - Smart accessor detects JsonElement, deserializes to TState, caches result
    /// T4: Update state - _states[key] = TState instance (concrete type again)
    /// </para>
    /// </remarks>
    protected TState? GetState<TState>(string key) where TState : class
    {
        // Fast path: Check deserialization cache first (post-checkpoint scenario)
        if (_deserializedCache.IsValueCreated &&
            _deserializedCache.Value.TryGetValue(key, out var cached))
        {
            return cached as TState;
        }

        if (!States.TryGetValue(key, out var value) || value is null)
            return null;

        // Pattern match handles both cases transparently
        var result = value switch
        {
            TState typed => typed,  // Runtime: already correct type
            JsonElement elem => elem.Deserialize<TState>(
                AIJsonUtilities.DefaultOptions),  // Deserialized from checkpoint
            _ => throw new InvalidOperationException(
                $"Unexpected type {value.GetType().Name} for middleware state '{key}'. " +
                $"Expected {typeof(TState).Name} or JsonElement.")
        };

        // Cache deserialized JsonElement results for subsequent accesses
        if (value is JsonElement && result != null)
        {
            _deserializedCache.Value.TryAdd(key, result);
        }

        return result;
    }

    /// <summary>
    /// Creates new container with updated state (immutable).
    /// Preserves schema metadata across updates.
    /// </summary>
    /// <typeparam name="TState">The middleware state type</typeparam>
    /// <param name="key">Fully-qualified type name</param>
    /// <param name="state">New state value</param>
    /// <returns>New container with updated state</returns>
    protected MiddlewareStateContainer SetState<TState>(
        string key,
        TState state) where TState : class
    {
        return new MiddlewareStateContainer
        {
            States = States.SetItem(key, state),

            // Preserve schema metadata across updates
            SchemaSignature = this.SchemaSignature,
            SchemaVersion = this.SchemaVersion,
            StateVersions = this.StateVersions
        };
    }
}

// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD.Agent.StructuredOutput;

/// <summary>
/// Configuration for structured output mode.
/// </summary>
public class StructuredOutputOptions
{
    /// <summary>
    /// Output mode: "native" (default) or "tool".
    /// - native: Use provider's ResponseFormat with JSON schema (supports streaming partials)
    /// - tool: Use tool-based output (auto-generated output tool, no streaming partials)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Native mode</b> is recommended for most use cases. It uses the provider's
    /// built-in JSON schema support via ResponseFormat, enabling real-time streaming
    /// of partial results as tokens arrive.
    /// </para>
    /// <para>
    /// <b>Tool mode</b> is for advanced scenarios where you need to mix structured
    /// output with regular tools. In this mode, an output tool is auto-generated
    /// and added to the agent's tools. The LLM calls this tool to return the result.
    /// Note: Tool mode does NOT support streaming partials because M.E.AI accumulates
    /// tool arguments internally before exposing them.
    /// </para>
    /// </remarks>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "native";

    /// <summary>
    /// JSON schema for the output type.
    /// For C#: Inferred from generic type parameter T.
    /// For FFI: Provided directly in JSON.
    /// </summary>
    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; set; }

    /// <summary>
    /// Schema/type name for observability and tool naming.
    /// Defaults to typeof(T).Name if not specified.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string? SchemaName { get; set; }

    /// <summary>
    /// Schema description for the LLM.
    /// Used in ResponseFormat and tool descriptions.
    /// </summary>
    [JsonPropertyName("schemaDescription")]
    public string? SchemaDescription { get; set; }

    /// <summary>
    /// Tool name override (tool mode only).
    /// Default: "return_{SchemaName}" or "return_{TypeName}".
    /// </summary>
    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    /// <summary>
    /// Enable streaming partial updates. Default: true.
    /// When false, only emits final complete result.
    /// </summary>
    /// <remarks>
    /// <b>NOTE:</b> Streaming partials only work in "native" mode.
    /// In "tool" mode, partials are never emitted because M.E.AI
    /// accumulates tool arguments internally before exposing them.
    /// For real-time partial updates, use native mode.
    /// </remarks>
    [JsonPropertyName("streamPartials")]
    public bool StreamPartials { get; set; } = true;

    /// <summary>
    /// Debounce interval for partial updates (ms). Default: 100.
    /// Reduces parse overhead for high-frequency token streams.
    /// </summary>
    [JsonPropertyName("partialDebounceMs")]
    public int PartialDebounceMs { get; set; } = 100;

    /// <summary>
    /// JSON serializer options for deserialization.
    /// Required for AOT/NativeAOT: Pass a JsonSerializerContext with your output type registered.
    /// Default: AIJsonUtilities.DefaultOptions (works with reflection, NOT AOT-safe for custom types).
    /// </summary>
    /// <example>
    /// // AOT-safe usage:
    /// [JsonSerializable(typeof(WeatherReport))]
    /// public partial class MyJsonContext : JsonSerializerContext;
    ///
    /// var options = new StructuredOutputOptions
    /// {
    ///     SerializerOptions = MyJsonContext.Default.Options
    /// };
    /// </example>
    [JsonIgnore] // Not FFI-serializable (JsonSerializerOptions is complex)
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Union types for "union" mode. Each type becomes an output tool.
    /// The LLM chooses which type to return by calling the corresponding tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Union mode enables discriminated union-like behavior where the LLM
    /// can return one of several possible types. Each union member type
    /// becomes an output tool (e.g., "return_SuccessResponse", "return_ErrorResponse").
    /// </para>
    /// <para>
    /// <b>Usage:</b>
    /// </para>
    /// <code>
    /// public abstract record ApiResponse;
    /// public sealed record SuccessResponse(string Data) : ApiResponse;
    /// public sealed record ErrorResponse(string Code, string Message) : ApiResponse;
    ///
    /// var options = new AgentRunOptions
    /// {
    ///     StructuredOutput = new StructuredOutputOptions
    ///     {
    ///         Mode = "union",
    ///         UnionTypes = new[] { typeof(SuccessResponse), typeof(ErrorResponse) }
    ///     }
    /// };
    ///
    /// await foreach (var evt in agent.RunStructuredAsync&lt;ApiResponse&gt;(messages, options: options))
    /// {
    ///     if (evt is StructuredResultEvent&lt;ApiResponse&gt; result)
    ///     {
    ///         switch (result.Value)
    ///         {
    ///             case SuccessResponse success: HandleSuccess(success); break;
    ///             case ErrorResponse error: HandleError(error); break;
    ///         }
    ///     }
    /// }
    /// </code>
    /// </remarks>
    [JsonIgnore] // Type[] not FFI-serializable
    public Type[]? UnionTypes { get; set; }
}

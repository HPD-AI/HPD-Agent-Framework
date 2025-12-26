using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HPDAgent.Graph.Abstractions.Caching;
using HPDAgent.Graph.Abstractions.Handlers;

namespace HPDAgent.Graph.Core.Caching;

/// <summary>
/// Computes hierarchical fingerprints for node executions.
/// Fingerprint = Hash(global | nodeId | inputs | upstreamFingerprints)
/// Changes automatically propagate downstream.
/// </summary>
public class HierarchicalFingerprintCalculator : INodeFingerprintCalculator
{
    public string Compute(
        string nodeId,
        HandlerInputs inputs,
        Dictionary<string, string> upstreamHashes,
        string globalHash)
    {
        var builder = new StringBuilder();

        // 1. Global hash (graph structure + environment)
        builder.Append(globalHash).Append('|');

        // 2. Node ID
        builder.Append(nodeId).Append('|');

        // 3. Direct inputs (sorted for consistency)
        var allInputs = inputs.GetAll();
        foreach (var (key, value) in allInputs.OrderBy(kv => kv.Key))
        {
            builder.Append(key).Append('=').Append(HashValue(value)).Append(';');
        }
        builder.Append('|');

        // 4. CRITICAL: Upstream fingerprints (transitive dependencies)
        // If any upstream node changes, this node's fingerprint changes too
        foreach (var (upstreamNodeId, upstreamHash) in upstreamHashes.OrderBy(kv => kv.Key))
        {
            builder.Append(upstreamNodeId).Append('=').Append(upstreamHash).Append(';');
        }

        // Compute final hash
        return ComputeHash(builder.ToString());
    }

    /// <summary>
    /// Hash a single value (handles primitives, collections, objects).
    /// </summary>
    private string HashValue(object? value)
    {
        if (value == null)
            return "null";

        // Primitive types
        if (value is string str)
            return str;

        if (value is int || value is long || value is double || value is float || value is decimal || value is bool)
            return value.ToString() ?? "null";

        // Collections - hash each element
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var item in enumerable)
            {
                if (!first) sb.Append(',');
                sb.Append(HashValue(item));
                first = false;
            }
            sb.Append(']');
            return ComputeHash(sb.ToString());
        }

        // Complex objects - serialize to JSON and hash
        try
        {
            var json = JsonSerializer.Serialize(value);
            return ComputeHash(json);
        }
        catch
        {
            // Fallback to ToString
            return ComputeHash(value.ToString() ?? "object");
        }
    }

    /// <summary>
    /// Compute SHA256 hash of a string.
    /// </summary>
    private string ComputeHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

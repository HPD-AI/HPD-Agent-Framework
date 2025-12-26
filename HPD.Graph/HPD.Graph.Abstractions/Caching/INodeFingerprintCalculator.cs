using HPDAgent.Graph.Abstractions.Handlers;

namespace HPDAgent.Graph.Abstractions.Caching;

/// <summary>
/// Computes content-addressable fingerprints for node executions.
 
/// Fingerprint = Hash(inputs + config + upstream_hashes + global_hash)
/// </summary>
public interface INodeFingerprintCalculator
{
    /// <summary>
    /// Compute fingerprint for a node execution.
    /// Uses hierarchical hashing: changes propagate downstream automatically.
    /// </summary>
    /// <param name="nodeId">ID of the node being executed</param>
    /// <param name="inputs">Input data for this node</param>
    /// <param name="upstreamHashes">Fingerprints from upstream nodes (transitive dependencies)</param>
    /// <param name="globalHash">Global hash (graph structure + environment config)</param>
    /// <returns>Unique fingerprint hash for this execution</returns>
    string Compute(
        string nodeId,
        HandlerInputs inputs,
        Dictionary<string, string> upstreamHashes,
        string globalHash);
}

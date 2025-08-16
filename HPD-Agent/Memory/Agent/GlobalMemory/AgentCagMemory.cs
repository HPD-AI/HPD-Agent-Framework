using System;


/// <summary>
/// Core memory model representing a single memory entry.
/// </summary>
public class AgentCagMemory
{
    /// <summary>Unique memory identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Short descriptive title of the memory.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Full content of the memory entry.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime Created { get; set; }

    /// <summary>Last update timestamp (UTC).</summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>Last accessed timestamp (UTC).</summary>
    public DateTime LastAccessed { get; set; }
}


// Copyright (c) Einstein Essibu. All rights reserved.
// Extension methods for working with tag collections.
// Provides Kernel Memory-like convenience while keeping simple Dictionary<string, List<string>>.

namespace HPDAgent.Memory.Abstractions.Models;

/// <summary>
/// Extension methods for working with tag dictionaries.
/// Provides fluent API and convenience methods similar to Kernel Memory's TagCollection,
/// but works with standard Dictionary<string, List<string>> for simplicity.
///
/// Second Mover's Advantage: We keep the simple Dictionary type but add convenience methods.
/// This is better than Kernel Memory's custom TagCollection class (200+ lines of boilerplate).
/// </summary>
public static class TagCollectionExtensions
{
    /// <summary>
    /// Add a tag value to the collection.
    /// If the tag key doesn't exist, creates it.
    /// If the tag key exists, appends the value (if not already present).
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="key">Tag key</param>
    /// <param name="value">Tag value to add</param>
    public static void AddTag(
        this IDictionary<string, List<string>> tags,
        string key,
        string value)
    {
        TagConstants.ValidateTagKey(key);

        if (tags.TryGetValue(key, out var list))
        {
            // Add value if not already present
            if (!list.Contains(value, StringComparer.Ordinal))
            {
                list.Add(value);
            }
        }
        else
        {
            // Create new list with value
            tags[key] = new List<string> { value };
        }
    }

    /// <summary>
    /// Add multiple tag values for a key.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="key">Tag key</param>
    /// <param name="values">Tag values to add</param>
    public static void AddTags(
        this IDictionary<string, List<string>> tags,
        string key,
        params string[] values)
    {
        foreach (var value in values)
        {
            tags.AddTag(key, value);
        }
    }

    /// <summary>
    /// Add a tag key without any values (creates empty list).
    /// Useful for marking presence of a tag category.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="key">Tag key</param>
    public static void AddTagKey(
        this IDictionary<string, List<string>> tags,
        string key)
    {
        TagConstants.ValidateTagKey(key);

        if (!tags.ContainsKey(key))
        {
            tags[key] = new List<string>();
        }
    }

    /// <summary>
    /// Remove a specific tag value.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="key">Tag key</param>
    /// <param name="value">Tag value to remove</param>
    /// <returns>True if the value was found and removed</returns>
    public static bool RemoveTag(
        this IDictionary<string, List<string>> tags,
        string key,
        string value)
    {
        if (tags.TryGetValue(key, out var list))
        {
            var removed = list.Remove(value);

            // Remove key if no values left
            if (list.Count == 0)
            {
                tags.Remove(key);
            }

            return removed;
        }

        return false;
    }

    /// <summary>
    /// Check if a tag key exists.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="key">Tag key to check</param>
    /// <returns>True if the key exists</returns>
    public static bool HasTag(
        this IDictionary<string, List<string>> tags,
        string key)
    {
        return tags.ContainsKey(key);
    }

    /// <summary>
    /// Check if a tag key has a specific value.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="key">Tag key</param>
    /// <param name="value">Tag value to check</param>
    /// <returns>True if the key exists and contains the value</returns>
    public static bool HasTagValue(
        this IDictionary<string, List<string>> tags,
        string key,
        string value)
    {
        return tags.TryGetValue(key, out var list) &&
               list.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Get all tag key-value pairs flattened.
    /// Similar to Kernel Memory's Pairs property.
    ///
    /// Example: { "user": ["alice", "bob"], "dept": ["eng"] }
    /// Returns: [("user", "alice"), ("user", "bob"), ("dept", "eng")]
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <returns>Flattened key-value pairs</returns>
    public static IEnumerable<KeyValuePair<string, string>> GetTagPairs(
        this IDictionary<string, List<string>> tags)
    {
        return from kvp in tags
               from value in kvp.Value
               select new KeyValuePair<string, string>(kvp.Key, value);
    }

    /// <summary>
    /// Copy tags from one collection to another.
    /// Preserves multi-value tags.
    /// </summary>
    /// <param name="source">Source tag collection</param>
    /// <param name="target">Target tag collection</param>
    public static void CopyTagsTo(
        this IDictionary<string, List<string>> source,
        IDictionary<string, List<string>> target)
    {
        foreach (var (key, values) in source)
        {
            foreach (var value in values)
            {
                target.AddTag(key, value);
            }
        }
    }

    /// <summary>
    /// Format tags as a human-readable string (for logging/debugging).
    /// Format: "key1:value1;key2:[value2a, value2b];key3:value3"
    ///
    /// Single-value tags: "key:value"
    /// Multi-value tags: "key:[value1, value2]"
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <param name="excludeReserved">If true, excludes tags starting with '__'</param>
    /// <returns>Formatted string</returns>
    public static string ToTagString(
        this IDictionary<string, List<string>> tags,
        bool excludeReserved = false)
    {
        var items = tags.Where(kvp =>
            kvp.Value.Count > 0 &&
            (!excludeReserved || !TagConstants.IsReserved(kvp.Key)));

        if (!items.Any())
        {
            return string.Empty;
        }

        return string.Join(';', items.Select(kvp =>
        {
            if (kvp.Value.Count == 1)
            {
                return $"{kvp.Key}:{kvp.Value[0]}";
            }
            else
            {
                return $"{kvp.Key}:[{string.Join(", ", kvp.Value)}]";
            }
        }));
    }

    /// <summary>
    /// Get only user-defined tags (excludes reserved system tags).
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <returns>Dictionary containing only non-reserved tags</returns>
    public static Dictionary<string, List<string>> GetUserTags(
        this IDictionary<string, List<string>> tags)
    {
        return tags
            .Where(kvp => !TagConstants.IsReserved(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => new List<string>(kvp.Value));
    }

    /// <summary>
    /// Get only reserved system tags.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <returns>Dictionary containing only reserved tags</returns>
    public static Dictionary<string, List<string>> GetReservedTags(
        this IDictionary<string, List<string>> tags)
    {
        return tags
            .Where(kvp => TagConstants.IsReserved(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => new List<string>(kvp.Value));
    }

    /// <summary>
    /// Clear all tags.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    public static void ClearTags(this IDictionary<string, List<string>> tags)
    {
        tags.Clear();
    }

    /// <summary>
    /// Get number of unique tag keys.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <returns>Number of tag keys</returns>
    public static int GetTagCount(this IDictionary<string, List<string>> tags)
    {
        return tags.Count;
    }

    /// <summary>
    /// Get total number of tag values across all keys.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <returns>Total number of values</returns>
    public static int GetTotalValueCount(this IDictionary<string, List<string>> tags)
    {
        return tags.Sum(kvp => kvp.Value.Count);
    }

    /// <summary>
    /// Check if tags collection is empty.
    /// </summary>
    /// <param name="tags">Tag collection</param>
    /// <returns>True if no tags exist</returns>
    public static bool IsEmpty(this IDictionary<string, List<string>> tags)
    {
        return tags.Count == 0;
    }

    /// <summary>
    /// Merge tags from another collection into this one.
    /// If tag keys conflict, values are combined (duplicates avoided).
    /// </summary>
    /// <param name="tags">Target tag collection</param>
    /// <param name="otherTags">Tags to merge in</param>
    public static void MergeTags(
        this IDictionary<string, List<string>> tags,
        IDictionary<string, List<string>> otherTags)
    {
        foreach (var (key, values) in otherTags)
        {
            foreach (var value in values)
            {
                tags.AddTag(key, value);
            }
        }
    }
}

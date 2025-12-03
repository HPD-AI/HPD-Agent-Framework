// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent.Skills.DocumentStore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HPD.Agent.FrontendTools;

/// <summary>
/// Registers frontend skill documents into the instruction document store.
/// Uses "frontend:" namespace prefix to prevent collision with compile-time skill documents.
/// </summary>
/// <remarks>
/// <para><b>Document ID Prefixing:</b></para>
/// <para>
/// Frontend documents are stored with a "frontend:" prefix to distinguish them from
/// compile-time skill documents. For example:
/// - FrontendSkillDocument with DocumentId="checkout-flow"
/// - Becomes "frontend:checkout-flow" in the document store
/// </para>
///
/// <para><b>Usage:</b></para>
/// <code>
/// var registrar = new FrontendSkillDocumentRegistrar(documentStore, logger);
///
/// // Register all documents from a plugin
/// await registrar.RegisterPluginDocumentsAsync(ecommercePlugin, ct);
///
/// // Later, when plugin is removed
/// await registrar.UnregisterPluginDocumentsAsync(ecommercePlugin, ct);
/// </code>
///
/// <para><b>Document Retrieval:</b></para>
/// <para>
/// The agent uses the standard read_skill_document() function to retrieve documents.
/// The middleware transforms "frontend:checkout-flow" back to "checkout-flow" when
/// showing documents in skill activation responses.
/// </para>
/// </remarks>
public class FrontendSkillDocumentRegistrar
{
    private readonly IInstructionDocumentStore _documentStore;
    private readonly ILogger _logger;

    /// <summary>
    /// Prefix for frontend document IDs in the store.
    /// </summary>
    public const string FrontendDocumentPrefix = "frontend:";

    /// <summary>
    /// Creates a new registrar.
    /// </summary>
    /// <param name="documentStore">The document store to register documents into</param>
    /// <param name="logger">Optional logger</param>
    public FrontendSkillDocumentRegistrar(
        IInstructionDocumentStore documentStore,
        ILogger? logger = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Registers all documents from all skills in a plugin.
    /// </summary>
    /// <param name="plugin">The plugin containing skills with documents</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of documents registered</returns>
    public async Task<int> RegisterPluginDocumentsAsync(
        FrontendPluginDefinition plugin,
        CancellationToken ct = default)
    {
        if (plugin.Skills == null || plugin.Skills.Count == 0)
        {
            _logger.LogDebug("Plugin '{PluginName}' has no skills, skipping document registration", plugin.Name);
            return 0;
        }

        var registeredCount = 0;

        foreach (var skill in plugin.Skills)
        {
            if (skill.Documents == null || skill.Documents.Count == 0)
                continue;

            foreach (var document in skill.Documents)
            {
                try
                {
                    await RegisterDocumentAsync(plugin.Name, skill.Name, document, ct);
                    registeredCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to register document '{DocumentId}' from skill '{SkillName}' in plugin '{PluginName}'",
                        document.DocumentId, skill.Name, plugin.Name);
                    throw;
                }
            }
        }

        _logger.LogInformation(
            "Registered {Count} documents from plugin '{PluginName}'",
            registeredCount, plugin.Name);

        return registeredCount;
    }

    /// <summary>
    /// Unregisters all documents from all skills in a plugin.
    /// </summary>
    /// <param name="plugin">The plugin containing skills with documents</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of documents unregistered</returns>
    public async Task<int> UnregisterPluginDocumentsAsync(
        FrontendPluginDefinition plugin,
        CancellationToken ct = default)
    {
        if (plugin.Skills == null || plugin.Skills.Count == 0)
            return 0;

        var unregisteredCount = 0;

        foreach (var skill in plugin.Skills)
        {
            if (skill.Documents == null || skill.Documents.Count == 0)
                continue;

            foreach (var document in skill.Documents)
            {
                try
                {
                    var storeId = GetStoreDocumentId(document.DocumentId);
                    await _documentStore.DeleteDocumentAsync(storeId, ct);
                    unregisteredCount++;
                }
                catch (NotImplementedException)
                {
                    // Delete not implemented - log and continue
                    _logger.LogWarning(
                        "Document deletion not implemented. Document '{DocumentId}' remains in store.",
                        document.DocumentId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to unregister document '{DocumentId}' from skill '{SkillName}'",
                        document.DocumentId, skill.Name);
                }
            }
        }

        _logger.LogInformation(
            "Unregistered {Count} documents from plugin '{PluginName}'",
            unregisteredCount, plugin.Name);

        return unregisteredCount;
    }

    /// <summary>
    /// Registers a single document into the store.
    /// </summary>
    private async Task RegisterDocumentAsync(
        string pluginName,
        string skillName,
        FrontendSkillDocument document,
        CancellationToken ct)
    {
        var storeId = GetStoreDocumentId(document.DocumentId);
        var content = await GetDocumentContentAsync(document, ct);

        var metadata = new DocumentMetadata
        {
            Name = document.DocumentId,
            Description = document.Description
        };

        await _documentStore.UploadFromContentAsync(storeId, metadata, content, ct);

        _logger.LogDebug(
            "Registered frontend document '{StoreId}' from skill '{SkillName}' in plugin '{PluginName}'",
            storeId, skillName, pluginName);
    }

    /// <summary>
    /// Gets document content from either inline content or URL.
    /// </summary>
    private async Task<string> GetDocumentContentAsync(
        FrontendSkillDocument document,
        CancellationToken ct)
    {
        // Prefer inline content
        if (!string.IsNullOrEmpty(document.Content))
        {
            return document.Content;
        }

        // Fetch from URL
        if (!string.IsNullOrEmpty(document.Url))
        {
            return await FetchDocumentFromUrlAsync(document.Url, document.DocumentId, ct);
        }

        // Should not happen if validation passed
        throw new ArgumentException(
            $"Document '{document.DocumentId}' has neither content nor URL");
    }

    /// <summary>
    /// Fetches document content from a URL.
    /// </summary>
    private async Task<string> FetchDocumentFromUrlAsync(
        string url,
        string documentId,
        CancellationToken ct)
    {
        using var httpClient = new HttpClient();

        try
        {
            _logger.LogDebug("Fetching document '{DocumentId}' from URL: {Url}", documentId, url);

            var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);

            _logger.LogDebug(
                "Fetched document '{DocumentId}' from URL ({Bytes} bytes)",
                documentId, content.Length);

            return content;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to fetch document '{documentId}' from URL '{url}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts a frontend document ID to its store ID (with prefix).
    /// </summary>
    /// <param name="documentId">The frontend document ID</param>
    /// <returns>The store document ID with "frontend:" prefix</returns>
    public static string GetStoreDocumentId(string documentId)
    {
        return $"{FrontendDocumentPrefix}{documentId}";
    }

    /// <summary>
    /// Converts a store document ID back to the frontend document ID (strips prefix).
    /// </summary>
    /// <param name="storeId">The store document ID</param>
    /// <returns>The frontend document ID without prefix, or null if not a frontend document</returns>
    public static string? GetFrontendDocumentId(string storeId)
    {
        if (storeId.StartsWith(FrontendDocumentPrefix))
        {
            return storeId.Substring(FrontendDocumentPrefix.Length);
        }
        return null;
    }

    /// <summary>
    /// Checks if a store document ID is a frontend document.
    /// </summary>
    /// <param name="storeId">The store document ID</param>
    /// <returns>True if this is a frontend document</returns>
    public static bool IsFrontendDocument(string storeId)
    {
        return storeId.StartsWith(FrontendDocumentPrefix);
    }
}

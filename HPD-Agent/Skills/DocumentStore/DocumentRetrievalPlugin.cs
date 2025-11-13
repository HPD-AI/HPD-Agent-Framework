using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel;

namespace HPD_Agent.Skills.DocumentStore;

/// <summary>
/// Global document retrieval functions for skills.
/// Agents can use these functions to retrieve skill documents on-demand.
/// </summary>
public class DocumentRetrievalPlugin
{
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Parameterless constructor required for plugin registration.
    /// </summary>
    public DocumentRetrievalPlugin()
    {
    }

    /// <summary>
    /// Constructor with logger (optional).
    /// </summary>
    public DocumentRetrievalPlugin(ILogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Read a skill document by ID.
    /// This function is available to all agents and allows them to retrieve
    /// skill documents on-demand based on the metadata they see in skill activation responses.
    /// </summary>
    /// <param name="documentId">The document ID to retrieve (shown in skill activation response)</param>
    /// <param name="documentStore">The document store (injected via DI)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The document content, or an error message if the document cannot be retrieved</returns>
    [AIFunction(Name = "read_skill_document", Description = "Read a skill document by ID. Use the document IDs shown in skill activation responses.")]
    public async Task<string> ReadSkillDocument(
        [Description("Document ID (e.g., 'debugging-workflow')")] string documentId,
        IInstructionDocumentStore documentStore,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await documentStore.ReadDocumentAsync(documentId, cancellationToken);

            if (content == null)
            {
                return $"⚠️ Document '{documentId}' not found.";
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read document {DocumentId}", documentId);
            return $"⚠️ Error reading document '{documentId}': {ex.Message}\n" +
                   $"The document may be temporarily unavailable. Please try again.";
        }
    }
}

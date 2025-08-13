using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.KernelMemory;

namespace HPD_Agent.MemoryRAG
{
    /// <summary>
    /// Unified RAG plugin providing agent-controlled memory operations (Pull strategy)
    /// </summary>
    public class RAGPlugin
    {
        private readonly RAGMemoryCapability _ragCapability;

        public RAGPlugin(RAGMemoryCapability ragCapability)
        {
            _ragCapability = ragCapability ?? throw new ArgumentNullException(nameof(ragCapability));
        }

        [AIFunction]
        [Description("Search across all available memory sources with optional source filtering")]
        public async Task<string> SearchMemories(
            [Description("The text to search for")] string query,
            [Description("Maximum number of results to return")] int limit = 5,
            [Description("Minimum relevance score (0.0 to 1.0)")] double minRelevance = 0.7,
            [Description("Specific memory source: 'all', 'agent', 'conversation', 'project', or empty for all")] string? source = null)
        {
            var ragContext = GetCurrentRAGContext();
            if (ragContext == null) return "No memory sources available.";

            var results = await _ragCapability.SearchAllSourcesAsync(query, ragContext);

            // Filter by source if specified
            if (!string.IsNullOrEmpty(source) && !source.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                results = results.Where(r => r.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return FormatSearchResults(results
                .Where(r => r.Relevance >= minRelevance)
                .Take(limit));
        }

        [AIFunction]
        [Description("Ask a question and get an AI-generated answer based on available memory sources")]
        public async Task<string> AskMemory(
            [Description("The question to ask")] string question,
            [Description("Specific memory source: 'all', 'agent', 'conversation', 'project', or empty for all")] string? source = null,
            [Description("Minimum relevance of sources to consider")] double minRelevance = 0.7)
        {
            var ragContext = GetCurrentRAGContext();
            if (ragContext == null) return "No memory sources available.";

            // Use the existing search functionality for now
            // In a full implementation, this could use the RAG capability's Ask functionality
            var searchResults = await SearchMemories(question, 5, minRelevance, source);
            return string.IsNullOrEmpty(searchResults) || searchResults == "No relevant information found."
                ? "I couldn't find relevant information in my memory to answer that question."
                : $"Based on my memory search: {searchResults}";
        }

        [AIFunction]
        [Description("Save text content to memory for future reference")]
        public async Task<string> SaveToMemory(
            [Description("The text content to save")] string content,
            [Description("Where to save: 'conversation' or 'project'. Defaults to conversation.")] string? target = null,
            [Description("Optional document ID for the saved content")] string? documentId = null,
            [Description("Optional tags (format: 'key:value,key2:value2')")] string? tags = null)
        {
            var ragContext = GetCurrentRAGContext();
            if (ragContext == null) return "No memory sources available for saving.";

            var targetMemory = ResolveTargetMemory(target, ragContext);
            if (targetMemory == null)
                return $"Cannot save: No suitable memory target available for '{target ?? "conversation"}'.";

            var id = await targetMemory.ImportTextAsync(
                text: content,
                documentId: documentId ?? Guid.NewGuid().ToString(),
                tags: ParseTags(tags),
                cancellationToken: default);

            return $"Saved content with ID: {id} to {target ?? "conversation"} memory.";
        }

        [AIFunction]
        [Description("Save a file to memory for future reference")]
        public async Task<string> SaveFileToMemory(
            [Description("Path to the file to save")] string filePath,
            [Description("Where to save: 'conversation' or 'project'. Defaults to conversation.")] string? target = null,
            [Description("Optional document ID for the saved file")] string? documentId = null,
            [Description("Optional tags (format: 'key:value,key2:value2')")] string? tags = null)
        {
            var ragContext = GetCurrentRAGContext();
            if (ragContext == null) return "No memory sources available for saving.";

            var targetMemory = ResolveTargetMemory(target, ragContext);
            if (targetMemory == null)
                return $"Cannot save file: No suitable memory target available for '{target ?? "conversation"}'.";

            try
            {
                var id = await targetMemory.ImportDocumentAsync(
                    filePath: filePath,
                    documentId: documentId,
                    tags: ParseTags(tags),
                    cancellationToken: default);

                return $"Saved file '{filePath}' with ID: {id} to {target ?? "conversation"} memory.";
            }
            catch (Exception ex)
            {
                return $"Error saving file '{filePath}': {ex.Message}";
            }
        }

        [AIFunction]
        [Description("Save web page content to memory for future reference")]
        public async Task<string> SaveWebPageToMemory(
            [Description("URL of the web page to save")] string url,
            [Description("Where to save: 'conversation' or 'project'. Defaults to conversation.")] string? target = null,
            [Description("Optional document ID for the saved page")] string? documentId = null,
            [Description("Optional tags (format: 'key:value,key2:value2')")] string? tags = null)
        {
            var ragContext = GetCurrentRAGContext();
            if (ragContext == null) return "No memory sources available for saving.";

            var targetMemory = ResolveTargetMemory(target, ragContext);
            if (targetMemory == null)
                return $"Cannot save web page: No suitable memory target available for '{target ?? "conversation"}'.";

            var id = await targetMemory.ImportWebPageAsync(
                url: url,
                documentId: documentId,
                tags: ParseTags(tags),
                cancellationToken: default);

            return $"Saved web page '{url}' with ID: {id} to {target ?? "conversation"} memory.";
        }

        [AIFunction]
        [Description("Delete content from memory by document ID")]
        public async Task<string> DeleteFromMemory(
            [Description("Document ID to delete")] string documentId,
            [Description("Memory source: 'conversation' or 'project'. Tries both if empty.")] string? source = null)
        {
            var ragContext = GetCurrentRAGContext();
            if (ragContext == null) return "No memory sources available.";

            var deleted = false;
            var attempts = new List<string>();

            if (string.IsNullOrEmpty(source) || "conversation".Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                if (ragContext.ConversationMemory != null)
                {
                    try 
                    { 
                        await ragContext.ConversationMemory.DeleteDocumentAsync(documentId); 
                        attempts.Add("conversation"); 
                        deleted = true; 
                    } 
                    catch 
                    { 
                        // Continue to other sources 
                    }
                }
            }

            if (string.IsNullOrEmpty(source) || "project".Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                if (ragContext.ProjectMemory != null)
                {
                    try 
                    { 
                        await ragContext.ProjectMemory.DeleteDocumentAsync(documentId); 
                        attempts.Add("project"); 
                        deleted = true; 
                    } 
                    catch 
                    { 
                        // Continue 
                    }
                }
            }

            return deleted
                ? $"Attempted to delete document '{documentId}' from {string.Join(" and ", attempts)} memory."
                : $"Document '{documentId}' not found or could not be deleted from the specified memory sources.";
        }

        private RAGContext? GetCurrentRAGContext()
        {
            // The capability will provide access to the current context
            // This will be implemented when we create the RAGMemoryCapability
            return _ragCapability.GetCurrentContext(); 
        }

        private IKernelMemory? ResolveTargetMemory(string? target, RAGContext context)
        {
            return target?.ToLowerInvariant() switch
            {
                "project" => context.ProjectMemory,
                "conversation" or null => context.ConversationMemory,
                _ => null // Do not fall back for invalid targets
            };
        }

        private string FormatSearchResults(IEnumerable<RetrievalResult> results)
        {
            if (!results.Any()) return "No relevant information found.";
            
            var formatted = results.Select(r => 
                $"[Source: {r.Source}, Relevance: {r.Relevance:F2}]\n{r.Content}\n---");
            
            return string.Join("\n", formatted);
        }

        private TagCollection? ParseTags(string? tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return null;

            var tagCollection = new TagCollection();
            var pairs = tags.Split(',', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var pair in pairs)
            {
                var parts = pair.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    tagCollection.Add(parts[0].Trim(), parts[1].Trim());
                }
            }

            return tagCollection.Count > 0 ? tagCollection : null;
        }
    }
}

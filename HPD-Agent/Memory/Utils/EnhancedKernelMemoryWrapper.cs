using Microsoft.KernelMemory;
using Microsoft.KernelMemory.Context;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


/// <summary>
/// Extension methods to provide runtime custom pipeline support for IKernelMemory
/// </summary>
public static class KernelMemoryCustomPipelineExtensions
{
    /// <summary>
    /// Import a document using a custom pipeline at runtime
    /// </summary>
    public static async Task<string> ImportDocumentWithCustomPipelineAsync(
        this IKernelMemory memory,
        string filePath,
        string[] customSteps,
        string? documentId = null,
        string? index = null,
        CancellationToken cancellationToken = default)
    {
        return await memory.ImportDocumentAsync(
            filePath,
            documentId: documentId,
            index: index,
            steps: customSteps,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Import a document using a custom pipeline at runtime
    /// </summary>
    public static async Task<string> ImportDocumentWithCustomPipelineAsync(
        this IKernelMemory memory,
        Document document,
        string[] customSteps,
        string? index = null,
        CancellationToken cancellationToken = default)
    {
        return await memory.ImportDocumentAsync(
            document,
            index: index,
            steps: customSteps,
            cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Enhanced wrapper that implements IKernelMemory while providing custom pipeline capabilities
/// </summary>
public class CustomPipelineMemoryWrapper : IKernelMemory
{
    public IKernelMemory Memory { get; }
    public string[] DefaultCustomSteps { get; }
    public string DefaultIndex { get; }

    public CustomPipelineMemoryWrapper(IKernelMemory memory, string[] defaultCustomSteps, string defaultIndex)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        DefaultCustomSteps = defaultCustomSteps ?? throw new ArgumentNullException(nameof(defaultCustomSteps));
        DefaultIndex = defaultIndex ?? throw new ArgumentNullException(nameof(defaultIndex));
    }

    /// <summary>
    /// Import a document using the default custom pipeline configured at build time
    /// </summary>
    public async Task<string> ImportDocumentWithDefaultPipelineAsync(
        string filePath,
        string? documentId = null,
        string? index = null,
        CancellationToken cancellationToken = default)
    {
        return await Memory.ImportDocumentWithCustomPipelineAsync(
            filePath,
            DefaultCustomSteps,
            documentId,
            index ?? DefaultIndex,
            cancellationToken);
    }

    /// <summary>
    /// Import a document using the default custom pipeline configured at build time
    /// </summary>
    public async Task<string> ImportDocumentWithDefaultPipelineAsync(
        Document document,
        string? index = null,
        CancellationToken cancellationToken = default)
    {
        return await Memory.ImportDocumentWithCustomPipelineAsync(
            document,
            DefaultCustomSteps,
            index ?? DefaultIndex,
            cancellationToken);
    }

    /// <summary>
    /// Get the registered pipeline steps
    /// </summary>
    public string[] GetRegisteredPipelineSteps() => DefaultCustomSteps;

    // IKernelMemory implementation - delegate all calls to the wrapped Memory instance
    public Task<string> ImportDocumentAsync(Document document, string? index = null, IEnumerable<string>? steps = null, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.ImportDocumentAsync(document, index, steps, context, cancellationToken);

    public Task<string> ImportDocumentAsync(string filePath, string? documentId = null, TagCollection? tags = null, string? index = null, IEnumerable<string>? steps = null, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.ImportDocumentAsync(filePath, documentId, tags, index, steps, context, cancellationToken);

    public Task<string> ImportDocumentAsync(DocumentUploadRequest uploadRequest, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.ImportDocumentAsync(uploadRequest, context, cancellationToken);

    public Task<string> ImportDocumentAsync(Stream documentData, string? fileName = null, string? documentId = null, TagCollection? tags = null, string? index = null, IEnumerable<string>? steps = null, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.ImportDocumentAsync(documentData, fileName, documentId, tags, index, steps, context, cancellationToken);

    public Task<string> ImportTextAsync(string text, string? documentId = null, TagCollection? tags = null, string? index = null, IEnumerable<string>? steps = null, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.ImportTextAsync(text, documentId, tags, index, steps, context, cancellationToken);

    public Task<string> ImportWebPageAsync(string url, string? documentId = null, TagCollection? tags = null, string? index = null, IEnumerable<string>? steps = null, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.ImportWebPageAsync(url, documentId, tags, index, steps, context, cancellationToken);

    public Task<IEnumerable<IndexDetails>> ListIndexesAsync(CancellationToken cancellationToken = default)
        => Memory.ListIndexesAsync(cancellationToken);

    public Task DeleteIndexAsync(string? index = null, CancellationToken cancellationToken = default)
        => Memory.DeleteIndexAsync(index, cancellationToken);

    public Task DeleteDocumentAsync(string documentId, string? index = null, CancellationToken cancellationToken = default)
        => Memory.DeleteDocumentAsync(documentId, index, cancellationToken);

    public Task<bool> IsDocumentReadyAsync(string documentId, string? index = null, CancellationToken cancellationToken = default)
        => Memory.IsDocumentReadyAsync(documentId, index, cancellationToken);

    public Task<DataPipelineStatus?> GetDocumentStatusAsync(string documentId, string? index = null, CancellationToken cancellationToken = default)
        => Memory.GetDocumentStatusAsync(documentId, index, cancellationToken);

    public Task<StreamableFileContent> ExportFileAsync(string documentId, string fileName, string? index = null, CancellationToken cancellationToken = default)
        => Memory.ExportFileAsync(documentId, fileName, index, cancellationToken);

    public Task<Microsoft.KernelMemory.SearchResult> SearchAsync(string query, string? index = null, MemoryFilter? filter = null, ICollection<MemoryFilter>? filters = null, double minRelevance = 0, int limit = -1, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.SearchAsync(query, index, filter, filters, minRelevance, limit, context, cancellationToken);

    public IAsyncEnumerable<MemoryAnswer> AskStreamingAsync(string question, string? index = null, MemoryFilter? filter = null, ICollection<MemoryFilter>? filters = null, double minRelevance = 0, SearchOptions? options = null, IContext? context = null, CancellationToken cancellationToken = default)
        => Memory.AskStreamingAsync(question, index, filter, filters, minRelevance, options, context, cancellationToken);
}


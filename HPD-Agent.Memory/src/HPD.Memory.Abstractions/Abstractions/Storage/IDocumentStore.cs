// Copyright (c) Einstein Essibu. All rights reserved.
// Separates document/file storage from pipeline orchestration.
// Kernel Memory mixes this into IPipelineOrchestrator - we keep it separate.

using System;

namespace HPDAgent.Memory.Abstractions.Storage;

/// <summary>
/// Abstraction for storing and retrieving files during pipeline execution.
/// Separated from orchestrator for better testability and flexibility.
/// </summary>
/// <remarks>
/// Unlike Kernel Memory which puts file I/O methods in IPipelineOrchestrator,
/// we keep storage concerns separate for:
/// - Better separation of concerns
/// - Easier testing (mock storage independently)
/// - Flexibility (different storage per pipeline/handler)
/// - Cleaner orchestrator interface
/// </remarks>
public interface IDocumentStore
{
    /// <summary>
    /// Read a file as binary data.
    /// </summary>
    /// <param name="index">Index/collection name</param>
    /// <param name="pipelineId">Pipeline identifier</param>
    /// <param name="fileName">Name of the file to read</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File content as binary data</returns>
    Task<byte[]> ReadFileAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read a file as text.
    /// </summary>
    Task<string> ReadTextFileAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read a file as a stream (for large files).
    /// </summary>
    Task<Stream> ReadFileStreamAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Write binary data to a file.
    /// </summary>
    Task WriteFileAsync(
        string index,
        string pipelineId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Write text to a file.
    /// </summary>
    Task WriteTextFileAsync(
        string index,
        string pipelineId,
        string fileName,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a stream to a file (for large files).
    /// </summary>
    Task WriteFileStreamAsync(
        string index,
        string pipelineId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file exists.
    /// </summary>
    Task<bool> FileExistsAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a file.
    /// </summary>
    Task DeleteFileAsync(
        string index,
        string pipelineId,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all files for a pipeline.
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all files for a pipeline (cleanup).
    /// </summary>
    Task DeleteAllFilesAsync(
        string index,
        string pipelineId,
        CancellationToken cancellationToken = default);
}

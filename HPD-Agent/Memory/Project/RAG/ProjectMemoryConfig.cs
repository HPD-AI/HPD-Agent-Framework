using System;
using System.Collections.Generic;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;

public class ProjectMemoryConfig
{
    public string ProjectId { get; set; } = string.Empty;
    public ProjectStorageType StorageType { get; set; } = ProjectStorageType.Persistent;
    public bool MultiUserAccess { get; set; } = true;
    public bool RuntimeManagement { get; set; } = true;
    public string? ConnectionString { get; set; }
    public MemoryEmbeddingProvider? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public TextGenerationProvider? TextGenerationProvider { get; set; }
    public string? TextGenerationModel { get; set; }
    public string IndexPrefix { get; set; } = "project";
    public ProjectAccessPattern AccessPattern { get; set; } = ProjectAccessPattern.Collaborative;
    public int MaxDocumentSizeMB { get; set; } = 100;
    public string[] AllowedFileTypes { get; set; } = new[] { "pdf", "docx", "txt", "md", "html" };

    public ProjectMemoryConfig(string projectId)
    {
        ProjectId = projectId;
    }
    
    // Custom RAG extension support
    public IMemoryDb? CustomMemoryDb { get; set; }
    public ISearchClient? CustomSearchClient { get; set; }
}


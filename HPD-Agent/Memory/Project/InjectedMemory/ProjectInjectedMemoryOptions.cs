/// <summary>
/// Configuration options for Project CAG document injection
/// </summary>
public class ProjectInjectedMemoryOptions
{
    /// <summary>Maximum tokens allocated for project documents (default: 8000)</summary>
    public int MaxTokens { get; set; } = 8000;
    
    /// <summary>Storage directory for project documents</summary>
    public string StorageDirectory { get; set; } = "./project-cag-storage";
    
    /// <summary>Document tag format for injection</summary>
    public string DocumentTagFormat { get; set; } = "[PROJECT_DOC[{0}]]{1}[/PROJECT_DOC]";
    
    /// <summary>Fluently set maximum token count</summary>
    public ProjectInjectedMemoryOptions WithMaxTokens(int tokens)
    {
        MaxTokens = tokens;
        return this;
    }
    
    /// <summary>Fluently set storage directory</summary>
    public ProjectInjectedMemoryOptions WithStorageDirectory(string directory)
    {
        StorageDirectory = directory;
        return this;
    }
    
    /// <summary>Fluently set document tag format</summary>
    public ProjectInjectedMemoryOptions WithDocumentTagFormat(string format)
    {
        DocumentTagFormat = format;
        return this;
    }
}
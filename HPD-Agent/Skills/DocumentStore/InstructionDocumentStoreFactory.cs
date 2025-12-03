using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.Logging;

namespace HPD.Agent.Skills.DocumentStore;



/// <summary>
/// Factory for creating instruction document stores from configuration.
/// Supports multiple backends: FileSystem, InMemory (more coming: S3, Database, AzureBlob)
/// </summary>
public static class InstructionDocumentStoreFactory
{
    /// <summary>
    /// Create a document store from configuration.
    /// Configuration format:
    /// {
    ///   "DocumentStore": {
    ///     "Type": "FileSystem",  // or "InMemory", "S3", "Database", "AzureBlob"
    ///     "FileSystem": {
    ///       "BaseDirectory": "./skill-docs"
    ///     }
    ///   }
    /// }
    /// </summary>
    public static IInstructionDocumentStore CreateFromConfig(
        IConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var storeType = config["DocumentStore:Type"] ?? "FileSystem";
        var logger = loggerFactory.CreateLogger("InstructionDocumentStore");

        return storeType switch
        {
            "FileSystem" => new FileSystemInstructionStore(
                logger,
                config["DocumentStore:FileSystem:BaseDirectory"] ?? "./skill-docs"),

            "InMemory" => new InMemoryInstructionStore(logger),

            // TODO: Implement additional backends
            // "S3" => new S3InstructionStore(...),
            // "Database" => new DatabaseInstructionStore(...),
            // "AzureBlob" => new AzureBlobInstructionStore(...),

            _ => throw new InvalidOperationException(
                $"Unknown document store type: {storeType}. " +
                $"Supported types: FileSystem, InMemory")
        };
    }

    /// <summary>
    /// Create a filesystem store with default configuration
    /// </summary>
    public static IInstructionDocumentStore CreateFileSystemStore(
        ILoggerFactory loggerFactory,
        string baseDirectory = "./skill-docs")
    {
        var logger = loggerFactory.CreateLogger("InstructionDocumentStore");
        return new FileSystemInstructionStore(logger, baseDirectory);
    }

    /// <summary>
    /// Create an in-memory store (useful for testing)
    /// </summary>
    public static IInstructionDocumentStore CreateInMemoryStore(
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("InstructionDocumentStore");
        return new InMemoryInstructionStore(logger);
    }
}

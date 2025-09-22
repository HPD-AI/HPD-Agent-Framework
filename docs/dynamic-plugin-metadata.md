# Dynamic Plugin Metadata System

The Dynamic Plugin Metadata System provides context-aware plugin behavior with dynamic descriptions, conditional function availability, and runtime metadata exposure.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [C# Plugin Development](#c-plugin-development)
- [Advanced Features](#advanced-features)
- [Examples](#examples)

## Overview

This system allows developers to create plugins that adapt their behavior, descriptions, and availability based on runtime context properties like user roles, language preferences, experience levels, and custom settings.

### Key Features

- **Dynamic Descriptions**: Plugin function descriptions that change based on context
- **Conditional Availability**: Functions that appear/disappear based on runtime conditions
- **Context-Aware Schemas**: Parameter schemas that adapt to user context
- **Type Safety**: Full type system integration with comprehensive error handling

### Performance Characteristics

| Operation | Performance | Notes |
|-----------|-------------|-------|
| Context Creation | ~10ms | JSON deserialization |
| Function Filtering | ~50ms | 100+ functions with conditionals |
| Conditional Evaluation | <1ms | Uses pre-compiled evaluators |
| Context Updates | ~5ms | In-memory updates |

## Architecture

The system provides context-aware plugin behavior through:

### Dynamic Configuration
- Plugin configuration with runtime properties
- Context serialization and deserialization
- Agent builder integration

### Source-Generated Metadata
- Pre-compiled conditional evaluation
- Function filtering based on runtime context
- Type-safe context handling

## Getting Started

### Prerequisites

- HPD-Agent with source generator support
- Understanding of plugin development concepts

### Basic Example

**C# Plugin Definition:**
```csharp
public class UserPreferencesContext : IPluginMetadataContext
{
    public string Language { get; set; } = "en";
    public int ExperienceLevel { get; set; } = 1;
    public bool HasPremiumAccess { get; set; } = false;
}

[AIFunction<UserPreferencesContext>]
[Description("{{context.Language == \"es\" ? \"Búsqueda simple\" : \"Simple search\"}}")]
public async Task<string> SearchAsync(
    [Description("{{context.Language == \"es\" ? \"Término de búsqueda\" : \"Search term\"}}")]
    string query,
    UserPreferencesContext context)
{
    return $"Searching for: {query} (Language: {context.Language})";
}
```

## C# Plugin Development

### Creating Context Classes

Context classes implement `IPluginMetadataContext` and define properties that can influence plugin behavior:

```csharp
public class AdvancedPluginContext : IPluginMetadataContext
{
    // User-related properties
    public string UserId { get; set; } = "";
    public string UserRole { get; set; } = "user";
    public string Language { get; set; } = "en";
    
    // Feature flags
    public bool HasPremiumFeatures { get; set; } = false;
    public bool IsInternalUser { get; set; } = false;
    
    // Behavioral settings
    public int ExperienceLevel { get; set; } = 1; // 1=beginner, 2=intermediate, 3=advanced
    public string Theme { get; set; } = "light";
    
    // Custom settings
    public Dictionary<string, object> CustomProperties { get; set; } = new();
}
```

### Dynamic Descriptions

Use templating syntax in `Description` attributes to create context-aware descriptions:

```csharp
[AIFunction<AdvancedPluginContext>]
[Description("""
{{#if (eq context.UserRole "admin")}}
    Administrative data export with full system access
{{else if (eq context.UserRole "moderator")}}
    Moderated data export with restricted access
{{else}}
    {{#if context.HasPremiumFeatures}}
        Premium data export with enhanced options
    {{else}}
        Basic data export functionality
    {{/if}}
{{/if}}
""")]
public async Task<string> ExportDataAsync(
    [Description("{{context.Language == \"es\" ? \"Formato de exportación\" : \"Export format\"}}")]
    string format,
    AdvancedPluginContext context)
{
    // Implementation varies based on context
    var accessLevel = context.UserRole switch
    {
        "admin" => AccessLevel.Full,
        "moderator" => AccessLevel.Restricted,
        _ => context.HasPremiumFeatures ? AccessLevel.Premium : AccessLevel.Basic
    };
    
    return await ExportWithAccessLevel(format, accessLevel);
}
```

### Conditional Function Availability

Functions can be conditionally available based on context properties:

```csharp
[AIFunction<AdvancedPluginContext>]
[Description("Delete sensitive data (Admin only)")]
[Conditional("context.UserRole == \"admin\"")]
public async Task<string> DeleteSensitiveDataAsync(
    string dataId,
    AdvancedPluginContext context)
{
    // Only available to admin users
    return await PerformDeletion(dataId);
}

[AIFunction<AdvancedPluginContext>]
[Description("Advanced analytics dashboard")]
[Conditional("context.HasPremiumFeatures && context.ExperienceLevel >= 2")]
public async Task<string> ShowAdvancedAnalyticsAsync(
    AdvancedPluginContext context)
{
    // Only available to premium users with intermediate+ experience
    return await GenerateAdvancedAnalytics();
}
```

### Multi-language Support

Create language-aware descriptions and parameter schemas:

```csharp
[AIFunction<AdvancedPluginContext>]
[Description("{{GetLocalizedDescription context.Language \"search_function\"}}")]
public async Task<SearchResult[]> SearchAsync(
    [Description("{{GetLocalizedDescription context.Language \"search_query_param\"}}")]
    string query,
    [Description("{{GetLocalizedDescription context.Language \"max_results_param\"}}")]
    int maxResults,
    AdvancedPluginContext context)
{
    // Localized search implementation
}

// Helper method for localization (can be in base class)
private string GetLocalizedDescription(string language, string key)
{
    var localizations = new Dictionary<string, Dictionary<string, string>>
    {
        ["en"] = new() {
            ["search_function"] = "Search through available data sources",
            ["search_query_param"] = "The search query to execute",
            ["max_results_param"] = "Maximum number of results to return"
        },
        ["es"] = new() {
            ["search_function"] = "Buscar en fuentes de datos disponibles",
            ["search_query_param"] = "La consulta de búsqueda a ejecutar",
            ["max_results_param"] = "Número máximo de resultados a devolver"
        },
        ["fr"] = new() {
            ["search_function"] = "Rechercher dans les sources de données disponibles",
            ["search_query_param"] = "La requête de recherche à exécuter",
            ["max_results_param"] = "Nombre maximum de résultats à retourner"
        }
    };
    
    return localizations.GetValueOrDefault(language, localizations["en"])
        ?.GetValueOrDefault(key, key) ?? key;
}
```

## Advanced Features

### Context Inheritance

Create hierarchical context structures:

```csharp
public class BasePluginContext : IPluginMetadataContext
{
    public string UserId { get; set; } = "";
    public string Language { get; set; } = "en";
}

public class EnhancedPluginContext : BasePluginContext
{
    public string UserRole { get; set; } = "user";
    public bool HasPremiumAccess { get; set; } = false;
    public Dictionary<string, object> Permissions { get; set; } = new();
}
```

### Dynamic Schema Generation

Create parameter schemas that adapt to context:

```csharp
[AIFunction<EnhancedPluginContext>]
public async Task<string> ConfigurableSearchAsync(
    string query,
    [Description("{{context.HasPremiumAccess ? \"Advanced search options\" : \"Basic search options\"}}")]
    [Schema("""
    {
      "type": "object",
      "properties": {
        "sortBy": {
          "type": "string",
          "enum": {{context.HasPremiumAccess ? "[\"relevance\", \"date\", \"popularity\", \"custom\"]" : "[\"relevance\", \"date\"]"}}
        },
        "filters": {
          "type": "object",
          "properties": {
            {{#if context.HasPremiumAccess}}
            "advanced": {
              "type": "boolean",
              "description": "Enable advanced filtering options"
            },
            {{/if}}
            "dateRange": {
              "type": "string",
              "description": "Date range filter"
            }
          }
        }
      }
    }
    """)]
    object options,
    EnhancedPluginContext context)
{
    // Implementation uses dynamic options based on context
}
```

## Examples

### Complete Real-World Example

Here's a comprehensive example showing a multi-language, role-based file management plugin:

**C# Plugin:**

```csharp
public class FileManagerContext : IPluginMetadataContext
{
    public string UserId { get; set; } = "";
    public string UserRole { get; set; } = "user";
    public string Language { get; set; } = "en";
    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
    public long MaxFileSize { get; set; } = 10_000_000; // 10MB default
    public bool CanAccessSystemFiles { get; set; } = false;
    public Dictionary<string, object> Permissions { get; set; } = new();
}

public class FileManagerPlugin
{
    [AIFunction<FileManagerContext>]
    [Description("{{GetFileOperationDescription context.Language \"list_files\"}}")]
    public async Task<FileInfo[]> ListFilesAsync(
        [Description("{{GetFileOperationDescription context.Language \"directory_path\"}}")]
        string directoryPath,
        FileManagerContext context)
    {
        var allowedPath = ValidatePathAccess(directoryPath, context);
        return await GetFilesInDirectory(allowedPath, context.AllowedExtensions);
    }
    
    [AIFunction<FileManagerContext>]
    [Description("{{GetFileOperationDescription context.Language \"upload_file\"}}")]
    [Conditional("context.UserRole != \"readonly\"")]
    public async Task<string> UploadFileAsync(
        [Description("{{GetFileOperationDescription context.Language \"file_data\"}}")]
        byte[] fileData,
        [Description("{{GetFileOperationDescription context.Language \"filename\"}}")]
        string filename,
        FileManagerContext context)
    {
        ValidateFileSize(fileData.Length, context.MaxFileSize);
        ValidateFileExtension(filename, context.AllowedExtensions);
        
        return await SaveFile(fileData, filename, context.UserId);
    }
    
    [AIFunction<FileManagerContext>]
    [Description("{{GetFileOperationDescription context.Language \"delete_file\"}}")]
    [Conditional("context.UserRole == \"admin\" || context.UserRole == \"moderator\"")]
    public async Task<string> DeleteFileAsync(
        string filePath,
        FileManagerContext context)
    {
        ValidateFileOwnership(filePath, context.UserId, context.UserRole);
        return await DeleteFile(filePath);
    }
    
    [AIFunction<FileManagerContext>]
    [Description("System file operations (Admin only)")]
    [Conditional("context.CanAccessSystemFiles && context.UserRole == \"admin\"")]
    public async Task<string> SystemFileOperationAsync(
        string operation,
        string targetPath,
        FileManagerContext context)
    {
        // Only available to admin users with system file access
        return await ExecuteSystemOperation(operation, targetPath);
    }
    
    private string GetFileOperationDescription(string language, string operation)
    {
        var descriptions = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() {
                ["list_files"] = "List files in the specified directory",
                ["directory_path"] = "The directory path to list files from",
                ["upload_file"] = "Upload a file to the system",
                ["file_data"] = "The binary data of the file to upload",
                ["filename"] = "The name of the file including extension",
                ["delete_file"] = "Delete a file from the system (requires elevated permissions)"
            },
            ["es"] = new() {
                ["list_files"] = "Listar archivos en el directorio especificado",
                ["directory_path"] = "La ruta del directorio del cual listar archivos",
                ["upload_file"] = "Subir un archivo al sistema",
                ["file_data"] = "Los datos binarios del archivo a subir",
                ["filename"] = "El nombre del archivo incluyendo la extensión",
                ["delete_file"] = "Eliminar un archivo del sistema (requiere permisos elevados)"
            },
            ["fr"] = new() {
                ["list_files"] = "Lister les fichiers dans le répertoire spécifié",
                ["directory_path"] = "Le chemin du répertoire à partir duquel lister les fichiers",
                ["upload_file"] = "Télécharger un fichier vers le système",
                ["file_data"] = "Les données binaires du fichier à télécharger",
                ["filename"] = "Le nom du fichier incluant l'extension",
                ["delete_file"] = "Supprimer un fichier du système (nécessite des permissions élevées)"
            }
        };
        
        return descriptions.GetValueOrDefault(language, descriptions["en"])
            ?.GetValueOrDefault(operation, operation) ?? operation;
    }
}
```

This example demonstrates:
- Multi-language support with dynamic descriptions
- Role-based function availability
- Complex context properties including arrays and objects
- Comprehensive permission systems
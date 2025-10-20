using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace HPD_Agent.Skills;

/// <summary>
/// Defines a skill as a semantic grouping of functions from multiple plugins.
/// Skills reference functions (M:N relationship) rather than owning them (1:N relationship).
/// Skills work identically to plugin containers with ephemeral instructions.
/// </summary>
public class SkillDefinition
{
    private const int MAX_DOCUMENT_SIZE = 1024 * 1024; // 1MB limit for security

    /// <summary>
    /// The name of the skill (used as container function name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this skill enables (shown before expansion).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Plugin references - references all functions from entire plugins.
    /// Example: ["FileSystemPlugin", "DebugPlugin"]
    /// These are expanded to individual function references during Build().
    /// </summary>
    public string[] PluginReferences { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Function references in format "PluginName.FunctionName" or just "FunctionName" for non-plugin functions.
    /// Example: ["FileSystemPlugin.ReadFile", "FileSystemPlugin.WriteFile", "DebugPlugin.GetStackTrace"]
    /// </summary>
    public string[] FunctionReferences { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Determines if this skill hides its referenced functions or not.
    /// Default: InstructionOnly (functions stay visible, skill provides instructions).
    /// </summary>
    public SkillScopingMode ScopingMode { get; set; } = SkillScopingMode.InstructionOnly;

    /// <summary>
    /// If true, hides plugin containers for plugins referenced via PluginReferences.
    /// Only applies when using PluginReferences on plugins with [PluginScope].
    /// Use this when you want the skill to "take ownership" and provide the only expansion path.
    /// Default: false (plugin containers remain visible, providing alternate expansion path).
    /// </summary>
    public bool SuppressPluginContainers { get; set; } = false;

    /// <summary>
    /// If true, this skill is automatically expanded at the start of each conversation.
    /// This replaces the "always visible" use case from plugin scoping.
    /// </summary>
    public bool AutoExpand { get; set; } = false;

    /// <summary>
    /// Optional inline post-expansion instructions (shown after skill is activated).
    /// </summary>
    public string? PostExpansionInstructions { get; set; }

    /// <summary>
    /// Optional file paths to markdown documents containing post-expansion instructions.
    /// Documents are loaded at Build() time and merged with PostExpansionInstructions.
    /// Paths are validated for security (must be within approved base directory).
    /// </summary>
    public string[]? PostExpansionInstructionDocuments { get; set; }

    /// <summary>
    /// Base directory for instruction documents (defaults to "skills/documents/").
    /// All document paths are resolved relative to this directory.
    /// </summary>
    public string InstructionDocumentBaseDirectory { get; set; } = "skills/documents/";

    /// <summary>
    /// Resolved instructions after loading all documents (set during Build()).
    /// This combines PostExpansionInstructions + all loaded documents.
    /// </summary>
    internal string? ResolvedInstructions { get; set; }

    /// <summary>
    /// Resolved function references after expanding PluginReferences (set during Build()).
    /// This combines PluginReferences (expanded to all functions) + FunctionReferences.
    /// </summary>
    internal string[] ResolvedFunctionReferences { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Validates this skill definition and loads all instruction documents.
    /// Called at Build() time to fail-fast on configuration errors.
    /// </summary>
    /// <param name="allFunctions">All registered functions for validation</param>
    /// <exception cref="InvalidOperationException">If validation fails</exception>
    /// <exception cref="FileNotFoundException">If a document file is not found</exception>
    /// <exception cref="SecurityException">If a document path is invalid</exception>
    public void Build(Dictionary<string, AIFunction> allFunctions)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Skill Name is required");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            throw new InvalidOperationException($"Skill '{Name}' Description is required");
        }

        // Resolve plugin references to function names
        var resolvedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process PluginReferences - expand to all functions from those plugins
        if (PluginReferences != null && PluginReferences.Length > 0)
        {
            foreach (var pluginRef in PluginReferences)
            {
                // Find all functions with ParentPlugin = pluginRef
                var pluginFunctions = allFunctions
                    .Where(kvp => kvp.Value.AdditionalProperties
                        ?.TryGetValue("ParentPlugin", out var parent) == true
                        && parent is string p && p.Equals(pluginRef, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key);

                var functionsAdded = false;
                foreach (var funcName in pluginFunctions)
                {
                    resolvedFunctions.Add(funcName);
                    functionsAdded = true;
                }

                // Warn if plugin reference didn't resolve to any functions
                if (!functionsAdded)
                {
                    throw new InvalidOperationException(
                        $"Skill '{Name}' references plugin '{pluginRef}' which has no registered functions. " +
                        $"Ensure the plugin is registered before building skills.");
                }
            }
        }

        // Process FunctionReferences - add individual function references
        if (FunctionReferences != null && FunctionReferences.Length > 0)
        {
            foreach (var funcRef in FunctionReferences)
            {
                resolvedFunctions.Add(funcRef);
            }
        }

        // Validate at least one reference (plugin or function)
        if (resolvedFunctions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Skill '{Name}' must reference at least one plugin or function");
        }

        // Validate all resolved function references exist
        var missingFunctions = new List<string>();
        foreach (var reference in resolvedFunctions)
        {
            if (!allFunctions.ContainsKey(reference))
            {
                missingFunctions.Add(reference);
            }
        }

        if (missingFunctions.Any())
        {
            var availableFunctions = string.Join(", ", allFunctions.Keys.Take(20));
            throw new InvalidOperationException(
                $"Skill '{Name}' references {missingFunctions.Count} unknown function(s): {string.Join(", ", missingFunctions)}. " +
                $"Available functions: {availableFunctions}{(allFunctions.Count > 20 ? "..." : "")}");
        }

        // Store resolved function references
        ResolvedFunctionReferences = resolvedFunctions.ToArray();

        // Load and merge all instruction documents
        ResolvedInstructions = LoadInstructions();
    }

    /// <summary>
    /// Loads and merges all instruction sources (inline + documents).
    /// </summary>
    private string LoadInstructions()
    {
        var instructions = new StringBuilder();

        // Add inline instructions first
        if (!string.IsNullOrEmpty(PostExpansionInstructions))
        {
            instructions.AppendLine(PostExpansionInstructions);
        }

        // Load and append document instructions
        if (PostExpansionInstructionDocuments != null && PostExpansionInstructionDocuments.Length > 0)
        {
            foreach (var documentPath in PostExpansionInstructionDocuments)
            {
                var content = LoadDocument(documentPath);
                if (!string.IsNullOrEmpty(content))
                {
                    if (instructions.Length > 0)
                    {
                        instructions.AppendLine(); // Separator between documents
                    }
                    instructions.AppendLine(content);
                }
            }
        }

        return instructions.Length > 0 ? instructions.ToString().Trim() : null;
    }

    /// <summary>
    /// Loads a single instruction document with security validation.
    /// </summary>
    private string LoadDocument(string documentPath)
    {
        // Resolve path relative to base directory
        var baseDirectory = Path.GetFullPath(InstructionDocumentBaseDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, documentPath));

        // Security: Validate path is within base directory (prevent path traversal)
        if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                $"Skill '{Name}' document path '{documentPath}' is outside allowed directory '{baseDirectory}'. " +
                $"Resolved path: '{fullPath}'");
        }

        // Check file exists
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Skill '{Name}' instruction document not found: '{documentPath}' (resolved to '{fullPath}')");
        }

        // Security: Validate file size
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MAX_DOCUMENT_SIZE)
        {
            throw new InvalidOperationException(
                $"Skill '{Name}' document '{documentPath}' exceeds maximum size of {MAX_DOCUMENT_SIZE:N0} bytes " +
                $"(actual: {fileInfo.Length:N0} bytes)");
        }

        // Load document content
        try
        {
            return File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is not SecurityException && ex is not FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Failed to read skill '{Name}' document '{documentPath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates the container AIFunction for this skill.
    /// Container functions work identically to plugin containers with ephemeral results.
    /// </summary>
    /// <returns>Container function that returns skill expansion message with instructions</returns>
    public AIFunction CreateContainer()
    {
        // Extract function names from resolved references (strip plugin prefix if present)
        var functionNames = ResolvedFunctionReferences
            .Select(ExtractFunctionName)
            .ToArray();

        var functionList = string.Join(", ", functionNames);

        // Build the full description shown before expansion
        var fullDescription = Description;

        // Container result message (ephemeral - filtered from persistent history)
        var returnMessage = $"{Name} expanded. Available functions: {functionList}";

        // Append resolved instructions if available
        if (!string.IsNullOrEmpty(ResolvedInstructions))
        {
            returnMessage += $"\n\n{ResolvedInstructions}";
        }

        // Create container function (returns instructions on activation)
        return HPDAIFunctionFactory.Create(
            async (arguments, cancellationToken) => returnMessage,
            new HPDAIFunctionFactoryOptions
            {
                Name = Name,
                Description = fullDescription,
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["IsContainer"] = true,
                    ["IsSkill"] = true,
                    ["SkillName"] = Name,
                    ["FunctionReferences"] = ResolvedFunctionReferences,
                    ["AutoExpand"] = AutoExpand
                }
            });
    }

    /// <summary>
    /// Extracts the function name from a reference.
    /// "FileSystemPlugin.ReadFile" -> "ReadFile"
    /// "ReadFile" -> "ReadFile"
    /// </summary>
    private static string ExtractFunctionName(string reference)
    {
        var lastDot = reference.LastIndexOf('.');
        return lastDot >= 0 ? reference.Substring(lastDot + 1) : reference;
    }
}

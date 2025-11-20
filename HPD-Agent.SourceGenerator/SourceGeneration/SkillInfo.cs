using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Information about a skill discovered during source generation.
/// </summary>
internal class SkillInfo
{
    /// <summary>
    /// Method name (e.g., "FileDebugging")
    /// </summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>
    /// Skill name from SkillFactory.Create() call (ideally matches MethodName)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Category from [Skill(Category = "...")] attribute (Phase 3)
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Priority from [Skill(Priority = N)] attribute (Phase 3)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Description shown before activation
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Instructions shown after activation
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Skill options extracted from SkillFactory.Create() call
    /// </summary>
    public SkillOptionsInfo Options { get; set; } = new();

    /// <summary>
    /// References to functions or other skills
    /// </summary>
    public List<ReferenceInfo> References { get; set; } = new();

    /// <summary>
    /// Class containing this skill method
    /// </summary>
    public ClassDeclarationSyntax ContainingClass { get; set; } = null!;

    /// <summary>
    /// Full name: "ClassName.MethodName"
    /// </summary>
    public string FullName => $"{ContainingClass?.Identifier.ValueText ?? "Unknown"}.{MethodName}";

    /// <summary>
    /// Resolved function references (populated during resolution phase)
    /// Format: "PluginName.FunctionName"
    /// </summary>
    public List<string> ResolvedFunctionReferences { get; set; } = new();

    /// <summary>
    /// Resolved plugin types (populated during resolution phase)
    /// </summary>
    public List<string> ResolvedPluginTypes { get; set; } = new();
}

/// <summary>
/// Information about skill options extracted from SkillFactory.Create() call
/// </summary>
internal class SkillOptionsInfo
{
    /// <summary>
    /// Optional instruction document paths (OLD - kept for backwards compatibility)
    /// </summary>
    public List<string> InstructionDocuments { get; set; } = new();

    /// <summary>
    /// Base directory for instruction documents (OLD - kept for backwards compatibility)
    /// </summary>
    public string InstructionDocumentBaseDirectory { get; set; } = "agent-skills/documents/";

    /// <summary>
    /// Document references (Phase 3: from AddDocument calls)
    /// </summary>
    public List<DocumentReferenceInfo> DocumentReferences { get; set; } = new();

    /// <summary>
    /// Document uploads (Phase 3: from AddDocumentFromFile calls)
    /// </summary>
    public List<DocumentUploadInfo> DocumentUploads { get; set; } = new();
}

/// <summary>
/// Information about a document reference (from AddDocument call)
/// </summary>
internal class DocumentReferenceInfo
{
    /// <summary>
    /// Document ID
    /// </summary>
    public string DocumentId { get; set; } = string.Empty;

    /// <summary>
    /// Optional description override
    /// </summary>
    public string? DescriptionOverride { get; set; }
}

/// <summary>
/// Information about a document upload (from AddDocumentFromFile call)
/// </summary>
internal class DocumentUploadInfo
{
    /// <summary>
    /// File path to upload
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Document ID (auto-derived or explicit)
    /// </summary>
    public string DocumentId { get; set; } = string.Empty;

    /// <summary>
    /// Document description
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Information about a reference in a skill (function or skill reference)
/// </summary>
internal class ReferenceInfo
{
    /// <summary>
    /// Type of reference (function or skill)
    /// </summary>
    public ReferenceType ReferenceType { get; set; }

    /// <summary>
    /// Plugin type name (e.g., "FileSystemPlugin")
    /// </summary>
    public string PluginType { get; set; } = string.Empty;

    /// <summary>
    /// Method name (e.g., "ReadFile" or "FileDebugging")
    /// </summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>
    /// Full name: "PluginType.MethodName"
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Location in source code (for diagnostics)
    /// </summary>
    public Location? Location { get; set; }
}

/// <summary>
/// Type of reference
/// </summary>
internal enum ReferenceType
{
    /// <summary>
    /// Reference to a function (method with [AIFunction])
    /// </summary>
    Function,

    /// <summary>
    /// Reference to another skill (method returning Skill)
    /// </summary>
    Skill
}

/// <summary>
/// Resolved skill information after flattening nested references
/// </summary>
internal class ResolvedSkillInfo
{
    /// <summary>
    /// All function references (deduplicated)
    /// Format: "PluginName.FunctionName"
    /// </summary>
    public List<string> FunctionReferences { get; set; } = new();

    /// <summary>
    /// All plugin types referenced (deduplicated)
    /// </summary>
    public List<string> PluginTypes { get; set; } = new();
}

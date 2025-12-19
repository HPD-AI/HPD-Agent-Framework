// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace HPD.Agent.SourceGenerator;

/// <summary>
/// Incremental source generator for middleware state container.
/// Generates properties and WithX() methods for types marked with [MiddlewareState].
/// Supports both source types and types from referenced assemblies.
/// </summary>
[Generator]
public class MiddlewareStateGenerator : IIncrementalGenerator
{
    //
    // DIAGNOSTIC DESCRIPTORS
    //

    private static readonly DiagnosticDescriptor HPD001_MustBeRecord = new(
        id: "HPD001",
        title: "Middleware state must be a record",
        messageFormat: "[MiddlewareState] can only be applied to record types. Class '{0}' must be declared as a record.",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Records provide immutability guarantees, structural equality, and 'with' expressions that are essential for the immutable state pattern.");

    private static readonly DiagnosticDescriptor HPD002_ShouldBeSealed = new(
        id: "HPD002",
        title: "Middleware state should be sealed",
        messageFormat: "Middleware state record '{0}' should be sealed for performance. Consider adding 'sealed' modifier.",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Sealed records enable compiler optimizations (devirtualization) and prevent unintended inheritance.");

    private static readonly DiagnosticDescriptor HPD003_DuplicateKey = new(
        id: "HPD003",
        title: "Duplicate middleware state key",
        messageFormat: "Middleware state key '{0}' is already registered by type '{1}'. Each middleware state must have a unique fully-qualified name.",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The container uses fully-qualified type names as dictionary keys. Duplicates would cause runtime conflicts.");

    private static readonly DiagnosticDescriptor HPD005_PropertyNameConflict = new(
        id: "HPD005",
        title: "Middleware state property name conflicts with container API",
        messageFormat: "Generated property name '{0}' conflicts with MiddlewareState API. Rename type '{1}' to avoid conflicts with: GetState, SetState, _states, _deserializedCache.",
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator creates properties based on type names. Conflicts with base class methods would cause compilation errors.");

    // Reserved names that would conflict with the container API
    private static readonly HashSet<string> ReservedPropertyNames = new(System.StringComparer.Ordinal)
    {
        "GetState",
        "SetState",
        "_states",
        "_deserializedCache"
    };

    //
    // INITIALIZATION
    //

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all types with [MiddlewareState] attribute in SOURCE CODE
        var sourceStateTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "HPD.Agent.MiddlewareStateAttribute",
                predicate: (node, _) => node is TypeDeclarationSyntax,
                transform: GetStateInfo)
            .Where(static info => info is not null);

        // Combine source types
        var allStateTypes = sourceStateTypes.Collect()
            .Select((sourceTypes, _) =>
            {
                // Only use source types - don't pull in referenced types for consumer projects
                var combined = new List<StateInfo>();
                foreach (var t in sourceTypes)
                {
                    if (t != null) combined.Add(t);
                }
                return combined.ToImmutableArray();
            });

        // Generate container with source state types only
        context.RegisterSourceOutput(
            allStateTypes,
            (spc, types) => GenerateContainerProperties(spc, types!));
    }

    /// <summary>
    /// Scans referenced assemblies for types marked with [MiddlewareState].
    /// </summary>
    private static ImmutableArray<StateInfo> GetReferencedMiddlewareStates(
        Compilation compilation,
        CancellationToken ct)
    {
        var results = new List<StateInfo>();
        var attributeSymbol = compilation.GetTypeByMetadataName("HPD.Agent.MiddlewareStateAttribute");

        if (attributeSymbol == null)
            return ImmutableArray<StateInfo>.Empty;

        // Scan all referenced assemblies
        foreach (var reference in compilation.References)
        {
            ct.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            // Skip the current assembly (those are handled by ForAttributeWithMetadataName)
            if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
                continue;

            // Find types with [MiddlewareState] in this assembly
            var stateTypes = FindTypesWithAttribute(assembly.GlobalNamespace, attributeSymbol, ct);

            foreach (var typeSymbol in stateTypes)
            {
                var stateInfo = GetStateInfoFromSymbol(typeSymbol, attributeSymbol);
                if (stateInfo != null)
                {
                    results.Add(stateInfo);
                }
            }
        }

        return results.ToImmutableArray();
    }

    /// <summary>
    /// Recursively finds all types with the specified attribute in a namespace.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> FindTypesWithAttribute(
        INamespaceSymbol ns,
        INamedTypeSymbol attributeSymbol,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var type in FindTypesWithAttribute(childNs, attributeSymbol, ct))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol typeSymbol)
            {
                // Check if type has the attribute
                foreach (var attr in typeSymbol.GetAttributes())
                {
                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeSymbol))
                    {
                        yield return typeSymbol;
                        break;
                    }
                }

                // Also check nested types
                foreach (var nested in typeSymbol.GetTypeMembers())
                {
                    foreach (var attr in nested.GetAttributes())
                    {
                        if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeSymbol))
                        {
                            yield return nested;
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates StateInfo from a type symbol (for referenced assembly types).
    /// </summary>
    private static StateInfo? GetStateInfoFromSymbol(
        INamedTypeSymbol typeSymbol,
        INamedTypeSymbol attributeSymbol)
    {
        // Must be a record
        if (typeSymbol.TypeKind != TypeKind.Class || !typeSymbol.IsRecord)
            return null;

        var typeName = typeSymbol.Name;
        var fullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "");
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var propertyName = GetPropertyName(typeName);

        // Check for reserved names
        if (ReservedPropertyNames.Contains(propertyName))
            return null;

        // Extract version from attribute
        int version = 1;
        var attribute = typeSymbol.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol));

        if (attribute != null)
        {
            foreach (var namedArg in attribute.NamedArguments)
            {
                if (namedArg.Key == "Version" && namedArg.Value.Value is int v)
                {
                    version = v;
                    break;
                }
            }
        }

        return new StateInfo(
            TypeName: typeName,
            FullyQualifiedName: fullyQualifiedName,
            PropertyName: propertyName,
            Namespace: namespaceName,
            Version: version,
            Diagnostics: new List<Diagnostic>(), // No diagnostics for referenced types
            IsFromReference: true);
    }

    //      
    // STATE INFO EXTRACTION
    //      

    private StateInfo? GetStateInfo(
        GeneratorAttributeSyntaxContext context,
        CancellationToken ct)
    {
        if (context.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        var syntax = context.TargetNode as TypeDeclarationSyntax;
        if (syntax is null)
            return null;

        var diagnostics = new List<Diagnostic>();

        // HPD001: Must be a record
        if (typeSymbol.TypeKind != TypeKind.Class || !typeSymbol.IsRecord)
        {
            diagnostics.Add(Diagnostic.Create(
                HPD001_MustBeRecord,
                syntax.Identifier.GetLocation(),
                typeSymbol.Name));
            return new StateInfo(
                TypeName: typeSymbol.Name,
                FullyQualifiedName: "",
                PropertyName: "",
                Namespace: "",
                Version: 1,
                Diagnostics: diagnostics);
        }

        // HPD002: Should be sealed (warning only)
        if (!typeSymbol.IsSealed)
        {
            diagnostics.Add(Diagnostic.Create(
                HPD002_ShouldBeSealed,
                syntax.Identifier.GetLocation(),
                typeSymbol.Name));
        }

        // Extract info
        var typeName = typeSymbol.Name;
        var fullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", ""); // Remove global:: prefix
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var propertyName = GetPropertyName(typeName);

        // Extract version from [MiddlewareState(Version = X)] attribute
        int version = 1; // Default
        var attribute = context.Attributes.FirstOrDefault();
        if (attribute != null)
        {
            foreach (var namedArg in attribute.NamedArguments)
            {
                if (namedArg.Key == "Version" && namedArg.Value.Value is int v)
                {
                    version = v;
                    break;
                }
            }
        }

        // HPD005: Check for property name conflicts
        if (ReservedPropertyNames.Contains(propertyName))
        {
            diagnostics.Add(Diagnostic.Create(
                HPD005_PropertyNameConflict,
                syntax.Identifier.GetLocation(),
                propertyName,
                typeName));
            return new StateInfo(
                TypeName: typeName,
                FullyQualifiedName: fullyQualifiedName,
                PropertyName: propertyName,
                Namespace: namespaceName,
                Version: version,
                Diagnostics: diagnostics);
        }

        return new StateInfo(
            TypeName: typeName,
            FullyQualifiedName: fullyQualifiedName,
            PropertyName: propertyName,
            Namespace: namespaceName,
            Version: version,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Generates property name from type name.
    /// Examples: "CircuitBreakerStateData" -> "CircuitBreaker"
    ///           "ErrorTrackingStateData" -> "ErrorTracking"
    /// </summary>
    private static string GetPropertyName(string typeName)
    {
        // Remove common suffixes
        var suffixes = new[] { "StateData", "State", "Data" };
        foreach (var suffix in suffixes)
        {
            if (typeName.EndsWith(suffix) && typeName.Length > suffix.Length)
            {
                return typeName.Substring(0, typeName.Length - suffix.Length);
            }
        }
        return typeName;
    }

    //      
    // CODE GENERATION
    //      

    private void GenerateContainerProperties(
        SourceProductionContext context,
        ImmutableArray<StateInfo> types)
    {
        if (types.IsEmpty)
            return;

        // Report all diagnostics from state info collection
        foreach (var type in types)
        {
            foreach (var diagnostic in type.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        // Filter out types with errors (keep warnings)
        var validTypes = types
            .Where(t => !t.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            .ToList();

        if (validTypes.Count == 0)
            return;

        // HPD003: Check for duplicate keys
        var duplicates = validTypes
            .GroupBy(t => t.FullyQualifiedName)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            var first = duplicate.First();
            var others = duplicate.Skip(1);
            foreach (var other in others)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    HPD003_DuplicateKey,
                    Location.None, // We don't have location info here
                    duplicate.Key,
                    first.TypeName));
            }
        }

        // Remove duplicates for generation
        var uniqueTypes = validTypes
            .GroupBy(t => t.FullyQualifiedName)
            .Select(g => g.First())
            .ToList();

        // Generate the partial class
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by MiddlewareStateGenerator");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Collections.Immutable;");
        sb.AppendLine();
        sb.AppendLine("namespace HPD.Agent;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Generated properties for middleware state container.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public sealed partial class MiddlewareState");
        sb.AppendLine("{");

        // Generate schema metadata constants
        var sortedTypeNames = uniqueTypes
            .Select(t => t.FullyQualifiedName)
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToList();

        sb.AppendLine("    //      ");
        sb.AppendLine("    // SCHEMA METADATA (Generated)");
        sb.AppendLine("    //      ");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Compiled schema signature (sorted list of middleware state FQNs).");
        sb.AppendLine("    /// Used for detecting middleware composition changes across deployments.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public const string CompiledSchemaSignature = \"{string.Join(",", sortedTypeNames)}\";");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Container schema version (for future container-level migrations).");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public const int CompiledSchemaVersion = 1;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Per-state version mapping (type FQN → version).");
        sb.AppendLine("    /// Used for detecting individual state schema changes.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    internal static readonly ImmutableDictionary<string, int> CompiledStateVersions =");
        sb.AppendLine("        new Dictionary<string, int>");
        sb.AppendLine("        {");
        foreach (var state in uniqueTypes)
        {
            sb.AppendLine($"            [\"{state.FullyQualifiedName}\"] = {state.Version},");
        }
        sb.AppendLine("        }.ToImmutableDictionary();");
        sb.AppendLine();

        // Generate properties and WithX methods for each state type
        foreach (var stateInfo in uniqueTypes)
        {
            sb.AppendLine($"    //      ");
            sb.AppendLine($"    // {stateInfo.PropertyName} ({stateInfo.TypeName})");
            sb.AppendLine($"    //      ");
            sb.AppendLine();

            // Generate property
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Gets {stateInfo.PropertyName} middleware state.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public {stateInfo.FullyQualifiedName}? {stateInfo.PropertyName}");
            sb.AppendLine($"    {{");
            sb.AppendLine($"        get => this.GetState<{stateInfo.FullyQualifiedName}>(\"{stateInfo.FullyQualifiedName}\");");
            sb.AppendLine($"    }}");
            sb.AppendLine();

            // Generate WithX method
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Creates a new container with updated {stateInfo.PropertyName} state (immutable).");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    /// <param name=\"value\">New state value (null to clear)</param>");
            sb.AppendLine($"    /// <returns>New container instance with updated state</returns>");
            sb.AppendLine($"    public MiddlewareState With{stateInfo.PropertyName}({stateInfo.FullyQualifiedName}? value)");
            sb.AppendLine($"    {{");
            sb.AppendLine($"        return value == null");
            sb.AppendLine($"            ? this");
            sb.AppendLine($"            : this.SetState<{stateInfo.FullyQualifiedName}>(\"{stateInfo.FullyQualifiedName}\", value);");
            sb.AppendLine($"    }}");
            sb.AppendLine();
        }

        sb.AppendLine("}");

        context.AddSource("MiddlewareState.g.cs", sb.ToString());
    }

    //
    // STATE INFO RECORD
    //

    private record StateInfo(
        string TypeName,
        string FullyQualifiedName,
        string PropertyName,
        string Namespace,
        int Version,
        List<Diagnostic> Diagnostics,
        bool IsFromReference = false);
}

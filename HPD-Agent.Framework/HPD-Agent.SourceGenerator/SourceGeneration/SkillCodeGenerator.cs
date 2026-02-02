using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Generates code for skill registration
/// </summary>
internal static class SkillCodeGenerator
{
    /// <summary>
    /// Generates the GetReferencedToolkits() method for auto-registration
    /// PHASE 5: Now uses SkillCapabilities (fully populated with resolved references)
    /// </summary>
    public static string GenerateGetReferencedToolkitsMethod(ToolkitInfo Toolkit)
    {
        if (!Toolkit.SkillCapabilities.Any())
            return string.Empty;

        var allReferencedToolkits = Toolkit.SkillCapabilities
            .SelectMany(s => s.ResolvedToolkitTypes)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (!allReferencedToolkits.Any())
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Gets the list of Toolkits referenced by skills in this class");
        sb.AppendLine("        /// Used by AgentBuilder for auto-registration");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static string[] GetReferencedToolkits()");
        sb.AppendLine("        {");
        sb.AppendLine("            return new string[]");
        sb.AppendLine("            {");

        for (int i = 0; i < allReferencedToolkits.Count; i++)
        {
            var comma = i < allReferencedToolkits.Count - 1 ? "," : "";
            sb.AppendLine($"                \"{allReferencedToolkits[i]}\"{comma}");
        }

        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the GetReferencedFunctions() method for selective function registration
    /// PHASE 5: Now uses SkillCapabilities (fully populated with resolved references)
    /// </summary>
    public static string GenerateGetReferencedFunctionsMethod(ToolkitInfo Toolkit)
    {
        if (!Toolkit.SkillCapabilities.Any())
            return string.Empty;

        // Build dictionary: ToolkitName -> HashSet<FunctionName>
        var toolFunctions = new Dictionary<string, HashSet<string>>();

        foreach (var skill in Toolkit.SkillCapabilities)
        {
            foreach (var funcRef in skill.ResolvedFunctionReferences)
            {
                // "FileSystemToolkit.ReadFile" -> ("FileSystemToolkit", "ReadFile")
                var parts = funcRef.Split('.');
                if (parts.Length == 2)
                {
                    var toolName = parts[0];
                    var functionName = parts[1];

                    if (!toolFunctions.ContainsKey(toolName))
                        toolFunctions[toolName] = new HashSet<string>();

                    toolFunctions[toolName].Add(functionName);
                }
            }
        }

        if (!toolFunctions.Any())
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Gets the specific functions referenced by skills (for selective registration)");
        sb.AppendLine("        /// Used by AgentBuilder to register only needed functions from each Toolkit");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static Dictionary<string, string[]> GetReferencedFunctions()");
        sb.AppendLine("        {");
        sb.AppendLine("            return new Dictionary<string, string[]>");
        sb.AppendLine("            {");

        var entries = toolFunctions.OrderBy(kvp => kvp.Key).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var comma = i < entries.Count - 1 ? "," : "";
            var functions = string.Join("\", \"", entries[i].Value.OrderBy(f => f));
            sb.AppendLine($"                {{ \"{entries[i].Key}\", new string[] {{ \"{functions}\" }} }}{comma}");
        }

        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates skill registration code to be added to CreateToolkit() method
    /// Handles both class-level collapsing (if [Toolkit] on class with Collapsed=true) and individual skill containers
    /// </summary>
    public static string GenerateSkillRegistrations(ToolkitInfo Toolkit)
    {
        // Early exit ONLY if no skills AND not collapsed
        // If Toolkit is collapsed, we need to register the container even without skills
        if (!Toolkit.SkillCapabilities.Any() && !Toolkit.IsCollapsed)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();

        // If the Toolkit is collapsed, create a class-level container first
        if (Toolkit.IsCollapsed)
        {
            sb.AppendLine("        // Register toolkit container");
            // Method name uses ClassName; the container's Name property uses EffectiveName
            sb.AppendLine($"        functions.Add(Create{Toolkit.ClassName}Container(instance));");
            sb.AppendLine();
        }

        // Early exit if no skills to register (but after registering collapse container if needed)
        if (!Toolkit.SkillCapabilities.Any())
            return sb.ToString();

        sb.AppendLine("        // Register skill containers");

        foreach (var skill in Toolkit.SkillCapabilities)
        {
            // Check if skill has conditional registration (same pattern as Functions/SubAgents)
            var hasConditionalEvaluator = skill.IsConditional &&
                                        skill.HasTypedMetadata;

            if (hasConditionalEvaluator)
            {
                sb.AppendLine($"        if (Evaluate{skill.Name}Condition(context))");
                sb.AppendLine("        {");
                sb.AppendLine($"            functions.Add(Create{skill.MethodName}Skill(instance, context));");
                sb.AppendLine("        }");
            }
            else
            {
                // Each skill generates exactly one container function
                sb.AppendLine($"        functions.Add(Create{skill.MethodName}Skill(instance, context));");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates skill container function.
    /// Skills ARE containers - there's only one function per skill.
    /// PHASE 5: Now accepts SkillCapability instead of SkillInfo
    /// </summary>
    public static string GenerateSkillContainerFunction(HPD.Agent.SourceGenerator.Capabilities.SkillCapability skill, ToolkitInfo Toolkit)
    {
        var sb = new StringBuilder();

        // Simple activation message for function result
        // The prompt Middleware will build the complete context from metadata
        var functionList = string.Join(", ", skill.ResolvedFunctionReferences);
        var returnMessage = $"{skill.Name} skill activated. Available functions: {functionList}";

        // Still include instructions in function result for backward compatibility
        // PHASE 5: SkillCapability uses FunctionResult instead of Instructions
        if (!string.IsNullOrEmpty(skill.FunctionResult))
        {
            returnMessage += $"\n\n{skill.FunctionResult}";
        }

        var escapedReturnMessage = returnMessage.Replace("\"", "\"\"");

        // Build description like Toolkit Collapsing: append function list
        var functionNames = string.Join(", ", skill.ResolvedFunctionReferences);

        // Support dynamic descriptions (like Functions)
        var descriptionCode = skill.HasDynamicDescription
            ? $"Resolve{skill.Name}Description(context)"
            : $"\"{skill.Description}\"";

        var fullDescriptionTemplate = $"{{0}}. References {skill.ResolvedFunctionReferences.Count} functions: {functionNames}";

        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Container function for {skill.Name} skill.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        /// <param name=\"instance\">Toolkit instance</param>");
        sb.AppendLine($"        /// <param name=\"context\">Execution context for dynamic descriptions</param>");
        sb.AppendLine($"        private static AIFunction Create{skill.MethodName}Skill({Toolkit.Name} instance, IToolMetadata? context)");
        sb.AppendLine("        {");

        // Generate runtime function body that checks configuration
        var baseMessage = $"{skill.Name} skill activated. Available functions: {functionList}";
        var escapedBaseMessage = baseMessage.Replace("\"", "\"\"");

        // Determine if skill has documents
        var hasDocuments = skill.Options.DocumentReferences.Any() || skill.Options.DocumentUploads.Any();

        // PHASE 5: SkillCapability uses FunctionResult instead of Instructions
        if (!string.IsNullOrEmpty(skill.FunctionResult))
        {
            var escapedInstructions = skill.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine("            return HPDAIFunctionFactory.Create(");
            sb.AppendLine("                async (arguments, cancellationToken) =>");
            sb.AppendLine("                {");
            sb.AppendLine("                    // Check if instructions should be included in function result");
            sb.AppendLine("                    var mode = HPD.Agent.AgentConfig.GlobalConfig?.Collapsing?.SkillInstructionMode ?? HPD.Agent.SkillInstructionMode.Both;");
            sb.AppendLine("                    if (mode == HPD.Agent.SkillInstructionMode.Both)");
            sb.AppendLine("                    {");
            sb.AppendLine($"                        return @\"{escapedBaseMessage}");
            sb.AppendLine();
            sb.AppendLine($"{escapedInstructions}\";");
            sb.AppendLine("                    }");

            // Generate appropriate message based on whether skill has documents
            if (hasDocuments)
            {
                var documentMessage = $"{skill.Name} skill activated. Available functions: {functionList}.\\n\\nIMPORTANT: This skill has associated documents. You MUST read the skill documents using the read_skill_document function to understand how to properly use this skill's functions.";
                var escapedDocumentMessage = documentMessage.Replace("\"", "\"\"");
                sb.AppendLine($"                    return @\"{escapedDocumentMessage}\";");
            }
            else
            {
                var reinforcementMessage = $"{skill.Name} skill activated. Available functions: {functionList}.\\n\\nREMINDER: Follow the instructions provided for this skill when using its functions.";
                var escapedReinforcementMessage = reinforcementMessage.Replace("\"", "\"\"");
                sb.AppendLine($"                    return @\"{escapedReinforcementMessage}\";");
            }
            sb.AppendLine("                },");
        }
        else
        {
            sb.AppendLine("            return HPDAIFunctionFactory.Create(");
            sb.AppendLine("                async (arguments, cancellationToken) =>");
            sb.AppendLine("                {");

            // Generate appropriate message based on whether skill has documents
            if (hasDocuments)
            {
                var documentMessage = $"{skill.Name} skill activated. Available functions: {functionList}.\\n\\nIMPORTANT: This skill has associated documents. You MUST read the skill documents using the read_skill_document function to understand how to properly use this skill's functions.";
                var escapedDocumentMessage = documentMessage.Replace("\"", "\"\"");
                sb.AppendLine($"                    return @\"{escapedDocumentMessage}\";");
            }
            else
            {
                sb.AppendLine($"                    return @\"{escapedBaseMessage}\";");
            }
            sb.AppendLine("                },");
        }
        sb.AppendLine("                new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                {");
        sb.AppendLine($"                    Name = \"{skill.Name}\",");

        // Use dynamic description if available, otherwise static
        if (skill.HasDynamicDescription)
        {
            // Generate: Description = Resolve{Name}Description(context) + ". References X functions: ..."
            sb.AppendLine($"                    Description = {descriptionCode} + \". References {skill.ResolvedFunctionReferences.Count} functions: {functionNames}\",");
        }
        else
        {
            var staticFullDescription = $"{skill.Description}. References {skill.ResolvedFunctionReferences.Count} functions: {functionNames}";
            sb.AppendLine($"                    Description = \"{staticFullDescription}\",");
        }

        sb.AppendLine($"                    RequiresPermission = {skill.RequiresPermission.ToString().ToLower()},");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");

        // Type-safe SkillDocuments property
        // Combine DocumentUploads and DocumentReferences
        var hasAnyDocuments = skill.Options.DocumentUploads.Any() || skill.Options.DocumentReferences.Any();

        if (hasAnyDocuments)
        {
            sb.AppendLine("                    SkillDocuments = new SkillDocumentContent[]");
            sb.AppendLine("                    {");

            // First, emit all DocumentUploads (files and URLs)
            foreach (var doc in skill.Options.DocumentUploads)
            {
                var escapedDesc = doc.Description.Replace("\"", "\\\"");
                var docId = string.IsNullOrEmpty(doc.DocumentId) ? "null" : $"\"{doc.DocumentId}\"";

                if (doc.SourceType == HPD.Agent.SourceGenerator.Capabilities.DocumentSourceType.FilePath)
                {
                    sb.AppendLine($"                        new SkillDocumentContent");
                    sb.AppendLine("                        {");
                    sb.AppendLine($"                            DocumentId = {docId},");
                    sb.AppendLine($"                            Description = \"{escapedDesc}\",");
                    sb.AppendLine($"                            FilePath = \"{doc.FilePath}\"");
                    sb.AppendLine("                        },");
                }
                else // DocumentSourceType.Url
                {
                    sb.AppendLine($"                        new SkillDocumentContent");
                    sb.AppendLine("                        {");
                    sb.AppendLine($"                            DocumentId = {docId},");
                    sb.AppendLine($"                            Description = \"{escapedDesc}\",");
                    sb.AppendLine($"                            Url = \"{doc.Url}\"");
                    sb.AppendLine("                        },");
                }
            }

            // Then, emit all DocumentReferences (references to existing documents)
            foreach (var docRef in skill.Options.DocumentReferences)
            {
                var escapedDesc = string.IsNullOrEmpty(docRef.DescriptionOverride)
                    ? "null"
                    : $"\"{docRef.DescriptionOverride.Replace("\"", "\\\"")}\"";

                sb.AppendLine($"                        new SkillDocumentContent");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            DocumentId = \"{docRef.DocumentId}\",");
                sb.AppendLine($"                            Description = {escapedDesc}");
                sb.AppendLine("                        },");
            }

            sb.AppendLine("                    },");
        }

        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine("                        [\"IsSkill\"] = true,");
        // PHASE 5: SkillCapability uses ParentToolkitName instead of ContainingClass
        sb.AppendLine($"                        [\"ParentSkillContainer\"] = \"{skill.ParentToolkitName}\",");
        sb.AppendLine($"                        [\"ReferencedFunctions\"] = new string[] {{ {string.Join(", ", skill.ResolvedFunctionReferences.Select(f => $"\"{f}\""))} }},");
        sb.AppendLine($"                        [\"ReferencedToolkits\"] = new string[] {{ {string.Join(", ", skill.ResolvedToolkitTypes.Select(p => $"\"{p}\""))} }},");

        // Store instructions separately for prompt Middleware to use
        // Middleware will build complete context from metadata (functions + documents + instructions)

        // NEW: StoreSystemPrompt for middleware injection
        if (!string.IsNullOrEmpty(skill.SystemPrompt))
        {
            var escapedSysPrompt = skill.SystemPrompt.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"SystemPrompt\"] = @\"{escapedSysPrompt}\",");
        }

        // Store FunctionResult for introspection
        if (!string.IsNullOrEmpty(skill.FunctionResult))
        {
            var escapedFuncResult = skill.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"FunctionResult\"] = @\"{escapedFuncResult}\",");
        }

        // LEGACY: Keep Instructions for backward compatibility (auto-maps to both contexts)
        // PHASE 5: SkillCapability uses FunctionResult instead of Instructions
        if (!string.IsNullOrEmpty(skill.FunctionResult))
        {
            var escapedInstructions = skill.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"Instructions\"] = @\"{escapedInstructions}\",");
        }

        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the container function for a collapsed toolkit marked with [Toolkit("...")].
    /// This groups all functions/skills in the class under a single container.
    /// </summary>
    public static string GenerateToolkitContainer(ToolkitInfo Toolkit)
    {
        if (!Toolkit.IsCollapsed)
            return string.Empty;

        // Must have at least one capability (function or skill) to collapse
        if (!Toolkit.FunctionCapabilities.Any() && !Toolkit.SkillCapabilities.Any())
            return string.Empty;

        var sb = new StringBuilder();

        // Combine both AI functions and skills
        var allCapabilities = Toolkit.FunctionCapabilities.Select(f => f.FunctionName)
            .Concat(Toolkit.SkillCapabilities.Select(s => s.Name))
            .ToList();
        var capabilitiesList = string.Join(", ", allCapabilities);
        var totalCount = Toolkit.FunctionCapabilities.Count() + Toolkit.SkillCapabilities.Count();

        var description = !string.IsNullOrEmpty(Toolkit.ContainerDescription)
            ? Toolkit.ContainerDescription
            : Toolkit.Description ?? string.Empty;

        // Use shared helper to generate description and return message
        // Use EffectiveName for LLM-visible container name
        var fullDescription = ToolkitContainerHelper.GenerateContainerDescription(description, Toolkit.EffectiveName, allCapabilities);
        var returnMessage = ToolkitContainerHelper.GenerateReturnMessage(description, allCapabilities, Toolkit.FunctionResult);

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Container function for {Toolkit.ClassName} toolkit.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        /// <param name=\"instance\">Toolkit instance</param>");
        // Method signature uses ClassName for type references
        sb.AppendLine($"        private static AIFunction Create{Toolkit.ClassName}Container({Toolkit.ClassName} instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            return HPDAIFunctionFactory.Create(");
        sb.AppendLine("                async (arguments, cancellationToken) =>");
        sb.AppendLine("                {");

        // Handle FunctionResult - either static literal or dynamic expression
        if (!string.IsNullOrEmpty(Toolkit.FunctionResultExpression))
        {
            // Using an interpolated string to combine the base message and the dynamic instructions
            var baseMessage = ToolkitContainerHelper.GenerateReturnMessage(description, allCapabilities, null);
            // Escape special characters for the interpolated string - we need to convert \n\n to \\n\\n in source code
            baseMessage = baseMessage.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"");
            // Add separator between capabilities list and dynamic instructions
            var separator = "\\n\\n";  // This will be two backslash-n sequences in the source code

            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = Toolkit.FunctionResultIsStatic
                ? Toolkit.FunctionResultExpression
                : $"instance.{Toolkit.FunctionResultExpression}";

            sb.AppendLine($"                    var dynamicInstructions = {expressionCall};");
            sb.AppendLine($"                    return $\"{baseMessage}{separator}{{dynamicInstructions}}\";");
        }
        else
        {
            // Using a verbatim string literal for static content
            // In a verbatim string, actual newlines are allowed but we need to represent them as \n
            var escapedReturnMessage = returnMessage
                .Replace("\\", "\\\\")  // Escape backslashes first
                .Replace("\"", "\"\"")  // Escape quotes (double them in verbatim strings)
                .Replace("\n", "\\n"); // Convert actual newlines to backslash-n
            sb.AppendLine($"                    return @\"{escapedReturnMessage}\";");
        }

        sb.AppendLine("                },");
        sb.AppendLine("                new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                {");
        // Use EffectiveName for LLM-visible container function name
        sb.AppendLine($"                    Name = \"{Toolkit.EffectiveName}\",");
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");
        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object?>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine("                        [\"IsToolkitContainer\"] = true,");
        sb.AppendLine($"                        [\"FunctionNames\"] = new string[] {{ {string.Join(", ", allCapabilities.Select(c => $"\"{c}\""))} }},");
        sb.AppendLine($"                        [\"FunctionCount\"] = {totalCount},");

        // Add FunctionResult if present
        if (!string.IsNullOrEmpty(Toolkit.FunctionResult))
        {
            var escapedFuncCtx = Toolkit.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"FunctionResult\"] = @\"{escapedFuncCtx}\",");
        }
        else if (!string.IsNullOrEmpty(Toolkit.FunctionResultExpression))
        {
            // Expression - evaluate at container creation time
            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = Toolkit.FunctionResultIsStatic
                ? Toolkit.FunctionResultExpression
                : $"instance.{Toolkit.FunctionResultExpression}";

            sb.AppendLine($"                        [\"FunctionResult\"] = {expressionCall},");
        }
        else
        {
            sb.AppendLine("                        [\"FunctionResult\"] = null,");
        }

        // AddSystemPrompt if present
        if (!string.IsNullOrEmpty(Toolkit.SystemPrompt))
        {
            var escapedSysCtx = Toolkit.SystemPrompt.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"SystemPrompt\"] = @\"{escapedSysCtx}\"");
        }
        else if (!string.IsNullOrEmpty(Toolkit.SystemPromptExpression))
        {
            // Expression - evaluate at container creation time
            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = Toolkit. SystemPromptIsStatic
                ? Toolkit.SystemPromptExpression
                : $"instance.{Toolkit.SystemPromptExpression}";

            sb.AppendLine($"                        [\"SystemPrompt\"] = {expressionCall}");
        }
        else
        {
            sb.AppendLine("                        [\"SystemPrompt\"] = null");
        }

        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates all skill-related code for a Toolkit
    /// </summary>
    public static string GenerateAllSkillCode(ToolkitInfo Toolkit)
    {
        // Early exit ONLY if no skills AND not collapsed
        // If Toolkit is collapsed, we need to generate the container even without skills
        if (!Toolkit.SkillCapabilities.Any() && !Toolkit.IsCollapsed)
            return string.Empty;

        var sb = new StringBuilder();

        // Generate toolkit container if collapsed (class-level collapsing)
        if (Toolkit.IsCollapsed)
        {
            sb.AppendLine(GenerateToolkitContainer(Toolkit));
            sb.AppendLine();
        }

        // Early exit if no skills to generate (but after generating container if needed)
        if (!Toolkit.SkillCapabilities.Any())
            return sb.ToString();

        // Generate context resolvers for skills (description and conditional)
        foreach (var skill in Toolkit.SkillCapabilities)
        {
            var resolvers = skill.GenerateContextResolvers();
            if (!string.IsNullOrEmpty(resolvers))
            {
                sb.AppendLine(resolvers);
            }
        }

        // Generate skill functions
        // PHASE 5: Now uses SkillCapabilities
        foreach (var skill in Toolkit.SkillCapabilities)
        {
            sb.AppendLine();
            // Skills ARE containers - only one function per skill
            sb.AppendLine(GenerateSkillContainerFunction(skill, Toolkit));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Derives document ID from file path (matches SkillOptions logic)
    /// </summary>
    private static string DeriveDocumentIdFromPath(string filePath)
    {
        // "./docs/debugging-workflow.md" -> "debugging-workflow"
        var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);

        // Normalize to lowercase-kebab-case
        return fileName.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");
    }

    /// <summary>
    /// Updates the Toolkit metadata to include skills
    /// </summary>
    public static string UpdateToolMetadataWithSkills(ToolkitInfo Toolkit, string originalMetadataCode)
    {
        if (!Toolkit.SkillCapabilities.Any())
            return originalMetadataCode;

        // Add skill information to metadata
        var sb = new StringBuilder();
        sb.AppendLine("        private static ToolMetadata? _cachedMetadata;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Gets metadata for the {Toolkit.ClassName} Toolkit (used for Collapsing).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static ToolMetadata GetToolMetadata()");
        sb.AppendLine("        {");
        sb.AppendLine("            return _cachedMetadata ??= new ToolMetadata");
        sb.AppendLine("            {");
        // Use EffectiveName for LLM-visible metadata name
        sb.AppendLine($"                Name = \"{Toolkit.EffectiveName}\",");

        var description = Toolkit.IsCollapsed && !string.IsNullOrEmpty(Toolkit.ContainerDescription)
            ? Toolkit.ContainerDescription
            : Toolkit.Description;
        sb.AppendLine($"                Description = \"{description}\",");

        // Include both functions and skills
        var allFunctionNames = Toolkit.FunctionCapabilities.Select(f => f.FunctionName)
            .Concat(Toolkit.SkillCapabilities.Select(s => s.Name))
            .ToList();
        var functionNamesArray = string.Join(", ", allFunctionNames.Select(n => $"\"{n}\""));

        sb.AppendLine($"                FunctionNames = new string[] {{ {functionNamesArray} }},");
        sb.AppendLine($"                FunctionCount = {allFunctionNames.Count},");
        sb.AppendLine($"                IsCollapsed = {Toolkit.IsCollapsed.ToString().ToLower()}");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }
}

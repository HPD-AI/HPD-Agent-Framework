using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Generates code for skill registration
/// </summary>
internal static class SkillCodeGenerator
{
    /// <summary>
    /// Generates the GetReferencedPlugins() method for auto-registration
    /// PHASE 5: Now uses SkillCapabilities (fully populated with resolved references)
    /// </summary>
    public static string GenerateGetReferencedPluginsMethod(PluginInfo plugin)
    {
        if (!plugin.SkillCapabilities.Any())
            return string.Empty;

        var allReferencedPlugins = plugin.SkillCapabilities
            .SelectMany(s => s.ResolvedPluginTypes)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (!allReferencedPlugins.Any())
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Gets the list of plugins referenced by skills in this class");
        sb.AppendLine("        /// Used by AgentBuilder for auto-registration");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static string[] GetReferencedPlugins()");
        sb.AppendLine("        {");
        sb.AppendLine("            return new string[]");
        sb.AppendLine("            {");

        for (int i = 0; i < allReferencedPlugins.Count; i++)
        {
            var comma = i < allReferencedPlugins.Count - 1 ? "," : "";
            sb.AppendLine($"                \"{allReferencedPlugins[i]}\"{comma}");
        }

        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the GetReferencedFunctions() method for selective function registration
    /// PHASE 5: Now uses SkillCapabilities (fully populated with resolved references)
    /// </summary>
    public static string GenerateGetReferencedFunctionsMethod(PluginInfo plugin)
    {
        if (!plugin.SkillCapabilities.Any())
            return string.Empty;

        // Build dictionary: PluginName -> HashSet<FunctionName>
        var pluginFunctions = new Dictionary<string, HashSet<string>>();

        foreach (var skill in plugin.SkillCapabilities)
        {
            foreach (var funcRef in skill.ResolvedFunctionReferences)
            {
                // "FileSystemPlugin.ReadFile" -> ("FileSystemPlugin", "ReadFile")
                var parts = funcRef.Split('.');
                if (parts.Length == 2)
                {
                    var pluginName = parts[0];
                    var functionName = parts[1];

                    if (!pluginFunctions.ContainsKey(pluginName))
                        pluginFunctions[pluginName] = new HashSet<string>();

                    pluginFunctions[pluginName].Add(functionName);
                }
            }
        }

        if (!pluginFunctions.Any())
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Gets the specific functions referenced by skills (for selective registration)");
        sb.AppendLine("        /// Used by AgentBuilder to register only needed functions from each plugin");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static Dictionary<string, string[]> GetReferencedFunctions()");
        sb.AppendLine("        {");
        sb.AppendLine("            return new Dictionary<string, string[]>");
        sb.AppendLine("            {");

        var entries = pluginFunctions.OrderBy(kvp => kvp.Key).ToList();
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
    /// Generates skill registration code to be added to CreatePlugin() method
    /// Handles both class-level Collapsing (if [Collapse] on class) and individual skill containers
    /// </summary>
    public static string GenerateSkillRegistrations(PluginInfo plugin)
    {
        // Early exit ONLY if no skills AND no collapse attribute
        // If plugin has [Collapse] attribute, we need to register the container even without skills
        if (!plugin.SkillCapabilities.Any() && !plugin.HasCollapseAttribute)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();

        // If the plugin (skill class) has [Collapse], create a class-level container first
        if (plugin.HasCollapseAttribute)
        {
            sb.AppendLine("        // Register skill class Collapse container");
            sb.AppendLine($"        functions.Add(Create{plugin.Name}CollapseContainer(instance));");
            sb.AppendLine();
        }

        // Early exit if no skills to register (but after registering collapse container if needed)
        if (!plugin.SkillCapabilities.Any())
            return sb.ToString();

        sb.AppendLine("        // Register skill containers");

        foreach (var skill in plugin.SkillCapabilities)
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
    public static string GenerateSkillContainerFunction(HPD.Agent.SourceGenerator.Capabilities.SkillCapability skill, PluginInfo plugin)
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

        // Build description like plugin Collapsing: append function list
        var functionNames = string.Join(", ", skill.ResolvedFunctionReferences);

        // Support dynamic descriptions (like Functions)
        var descriptionCode = skill.HasDynamicDescription
            ? $"Resolve{skill.Name}Description(context)"
            : $"\"{skill.Description}\"";

        var fullDescriptionTemplate = $"{{0}}. References {skill.ResolvedFunctionReferences.Count} functions: {functionNames}";

        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Container function for {skill.Name} skill.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        /// <param name=\"instance\">Plugin instance</param>");
        sb.AppendLine($"        /// <param name=\"context\">Execution context for dynamic descriptions</param>");
        sb.AppendLine($"        private static AIFunction Create{skill.MethodName}Skill({plugin.Name} instance, IPluginMetadata? context)");
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
        // PHASE 5: SkillCapability uses ParentPluginName instead of ContainingClass
        sb.AppendLine($"                        [\"ParentSkillContainer\"] = \"{skill.ParentPluginName}\",");
        sb.AppendLine($"                        [\"ReferencedFunctions\"] = new string[] {{ {string.Join(", ", skill.ResolvedFunctionReferences.Select(f => $"\"{f}\""))} }},");
        sb.AppendLine($"                        [\"ReferencedPlugins\"] = new string[] {{ {string.Join(", ", skill.ResolvedPluginTypes.Select(p => $"\"{p}\""))} }},");

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
    /// Generates the Collapse container function for a skill class marked with [Collapse].
    /// This groups all skills in the class under a single collapsible container.
    /// </summary>
    public static string GenerateSkillClassCollapseContainer(PluginInfo plugin)
    {
        if (!plugin.HasCollapseAttribute)
            return string.Empty;

        // Must have at least one capability (function or skill) to collapse
        if (!plugin.FunctionCapabilities.Any() && !plugin.SkillCapabilities.Any())
            return string.Empty;

        var sb = new StringBuilder();

        // Combine both AI functions and skills
        var allCapabilities = plugin.FunctionCapabilities.Select(f => f.FunctionName)
            .Concat(plugin.SkillCapabilities.Select(s => s.Name))
            .ToList();
        var capabilitiesList = string.Join(", ", allCapabilities);
        var totalCount = plugin.FunctionCapabilities.Count() + plugin.SkillCapabilities.Count();

        var description = !string.IsNullOrEmpty(plugin.CollapseDescription)
            ? plugin.CollapseDescription
            : plugin.Description ?? string.Empty;

        // Use shared helper to generate description and return message
        var fullDescription = CollapseContainerHelper.GenerateContainerDescription(description, plugin.Name, allCapabilities);
        var returnMessage = CollapseContainerHelper.GenerateReturnMessage(description, allCapabilities, plugin.FunctionResult);

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Collapse container for {plugin.Name} skill class.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        /// <param name=\"instance\">Plugin instance</param>");
        sb.AppendLine($"        private static AIFunction Create{plugin.Name}CollapseContainer({plugin.Name} instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            return HPDAIFunctionFactory.Create(");
        sb.AppendLine("                async (arguments, cancellationToken) =>");
        sb.AppendLine("                {");

        // Handle FunctionResult - either static literal or dynamic expression
        if (!string.IsNullOrEmpty(plugin.FunctionResultExpression))
        {
            // Using an interpolated string to combine the base message and the dynamic instructions
            var baseMessage = CollapseContainerHelper.GenerateReturnMessage(description, allCapabilities, null);
            // Escape special characters for the interpolated string - we need to convert \n\n to \\n\\n in source code
            baseMessage = baseMessage.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"");
            // Add separator between capabilities list and dynamic instructions
            var separator = "\\n\\n";  // This will be two backslash-n sequences in the source code

            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = plugin.FunctionResultIsStatic
                ? plugin.FunctionResultExpression
                : $"instance.{plugin.FunctionResultExpression}";

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
        sb.AppendLine($"                    Name = \"{plugin.Name}\",");
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");
        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object?>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine("                        [\"IsCollapse\"] = true,");
        sb.AppendLine($"                        [\"FunctionNames\"] = new string[] {{ {string.Join(", ", allCapabilities.Select(c => $"\"{c}\""))} }},");
        sb.AppendLine($"                        [\"FunctionCount\"] = {totalCount},");

        // Add FunctionResult if present
        if (!string.IsNullOrEmpty(plugin.FunctionResult))
        {
            var escapedFuncCtx = plugin.FunctionResult.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"FunctionResult\"] = @\"{escapedFuncCtx}\",");
        }
        else if (!string.IsNullOrEmpty(plugin.FunctionResultExpression))
        {
            // Expression - evaluate at container creation time
            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = plugin.FunctionResultIsStatic
                ? plugin.FunctionResultExpression
                : $"instance.{plugin.FunctionResultExpression}";

            sb.AppendLine($"                        [\"FunctionResult\"] = {expressionCall},");
        }
        else
        {
            sb.AppendLine("                        [\"FunctionResult\"] = null,");
        }

        // AddSystemPrompt if present
        if (!string.IsNullOrEmpty(plugin.SystemPrompt))
        {
            var escapedSysCtx = plugin.SystemPrompt.Replace("\"", "\"\"");
            sb.AppendLine($"                        [\"SystemPrompt\"] = @\"{escapedSysCtx}\"");
        }
        else if (!string.IsNullOrEmpty(plugin.SystemPromptExpression))
        {
            // Expression - evaluate at container creation time
            // Use instance. prefix for instance methods, nothing for static
            var expressionCall = plugin. SystemPromptIsStatic
                ? plugin.SystemPromptExpression
                : $"instance.{plugin.SystemPromptExpression}";

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
    /// Generates skill activation function
    /// </summary>
    /// <summary>
    /// Generates all skill-related code for a plugin
    /// </summary>
    public static string GenerateAllSkillCode(PluginInfo plugin)
    {
        // Early exit ONLY if no skills AND no collapse attribute
        // If plugin has [Collapse] attribute, we need to generate the container even without skills
        if (!plugin.SkillCapabilities.Any() && !plugin.HasCollapseAttribute)
            return string.Empty;

        var sb = new StringBuilder();

        // Generate skill class Collapse container if needed (class-level Collapsing)
        if (plugin.HasCollapseAttribute)
        {
            sb.AppendLine(GenerateSkillClassCollapseContainer(plugin));
            sb.AppendLine();
        }

        // Early exit if no skills to generate (but after generating collapse container if needed)
        if (!plugin.SkillCapabilities.Any())
            return sb.ToString();

        // Generate context resolvers for skills (description and conditional)
        foreach (var skill in plugin.SkillCapabilities)
        {
            var resolvers = skill.GenerateContextResolvers();
            if (!string.IsNullOrEmpty(resolvers))
            {
                sb.AppendLine(resolvers);
            }
        }

        // Generate skill functions
        // PHASE 5: Now uses SkillCapabilities
        foreach (var skill in plugin.SkillCapabilities)
        {
            sb.AppendLine();
            // Skills ARE containers - only one function per skill
            sb.AppendLine(GenerateSkillContainerFunction(skill, plugin));
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
    /// Updates the plugin metadata to include skills
    /// </summary>
    public static string UpdatePluginMetadataWithSkills(PluginInfo plugin, string originalMetadataCode)
    {
        if (!plugin.SkillCapabilities.Any())
            return originalMetadataCode;

        // Add skill information to metadata
        var sb = new StringBuilder();
        sb.AppendLine("        private static PluginMetadata? _cachedMetadata;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Gets metadata for the {plugin.Name} plugin (used for Collapsing).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static PluginMetadata GetPluginMetadata()");
        sb.AppendLine("        {");
        sb.AppendLine("            return _cachedMetadata ??= new PluginMetadata");
        sb.AppendLine("            {");
        sb.AppendLine($"                Name = \"{plugin.Name}\",");

        var description = plugin.HasCollapseAttribute && !string.IsNullOrEmpty(plugin.CollapseDescription)
            ? plugin.CollapseDescription
            : plugin.Description;
        sb.AppendLine($"                Description = \"{description}\",");

        // Include both functions and skills
        var allFunctionNames = plugin.FunctionCapabilities.Select(f => f.FunctionName)
            .Concat(plugin.SkillCapabilities.Select(s => s.Name))
            .ToList();
        var functionNamesArray = string.Join(", ", allFunctionNames.Select(n => $"\"{n}\""));

        sb.AppendLine($"                FunctionNames = new string[] {{ {functionNamesArray} }},");
        sb.AppendLine($"                FunctionCount = {allFunctionNames.Count},");
        sb.AppendLine($"                HasCollapseAttribute = {plugin.HasCollapseAttribute.ToString().ToLower()}");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }
}

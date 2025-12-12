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
    /// </summary>
    public static string GenerateGetReferencedPluginsMethod(PluginInfo plugin)
    {
        if (!plugin.Skills.Any())
            return string.Empty;

        var allReferencedPlugins = plugin.Skills
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
    /// Phase 4.5: Returns a dictionary mapping plugin names to their referenced function names
    /// </summary>
    public static string GenerateGetReferencedFunctionsMethod(PluginInfo plugin)
    {
        if (!plugin.Skills.Any())
            return string.Empty;

        // Build dictionary: PluginName -> HashSet<FunctionName>
        var pluginFunctions = new Dictionary<string, HashSet<string>>();

        foreach (var skill in plugin.Skills)
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
        if (!plugin.Skills.Any())
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
        
        sb.AppendLine("        // Register skill containers");

        foreach (var skill in plugin.Skills)
        {
            // Each skill generates exactly one container function
            sb.AppendLine($"        functions.Add(Create{skill.MethodName}Skill(instance));");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates skill container function.
    /// Skills ARE containers - there's only one function per skill.
    /// </summary>
    public static string GenerateSkillContainerFunction(SkillInfo skill, PluginInfo plugin)
    {
        var sb = new StringBuilder();

        // Simple activation message for function result
        // The prompt Middleware will build the complete context from metadata
        var functionList = string.Join(", ", skill.ResolvedFunctionReferences);
        var returnMessage = $"{skill.Name} skill activated. Available functions: {functionList}";

        // Still include instructions in function result for backward compatibility
        if (!string.IsNullOrEmpty(skill.Instructions))
        {
            returnMessage += $"\n\n{skill.Instructions}";
        }

        var escapedReturnMessage = returnMessage.Replace("\"", "\"\"");

        // Build description like plugin Collapsing: append function list
        var functionNames = string.Join(", ", skill.ResolvedFunctionReferences);
        var fullDescription = $"{skill.Description}. References {skill.ResolvedFunctionReferences.Count} functions: {functionNames}";

        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Container function for {skill.Name} skill.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        private static AIFunction Create{skill.MethodName}Skill({plugin.Name} instance)");
        sb.AppendLine("        {");

        // Generate runtime function body that checks configuration
        var baseMessage = $"{skill.Name} skill activated. Available functions: {functionList}";
        var escapedBaseMessage = baseMessage.Replace("\"", "\"\"");

        // Determine if skill has documents
        var hasDocuments = skill.Options.DocumentReferences.Any() || skill.Options.DocumentUploads.Any();

        if (!string.IsNullOrEmpty(skill.Instructions))
        {
            var escapedInstructions = skill.Instructions.Replace("\"", "\"\"");
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
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");

        // Type-safe SkillDocuments property
        if (skill.Options.DocumentUploads.Any())
        {
            sb.AppendLine("                    SkillDocuments = new SkillDocumentContent[]");
            sb.AppendLine("                    {");
            foreach (var doc in skill.Options.DocumentUploads)
            {
                var escapedDesc = doc.Description.Replace("\"", "\\\"");
                var docId = string.IsNullOrEmpty(doc.DocumentId) ? "null" : $"\"{doc.DocumentId}\"";

                if (doc.SourceType == DocumentSourceType.FilePath)
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
            sb.AppendLine("                    },");
        }

        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine("                        [\"IsSkill\"] = true,");
        sb.AppendLine($"                        [\"ParentSkillContainer\"] = \"{skill.ContainingClass.Identifier.ValueText}\",");
        sb.AppendLine($"                        [\"ReferencedFunctions\"] = new string[] {{ {string.Join(", ", skill.ResolvedFunctionReferences.Select(f => $"\"{f}\""))} }},");
        sb.AppendLine($"                        [\"ReferencedPlugins\"] = new string[] {{ {string.Join(", ", skill.ResolvedPluginTypes.Select(p => $"\"{p}\""))} }},");

        // Store instructions separately for prompt Middleware to use
        // Middleware will build complete context from metadata (functions + documents + instructions)
        if (!string.IsNullOrEmpty(skill.Instructions))
        {
            var escapedInstructions = skill.Instructions.Replace("\"", "\"\"");
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
        if (!plugin.HasCollapseAttribute || !plugin.Skills.Any())
            return string.Empty;

        var sb = new StringBuilder();

        // Combine both AI functions and skills
        var allCapabilities = plugin.Functions.Select(f => f.FunctionName)
            .Concat(plugin.Skills.Select(s => s.Name))
            .ToList();
        var capabilitiesList = string.Join(", ", allCapabilities);
        var totalCount = plugin.Functions.Count + plugin.Skills.Count;

        var description = !string.IsNullOrEmpty(plugin.CollapseDescription)
            ? plugin.CollapseDescription
            : plugin.Description ?? string.Empty;

        // Use shared helper to generate description and return message
        var fullDescription = CollapseContainerHelper.GenerateContainerDescription(description, plugin.Name, allCapabilities);
        var returnMessage = CollapseContainerHelper.GenerateReturnMessage(description, allCapabilities, plugin.PostExpansionInstructions);

        // Escape the return message for C# verbatim string literal (@"...")
        var escapedReturnMessage = returnMessage.Replace("\"", "\"\"");

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Collapse container for {plugin.Name} skill class.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        private static AIFunction Create{plugin.Name}CollapseContainer({plugin.Name} instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            return HPDAIFunctionFactory.Create(");
        sb.AppendLine("                async (arguments, cancellationToken) =>");
        sb.AppendLine("                {");
        sb.AppendLine($"                    return @\"{escapedReturnMessage}\";");
        sb.AppendLine("                },");
        sb.AppendLine("                new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                {");
        sb.AppendLine($"                    Name = \"{plugin.Name}\",");
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");
        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine("                        [\"IsCollapse\"] = true,");
        sb.AppendLine($"                        [\"FunctionNames\"] = new string[] {{ {string.Join(", ", allCapabilities.Select(c => $"\"{c}\""))} }},");
        sb.AppendLine($"                        [\"FunctionCount\"] = {totalCount}");
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
        if (!plugin.Skills.Any())
            return string.Empty;

        var sb = new StringBuilder();

        // Generate skill class Collapse container if needed (class-level Collapsing)
        if (plugin.HasCollapseAttribute)
        {
            sb.AppendLine(GenerateSkillClassCollapseContainer(plugin));
            sb.AppendLine();
        }

        // Generate skill functions
        foreach (var skill in plugin.Skills)
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
        if (!plugin.Skills.Any())
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
        var allFunctionNames = plugin.Functions.Select(f => f.FunctionName)
            .Concat(plugin.Skills.Select(s => s.Name))
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

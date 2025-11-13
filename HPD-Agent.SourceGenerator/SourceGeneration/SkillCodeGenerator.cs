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
    /// Handles both class-level scoping (if [Scope] on class) and individual skill containers
    /// </summary>
    public static string GenerateSkillRegistrations(PluginInfo plugin)
    {
        if (!plugin.Skills.Any())
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        
        // If the plugin (skill class) has [Scope], create a class-level container first
        if (plugin.HasScopeAttribute)
        {
            sb.AppendLine("        // Register skill class scope container");
            sb.AppendLine($"        functions.Add(Create{plugin.Name}ScopeContainer());");
            sb.AppendLine();
        }
        
        sb.AppendLine("        // Register skill containers");

        foreach (var skill in plugin.Skills)
        {
            // Each skill generates exactly one container function
            sb.AppendLine($"        functions.Add(Create{skill.MethodName}Skill());");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates skill container function.
    /// Skills ARE containers - there's only one function per skill.
    /// </summary>
    public static string GenerateSkillContainerFunction(SkillInfo skill)
    {
        var sb = new StringBuilder();

        var functionList = string.Join(", ", skill.ResolvedFunctionReferences);
        var returnMessage = $"{skill.Name} skill activated. Available functions: {functionList}";

        // Add document information if documents are referenced
        if (skill.Options.DocumentReferences.Any() || skill.Options.DocumentUploads.Any())
        {
            returnMessage += "\n\n📚 Available Documents:";

            // Document uploads
            foreach (var upload in skill.Options.DocumentUploads)
            {
                // Auto-derive document ID if not provided (same logic as SkillOptions)
                var docId = string.IsNullOrEmpty(upload.DocumentId)
                    ? DeriveDocumentIdFromPath(upload.FilePath)
                    : upload.DocumentId;
                returnMessage += $"\n- {docId}: {upload.Description}";
            }

            // Document references
            foreach (var reference in skill.Options.DocumentReferences)
            {
                var description = reference.DescriptionOverride ?? "[Use read_skill_document to view]";
                returnMessage += $"\n- {reference.DocumentId}: {description}";
            }

            returnMessage += "\n\nUse read_skill_document(documentId) to retrieve document content.";
        }

        if (!string.IsNullOrEmpty(skill.Instructions))
        {
            returnMessage += $"\n\n{skill.Instructions}";
        }

        var escapedReturnMessage = returnMessage.Replace("\"", "\"\"");

        // Build description like plugin scoping: append function list
        var functionNames = string.Join(", ", skill.ResolvedFunctionReferences);
        var fullDescription = $"{skill.Description}. References {skill.ResolvedFunctionReferences.Count} functions: {functionNames}";

        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Container function for {skill.Name} skill.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        private static AIFunction Create{skill.MethodName}Skill()");
        sb.AppendLine("        {");
        sb.AppendLine("            return HPDAIFunctionFactory.Create(");
        sb.AppendLine("                async (arguments, cancellationToken) =>");
        sb.AppendLine("                {");
        sb.AppendLine($"                    return @\"{escapedReturnMessage}\";");
        sb.AppendLine("                },");
        sb.AppendLine("                new HPDAIFunctionFactoryOptions");
        sb.AppendLine("                {");
        sb.AppendLine($"                    Name = \"{skill.Name}\",");
        sb.AppendLine($"                    Description = \"{fullDescription}\",");
        sb.AppendLine("                    SchemaProvider = () => CreateEmptyContainerSchema(),");
        sb.AppendLine("                    AdditionalProperties = new Dictionary<string, object>");
        sb.AppendLine("                    {");
        sb.AppendLine("                        [\"IsContainer\"] = true,");
        sb.AppendLine("                        [\"IsSkill\"] = true,");
        sb.AppendLine($"                        [\"ParentSkillContainer\"] = \"{skill.ContainingClass.Identifier.ValueText}\",");
        sb.AppendLine($"                        [\"ReferencedFunctions\"] = new string[] {{ {string.Join(", ", skill.ResolvedFunctionReferences.Select(f => $"\"{f}\""))} }},");
        sb.AppendLine($"                        [\"ReferencedPlugins\"] = new string[] {{ {string.Join(", ", skill.ResolvedPluginTypes.Select(p => $"\"{p}\""))} }},");

        // Add Document References and Uploads (metadata only, actual functionality in Phase 5/6)
        if (skill.Options.DocumentReferences.Any())
        {
            var docRefs = string.Join(", ", skill.Options.DocumentReferences.Select(dr =>
            {
                if (dr.DescriptionOverride != null)
                    return $"(object)new Dictionary<string, string?> {{ [\"DocumentId\"] = \"{dr.DocumentId}\", [\"DescriptionOverride\"] = \"{dr.DescriptionOverride}\" }}";
                else
                    return $"(object)new Dictionary<string, string?> {{ [\"DocumentId\"] = \"{dr.DocumentId}\", [\"DescriptionOverride\"] = null }}";
            }));
            sb.AppendLine($"                        [\"DocumentReferences\"] = new object[] {{ {docRefs} }},");
        }

        if (skill.Options.DocumentUploads.Any())
        {
            var docUploads = string.Join(", ", skill.Options.DocumentUploads.Select(du =>
                $"(object)new Dictionary<string, string> {{ [\"FilePath\"] = \"{du.FilePath}\", [\"DocumentId\"] = \"{du.DocumentId}\", [\"Description\"] = \"{du.Description}\" }}"));
            sb.AppendLine($"                        [\"DocumentUploads\"] = new object[] {{ {docUploads} }},");
        }

        sb.AppendLine($"                        [\"AutoExpand\"] = {skill.Options.AutoExpand.ToString().ToLower()}");
        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("        }");

        return sb.ToString();
    }

    /// <summary>
    /// Generates the scope container function for a skill class marked with [Scope].
    /// This groups all skills in the class under a single collapsible container.
    /// </summary>
    public static string GenerateSkillClassScopeContainer(PluginInfo plugin)
    {
        if (!plugin.HasScopeAttribute || !plugin.Skills.Any())
            return string.Empty;

        var sb = new StringBuilder();

        var skillList = string.Join(", ", plugin.Skills.Select(s => s.Name));
        var description = !string.IsNullOrEmpty(plugin.ScopeDescription)
            ? plugin.ScopeDescription
            : plugin.Description;
        var fullDescription = $"{description}. Contains {plugin.Skills.Count} skills: {skillList}";

        // Build the return message with optional post-expansion instructions
        var returnMessage = $"{plugin.Name} expanded. Available skills: {skillList}";
        if (!string.IsNullOrEmpty(plugin.PostExpansionInstructions))
        {
            returnMessage += $"\n\n{plugin.PostExpansionInstructions}";
        }

        // Escape the return message for C# verbatim string literal (@"...")
        var escapedReturnMessage = returnMessage.Replace("\"", "\"\"");

        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Scope container for {plugin.Name} skill class.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        private static AIFunction Create{plugin.Name}ScopeContainer()");
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
        sb.AppendLine("                        [\"IsScope\"] = true,");
        sb.AppendLine($"                        [\"SkillNames\"] = new string[] {{ {string.Join(", ", plugin.Skills.Select(s => $"\"{s.Name}\""))} }},");
        sb.AppendLine($"                        [\"SkillCount\"] = {plugin.Skills.Count}");
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

        // Generate skill class scope container if needed (class-level scoping)
        if (plugin.HasScopeAttribute)
        {
            sb.AppendLine(GenerateSkillClassScopeContainer(plugin));
            sb.AppendLine();
        }

        // Generate skill functions
        foreach (var skill in plugin.Skills)
        {
            sb.AppendLine();
            // Skills ARE containers - only one function per skill
            sb.AppendLine(GenerateSkillContainerFunction(skill));
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
        sb.AppendLine($"        /// Gets metadata for the {plugin.Name} plugin (used for scoping).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static PluginMetadata GetPluginMetadata()");
        sb.AppendLine("        {");
        sb.AppendLine("            return _cachedMetadata ??= new PluginMetadata");
        sb.AppendLine("            {");
        sb.AppendLine($"                Name = \"{plugin.Name}\",");

        var description = plugin.HasScopeAttribute && !string.IsNullOrEmpty(plugin.ScopeDescription)
            ? plugin.ScopeDescription
            : plugin.Description;
        sb.AppendLine($"                Description = \"{description}\",");

        // Include both functions and skills
        var allFunctionNames = plugin.Functions.Select(f => f.FunctionName)
            .Concat(plugin.Skills.Select(s => s.Name))
            .ToList();
        var functionNamesArray = string.Join(", ", allFunctionNames.Select(n => $"\"{n}\""));

        sb.AppendLine($"                FunctionNames = new string[] {{ {functionNamesArray} }},");
        sb.AppendLine($"                FunctionCount = {allFunctionNames.Count},");
        sb.AppendLine($"                HasScopeAttribute = {plugin.HasScopeAttribute.ToString().ToLower()}");
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        return sb.ToString();
    }
}

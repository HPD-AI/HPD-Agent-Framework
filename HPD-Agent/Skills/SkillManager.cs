using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HPD_Agent.Skills;

/// <summary>
/// Manages skill registrations and builds skill containers.
/// Parallel to PluginManager but for skill-based function grouping.
/// </summary>
public class SkillManager
{
    private readonly List<SkillDefinition> _skills = new();
    private readonly ILogger<SkillManager>? _logger;
    private bool _isBuilt = false;

    public SkillManager(ILogger<SkillManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a skill definition.
    /// </summary>
    public SkillManager RegisterSkill(SkillDefinition skill)
    {
        if (skill == null)
            throw new ArgumentNullException(nameof(skill));

        if (_isBuilt)
        {
            throw new InvalidOperationException(
                "Cannot register skills after Build() has been called. " +
                "Register all skills before building.");
        }

        // Check for duplicate skill names
        if (_skills.Any(s => s.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A skill with name '{skill.Name}' is already registered");
        }

        _skills.Add(skill);
        _logger?.LogDebug("Registered skill '{SkillName}' with {Count} function references",
            skill.Name, skill.FunctionReferences?.Length ?? 0);

        return this;
    }

    /// <summary>
    /// Registers multiple skill definitions.
    /// </summary>
    public SkillManager RegisterSkills(IEnumerable<SkillDefinition> skills)
    {
        foreach (var skill in skills)
        {
            RegisterSkill(skill);
        }
        return this;
    }

    /// <summary>
    /// Builds all registered skills, validating function references and loading instruction documents.
    /// Must be called after all skills are registered and before using GetSkillContainers().
    /// </summary>
    /// <param name="allFunctions">All available functions (from plugins and other sources) for validation</param>
    /// <exception cref="InvalidOperationException">If any skill validation fails</exception>
    public SkillManager Build(IEnumerable<AIFunction> allFunctions)
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("Build() has already been called");
        }

        // Build function lookup dictionary for validation
        var functionLookup = BuildFunctionLookup(allFunctions);

        _logger?.LogInformation("Building {Count} skills with {FunctionCount} available functions",
            _skills.Count, functionLookup.Count);

        // Validate and build each skill
        var errors = new List<string>();
        foreach (var skill in _skills)
        {
            try
            {
                skill.Build(functionLookup);
                _logger?.LogDebug("Successfully built skill '{SkillName}'", skill.Name);
            }
            catch (Exception ex)
            {
                var errorMessage = $"Failed to build skill '{skill.Name}': {ex.Message}";
                errors.Add(errorMessage);
                _logger?.LogError(ex, "Failed to build skill '{SkillName}'", skill.Name);
            }
        }

        // Fail-fast if any skills failed validation
        if (errors.Any())
        {
            throw new InvalidOperationException(
                $"Failed to build {errors.Count} skill(s):\n" + string.Join("\n", errors));
        }

        _isBuilt = true;
        _logger?.LogInformation("Successfully built all {Count} skills", _skills.Count);

        return this;
    }

    /// <summary>
    /// Gets all skill container functions (for inclusion in ChatOptions.Tools).
    /// Must call Build() first.
    /// </summary>
    /// <returns>List of skill container AIFunctions</returns>
    public List<AIFunction> GetSkillContainers()
    {
        if (!_isBuilt)
        {
            throw new InvalidOperationException(
                "Must call Build() before getting skill containers");
        }

        return _skills
            .Select(skill => skill.CreateContainer())
            .ToList();
    }

    /// <summary>
    /// Creates a SkillScopingManager for managing skill expansion and filtering.
    /// Must call Build() first.
    /// </summary>
    /// <param name="allFunctions">All available functions for the scoping manager</param>
    /// <returns>Configured SkillScopingManager</returns>
    public SkillScopingManager CreateScopingManager(IEnumerable<AIFunction> allFunctions)
    {
        if (!_isBuilt)
        {
            throw new InvalidOperationException(
                "Must call Build() before creating scoping manager");
        }

        return new SkillScopingManager(_skills, allFunctions, _logger as ILogger<SkillScopingManager>);
    }

    /// <summary>
    /// Gets all registered skill definitions (read-only).
    /// </summary>
    public IReadOnlyList<SkillDefinition> GetSkills() => _skills.AsReadOnly();

    /// <summary>
    /// Gets a skill by name.
    /// </summary>
    public SkillDefinition? GetSkillByName(string skillName)
    {
        return _skills.FirstOrDefault(s =>
            s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Clears all registered skills. Cannot be called after Build().
    /// </summary>
    public void Clear()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException(
                "Cannot clear skills after Build() has been called");
        }

        _skills.Clear();
        _logger?.LogDebug("Cleared all skill registrations");
    }

    /// <summary>
    /// Builds a function lookup dictionary by reference identifier.
    /// </summary>
    private Dictionary<string, AIFunction> BuildFunctionLookup(IEnumerable<AIFunction> functions)
    {
        var lookup = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);

        foreach (var function in functions)
        {
            if (string.IsNullOrEmpty(function.Name))
                continue;

            // Index by function name alone
            lookup[function.Name] = function;

            // Index by "PluginName.FunctionName" if parent plugin exists
            var parentPlugin = function.AdditionalProperties
                ?.TryGetValue("ParentPlugin", out var value) == true
                && value is string plugin
                ? plugin
                : null;

            if (!string.IsNullOrEmpty(parentPlugin))
            {
                var qualifiedName = $"{parentPlugin}.{function.Name}";
                lookup[qualifiedName] = function;
            }
        }

        return lookup;
    }
}

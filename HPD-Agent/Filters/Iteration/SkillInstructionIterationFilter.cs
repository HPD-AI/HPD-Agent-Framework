using System.Text;
using Microsoft.Extensions.AI;

namespace HPD.Agent.Internal.Filters;

/// <summary>
/// Injects active skill instructions before each LLM call.
/// This ensures skills activated during iteration N are visible to iteration N+1.
/// </summary>
/// <remarks>
/// <para><b>Problem Solved:</b></para>
/// <para>
/// Skills are activated during agentic execution (iteration 0), but their instructions
/// need to be available for subsequent LLM calls (iteration 1+). Prompt filters only run
/// once at message turn start, so they cannot inject skill instructions dynamically.
/// </para>
///
/// <para><b>Solution:</b></para>
/// <para>
/// This iteration filter runs before EVERY LLM call and checks the agent's state for
/// active skills. If skills are active, it injects their instructions into the system prompt.
/// </para>
///
/// <para><b>Flow Example:</b></para>
/// <code>
/// Turn 1: "Activate trading skill and buy AAPL"
///   Iteration 0:
///     - Filter: No active skills yet
///     - LLM: Returns activate_skill("trading")
///     - Execute: Skill activated → State.ActiveSkillInstructions += {"trading": "..."}
///
///   Iteration 1: 🔥 KEY MOMENT
///     - Filter: Detects State.ActiveSkillInstructions["trading"]
///     - Filter: Injects trading instructions into ChatOptions.Instructions
///     - LLM: NOW SEES trading instructions, knows how to buy stocks!
///     - LLM: Returns buy_stock(symbol="AAPL", quantity=10)
///
///   Iteration 2:
///     - Filter: Still injects trading instructions
///     - LLM: Returns final response
///     - Filter: Detects IsFinalIteration = true, signals cleanup
///     - Agent: At end of message turn, clears ActiveSkillInstructions
///
/// Turn 2: "What's the weather?"
///   - Skills are cleared → Trading instructions NOT injected
/// </code>
/// </remarks>
internal class SkillInstructionIterationFilter : IIterationFilter
{
    /// <summary>
    /// Called BEFORE the LLM call begins.
    /// Injects active skill instructions into the system prompt with rich formatting.
    /// </summary>
    public Task BeforeIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        var activeSkills = context.State.ActiveSkillInstructions;

        if (activeSkills.Any() && context.Options != null)
        {
            // Build rich skill protocols section
            var protocolsSection = BuildSkillProtocolsSection(
                activeSkills,
                context.Options);

            // Inject with proper formatting
            var currentInstructions = context.Options.Instructions ?? string.Empty;

            // Avoid duplicate injection
            if (!currentInstructions.Contains("🔧 ACTIVE SKILL PROTOCOLS"))
            {
                context.Options.Instructions = string.IsNullOrEmpty(currentInstructions)
                    ? protocolsSection
                    : $"{protocolsSection}\n\n{currentInstructions}";
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called AFTER the LLM call completes.
    /// Detects final iteration and signals cleanup.
    /// </summary>
    public Task AfterIterationAsync(
        IterationFilterContext context,
        CancellationToken cancellationToken)
    {
        var activeSkills = context.State.ActiveSkillInstructions;

        // If this is the final iteration (no tool calls), mark skills for cleanup
        // The agent loop will clear ActiveSkillInstructions between message turns
        if (context.IsFinalIteration && activeSkills.Any())
        {
            context.Properties["ShouldClearActiveSkills"] = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a rich, formatted skill protocols section with metadata.
    /// Matches the formatting from SkillInstructionPromptFilter.
    /// </summary>
    private static string BuildSkillProtocolsSection(
        System.Collections.Immutable.ImmutableDictionary<string, string> activeSkills,
        ChatOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine("🔧 ACTIVE SKILL PROTOCOLS (Execute ALL steps completely)");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        // Order alphabetically for consistency
        foreach (var (skillName, instructions) in activeSkills.OrderBy(kvp => kvp.Key))
        {
            sb.AppendLine($"## {skillName}:");
            sb.AppendLine();

            // Find the skill's AIFunction to extract metadata
            var skillFunction = options.Tools?.OfType<AIFunction>()
                .FirstOrDefault(f => f.Name == skillName);

            if (skillFunction != null)
            {
                // Add function list from metadata
                if (skillFunction.AdditionalProperties?.TryGetValue("ReferencedFunctions", out var functionsObj) == true
                    && functionsObj is string[] functions && functions.Length > 0)
                {
                    sb.AppendLine($"**Available functions:** {string.Join(", ", functions)}");
                    sb.AppendLine();
                }

                // Add document information from metadata
                var hasDocuments = BuildDocumentSection(skillFunction, sb);
                if (hasDocuments)
                {
                    sb.AppendLine();
                }
            }

            // Add the skill instructions
            sb.AppendLine(instructions);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the document section for a skill, showing available documents.
    /// </summary>
    private static bool BuildDocumentSection(AIFunction skillFunction, StringBuilder sb)
    {
        var hasAnyDocuments = false;

        // Check for DocumentUploads
        if (skillFunction.AdditionalProperties?.TryGetValue("DocumentUploads", out var uploadsObj) == true
            && uploadsObj is Array uploadsArray && uploadsArray.Length > 0)
        {
            if (!hasAnyDocuments)
            {
                sb.AppendLine("📚 **Available Documents:**");
                hasAnyDocuments = true;
            }

            foreach (var upload in uploadsArray)
            {
                if (upload is Dictionary<string, string> uploadDict)
                {
                    var docId = uploadDict.GetValueOrDefault("DocumentId", "");
                    var description = uploadDict.GetValueOrDefault("Description", "");
                    sb.AppendLine($"- {docId}: {description}");
                }
            }
        }

        // Check for DocumentReferences
        if (skillFunction.AdditionalProperties?.TryGetValue("DocumentReferences", out var refsObj) == true
            && refsObj is Array refsArray && refsArray.Length > 0)
        {
            if (!hasAnyDocuments)
            {
                sb.AppendLine("📚 **Available Documents:**");
                hasAnyDocuments = true;
            }

            foreach (var reference in refsArray)
            {
                if (reference is Dictionary<string, string?> refDict)
                {
                    var docId = refDict.GetValueOrDefault("DocumentId", "");
                    var description = refDict.GetValueOrDefault("DescriptionOverride")
                        ?? "[Use read_skill_document to view]";
                    sb.AppendLine($"- {docId}: {description}");
                }
            }
        }

        if (hasAnyDocuments)
        {
            sb.AppendLine();
            sb.AppendLine("Use `read_skill_document(documentId)` to retrieve document content.");
        }

        return hasAnyDocuments;
    }
}

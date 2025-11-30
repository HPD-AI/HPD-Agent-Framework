using System.Collections.Immutable;
using System.Text;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;

namespace HPD.Agent;

/// <summary>
/// Injects active skill instructions before each LLM call.
/// This ensures skills activated during iteration N are visible to iteration N+1.
/// </summary>
/// <remarks>
/// <para><b>Problem Solved:</b></para>
/// <para>
/// Skills are activated during agentic execution (iteration 0), but their instructions
/// need to be available for subsequent LLM calls (iteration 1+). Prompt middlewares only run
/// once at message turn start, so they cannot inject skill instructions dynamically.
/// </para>
///
/// <para><b>Solution:</b></para>
/// <para>
/// This iteration middleware runs before EVERY LLM call and checks the agent's state for
/// active skills. If skills are active, it injects their instructions into the system prompt.
/// </para>
///
/// <para><b>Flow Example:</b></para>
/// <code>
/// Turn 1: "Activate trading skill and buy AAPL"
///   Iteration 0:
///     - Middleware: No active skills yet
///     - LLM: Returns activate_skill("trading")
///     - Execute: Skill activated → State.ActiveSkillInstructions += {"trading": "..."}
///
///   Iteration 1: 🔥 KEY MOMENT
///     - Middleware: Detects State.ActiveSkillInstructions["trading"]
///     - Middleware: Injects trading instructions into ChatOptions.Instructions
///     - LLM: NOW SEES trading instructions, knows how to buy stocks!
///     - LLM: Returns buy_stock(symbol="AAPL", quantity=10)
///
///   Iteration 2:
///     - Middleware: Still injects trading instructions
///     - LLM: Returns final response
///     - Middleware: Detects IsFinalIteration = true, signals cleanup
///     - Agent: At end of message turn, clears ActiveSkillInstructions
///
/// Turn 2: "What's the weather?"
///   - Skills are cleared → Trading instructions NOT injected
/// </code>
/// </remarks>
public class SkillInstructionMiddleware : IAgentMiddleware
{
    /// <summary>
    /// Called BEFORE the LLM call begins.
    /// Injects active skill instructions into the system prompt with rich formatting.
    /// </summary>
    public Task BeforeIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        var scopingState = context.State.MiddlewareState.Scoping ?? new ScopingStateData();
        var activeSkills = scopingState.ActiveSkillInstructions;

        if (activeSkills.Any() && context.Options != null)
        {
            // Build rich skill protocols section
            var protocolsSection = BuildSkillProtocolsSection(
                activeSkills,
                context.Options);

            // Inject with proper formatting - append AFTER original instructions
            var currentInstructions = context.Options.Instructions ?? string.Empty;

            // Avoid duplicate injection
            if (!currentInstructions.Contains("🔧 ACTIVE SKILL PROTOCOLS"))
            {
                context.Options.Instructions = string.IsNullOrEmpty(currentInstructions)
                    ? protocolsSection
                    : $"{currentInstructions}\n\n{protocolsSection}";
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called AFTER all tools complete for this iteration.
    /// Clears active skill instructions on final iteration to prevent leakage across message turns.
    /// </summary>
    public Task AfterIterationAsync(
        AgentMiddlewareContext context,
        CancellationToken cancellationToken)
    {
        var scopingState = context.State.MiddlewareState.Scoping ?? new ScopingStateData();
        var activeSkills = scopingState.ActiveSkillInstructions;

        // If this is the final iteration (no tool calls), clear active skill instructions
        // This ensures skills don't leak across message turns
        if (context.IsFinalIteration && activeSkills.Any())
        {
            var updatedScoping = scopingState with
            {
                ActiveSkillInstructions = ImmutableDictionary<string, string>.Empty
            };
            context.UpdateState(s => s with
            {
                MiddlewareState = s.MiddlewareState.WithScoping(updatedScoping)
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a rich, formatted skill protocols section with metadata.
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
    /// Internal for testing purposes.
    /// </summary>
    internal static string BuildDocumentSectionForTesting(AIFunction skillFunction)
    {
        var sb = new StringBuilder();
        BuildDocumentSection(skillFunction, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the document section for a skill, showing available documents.
    /// </summary>
    private static bool BuildDocumentSection(AIFunction skillFunction, StringBuilder sb)
    {
        // Use type-safe SkillDocuments property
        if (skillFunction is HPDAIFunctionFactory.HPDAIFunction hpdFunction &&
            hpdFunction.SkillDocuments?.Any() == true)
        {
            sb.AppendLine("📚 **Available Documents:**");
            foreach (var doc in hpdFunction.SkillDocuments)
            {
                sb.AppendLine($"- {doc.DocumentId}: {doc.Description} ({doc.SourceType})");
            }
            sb.AppendLine();
            sb.AppendLine("Use `read_skill_document(documentId)` to retrieve document content.");
            return true;
        }

        return false;
    }
}

using Microsoft.Extensions.Logging;
using HPD.Agent.Internal.Filters;
using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HPD_Agent.Memory.Agent.PlanMode;

/// <summary>
/// Prompt filter that injects the current plan into system messages.
/// Only injects when a plan exists.
/// </summary>
internal class AgentPlanFilter : IPromptFilter
{
    private readonly AgentPlanStore _store;
    private readonly ILogger<AgentPlanFilter>? _logger;

    public AgentPlanFilter(AgentPlanStore store, ILogger<AgentPlanFilter>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        // Get conversation ID from options (injected by Conversation class)
        var conversationId = context.Options?.AdditionalProperties?["ConversationId"] as string;
        if (string.IsNullOrEmpty(conversationId))
        {
            // No conversation ID available, skip plan injection
            return await next(context);
        }

        // Only inject plan if one exists for this conversation
        if (!await _store.HasPlanAsync(conversationId))
        {
            return await next(context);
        }

        var planPrompt = await _store.BuildPlanPromptAsync(conversationId);
        if (string.IsNullOrEmpty(planPrompt))
        {
            return await next(context);
        }

        // Inject plan as a system message
        var messagesWithPlan = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, planPrompt)
        };
        messagesWithPlan.AddRange(context.Messages);

        context.Messages = messagesWithPlan;

        _logger?.LogDebug("Injected plan into prompt for agent {AgentName}, conversation {ConversationId}", context.AgentName, conversationId);

        return await next(context);
    }
}

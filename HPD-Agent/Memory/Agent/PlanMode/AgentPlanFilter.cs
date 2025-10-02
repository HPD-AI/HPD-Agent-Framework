using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Prompt filter that injects the current plan into system messages.
/// Only injects when a plan exists.
/// </summary>
public class AgentPlanFilter : IPromptFilter
{
    private readonly AgentPlanManager _manager;
    private readonly ILogger<AgentPlanFilter>? _logger;

    public AgentPlanFilter(AgentPlanManager manager, ILogger<AgentPlanFilter>? logger = null)
    {
        _manager = manager;
        _logger = logger;
    }

    public Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        // Get conversation ID from options (injected by Conversation class)
        var conversationId = context.Options?.AdditionalProperties?["ConversationId"] as string;
        if (string.IsNullOrEmpty(conversationId))
        {
            // No conversation ID available, skip plan injection
            return next(context);
        }

        // Only inject plan if one exists for this conversation
        if (!_manager.HasPlan(conversationId))
        {
            return next(context);
        }

        var planPrompt = _manager.BuildPlanPrompt(conversationId);
        if (string.IsNullOrEmpty(planPrompt))
        {
            return next(context);
        }

        // Inject plan as a system message
        var messagesWithPlan = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, planPrompt)
        };
        messagesWithPlan.AddRange(context.Messages);

        context.Messages = messagesWithPlan;

        _logger?.LogDebug("Injected plan into prompt for agent {AgentName}, conversation {ConversationId}", context.AgentName, conversationId);

        return next(context);
    }
}

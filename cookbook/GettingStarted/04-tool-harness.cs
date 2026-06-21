#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:package HPD-Agent.MultiAgent@0.5.5
#:property TargetFramework=net10.0

// This sample registers a ToolHarness: a class that groups related agent capabilities.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;
using HPD.MultiAgent;

var agent = await new AgentBuilder()
                    .WithInstructions("You are a support assistant. Use direct order tools for simple order and return questions.")
                    .WithOpenAI("gpt-5-mini")
                    .WithHarnessCollapsing()
                    .WithToolHarness<SupportToolHarness>()
                    .BuildAsync();

var result = await agent.RunAsync("Look up order HPD-1050 and tell me the return policy.");

Console.WriteLine(result.Text);

// Collapse hides the harness methods behind one container tool until the model needs them.
[Collapse(
    "Support tools for looking up orders and return policies.",
    FunctionResult = "Support tools are available. Use the narrowest tool that answers the user.",
    SystemPrompt = "When support tools return data, answer from the tool result instead of guessing."
)]
public class SupportToolHarness
{
    // AIFunction exposes one method as one callable tool.
    [AIFunction(Name = "lookup_order")]
    [AIDescription("Looks up a customer order by order number.")]
    public string LookupOrder([AIDescription("The order number to look up.")] string orderNumber)
    {
        return $"Order {orderNumber} shipped yesterday and is expected to arrive tomorrow.";
    }

    [RequiresPermission]
    [AIFunction(Name = "cancel_order")]
    [AIDescription("Cancels an order. This requires permission before execution.")]
    public string CancelOrder([AIDescription("The order number to cancel.")] string orderNumber)
    {
        return $"Order {orderNumber} was canceled.";
    }

    // Skill groups several tools behind a named workflow the model can activate.
    [Skill]
    [AIDescription("Activates the order support tools as a guided workflow.")]
    public static Skill OrderSupportSkill() => SkillFactory.Create(
        "order_support",
        "Order support workflow",
        "Use lookup_order first, then get_return_policy when the user asks about returns.",
        "Answer from support tool results. Ask one clarifying question if the order number is missing.",
        "SupportToolHarness.lookup_order",
        "SupportToolHarness.get_return_policy");

    // SubAgent lets the harness delegate a task to another agent.
    [SubAgent]
    public static SubAgent EscalationAgent() => SubAgent.FromConfig(
        "support_escalation",
        "Escalates complex support cases to a specialist agent.",
        new AgentConfig
        {
            Name = "Support Escalation",
            SystemInstructions = "You are a specialist support agent. Resolve complex order and return cases clearly."
        },
        SubAgentExecutionPolicies.NewSession());

    // MultiAgent lets the harness expose a whole workflow as one capability.
    [MultiAgent("Runs a two-agent support workflow.", Name = "support_workflow", StreamEvents = false)]
    public static async Task<AgentWorkflowInstance> SupportWorkflow()
    {
        return await AgentWorkflow.Create()
            .WithName("support-workflow")
            .AddAgent("researcher", agent =>
            {
                agent.WithInstructions("Find the key support facts. Keep it concise.")
                    .WithOpenAI("gpt-5-mini");
            })
            .AddAgent("writer", agent =>
            {
                agent.WithInstructions("Turn the support facts into a friendly customer reply.")
                    .WithOpenAI("gpt-5-mini");
            })
            .From("researcher").To("writer")
            .BuildAsync();
    }
}
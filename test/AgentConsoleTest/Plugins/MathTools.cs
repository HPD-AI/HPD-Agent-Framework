using HPD.Agent;
using HPD.MultiAgent;
using System;
using System.ComponentModel;
using Microsoft.Extensions.AI;

/// <summary>
/// Simple math Toolkit for testing Toolkit registration and invocation.
/// </summary>
public class MathToolMetadataContext : IToolMetadata
{
    private readonly Dictionary<string, object> _properties = new();

    public MathToolMetadataContext(long maxValue = 1000, bool allowNegative = true)
    {
        _properties["maxValue"] = maxValue;
        _properties["allowNegative"] = allowNegative;
        MaxValue = maxValue;
        AllowNegative = allowNegative;
    }

    //  V2: Strongly-typed properties for compile-time validation
    public long MaxValue { get; }
    public bool AllowNegative { get; }

    public T? GetProperty<T>(string propertyName, T? defaultValue = default)
    {
        if (_properties.TryGetValue(propertyName, out var value))
        {
            if (value is T typedValue)
                return typedValue;
            if (typeof(T) == typeof(string))
                return (T)(object)value.ToString()!;
        }
        return defaultValue;
    }

    public bool HasProperty(string propertyName) => _properties.ContainsKey(propertyName);
    public IEnumerable<string> GetPropertyNames() => _properties.Keys;
}


public class MathToolkit
{
    [AIFunction<MathToolMetadataContext>]
    [AIDescription("Adds two numbers and returns the sum.")]
    [RequiresPermission]
    public decimal Add(
        [AIDescription("First addend.")] decimal a,
        [AIDescription("Second addend.")] decimal b)
        => a + b;

    [AIFunction<MathToolMetadataContext>]
    [AIDescription("Multiplies two numbers and returns the product.")]
    public long Multiply(
        [AIDescription("First factor.")] long a,
        [AIDescription("Second factor.")] long b)
        => a * b;

    [AIFunction<MathToolMetadataContext>]
    [ConditionalFunction("AllowNegative == false")]
    [AIDescription("Returns the absolute value. Only available if negatives are not allowed.")]
    public long Abs(
        [AIDescription("Input value.")] long value)
        => Math.Abs(value);

    [AIFunction<MathToolMetadataContext>]
    [ConditionalFunction("MaxValue > 1000")]
    [AIDescription("Squares a number. Only available if maxValue > 1000.")]
    public long Square(
        [AIDescription("Input value.")] long value)
        => value * value;

    [AIFunction<MathToolMetadataContext>]
    [ConditionalFunction("AllowNegative == true")]
    [AIDescription("Subtracts b from a. Only available if negatives are allowed.")]
    public long Subtract(
        [AIDescription("Minuend.")] long a,
        [AIDescription("Subtrahend.")] long b)
        => a - b;

    [AIFunction<MathToolMetadataContext>]
    [ConditionalFunction("MaxValue < 500")]
    [AIDescription("Returns the minimum of two numbers. Only available if maxValue < 500.")]
    public long Min(
        [AIDescription("First value.")] long a,
        [AIDescription("Second value.")] long b)
        => Math.Min(a, b);

    [Skill]
    public Skill SolveQuadraticSkill(SkillOptions? options = null)
    {
        return SkillFactory.Create(
            name: "SolveQuadratic",
            description: "Gives instruction on how to solve quadratic equations (ax² + bx + c = 0)",
            functionResult: "Quadratic solver activated",
            systemPrompt: @"
                    Step 1: Calculate discriminant (b² - 4ac)
                    Step 2: Calculate square root using sqrt function
                    Step 3: Calculate two solutions using Add/Subtract",
            options: options,
            "MathTools.Multiply",
            "MathTools.Add",
            "MathTools.Subtract"
        );
    }

    // ========== Multi-Agent Workflow ==========

    /// <summary>
    /// Multi-agent consensus workflow for solving math problems.
    /// Routes to 3 solver agents in parallel, then a verifier determines consensus.
    /// </summary>
    [MultiAgent("Solve complex math problems using multiple AI solvers and consensus verification. " +
                "Uses 3 parallel solvers (Claude, GPT, Gemini) followed by a verifier.")]
    public async Task<AgentWorkflowInstance> MathConsensusWorkflow()
    {
        // Create solver agents with different models
        var solver1 = await CreateSolverAgent("Solver-Claude", "anthropic/claude-sonnet-4.5");
        var solver2 = await CreateSolverAgent("Solver-GPT", "openai/gpt-4o");
        var solver3 = await CreateSolverAgent("Solver-Gemini", "google/gemini-2.0-flash-exp");
        var verifier = await CreateVerifierAgent();

        // Build workflow: 3 solvers in parallel → verifier
        return await AgentWorkflow.Create()
            .WithName("MathConsensus")

            // Add all agents
            .AddAgent("solver1", solver1)
            .AddAgent("solver2", solver2)
            .AddAgent("solver3", solver3)
            .AddAgent("verifier", verifier)

            // Parallel entry: START → all 3 solvers
            .From("START").To("solver1")
            .From("START").To("solver2")
            .From("START").To("solver3")

            // All solvers converge to verifier
            .From("solver1", "solver2", "solver3").To("verifier")

            // Verifier → END
            .From("verifier").To("END")

            .BuildAsync();
    }

    private static readonly string SolverPrompt = @"You are a quantitative analyst and mathematician.
Solve the given problem step-by-step, showing your work clearly.
Provide a precise, numerical final answer when applicable.";

    private static async Task<Agent> CreateSolverAgent(string name, string model)
    {
        var config = new AgentConfig
        {
            Name = name,
            MaxAgenticIterations = 10,
            SystemInstructions = SolverPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", model)
            .Build();
    }

    private static readonly string VerifierPrompt = @"You are a mathematical verifier.
Compare the solver answers provided and determine the correct consensus answer.
If solvers disagree, analyze each approach and determine which is correct.
Return only the final verified answer.";

    private static async Task<Agent> CreateVerifierAgent()
    {
        var config = new AgentConfig
        {
            Name = "Verifier",
            MaxAgenticIterations = 5,
            SystemInstructions = VerifierPrompt,
        };

        return await new AgentBuilder(config)
            .WithProvider("openrouter", "anthropic/claude-3.5-haiku")
            .Build();
    }
}

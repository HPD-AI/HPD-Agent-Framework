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

[Collapse("A toolkit providing basic mathematical operations")]
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

    // ========== Sub-Agent ==========

    /// <summary>
    /// Sub-agent specialized in solving differentiation problems.
    /// </summary>
    [SubAgent]
    public SubAgent DifferentiationSolver()
    {
        return SubAgentFactory.Create(
            name: "DifferentiationSolver",
            description: "Specialized agent for solving calculus differentiation problems. " +
                        "Finds derivatives of functions using rules like power rule, chain rule, " +
                        "product rule, quotient rule, and implicit differentiation.",
            agentConfig: new AgentConfig
            {
                Name = "DifferentiationSolver",
                MaxAgenticIterations = 5,
                SystemInstructions = @"You are an expert calculus tutor specializing in differentiation.

When given a function to differentiate:
1. Identify the type of function (polynomial, trigonometric, exponential, logarithmic, composite, etc.)
2. State which differentiation rule(s) apply (power rule, chain rule, product rule, quotient rule, etc.)
3. Show each step of the differentiation clearly
4. Simplify the final answer
5. Optionally verify by checking special cases or using implicit differentiation

Common rules to apply:
- Power Rule: d/dx[x^n] = n·x^(n-1)
- Chain Rule: d/dx[f(g(x))] = f'(g(x))·g'(x)
- Product Rule: d/dx[f·g] = f'·g + f·g'
- Quotient Rule: d/dx[f/g] = (f'·g - f·g')/g²
- Trig: d/dx[sin(x)] = cos(x), d/dx[cos(x)] = -sin(x), etc.
- Exponential: d/dx[e^x] = e^x, d/dx[a^x] = a^x·ln(a)
- Logarithmic: d/dx[ln(x)] = 1/x

Always provide the final derivative in simplified form."
            });
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
        // Build workflow with agent configs (deferred building with chat client inheritance)
        return await AgentWorkflow.Create()
            .WithName("MathConsensus")

            // Add solver agents via config (no provider = inherit from parent at runtime)
            .AddAgent("solver1", CreateSolverConfig("Solver-1"))
            .AddAgent("solver2", CreateSolverConfig("Solver-2"))
            .AddAgent("solver3", CreateSolverConfig("Solver-3"))
            .AddAgent("verifier", CreateVerifierConfig())

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

    private static AgentConfig CreateSolverConfig(string name) => new()
    {
        Name = name,
        MaxAgenticIterations = 10,
        SystemInstructions = SolverPrompt,
        // No Provider = will inherit from parent agent at execution time
    };

    private static readonly string VerifierPrompt = @"You are a mathematical verifier.
Compare the solver answers provided and determine the correct consensus answer.
If solvers disagree, analyze each approach and determine which is correct.
Return only the final verified answer.";

    private static AgentConfig CreateVerifierConfig() => new()
    {
        Name = "Verifier",
        MaxAgenticIterations = 5,
        SystemInstructions = VerifierPrompt,
        // No Provider = will inherit from parent agent at execution time
    };
}

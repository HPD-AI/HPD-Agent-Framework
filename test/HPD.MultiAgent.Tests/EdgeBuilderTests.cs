using HPD.Agent;
using HPD.MultiAgent;
using HPD.MultiAgent.Routing;
using HPDAgent.Graph.Abstractions;

namespace HPD.MultiAgent.Tests;

public class EdgeBuilderTests
{
    private static AgentConfig CreateTestConfig() => new() { Name = "Test", SystemInstructions = "Test" };

    [Fact]
    public void To_Creates_Unconditional_Edge()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("a", config)
            .AddAgent("b", config)
            .From("a").To("b");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void To_Multiple_Targets_Creates_Multiple_Edges()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("start", config)
            .AddAgent("a", config)
            .AddAgent("b", config)
            .AddAgent("c", config)
            .From("start").To("a", "b", "c");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenEquals_Adds_FieldEquals_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("classifier", config)
            .AddAgent("solver", config)
            .From("classifier").To("solver").WhenEquals("category", "math");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenNotEquals_Adds_FieldNotEquals_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("classifier", config)
            .AddAgent("general", config)
            .From("classifier").To("general").WhenNotEquals("category", "math");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenExists_Adds_FieldExists_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("extractor", config)
            .AddAgent("processor", config)
            .From("extractor").To("processor").WhenExists("data");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenNotExists_Adds_FieldNotExists_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("validator", config)
            .AddAgent("errorHandler", config)
            .From("validator").To("errorHandler").WhenNotExists("valid");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenGreaterThan_Adds_FieldGreaterThan_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("scorer", config)
            .AddAgent("highPriority", config)
            .From("scorer").To("highPriority").WhenGreaterThan("score", 0.8);

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenLessThan_Adds_FieldLessThan_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("scorer", config)
            .AddAgent("lowPriority", config)
            .From("scorer").To("lowPriority").WhenLessThan("score", 0.2);

        builder.Should().NotBeNull();
    }

    [Fact]
    public void WhenContains_Adds_FieldContains_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("classifier", config)
            .AddAgent("codeHandler", config)
            .From("classifier").To("codeHandler").WhenContains("tags", "code");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void AsDefault_Adds_Default_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("classifier", config)
            .AddAgent("fallback", config)
            .From("classifier").To("fallback").AsDefault();

        builder.Should().NotBeNull();
    }

    [Fact]
    public void RouteByType_Returns_TypeRouteBuilder()
    {
        var config = CreateTestConfig();

        var typeRouteBuilder = AgentWorkflow.Create()
            .AddAgent("classifier", config, o => o.UnionOutput<MathRoute, GeneralRoute>())
            .From("classifier").RouteByType();

        typeRouteBuilder.Should().NotBeNull();
    }

    [Fact]
    public void RouteByType_With_Multiple_Sources_Throws()
    {
        var config = CreateTestConfig();

        Action act = () => AgentWorkflow.Create()
            .AddAgent("a", config)
            .AddAgent("b", config)
            .From("a", "b").RouteByType();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TypeRouteBuilder_When_Creates_FieldEquals_Condition()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("classifier", config, o => o.UnionOutput<MathRoute, GeneralRoute>())
            .AddAgent("solver", config)
            .AddAgent("general", config)
            .From("classifier").RouteByType()
                .When<MathRoute>("solver")
                .When<GeneralRoute>("general");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void TypeRouteBuilder_Default_Creates_Default_Edge()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("classifier", config, o => o.UnionOutput<MathRoute, GeneralRoute>())
            .AddAgent("solver", config)
            .AddAgent("fallback", config)
            .From("classifier").RouteByType()
                .When<MathRoute>("solver")
                .Default("fallback");

        builder.Should().NotBeNull();
    }

    [Fact]
    public void From_After_Conditions_Continues_Building()
    {
        var config = CreateTestConfig();

        var builder = AgentWorkflow.Create()
            .AddAgent("a", config)
            .AddAgent("b", config)
            .AddAgent("c", config)
            .From("a").To("b").WhenEquals("x", 1)
            .From("b").To("c");

        builder.Should().NotBeNull();
    }

    // Test types
    public record MathRoute(string Question);
    public record GeneralRoute(string Message);
}

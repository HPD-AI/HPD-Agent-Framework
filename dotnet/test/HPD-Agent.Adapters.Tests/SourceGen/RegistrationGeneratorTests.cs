using FluentAssertions;
using HPD.Agent.Adapters.Tests.TestInfrastructure;

namespace HPD.Agent.Adapters.Tests.SourceGen;

/// <summary>
/// Tests for <see cref="HPD.Agent.Adapters.SourceGenerator.Generators.RegistrationGenerator"/>.
/// Verifies that <c>Add{Pascal}Adapter()</c> and <c>Map{Pascal}Webhook()</c> extension methods
/// are generated correctly for each <c>[HpdAdapter]</c> class.
/// </summary>
public class RegistrationGeneratorTests
{
    private static readonly string MinimalSlackAdapter = """
        using HPD.Agent.Adapters;
        namespace My.Adapters;
        [HpdAdapter("slack")]
        public partial class SlackAdapter { }
        """;

    // ── File names ────────────────────────────────────────────────────

    [Fact]
    public void Registration_GeneratesFileNamed_AdapterClassRegistration()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);

        var names = SourceGenHelper.GetGeneratedFileNames(result);
        names.Should().Contain("SlackAdapterRegistration.g.cs");
    }

    // ── DI extension ──────────────────────────────────────────────────

    [Fact]
    public void Registration_GeneratesAddAdapterExtensionMethod()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source.Should().NotBeNull();
        source!.Should().Contain("AddSlackAdapter(");
    }

    [Fact]
    public void Registration_AddAdapter_TakesActionOfConfig()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        // The configure callback uses the Config type derived from the class name
        source!.Should().Contain("Action<SlackAdapterConfig>");
    }

    [Fact]
    public void Registration_AddAdapter_RegistersAdapterAsSingleton()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("TryAddSingleton<SlackAdapter>");
    }

    [Fact]
    public void Registration_AddAdapter_RegistersPlatformSessionMapper()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("PlatformSessionMapper");
    }

    [Fact]
    public void Registration_AddAdapter_CallsServicesConfigure()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("services.Configure(configure)");
    }

    // ── Endpoint extension ────────────────────────────────────────────

    [Fact]
    public void Registration_GeneratesMapWebhookExtensionMethod()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("MapSlackWebhook(");
    }

    [Fact]
    public void Registration_DefaultPath_MatchesAdapterName()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("/webhooks/slack");
    }

    [Fact]
    public void Registration_MapWebhook_CallsMapPost()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("MapPost(");
    }

    [Fact]
    public void Registration_MapWebhook_WiresHandleWebhookAsync()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("HandleWebhookAsync");
    }

    // ── Pascal casing ─────────────────────────────────────────────────

    [Fact]
    public void Registration_PascalCasesMethodNames_FromAdapterName()
    {
        // Adapter named "teams" should produce AddTeamsAdapter / MapTeamsWebhook
        var source = """
            using HPD.Agent.Adapters;
            namespace My.Adapters;
            [HpdAdapter("teams")]
            public partial class TeamsAdapter { }
            """;

        var result = SourceGenHelper.RunGenerator(source, out _);
        var generated = SourceGenHelper.GetGeneratedFile(result, "TeamsAdapterRegistration.g.cs");

        generated.Should().NotBeNull();
        generated!.Should().Contain("AddTeamsAdapter(");
        generated.Should().Contain("MapTeamsWebhook(");
    }

    // ── Namespace placement ───────────────────────────────────────────

    [Fact]
    public void Registration_GeneratedCode_UsesAdapterNamespace()
    {
        var result = SourceGenHelper.RunGenerator(MinimalSlackAdapter, out _);
        var source = SourceGenHelper.GetGeneratedFile(result, "SlackAdapterRegistration.g.cs");

        source!.Should().Contain("namespace My.Adapters");
    }

    // ── Multiple adapters ─────────────────────────────────────────────

    [Fact]
    public void Registration_MultipleAdapters_GeneratesSeparateFiles()
    {
        var source = """
            using HPD.Agent.Adapters;
            namespace My.Adapters;
            [HpdAdapter("slack")]
            public partial class SlackAdapter { }
            [HpdAdapter("teams")]
            public partial class TeamsAdapter { }
            """;

        var result = SourceGenHelper.RunGenerator(source, out _);
        var names  = SourceGenHelper.GetGeneratedFileNames(result);

        names.Should().Contain("SlackAdapterRegistration.g.cs");
        names.Should().Contain("TeamsAdapterRegistration.g.cs");
    }
}

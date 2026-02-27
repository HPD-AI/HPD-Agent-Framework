using FluentAssertions;
using HPD.Agent.Adapters.Session;
using HPD.Agent.Adapters.Slack;
using HPD.Agent.Adapters.Tests.TestInfrastructure;
using HPD.Agent.Hosting.Lifecycle;
using HPD.Agent.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HPD.Agent.Adapters.Tests.Unit.SlackAdapterRegistration;

/// <summary>
/// DI-integration tests for <see cref="SlackAdapterServiceCollectionExtensions.AddSlackAdapter(IServiceCollection, Action{SlackAdapterConfig}, bool)"/>.
/// Each test verifies that the specified service type is resolvable from the container
/// after calling <c>AddSlackAdapter(configure, registerDefaultSecretResolver: true)</c>.
/// </summary>
public class SlackAdapterRegistrationTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fully wired <see cref="ServiceProvider"/> for the Slack adapter.
    /// Satisfies all mandatory prerequisites:
    ///   • <see cref="IConfiguration"/>   — required by <see cref="ChainedSecretResolver"/>
    ///   • <see cref="IHttpClientFactory"/> — required by <see cref="SlackApiClient"/>
    ///   • <see cref="AgentSessionManager"/> — required by <see cref="SlackAdapter"/> and <see cref="PlatformSessionMapper"/>
    /// </summary>
    private static ServiceProvider BuildProvider(
        Action<IServiceCollection>? extra = null,
        bool registerDefaultSecretResolver = true)
    {
        var services = new ServiceCollection();

        // IConfiguration: ChainedSecretResolver resolves ConfigurationSecretResolver from here.
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Slack:SigningSecret"] = "test-signing-secret",
                    ["Slack:BotToken"]      = "xoxb-test-token",
                })
                .Build());

        // IHttpClientFactory: SlackApiClient and SlackUserCache depend on named/typed HTTP clients.
        services.AddHttpClient();

        // AgentSessionManager is abstract — register the test double to satisfy DI.
        services.AddSingleton<AgentSessionManager>(
            new TestSessionManager(new InMemorySessionStore()));

        // Allow individual tests to pre-register services before AddSlackAdapter runs.
        extra?.Invoke(services);

        services.AddSlackAdapter(
            c =>
            {
                c.SigningSecret = "test-signing-secret";
                c.BotToken      = "xoxb-test-token";
            },
            registerDefaultSecretResolver);

        return services.BuildServiceProvider();
    }

    // ── 1. SlackAdapter ───────────────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_RegistersSlackAdapter()
    {
        using var sp = BuildProvider();
        sp.GetService<SlackAdapter>().Should().NotBeNull();
    }

    // ── 2. PlatformSessionMapper ──────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_RegistersPlatformSessionMapper()
    {
        using var sp = BuildProvider();
        sp.GetService<PlatformSessionMapper>().Should().NotBeNull();
    }

    // ── 3. SlackApiClient ─────────────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_RegistersSlackApiClient()
    {
        using var sp = BuildProvider();
        sp.GetService<SlackApiClient>().Should().NotBeNull();
    }

    // ── 4. SlackFormatConverter ───────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_RegistersSlackFormatConverter()
    {
        using var sp = BuildProvider();
        sp.GetService<SlackFormatConverter>().Should().NotBeNull();
    }

    // ── 5. SlackUserCache ─────────────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_RegistersSlackUserCache()
    {
        using var sp = BuildProvider();
        sp.GetService<SlackUserCache>().Should().NotBeNull();
    }

    // ── 6. ISecretResolver ────────────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_RegistersISecretResolver()
    {
        using var sp = BuildProvider();
        sp.GetService<ISecretResolver>().Should().NotBeNull();
    }

    // ── 7. Config applied ─────────────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_ConfigApplied()
    {
        using var sp = BuildProvider();
        var opts = sp.GetRequiredService<IOptions<SlackAdapterConfig>>().Value;

        opts.SigningSecret.Should().Be("test-signing-secret");
        opts.BotToken.Should().Be("xoxb-test-token");
    }

    // ── 8. Null services guard ────────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_NullServices_Throws()
    {
        var act = () => SlackAdapterServiceCollectionExtensions
            .AddSlackAdapter(null!, c => c.SigningSecret = "x", registerDefaultSecretResolver: false);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── 9. Null configure guard ───────────────────────────────────────────────

    [Fact]
    public void AddSlackAdapter_NullConfigure_Throws()
    {
        var act = () => new ServiceCollection()
            .AddSlackAdapter(null!, registerDefaultSecretResolver: false);

        act.Should().Throw<ArgumentNullException>();
    }
}

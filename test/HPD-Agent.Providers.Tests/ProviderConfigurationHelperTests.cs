using FluentAssertions;
using HPD.Agent.Providers;
using Xunit;

namespace HPD.Agent.Providers.Tests;

public class ProviderConfigurationHelperTests
{
    [Fact]
    public void ResolveApiKey_ShouldReturnExplicitKey()
    {
        // Arrange
        var explicitKey = "explicit-key";

        // Act
        var result = ProviderConfigurationHelper.ResolveApiKey(explicitKey, "anthropic");

        // Assert
        result.Should().Be("explicit-key");
    }

    [Fact]
    public void ResolveApiKey_ShouldResolveFromUppercaseEnvVar()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TESTPROVIDER_API_KEY", "env-key");

        try
        {
            // Act
            var result = ProviderConfigurationHelper.ResolveApiKey(null, "testprovider");

            // Assert
            result.Should().Be("env-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESTPROVIDER_API_KEY", null);
        }
    }

    [Fact]
    public void ResolveApiKey_ShouldResolveFromCapitalizedEnvVar()
    {
        // Arrange
        Environment.SetEnvironmentVariable("Testprovider_API_KEY", "env-key-cap");

        try
        {
            // Act
            var result = ProviderConfigurationHelper.ResolveApiKey(null, "testprovider");

            // Assert
            result.Should().Be("env-key-cap");
        }
        finally
        {
            Environment.SetEnvironmentVariable("Testprovider_API_KEY", null);
        }
    }

    [Fact]
    public void ResolveApiKey_ShouldPreferExplicitOverEnvironment()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TESTPROVIDER_API_KEY", "env-key");

        try
        {
            // Act
            var result = ProviderConfigurationHelper.ResolveApiKey("explicit-key", "testprovider");

            // Assert
            result.Should().Be("explicit-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESTPROVIDER_API_KEY", null);
        }
    }

    [Fact]
    public void ResolveApiKey_ShouldPreferUppercaseOverCapitalized()
    {
        // Arrange
        Environment.SetEnvironmentVariable("TESTPROVIDER_API_KEY", "uppercase-key");
        Environment.SetEnvironmentVariable("Testprovider_API_KEY", "capitalized-key");

        try
        {
            // Act
            var result = ProviderConfigurationHelper.ResolveApiKey(null, "testprovider");

            // Assert
            result.Should().Be("uppercase-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESTPROVIDER_API_KEY", null);
            Environment.SetEnvironmentVariable("Testprovider_API_KEY", null);
        }
    }

    [Fact]
    public void ResolveApiKey_ShouldReturnNullWhenNotFound()
    {
        // Act
        var result = ProviderConfigurationHelper.ResolveApiKey(null, "nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetApiKeyErrorMessage_ShouldReturnHelpfulMessage()
    {
        // Act
        var message = ProviderConfigurationHelper.GetApiKeyErrorMessage("anthropic", "Anthropic");

        // Assert
        message.Should().Contain("ANTHROPIC_API_KEY");
        message.Should().Contain(".WithAnthropic");
        message.Should().Contain("appsettings.json");
        message.Should().Contain("anthropic");
    }
}

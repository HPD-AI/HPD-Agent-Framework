using System.ComponentModel.DataAnnotations;
using HPD.Agent.Sandbox;
using Xunit;

namespace HPD.Sandbox.Local.Tests.Configuration;

public class SandboxConfigTests
{
    [Fact]
    public void DefaultConfig_HasSensibleDefaults()
    {
        var config = new SandboxConfig();
        
        Assert.Empty(config.AllowedDomains);
        Assert.Contains(".", config.AllowWrite);
        Assert.Contains("/tmp", config.AllowWrite);
        Assert.Contains("~/.ssh", config.DenyRead);
        Assert.Equal(3, config.MandatoryDenySearchDepth);
        Assert.False(config.AllowGitConfig);
        Assert.False(config.AllowAllUnixSockets);
        Assert.True(config.EnableViolationMonitoring);
    }

    [Fact]
    public void Validate_PassesForValidConfig()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["github.com", "*.npmjs.org"],
            AllowWrite = ["."],
            DenyRead = ["~/.ssh"]
        };

        // Should not throw
        config.Validate();
    }

    [Fact]
    public void Validate_ThrowsForInvalidDomain_WithUrl()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["https://github.com"]  // URLs not allowed
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_ThrowsForInvalidDomain_WithPort()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["github.com:443"]  // Ports not allowed
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_ThrowsForInvalidDomain_WithPath()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["github.com/user/repo"]  // Paths not allowed
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_AcceptsWildcardDomains()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["*.github.com", "*.npmjs.org"]
        };

        // Should not throw
        config.Validate();
    }

    [Fact]
    public void Validate_AcceptsLocalhost()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["localhost"]
        };

        // Should not throw
        config.Validate();
    }

    [Fact]
    public void Validate_ThrowsForInvalidSearchDepth_TooLow()
    {
        var config = new SandboxConfig
        {
            MandatoryDenySearchDepth = 0
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_ThrowsForInvalidSearchDepth_TooHigh()
    {
        var config = new SandboxConfig
        {
            MandatoryDenySearchDepth = 100
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_ThrowsForInvalidProxyPort()
    {
        var config = new SandboxConfig
        {
            ExternalHttpProxyPort = 99999
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_ThrowsForEmptyPath()
    {
        var config = new SandboxConfig
        {
            AllowWrite = [".", "", "/tmp"]  // Empty string in array
        };

        Assert.Throws<ValidationException>(() => config.Validate());
    }

    [Fact]
    public void Restrictive_HasNoNetworkAccess()
    {
        var config = SandboxConfig.Restrictive;
        
        Assert.Empty(config.AllowedDomains);
        Assert.Equal(SandboxFailureBehavior.Block, config.OnInitializationFailure);
    }

    [Fact]
    public void Restrictive_HasLimitedWriteAccess()
    {
        var config = SandboxConfig.Restrictive;
        
        Assert.Contains(".", config.AllowWrite);
        Assert.Contains("/tmp", config.AllowWrite);
        Assert.Equal(2, config.AllowWrite.Length);
    }

    [Fact]
    public void Restrictive_DeniesCredentialPaths()
    {
        var config = SandboxConfig.Restrictive;
        
        Assert.Contains("~/.ssh", config.DenyRead);
        Assert.Contains("~/.aws", config.DenyRead);
        Assert.Contains("~/.gnupg", config.DenyRead);
    }

    [Fact]
    public void Permissive_HasNoNetworkFiltering()
    {
        var config = SandboxConfig.Permissive;
        
        // null means no filtering (allow all)
        Assert.Null(config.AllowedDomains);
    }

    [Fact]
    public void Permissive_AllowsUnixSockets()
    {
        var config = SandboxConfig.Permissive;
        
        Assert.True(config.AllowAllUnixSockets);
    }

    [Fact]
    public void Permissive_WarnsOnFailure()
    {
        var config = SandboxConfig.Permissive;
        
        Assert.Equal(SandboxFailureBehavior.Warn, config.OnInitializationFailure);
    }

    [Fact]
    public void NetworkOnly_FiltersOnlyNetwork()
    {
        var config = SandboxConfig.NetworkOnly("github.com", "api.nuget.org");
        
        Assert.Equal(2, config.AllowedDomains.Length);
        Assert.Contains("github.com", config.AllowedDomains);
        Assert.Contains("api.nuget.org", config.AllowedDomains);
        
        // Should have permissive filesystem access
        Assert.Contains("~", config.AllowWrite);
        Assert.Empty(config.DenyRead);
    }

    [Fact]
    public void Config_CanBeModifiedWithInit()
    {
        var config = new SandboxConfig
        {
            AllowedDomains = ["github.com"],
            AllowWrite = ["."],
            MandatoryDenySearchDepth = 5,
            AllowGitConfig = true
        };

        Assert.Single(config.AllowedDomains);
        Assert.Equal(5, config.MandatoryDenySearchDepth);
        Assert.True(config.AllowGitConfig);
    }

    [Fact]
    public void SandboxFailureBehavior_HasExpectedValues()
    {
        Assert.Equal(0, (int)SandboxFailureBehavior.Block);
        Assert.Equal(1, (int)SandboxFailureBehavior.Warn);
        Assert.Equal(2, (int)SandboxFailureBehavior.Ignore);
    }

    [Fact]
    public void SandboxViolationBehavior_HasExpectedValues()
    {
        Assert.Equal(0, (int)SandboxViolationBehavior.EmitAndContinue);
        Assert.Equal(1, (int)SandboxViolationBehavior.BlockAndEmit);
        Assert.Equal(2, (int)SandboxViolationBehavior.Ignore);
    }
}

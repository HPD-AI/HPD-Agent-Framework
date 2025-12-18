using HPD.Sandbox.Local.Platforms.MacOS;
using Xunit;

namespace HPD.Sandbox.Local.Tests.Platforms.MacOS;

public class GlobToRegexTests
{
    [Theory]
    [InlineData("*", "^[^/]*$")]
    [InlineData("*.txt", "^[^/]*\\.txt$")]
    [InlineData("*.cs", "^[^/]*\\.cs$")]
    public void Convert_SingleAsterisk_MatchesNonSlashChars(string glob, string expectedRegex)
    {
        var result = GlobToRegex.Convert(glob);
        Assert.Equal(expectedRegex, result);
    }

    [Theory]
    [InlineData("**", "^.*$")]
    [InlineData("**/file.txt", "^(.*/)?file\\.txt$")]
    [InlineData("src/**/*.cs", "^src/(.*/)?[^/]*\\.cs$")]
    public void Convert_DoubleAsterisk_MatchesAnyCharsIncludingSlash(string glob, string expectedRegex)
    {
        var result = GlobToRegex.Convert(glob);
        Assert.Equal(expectedRegex, result);
    }

    [Theory]
    [InlineData("file?.txt", "^file[^/]\\.txt$")]
    [InlineData("???.cs", "^[^/][^/][^/]\\.cs$")]
    public void Convert_QuestionMark_MatchesSingleNonSlashChar(string glob, string expectedRegex)
    {
        var result = GlobToRegex.Convert(glob);
        Assert.Equal(expectedRegex, result);
    }

    [Theory]
    [InlineData("[abc]", "^[abc]$")]
    [InlineData("[a-z]", "^[a-z]$")]
    [InlineData("file[0-9].txt", "^file[0-9]\\.txt$")]
    public void Convert_CharacterClass_PassesThrough(string glob, string expectedRegex)
    {
        var result = GlobToRegex.Convert(glob);
        Assert.Equal(expectedRegex, result);
    }

    [Theory]
    [InlineData("[!abc]", "^[^abc]$")]
    [InlineData("[!0-9]", "^[^0-9]$")]
    public void Convert_NegatedCharacterClass_ConvertsToCaretSyntax(string glob, string expectedRegex)
    {
        var result = GlobToRegex.Convert(glob);
        Assert.Equal(expectedRegex, result);
    }

    [Theory]
    [InlineData("file.txt", "^file\\.txt$")]
    [InlineData("path/to/file", "^path/to/file$")]
    [InlineData("no-glob-here", "^no-glob-here$")]
    public void Convert_NoGlobChars_EscapesSpecialChars(string glob, string expectedRegex)
    {
        var result = GlobToRegex.Convert(glob);
        Assert.Equal(expectedRegex, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Convert_EmptyOrNull_ReturnsEmptyPattern(string? glob)
    {
        var result = GlobToRegex.Convert(glob ?? "");
        Assert.Equal("^$", result);
    }

    [Theory]
    [InlineData("*.txt")]
    [InlineData("**/*.cs")]
    [InlineData("file?.log")]
    [InlineData("[abc].txt")]
    public void ContainsGlobChars_ReturnsTrue_ForGlobPatterns(string pattern)
    {
        Assert.True(GlobToRegex.ContainsGlobChars(pattern));
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData("/path/to/file")]
    [InlineData("no-special-chars")]
    public void ContainsGlobChars_ReturnsFalse_ForNormalPaths(string path)
    {
        Assert.False(GlobToRegex.ContainsGlobChars(path));
    }

    [Fact]
    public void ConvertAndEscape_DoublesBackslashes()
    {
        var result = GlobToRegex.ConvertAndEscape("*.txt");
        
        // Should have double backslashes for sandbox profile string
        Assert.Contains("\\\\", result);
    }
}

public class SeatbeltProfileBuilderTests
{
    [Fact]
    public void Build_IncludesVersionHeader()
    {
        var builder = new SeatbeltProfileBuilder("test-session");
        var profile = builder.Build();
        
        Assert.Contains("(version 1)", profile);
    }

    [Fact]
    public void Build_IncludesDenyDefault()
    {
        var builder = new SeatbeltProfileBuilder("test-session");
        var profile = builder.Build();
        
        Assert.Contains("(deny default", profile);
    }

    [Fact]
    public void Build_IncludesLogTag()
    {
        var builder = new SeatbeltProfileBuilder("my-log-tag-123");
        var profile = builder.Build();
        
        Assert.Contains("my-log-tag-123", profile);
    }

    [Fact]
    public void AllowWrite_AddsAllowFileWriteRule()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.AllowWrite("/tmp/test");
        var profile = builder.Build();
        
        Assert.Contains("(allow file-write*", profile);
        Assert.Contains("/tmp/test", profile);
    }

    [Fact]
    public void AllowWrite_HandlesMultiplePaths()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.AllowWrite(["/tmp/a", "/tmp/b", "/tmp/c"]);
        var profile = builder.Build();
        
        Assert.Contains("/tmp/a", profile);
        Assert.Contains("/tmp/b", profile);
        Assert.Contains("/tmp/c", profile);
    }

    [Fact]
    public void DenyWrite_AddsDenyFileWriteRule()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.DenyWrite("/protected/path");
        var profile = builder.Build();
        
        Assert.Contains("(deny file-write*", profile);
        Assert.Contains("/protected/path", profile);
    }

    [Fact]
    public void DenyRead_AddsDenyFileReadRule()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.DenyRead("/secret/path");
        var profile = builder.Build();
        
        Assert.Contains("(deny file-read*", profile);
        Assert.Contains("/secret/path", profile);
    }

    [Fact]
    public void WithNetwork_AllowsNetworkWhenTrue()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.WithNetwork(allowed: true);
        var profile = builder.Build();
        
        Assert.Contains("(allow network*)", profile);
    }

    [Fact]
    public void WithNetwork_DeniesNetworkWhenFalse()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.WithNetwork(allowed: false);
        var profile = builder.Build();
        
        Assert.Contains("(deny network*)", profile);
    }

    [Fact]
    public void WithNetwork_IncludesProxyPortRules()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.WithNetwork(allowed: true, httpProxyPort: 8080, socksProxyPort: 1080);
        var profile = builder.Build();
        
        Assert.Contains("localhost:8080", profile);
        Assert.Contains("localhost:1080", profile);
    }

    [Fact]
    public void AllowPty_AddsPseudoTtyRules()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.AllowPty();
        var profile = builder.Build();
        
        Assert.Contains("(allow pseudo-tty)", profile);
        Assert.Contains("/dev/ptmx", profile);
    }

    [Fact]
    public void AllowLocalBinding_AddsBindRule()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.AllowLocalBinding();
        var profile = builder.Build();
        
        Assert.Contains("network-bind", profile);
        Assert.Contains("localhost", profile);
    }

    [Fact]
    public void Build_IncludesMoveBlockingRules()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.DenyWrite("/protected");
        var profile = builder.Build();
        
        // Should include file-write-unlink to prevent mv bypass
        Assert.Contains("file-write-unlink", profile);
    }

    [Fact]
    public void Build_IncludesEssentialPermissions()
    {
        var builder = new SeatbeltProfileBuilder("test");
        var profile = builder.Build();
        
        // Should include essential process permissions
        Assert.Contains("(allow process-exec)", profile);
        Assert.Contains("(allow process-fork)", profile);
    }

    [Fact]
    public void Build_HandleGlobPatterns()
    {
        var builder = new SeatbeltProfileBuilder("test");
        builder.DenyWrite("**/*.config");
        var profile = builder.Build();
        
        // Should use regex for glob patterns
        Assert.Contains("regex", profile);
    }

    [Fact]
    public void FluentInterface_AllowsChaining()
    {
        var profile = new SeatbeltProfileBuilder("test")
            .AllowWrite("/tmp")
            .DenyRead("/secret")
            .WithNetwork(true)
            .AllowPty()
            .Build();
        
        Assert.Contains("/tmp", profile);
        Assert.Contains("/secret", profile);
        Assert.Contains("network", profile);
        Assert.Contains("pseudo-tty", profile);
    }
}

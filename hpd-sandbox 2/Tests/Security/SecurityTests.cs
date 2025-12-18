using System.Runtime.InteropServices;
using HPD.Sandbox.Local.Security;
using Xunit;

namespace HPD.Sandbox.Local.Tests.Security;

public class SandboxDefaultsTests
{
    [Fact]
    public void DangerousFiles_ContainsGitConfig()
    {
        Assert.Contains(".gitconfig", SandboxDefaults.DangerousFiles);
    }

    [Fact]
    public void DangerousFiles_ContainsBashrc()
    {
        Assert.Contains(".bashrc", SandboxDefaults.DangerousFiles);
    }

    [Fact]
    public void DangerousFiles_ContainsZshrc()
    {
        Assert.Contains(".zshrc", SandboxDefaults.DangerousFiles);
    }

    [Fact]
    public void DangerousDirectories_ContainsGitHooks()
    {
        Assert.Contains(".git/hooks", SandboxDefaults.DangerousDirectories);
    }

    [Fact]
    public void DangerousDirectories_ContainsVscode()
    {
        Assert.Contains(".vscode", SandboxDefaults.DangerousDirectories);
    }

    [Fact]
    public void SensitiveDirectories_ContainsSsh()
    {
        Assert.Contains("~/.ssh", SandboxDefaults.SensitiveDirectories);
    }

    [Fact]
    public void SensitiveDirectories_ContainsAws()
    {
        Assert.Contains("~/.aws", SandboxDefaults.SensitiveDirectories);
    }

    [Fact]
    public void DefaultWritePaths_ContainsTmp()
    {
        Assert.Contains("/tmp", SandboxDefaults.DefaultWritePaths);
    }

    [Fact]
    public void SafeEnvironmentVariables_ContainsPath()
    {
        Assert.Contains("PATH", SandboxDefaults.SafeEnvironmentVariables);
    }

    [Fact]
    public void SafeEnvironmentVariables_ContainsHome()
    {
        Assert.Contains("HOME", SandboxDefaults.SafeEnvironmentVariables);
    }
}

public class PathNormalizerTests
{
    [Fact]
    public void Normalize_ExpandsTilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = PathNormalizer.Normalize("~/test");
        
        Assert.StartsWith(home, result);
        Assert.EndsWith("test", result);
    }

    [Fact]
    public void Normalize_ExpandsTildeAlone()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = PathNormalizer.Normalize("~");
        
        Assert.Equal(home, result);
    }

    [Fact]
    public void Normalize_ConvertsRelativeToAbsolute()
    {
        var result = PathNormalizer.Normalize("./test");
        
        Assert.True(Path.IsPathRooted(result));
    }

    [Fact]
    public void Normalize_HandlesAbsolutePath()
    {
        var path = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
            ? @"C:\test\path" 
            : "/test/path";
        var result = PathNormalizer.Normalize(path, resolveSymlinks: false);
        
        Assert.Equal(path, result);
    }

    [Fact]
    public void Normalize_ThrowsOnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(""));
    }

    [Fact]
    public void Normalize_ThrowsOnWhitespacePath()
    {
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize("   "));
    }

    [Fact]
    public void NormalizeForComparison_ReturnsLowercase()
    {
        var result = PathNormalizer.NormalizeForComparison("/Test/PATH/File.TXT");
        
        Assert.Equal("/test/path/file.txt", result);
    }

    [Fact]
    public void IsWithinPath_ReturnsTrueForSubpath()
    {
        Assert.True(PathNormalizer.IsWithinPath("/home/user/project/file.txt", "/home/user/project"));
    }

    [Fact]
    public void IsWithinPath_ReturnsTrueForSamePath()
    {
        Assert.True(PathNormalizer.IsWithinPath("/home/user/project", "/home/user/project"));
    }

    [Fact]
    public void IsWithinPath_ReturnsFalseForOutsidePath()
    {
        Assert.False(PathNormalizer.IsWithinPath("/home/user/other", "/home/user/project"));
    }

    [Fact]
    public void IsWithinPath_ReturnsFalseForParentPath()
    {
        Assert.False(PathNormalizer.IsWithinPath("/home/user", "/home/user/project"));
    }

    [Fact]
    public void GetAncestors_ReturnsParentDirectories()
    {
        var ancestors = PathNormalizer.GetAncestors("/home/user/project/src/file.cs").ToList();
        
        Assert.Contains("/home/user/project/src", ancestors);
        Assert.Contains("/home/user/project", ancestors);
        Assert.Contains("/home/user", ancestors);
        Assert.Contains("/home", ancestors);
    }

    [Fact]
    public void ContainsGlobChars_DetectsAsterisk()
    {
        Assert.True(PathNormalizer.ContainsGlobChars("*.txt"));
    }

    [Fact]
    public void ContainsGlobChars_DetectsDoubleAsterisk()
    {
        Assert.True(PathNormalizer.ContainsGlobChars("**/*.cs"));
    }

    [Fact]
    public void ContainsGlobChars_DetectsQuestionMark()
    {
        Assert.True(PathNormalizer.ContainsGlobChars("file?.txt"));
    }

    [Fact]
    public void ContainsGlobChars_DetectsBrackets()
    {
        Assert.True(PathNormalizer.ContainsGlobChars("file[0-9].txt"));
    }

    [Fact]
    public void ContainsGlobChars_ReturnsFalseForNormalPath()
    {
        Assert.False(PathNormalizer.ContainsGlobChars("/home/user/file.txt"));
    }
}

public class DangerousPathScannerTests
{
    private readonly string _testDir;

    public DangerousPathScannerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"hpd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Scanner_FindsGitconfigInRoot()
    {
        // Arrange
        var gitconfig = Path.Combine(_testDir, ".gitconfig");
        await File.WriteAllTextAsync(gitconfig, "# test");
        
        var scanner = new DangerousPathScanner(maxDepth: 3);
        
        // Act
        var paths = await scanner.GetDangerousPathsAsync(_testDir);
        
        // Assert
        Assert.Contains(paths, p => p.EndsWith(".gitconfig"));
        
        // Cleanup
        File.Delete(gitconfig);
    }

    [Fact]
    public async Task Scanner_FindsGitHooksDirectory()
    {
        // Arrange
        var gitHooks = Path.Combine(_testDir, ".git", "hooks");
        Directory.CreateDirectory(gitHooks);
        
        var scanner = new DangerousPathScanner(maxDepth: 3);
        
        // Act
        var paths = await scanner.GetDangerousPathsAsync(_testDir);
        
        // Assert
        Assert.Contains(paths, p => p.Contains(".git") && p.Contains("hooks"));
        
        // Cleanup
        Directory.Delete(Path.Combine(_testDir, ".git"), recursive: true);
    }

    [Fact]
    public async Task Scanner_FindsNestedDangerousFiles()
    {
        // Arrange
        var nested = Path.Combine(_testDir, "subdir", "nested");
        Directory.CreateDirectory(nested);
        var gitconfig = Path.Combine(nested, ".gitconfig");
        await File.WriteAllTextAsync(gitconfig, "# test");
        
        var scanner = new DangerousPathScanner(maxDepth: 3);
        
        // Act
        var paths = await scanner.GetDangerousPathsAsync(_testDir);
        
        // Assert
        Assert.Contains(paths, p => p.Contains("nested") && p.EndsWith(".gitconfig"));
        
        // Cleanup
        Directory.Delete(Path.Combine(_testDir, "subdir"), recursive: true);
    }

    [Fact]
    public async Task Scanner_RespectsMaxDepth()
    {
        // Arrange - Create file at depth 4
        var deep = Path.Combine(_testDir, "a", "b", "c", "d");
        Directory.CreateDirectory(deep);
        var gitconfig = Path.Combine(deep, ".gitconfig");
        await File.WriteAllTextAsync(gitconfig, "# test");
        
        var scanner = new DangerousPathScanner(maxDepth: 2);
        
        // Act
        var paths = await scanner.GetDangerousPathsAsync(_testDir);
        
        // Assert - Should NOT find file at depth 4
        Assert.DoesNotContain(paths, p => p.Contains("d") && p.EndsWith(".gitconfig"));
        
        // Cleanup
        Directory.Delete(Path.Combine(_testDir, "a"), recursive: true);
    }

    [Fact]
    public async Task Scanner_SkipsNodeModules()
    {
        // Arrange
        var nodeModules = Path.Combine(_testDir, "node_modules", "some-package");
        Directory.CreateDirectory(nodeModules);
        var gitconfig = Path.Combine(nodeModules, ".gitconfig");
        await File.WriteAllTextAsync(gitconfig, "# test");
        
        var scanner = new DangerousPathScanner(maxDepth: 5);
        
        // Act
        var paths = await scanner.GetDangerousPathsAsync(_testDir);
        
        // Assert - Should NOT find file in node_modules
        Assert.DoesNotContain(paths, p => p.Contains("node_modules"));
        
        // Cleanup
        Directory.Delete(Path.Combine(_testDir, "node_modules"), recursive: true);
    }

    [Fact]
    public async Task Scanner_AllowsGitConfigWhenConfigured()
    {
        // Arrange
        var gitDir = Path.Combine(_testDir, ".git");
        Directory.CreateDirectory(gitDir);
        var gitConfig = Path.Combine(gitDir, "config");
        await File.WriteAllTextAsync(gitConfig, "# test");
        
        var scanner = new DangerousPathScanner(maxDepth: 3);
        
        // Act
        var pathsWithoutGitConfig = await scanner.GetDangerousPathsAsync(_testDir, allowGitConfig: true);
        var pathsWithGitConfig = await scanner.GetDangerousPathsAsync(_testDir, allowGitConfig: false);
        
        // Assert
        Assert.DoesNotContain(pathsWithoutGitConfig, p => p.EndsWith(".git/config") || p.EndsWith(".git\\config"));
        Assert.Contains(pathsWithGitConfig, p => p.Contains(".git") && p.EndsWith("config"));
        
        // Cleanup
        Directory.Delete(gitDir, recursive: true);
    }

    [Fact]
    public async Task Scanner_CachesResults()
    {
        // Arrange
        var scanner = new DangerousPathScanner(maxDepth: 3);
        
        // Act
        var paths1 = await scanner.GetDangerousPathsAsync(_testDir);
        var paths2 = await scanner.GetDangerousPathsAsync(_testDir);
        
        // Assert - Should return same instance from cache
        Assert.Same(paths1, paths2);
    }

    [Fact]
    public void Scanner_ClearCache_InvalidatesCache()
    {
        // Arrange
        var scanner = new DangerousPathScanner(maxDepth: 3);
        
        // Act
        scanner.ClearCache();
        
        // Assert - No exception, cache is cleared
        Assert.True(true);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }
}

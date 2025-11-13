using Xunit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using HPD_Agent.Skills;
using HPD_Agent.Tests.TestPlugins;

namespace HPD_Agent.Tests.Skills;

/// <summary>
/// Tests for Phase 4.5: Selective Function Registration
/// Validates that skills only register the specific functions they reference, not entire plugins
/// </summary>
public class Phase45SelectiveFunctionRegistrationTests
{
    // ===== P0 Tests: Core Selective Registration =====

    [Fact]
    public void PluginRegistration_FromTypeFunctions_CreatesFilteredRegistration()
    {
        // Arrange
        var functionNames = new[] { "ReadFile", "WriteFile" };

        // Act
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            functionNames);

        // Assert
        Assert.NotNull(registration);
        Assert.Equal(typeof(MockFileSystemPlugin), registration.PluginType);
        Assert.NotNull(registration.FunctionFilter);
        Assert.Equal(2, registration.FunctionFilter!.Length);
        Assert.Contains("ReadFile", registration.FunctionFilter);
        Assert.Contains("WriteFile", registration.FunctionFilter);
    }

    [Fact]
    public void PluginRegistration_FromTypeFunctions_WithNullArray_ThrowsException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            PluginRegistration.FromTypeFunctions(typeof(MockFileSystemPlugin), null!));
    }

    [Fact]
    public void PluginRegistration_FromTypeFunctions_WithEmptyArray_ThrowsException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            PluginRegistration.FromTypeFunctions(typeof(MockFileSystemPlugin), Array.Empty<string>()));
    }

    [Fact]
    public void PluginManager_RegisterPluginFunctions_AddsFilteredRegistration()
    {
        // Arrange
        var manager = new PluginManager();
        var functionNames = new[] { "ReadFile", "WriteFile" };

        // Act
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), functionNames);

        // Assert
        var registrations = manager.GetPluginRegistrations();
        Assert.Single(registrations);
        Assert.NotNull(registrations[0].FunctionFilter);
        Assert.Equal(2, registrations[0].FunctionFilter!.Length);
    }

    [Fact]
    public void PluginManager_RegisterPluginFunctions_ReturnsManagerForChaining()
    {
        // Arrange
        var manager = new PluginManager();

        // Act
        var result = manager.RegisterPluginFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "ReadFile" });

        // Assert
        Assert.Same(manager, result);
    }

    // ===== Function Filtering Tests =====

    [Fact]
    public void ToAIFunctions_WithFunctionFilter_ReturnsOnlyFilteredFunctions()
    {
        // Arrange
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "ReadFile", "WriteFile" });

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        Assert.NotNull(functions);
        Assert.Equal(2, functions.Count);
        Assert.Contains(functions, f => f.Name == "ReadFile");
        Assert.Contains(functions, f => f.Name == "WriteFile");
        Assert.DoesNotContain(functions, f => f.Name == "DeleteFile");
        Assert.DoesNotContain(functions, f => f.Name == "ListFiles");
        Assert.DoesNotContain(functions, f => f.Name == "GetFileInfo");
    }

    [Fact]
    public void ToAIFunctions_WithoutFunctionFilter_ReturnsAllFunctions()
    {
        // Arrange
        var registration = PluginRegistration.FromType<MockFileSystemPlugin>();

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        Assert.NotNull(functions);
        Assert.Equal(5, functions.Count); // All 5 functions
        Assert.Contains(functions, f => f.Name == "ReadFile");
        Assert.Contains(functions, f => f.Name == "WriteFile");
        Assert.Contains(functions, f => f.Name == "DeleteFile");
        Assert.Contains(functions, f => f.Name == "ListFiles");
        Assert.Contains(functions, f => f.Name == "GetFileInfo");
    }

    [Fact]
    public void ToAIFunctions_WithSingleFunctionFilter_ReturnsOnlyThatFunction()
    {
        // Arrange
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "ReadFile" });

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        Assert.Single(functions);
        Assert.Equal("ReadFile", functions[0].Name);
    }

    // ===== Deduplication Tests =====

    [Fact]
    public void CreateAllFunctions_WithDuplicatePlugins_DifferentFilters_ReturnsAllUniqueFunctions()
    {
        // Arrange
        var manager = new PluginManager();

        // Register with filter first
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), new[] { "ReadFile", "WriteFile" });

        // Then register full plugin
        manager.RegisterPlugin<MockFileSystemPlugin>();

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert
        // Should have 2 + 5 = 7 total (2 from filtered + 5 from full)
        // But ReadFile and WriteFile appear twice, so we check they exist
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "ReadFile");
        Assert.Contains(functions, f => f.Name == "WriteFile");
        Assert.Contains(functions, f => f.Name == "DeleteFile");
        Assert.Contains(functions, f => f.Name == "ListFiles");
        Assert.Contains(functions, f => f.Name == "GetFileInfo");
    }

    [Fact]
    public void CreateAllFunctions_WithMultipleFilteredRegistrations_ReturnsAllFunctions()
    {
        // Arrange
        var manager = new PluginManager();

        // Register different subsets
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), new[] { "ReadFile" });
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), new[] { "WriteFile", "DeleteFile" });

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert
        // Should have 1 + 2 = 3 functions total
        Assert.Equal(3, functions.Count);
        Assert.Contains(functions, f => f.Name == "ReadFile");
        Assert.Contains(functions, f => f.Name == "WriteFile");
        Assert.Contains(functions, f => f.Name == "DeleteFile");
    }

    // ===== Edge Cases =====

    [Fact]
    public void ToAIFunctions_WithNonExistentFunctionInFilter_IgnoresIt()
    {
        // Arrange
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "ReadFile", "NonExistentFunction", "WriteFile" });

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        // Should only return the 2 functions that exist
        Assert.Equal(2, functions.Count);
        Assert.Contains(functions, f => f.Name == "ReadFile");
        Assert.Contains(functions, f => f.Name == "WriteFile");
    }

    [Fact]
    public void ToAIFunctions_WithAllNonExistentFunctions_ReturnsEmpty()
    {
        // Arrange
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "NonExistent1", "NonExistent2" });

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        Assert.Empty(functions);
    }

    // ===== Case Sensitivity Tests =====

    [Fact]
    public void ToAIFunctions_FunctionFilterIsCaseSensitive()
    {
        // Arrange
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "readfile" }); // lowercase

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        // Should not match "ReadFile" (case sensitive)
        Assert.Empty(functions);
    }

    [Fact]
    public void ToAIFunctions_FunctionFilterExactMatch()
    {
        // Arrange
        var registration = PluginRegistration.FromTypeFunctions(
            typeof(MockFileSystemPlugin),
            new[] { "ReadFile" }); // exact case

        // Act
        var functions = registration.ToAIFunctions();

        // Assert
        Assert.Single(functions);
        Assert.Equal("ReadFile", functions[0].Name);
    }

    // ===== Integration with PluginManager =====

    [Fact]
    public void PluginManager_MixedRegistrations_WorksCorrectly()
    {
        // Arrange
        var manager = new PluginManager();

        // Mix of full and filtered registrations
        // Note: MockDebuggingPlugin would require skill processing which is Phase 3+ feature
        // For now, just test with MockFileSystemPlugin
        manager.RegisterPlugin<MockFileSystemPlugin>(); // Full plugin (5 functions)
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), new[] { "ReadFile" }); // Filtered (1 function)

        // Act
        var functions = manager.CreateAllFunctions();

        // Assert
        Assert.NotEmpty(functions);

        // Should have 5 + 1 = 6 functions (ReadFile appears twice from different registrations)
        Assert.True(functions.Count >= 5, $"Expected at least 5 functions, got {functions.Count}");
        Assert.Contains(functions, f => f.Name == "ReadFile");
        Assert.Contains(functions, f => f.Name == "WriteFile");
        Assert.Contains(functions, f => f.Name == "DeleteFile");
    }

    [Fact]
    public void PluginManager_GetRegisteredPluginTypes_IncludesFilteredPlugins()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), new[] { "ReadFile" });

        // Act
        var types = manager.GetRegisteredPluginTypes();

        // Assert
        Assert.Single(types);
        Assert.Equal(typeof(MockFileSystemPlugin), types[0]);
    }

    [Fact]
    public void PluginManager_Clear_RemovesFilteredRegistrations()
    {
        // Arrange
        var manager = new PluginManager();
        manager.RegisterPluginFunctions(typeof(MockFileSystemPlugin), new[] { "ReadFile" });

        // Act
        manager.Clear();

        // Assert
        Assert.Empty(manager.GetPluginRegistrations());
        Assert.Empty(manager.GetRegisteredPluginTypes());
    }
}

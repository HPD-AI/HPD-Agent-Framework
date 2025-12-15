// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent;
using HPD.Agent.ClientTools;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.ClientTools;

/// <summary>
/// Unit tests for ClientToolStateData immutable state management.
/// Tests all With* methods and state transitions.
/// </summary>
public class ClientToolStateDataTests
{
    // ============================================
    // Constructor Tests
    // ============================================

    [Fact]
    public void Constructor_CreatesEmptyState()
    {
        // Arrange & Act
        var state = new ClientToolStateData();

        // Assert
        Assert.NotNull(state);
        Assert.Empty(state.RegisteredPlugins);
        Assert.Empty(state.ExpandedPlugins);
        Assert.Empty(state.HiddenTools);
        Assert.Empty(state.Context);
        Assert.Null(state.State);
        Assert.Null(state.PendingAugmentation);
    }

    // ============================================
    // Plugin Registration Tests
    // ============================================

    [Fact]
    public void WithRegisteredPlugin_AddsPlugin()
    {
        // Arrange
        var state = new ClientToolStateData();
        var plugin = CreateTestPlugin("TestPlugin");

        // Act
        var updated = state.WithRegisteredPlugin(plugin);

        // Assert
        Assert.NotSame(state, updated);
        Assert.Single(updated.RegisteredPlugins);
        Assert.True(updated.RegisteredPlugins.ContainsKey("TestPlugin"));
        Assert.Equal(plugin, updated.RegisteredPlugins["TestPlugin"]);
    }

    [Fact]
    public void WithRegisteredPlugin_MultipleCalls_AddsAllPlugins()
    {
        // Arrange
        var state = new ClientToolStateData();
        var plugin1 = CreateTestPlugin("Plugin1");
        var plugin2 = CreateTestPlugin("Plugin2");

        // Act
        var updated = state
            .WithRegisteredPlugin(plugin1)
            .WithRegisteredPlugin(plugin2);

        // Assert
        Assert.Equal(2, updated.RegisteredPlugins.Count);
        Assert.True(updated.RegisteredPlugins.ContainsKey("Plugin1"));
        Assert.True(updated.RegisteredPlugins.ContainsKey("Plugin2"));
    }

    [Fact]
    public void WithRegisteredPlugin_SameName_ReplacesPlugin()
    {
        // Arrange
        var state = new ClientToolStateData();
        var plugin1 = CreateTestPlugin("TestPlugin", tools: new[] { CreateTestTool("Tool1") });
        var plugin2 = CreateTestPlugin("TestPlugin", tools: new[] { CreateTestTool("Tool2") });

        // Act
        var updated = state
            .WithRegisteredPlugin(plugin1)
            .WithRegisteredPlugin(plugin2);

        // Assert
        Assert.Single(updated.RegisteredPlugins);
        Assert.Equal("Tool2", updated.RegisteredPlugins["TestPlugin"].Tools[0].Name);
    }

    [Fact]
    public void WithoutRegisteredPlugin_RemovesPlugin()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1"))
            .WithRegisteredPlugin(CreateTestPlugin("Plugin2"));

        // Act
        var updated = state.WithoutRegisteredPlugin("Plugin1");

        // Assert
        Assert.Single(updated.RegisteredPlugins);
        Assert.False(updated.RegisteredPlugins.ContainsKey("Plugin1"));
        Assert.True(updated.RegisteredPlugins.ContainsKey("Plugin2"));
    }

    [Fact]
    public void WithoutRegisteredPlugin_NonExistent_ReturnsUnchanged()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1"));

        // Act
        var updated = state.WithoutRegisteredPlugin("NonExistent");

        // Assert
        Assert.Single(updated.RegisteredPlugins);
    }

    // ============================================
    // Expanded Plugins Tests
    // ============================================

    [Fact]
    public void WithExpandedPlugin_AddsToSet()
    {
        // Arrange
        var state = new ClientToolStateData();

        // Act
        var updated = state.WithExpandedPlugin("Plugin1");

        // Assert
        Assert.NotSame(state, updated);
        Assert.Single(updated.ExpandedPlugins);
        Assert.Contains("Plugin1", updated.ExpandedPlugins);
    }

    [Fact]
    public void WithExpandedPlugin_Duplicate_NoEffect()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithExpandedPlugin("Plugin1");

        // Act
        var updated = state.WithExpandedPlugin("Plugin1");

        // Assert
        Assert.Single(updated.ExpandedPlugins);
    }

    [Fact]
    public void WithCollapsedPlugin_RemovesFromSet()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithExpandedPlugin("Plugin1")
            .WithExpandedPlugin("Plugin2");

        // Act
        var updated = state.WithCollapsedPlugin("Plugin1");

        // Assert
        Assert.Single(updated.ExpandedPlugins);
        Assert.DoesNotContain("Plugin1", updated.ExpandedPlugins);
        Assert.Contains("Plugin2", updated.ExpandedPlugins);
    }

    // ============================================
    // Hidden Tools Tests
    // ============================================

    [Fact]
    public void WithHiddenTool_AddsToSet()
    {
        // Arrange
        var state = new ClientToolStateData();

        // Act
        var updated = state.WithHiddenTool("Tool1");

        // Assert
        Assert.NotSame(state, updated);
        Assert.Single(updated.HiddenTools);
        Assert.Contains("Tool1", updated.HiddenTools);
    }

    [Fact]
    public void WithVisibleTool_RemovesFromSet()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithHiddenTool("Tool1")
            .WithHiddenTool("Tool2");

        // Act
        var updated = state.WithVisibleTool("Tool1");

        // Assert
        Assert.Single(updated.HiddenTools);
        Assert.DoesNotContain("Tool1", updated.HiddenTools);
        Assert.Contains("Tool2", updated.HiddenTools);
    }

    // ============================================
    // Context Tests
    // ============================================

    [Fact]
    public void WithContextItem_AddsItem()
    {
        // Arrange
        var state = new ClientToolStateData();
        var contextItem = new ContextItem("Test description", "test-value", "test-key");

        // Act
        var updated = state.WithContextItem(contextItem);

        // Assert
        Assert.NotSame(state, updated);
        Assert.Single(updated.Context);
        Assert.True(updated.Context.ContainsKey("test-key"));
    }

    [Fact]
    public void WithouTMetadata_RemovesItem()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithContextItem(new ContextItem("Desc1", "val1", "key1"))
            .WithContextItem(new ContextItem("Desc2", "val2", "key2"));

        // Act
        var updated = state.WithouTMetadata("key1");

        // Assert
        Assert.Single(updated.Context);
        Assert.False(updated.Context.ContainsKey("key1"));
        Assert.True(updated.Context.ContainsKey("key2"));
    }

    [Fact]
    public void ClearContext_RemovesAllItems()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithContextItem(new ContextItem("Desc1", "val1", "key1"))
            .WithContextItem(new ContextItem("Desc2", "val2", "key2"));

        // Act
        var updated = state.ClearContext();

        // Assert
        Assert.Empty(updated.Context);
    }

    // ============================================
    // State Tests
    // ============================================

    [Fact]
    public void WithState_SetsState()
    {
        // Arrange
        var state = new ClientToolStateData();
        var appState = System.Text.Json.JsonSerializer.SerializeToElement(new { foo = "bar" });

        // Act
        var updated = state.WithState(appState);

        // Assert
        Assert.NotSame(state, updated);
        Assert.NotNull(updated.State);
    }

    // ============================================
    // Pending Augmentation Tests
    // ============================================

    [Fact]
    public void WithPendingAugmentation_SetsAugmentation()
    {
        // Arrange
        var state = new ClientToolStateData();
        var augmentation = new ClientToolAugmentation
        {
            ExpandPlugins = new HashSet<string> { "Plugin1" }
        };

        // Act
        var updated = state.WithPendingAugmentation(augmentation);

        // Assert
        Assert.NotSame(state, updated);
        Assert.NotNull(updated.PendingAugmentation);
        Assert.Contains("Plugin1", updated.PendingAugmentation.ExpandPlugins!);
    }

    [Fact]
    public void ClearPendingAugmentation_RemovesAugmentation()
    {
        // Arrange
        var state = new ClientToolStateData()
            .WithPendingAugmentation(new ClientToolAugmentation { ExpandPlugins = new HashSet<string> { "Plugin1" } });

        // Act
        var updated = state.ClearPendingAugmentation();

        // Assert
        Assert.Null(updated.PendingAugmentation);
    }

    // ============================================
    // MiddlewareState Integration Tests
    // ============================================

    [Fact]
    public void MiddlewareState_ClientTool_PropertyExists()
    {
        // Arrange
        var container = new MiddlewareState();

        // Act
        var state = container.ClientTool;

        // Assert - property exists and returns null initially
        Assert.Null(state);
    }

    [Fact]
    public void MiddlewareState_WithClientTool_CreatesNewInstance()
    {
        // Arrange
        var container = new MiddlewareState();
        var testState = new ClientToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("TestPlugin"));

        // Act
        var updated = container.WithClientTool(testState);

        // Assert
        Assert.NotSame(container, updated);
        Assert.NotNull(updated.ClientTool);
        Assert.Single(updated.ClientTool.RegisteredPlugins);
    }

    [Fact]
    public void ImmutableUpdate_DoesNotModifyOriginal()
    {
        // Arrange
        var original = new ClientToolStateData();

        // Act
        var updated = original
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1"))
            .WithExpandedPlugin("Plugin1")
            .WithHiddenTool("Tool1");

        // Assert
        Assert.Empty(original.RegisteredPlugins);
        Assert.Empty(original.ExpandedPlugins);
        Assert.Empty(original.HiddenTools);

        Assert.Single(updated.RegisteredPlugins);
        Assert.Single(updated.ExpandedPlugins);
        Assert.Single(updated.HiddenTools);
    }

    // ============================================
    // Helper Methods
    // ============================================

    private static ClientToolGroupDefinition CreateTestPlugin(
        string name,
        ClientToolDefinition[]? tools = null)
    {
        return new ClientToolGroupDefinition(
            Name: name,
            Description: $"Test plugin {name}",
            Tools: tools ?? new[] { CreateTestTool($"{name}_Tool") }
        );
    }

    private static ClientToolDefinition CreateTestTool(string name)
    {
        return new ClientToolDefinition(
            Name: name,
            Description: $"Test tool {name}",
            ParametersSchema: System.Text.Json.JsonDocument.Parse("{}").RootElement
        );
    }
}

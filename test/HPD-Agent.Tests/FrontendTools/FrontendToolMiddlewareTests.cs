// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent;
using HPD.Agent.FrontendTools;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Xunit;

namespace HPD.Agent.Tests.FrontendTools;

/// <summary>
/// Unit tests for FrontendToolMiddleware.
/// Tests plugin registration, tool visibility, and tool invocation interception.
/// </summary>
public class FrontendToolMiddlewareTests
{
    // ============================================
    // BeforeMessageTurnAsync - Plugin Registration Tests
    // ============================================

    [Fact]
    public async Task BeforeMessageTurn_NoAgentRunInput_DoesNothing()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert - state unchanged
        Assert.Null(context.State.MiddlewareState.FrontendTool);
    }

    [Fact]
    public async Task BeforeMessageTurn_WithPlugins_RegistersPlugins()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            FrontendPlugins = new[]
            {
                CreateTestPlugin("Plugin1", tools: new[] { CreateTestTool("Tool1") }),
                CreateTestPlugin("Plugin2", tools: new[] { CreateTestTool("Tool2") })
            }
        };
        context.Properties["AgentRunInput"] = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Equal(2, state.RegisteredPlugins.Count);
        Assert.True(state.RegisteredPlugins.ContainsKey("Plugin1"));
        Assert.True(state.RegisteredPlugins.ContainsKey("Plugin2"));
    }

    [Fact]
    public async Task BeforeMessageTurn_WithExpandedContainers_MarksAsExpanded()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            FrontendPlugins = new[]
            {
                CreateTestPlugin("Plugin1", startCollapsed: true),
                CreateTestPlugin("Plugin2", startCollapsed: true)
            },
            ExpandedContainers = new HashSet<string> { "Plugin1" }
        };
        context.Properties["AgentRunInput"] = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Contains("Plugin1", state.ExpandedPlugins);
        Assert.DoesNotContain("Plugin2", state.ExpandedPlugins);
    }

    [Fact]
    public async Task BeforeMessageTurn_WithHiddenTools_MarksAsHidden()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            FrontendPlugins = new[]
            {
                CreateTestPlugin("Plugin1", tools: new[]
                {
                    CreateTestTool("Tool1"),
                    CreateTestTool("Tool2")
                })
            },
            HiddenTools = new HashSet<string> { "Tool1" }
        };
        context.Properties["AgentRunInput"] = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Contains("Tool1", state.HiddenTools);
        Assert.DoesNotContain("Tool2", state.HiddenTools);
    }

    [Fact]
    public async Task BeforeMessageTurn_WithContext_StoresContext()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            FrontendPlugins = new[] { CreateTestPlugin("Plugin1") },
            Context = new[]
            {
                new ContextItem("User preferences", "dark-theme", "prefs"),
                new ContextItem("Current page", "/dashboard", "page")
            }
        };
        context.Properties["AgentRunInput"] = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Equal(2, state.Context.Count);
        Assert.True(state.Context.ContainsKey("prefs"));
        Assert.True(state.Context.ContainsKey("page"));
    }

    [Fact]
    public async Task BeforeMessageTurn_WithState_StoresState()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();
        var appState = JsonSerializer.SerializeToElement(new { cartItems = 3, userId = "user123" });
        var runInput = new AgentRunInput
        {
            FrontendPlugins = new[] { CreateTestPlugin("Plugin1") },
            State = appState
        };
        context.Properties["AgentRunInput"] = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.NotNull(state.State);
    }

    [Fact]
    public async Task BeforeMessageTurn_ResetFrontendState_ClearsExistingState()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        // First call - register plugins
        var context1 = CreateContext();
        var runInput1 = new AgentRunInput
        {
            FrontendPlugins = new[] { CreateTestPlugin("OldPlugin") }
        };
        context1.Properties["AgentRunInput"] = runInput1;
        await middleware.BeforeMessageTurnAsync(context1, CancellationToken.None);

        // Second call with reset
        var context2 = CreateContext(context1.State);
        var runInput2 = new AgentRunInput
        {
            FrontendPlugins = new[] { CreateTestPlugin("NewPlugin") },
            ResetFrontendState = true
        };
        context2.Properties["AgentRunInput"] = runInput2;

        // Act
        await middleware.BeforeMessageTurnAsync(context2, CancellationToken.None);

        // Assert - only new plugin registered
        var state = context2.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Single(state.RegisteredPlugins);
        Assert.True(state.RegisteredPlugins.ContainsKey("NewPlugin"));
        Assert.False(state.RegisteredPlugins.ContainsKey("OldPlugin"));
    }

    // ============================================
    // BeforeIterationAsync - Tool Visibility Tests
    // ============================================

    [Fact]
    public async Task BeforeIteration_NoState_DoesNothing()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - no exception, tools unchanged
        Assert.Empty(context.Options.Tools);
    }

    [Fact]
    public async Task BeforeIteration_WithExpandedPlugin_AddsToolsToOptions()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        // Set up state with registered and expanded plugin
        var state = new FrontendToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1", tools: new[]
            {
                CreateTestTool("Tool1"),
                CreateTestTool("Tool2")
            }))
            .WithExpandedPlugin("Plugin1");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithFrontendTool(state)
        };

        var context = CreateContext(agentState);
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - tools should be added
        Assert.True(context.Options.Tools.Count >= 2);
    }

    [Fact]
    public async Task BeforeIteration_WithHiddenTool_ExcludesTool()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        var state = new FrontendToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1", tools: new[]
            {
                CreateTestTool("Tool1"),
                CreateTestTool("Tool2")
            }))
            .WithExpandedPlugin("Plugin1")
            .WithHiddenTool("Tool1");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithFrontendTool(state)
        };

        var context = CreateContext(agentState);
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - Tool1 should be excluded
        var toolNames = context.Options.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.DoesNotContain("Tool1", toolNames);
        Assert.Contains("Tool2", toolNames);
    }

    // ============================================
    // Plugin Validation Tests
    // ============================================

    [Fact]
    public async Task BeforeMessageTurn_CollapsedPluginWithoutDescription_ThrowsValidationError()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware(new FrontendToolConfig { ValidateSchemaOnRegistration = true });
        var context = CreateContext();

        // Create plugin with startCollapsed=true but no description
        var plugin = new FrontendPluginDefinition(
            Name: "BadPlugin",
            Description: null, // No description!
            Tools: new[] { CreateTestTool("Tool1") },
            StartCollapsed: true
        );

        var runInput = new AgentRunInput { FrontendPlugins = new[] { plugin } };
        context.Properties["AgentRunInput"] = runInput;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await middleware.BeforeMessageTurnAsync(context, CancellationToken.None));
    }

    // ============================================
    // FrontendToolAugmentation Tests
    // ============================================

    [Fact]
    public async Task BeforeIteration_WithPendingAugmentation_AppliesChanges()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        var augmentation = new FrontendToolAugmentation
        {
            ExpandPlugins = new HashSet<string> { "Plugin2" },
            HideTools = new HashSet<string> { "Tool1" }
        };

        var state = new FrontendToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1", tools: new[] { CreateTestTool("Tool1") }))
            .WithRegisteredPlugin(CreateTestPlugin("Plugin2", tools: new[] { CreateTestTool("Tool2") }))
            .WithExpandedPlugin("Plugin1")
            .WithPendingAugmentation(augmentation);

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithFrontendTool(state)
        };

        var context = CreateContext(agentState);
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        var updatedState = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(updatedState);

        // Augmentation should have been applied
        Assert.Contains("Plugin2", updatedState.ExpandedPlugins);
        Assert.Contains("Tool1", updatedState.HiddenTools);

        // Augmentation should be cleared
        Assert.Null(updatedState.PendingAugmentation);
    }

    // ============================================
    // Frontend Skill Tests
    // ============================================

    [Fact]
    public async Task BeforeMessageTurn_WithSkills_RegistersSkills()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();

        var skill = new FrontendSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            Instructions: "1. Verify cart\n2. Get payment\n3. Confirm order",
            References: new[] { new FrontendSkillReference("AddToCart") }
        );

        var plugin = new FrontendPluginDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput { FrontendPlugins = new[] { plugin } };
        context.Properties["AgentRunInput"] = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Single(state.RegisteredPlugins);
        Assert.NotNull(state.RegisteredPlugins["ECommerce"].Skills);
        Assert.Single(state.RegisteredPlugins["ECommerce"].Skills!);
        Assert.Equal("CheckoutWorkflow", state.RegisteredPlugins["ECommerce"].Skills![0].Name);
    }

    [Fact]
    public async Task BeforeIteration_WithSkills_AddsSkillsAsAIFunctions()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        var skill = new FrontendSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            Instructions: "1. Verify cart\n2. Get payment\n3. Confirm order"
        );

        var plugin = new FrontendPluginDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var state = new FrontendToolStateData()
            .WithRegisteredPlugin(plugin)
            .WithExpandedPlugin("ECommerce");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithFrontendTool(state)
        };

        var context = CreateContext(agentState);
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - both tool and skill should be added as AIFunctions
        var functionNames = context.Options.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("AddToCart", functionNames);
        Assert.Contains("CheckoutWorkflow", functionNames);
    }

    [Fact]
    public async Task BeforeIteration_SkillWithDocuments_IncludesDocumentInfoInDescription()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        var skill = new FrontendSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            Instructions: "Follow these steps for checkout",
            Documents: new[]
            {
                new FrontendSkillDocument("checkout-guide", "Detailed checkout documentation", Content: "# Checkout Guide\n..."),
                new FrontendSkillDocument("payment-api", "Payment API reference", Content: "# Payment API\n...")
            }
        );

        var plugin = new FrontendPluginDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var state = new FrontendToolStateData()
            .WithRegisteredPlugin(plugin)
            .WithExpandedPlugin("ECommerce");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithFrontendTool(state)
        };

        var context = CreateContext(agentState);
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - skill should be added and mention documents
        var skillFunction = context.Options.Tools.OfType<AIFunction>()
            .FirstOrDefault(f => f.Name == "CheckoutWorkflow");

        Assert.NotNull(skillFunction);
        // Skill description should be present (documents are referenced in activation)
        Assert.Equal("Guides through checkout process", skillFunction.Description);
    }

    [Fact]
    public async Task BeforeMessageTurn_SkillWithInvalidReference_ThrowsValidationError()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();

        // Skill references a tool that doesn't exist in the plugin
        var skill = new FrontendSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            Instructions: "Follow these steps",
            References: new[] { new FrontendSkillReference("NonExistentTool") }
        );

        var plugin = new FrontendPluginDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput { FrontendPlugins = new[] { plugin } };
        context.Properties["AgentRunInput"] = runInput;

        // Act & Assert - should throw because skill references non-existent tool
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await middleware.BeforeMessageTurnAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task BeforeMessageTurn_SkillWithCrossPluginReference_ValidatesCorrectly()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();

        // Plugin A has a skill that references a tool in Plugin B
        var skillWithCrossRef = new FrontendSkillDefinition(
            Name: "FullOrderWorkflow",
            Description: "Complete order workflow",
            Instructions: "Use tools from both plugins",
            References: new[]
            {
                new FrontendSkillReference("AddToCart"),  // Local tool
                new FrontendSkillReference("ProcessPayment", "PaymentPlugin")  // Cross-plugin ref
            }
        );

        var ecommercePlugin = new FrontendPluginDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skillWithCrossRef },
            StartCollapsed: false
        );

        var paymentPlugin = new FrontendPluginDefinition(
            Name: "PaymentPlugin",
            Description: "Payment tools",
            Tools: new[] { CreateTestTool("ProcessPayment") },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput
        {
            FrontendPlugins = new[] { ecommercePlugin, paymentPlugin }
        };
        context.Properties["AgentRunInput"] = runInput;

        // Act - should succeed because PaymentPlugin.ProcessPayment exists
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.FrontendTool;
        Assert.NotNull(state);
        Assert.Equal(2, state.RegisteredPlugins.Count);
    }

    [Fact]
    public async Task BeforeMessageTurn_SkillWithInvalidCrossPluginReference_ThrowsValidationError()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();
        var context = CreateContext();

        // Skill references a tool in a plugin that doesn't exist
        var skillWithBadRef = new FrontendSkillDefinition(
            Name: "BadWorkflow",
            Description: "Workflow with invalid reference",
            Instructions: "This will fail",
            References: new[]
            {
                new FrontendSkillReference("SomeTool", "NonExistentPlugin")
            }
        );

        var plugin = new FrontendPluginDefinition(
            Name: "MyPlugin",
            Description: "My tools",
            Tools: new[] { CreateTestTool("LocalTool") },
            Skills: new[] { skillWithBadRef },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput { FrontendPlugins = new[] { plugin } };
        context.Properties["AgentRunInput"] = runInput;

        // Act & Assert - should throw because referenced plugin doesn't exist
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await middleware.BeforeMessageTurnAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task BeforeIteration_CollapsedPlugin_HasContainerAndSkills()
    {
        // Arrange
        var middleware = new FrontendToolMiddleware();

        // When a plugin is collapsed:
        // - Container function (Frontend_ECommerce) is added
        // - Collapsed tools (with ParentPlugin metadata) are added for ToolCollapsingMiddleware
        // - Skills are ALWAYS added (they're entry points)
        var skill = new FrontendSkillDefinition(
            Name: "QuickCheckout",
            Description: "Fast checkout process",
            Instructions: "Use this for quick orders"
        );

        var plugin = new FrontendPluginDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart"), CreateTestTool("RemoveFromCart") },
            Skills: new[] { skill },
            StartCollapsed: true
        );

        // Plugin is NOT expanded
        var state = new FrontendToolStateData()
            .WithRegisteredPlugin(plugin);
        // Note: NOT calling .WithExpandedPlugin()

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithFrontendTool(state)
        };

        var context = CreateContext(agentState);
        context.Options = new ChatOptions { Tools = new List<AITool>() };

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - container and skill should be visible
        var functions = context.Options.Tools.OfType<AIFunction>().ToList();
        var functionNames = functions.Select(f => f.Name).ToList();

        // Container function exists
        Assert.Contains("Frontend_ECommerce", functionNames);

        // Skill is visible (skills are always available as entry points)
        Assert.Contains("QuickCheckout", functionNames);

        // Tools exist (with ParentPlugin metadata for ToolCollapsingMiddleware to filter)
        Assert.Contains("AddToCart", functionNames);
        Assert.Contains("RemoveFromCart", functionNames);

        // Verify tools have ParentPlugin metadata (for Collapsing middleware)
        var addToCart = functions.First(f => f.Name == "AddToCart");
        Assert.True(addToCart.AdditionalProperties?.ContainsKey("ParentPlugin") == true);
        Assert.Equal("Frontend_ECommerce", addToCart.AdditionalProperties!["ParentPlugin"]);
    }

    [Fact]
    public void SkillDefinition_Validation_RequiresName()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new FrontendSkillDefinition(
                Name: "",
                Description: "Description",
                Instructions: "Instructions"
            ).Validate());
    }

    [Fact]
    public void SkillDefinition_Validation_RequiresDescription()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new FrontendSkillDefinition(
                Name: "Skill",
                Description: "",
                Instructions: "Instructions"
            ).Validate());
    }

    [Fact]
    public void SkillDefinition_Validation_RequiresInstructions()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new FrontendSkillDefinition(
                Name: "Skill",
                Description: "Description",
                Instructions: ""
            ).Validate());
    }

    [Fact]
    public void SkillDocument_Validation_RequiresContentOrUrl()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new FrontendSkillDocument(
                DocumentId: "doc1",
                Description: "A document",
                Content: null,
                Url: null
            ).Validate());
    }

    [Fact]
    public void SkillDocument_Validation_AcceptsContent()
    {
        // Arrange
        var doc = new FrontendSkillDocument(
            DocumentId: "doc1",
            Description: "A document",
            Content: "# Document content"
        );

        // Act & Assert - should not throw
        doc.Validate();
    }

    [Fact]
    public void SkillDocument_Validation_AcceptsUrl()
    {
        // Arrange
        var doc = new FrontendSkillDocument(
            DocumentId: "doc1",
            Description: "A document",
            Url: "https://example.com/doc.md"
        );

        // Act & Assert - should not throw
        doc.Validate();
    }

    // ============================================
    // Helper Methods
    // ============================================

    private static AgentMiddlewareContext CreateContext(AgentLoopState? state = null)
    {
        var agentState = state ?? CreateEmptyState();
        var context = new AgentMiddlewareContext
        {
            AgentName = "TestAgent",
            ConversationId = "test-conv-id",
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            ToolCalls = Array.Empty<FunctionCallContent>(),
            Iteration = 0,
            CancellationToken = CancellationToken.None
        };
        context.SetOriginalState(agentState);
        return context;
    }

    private static AgentLoopState CreateEmptyState()
    {
        return AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: Guid.NewGuid().ToString(),
            conversationId: "test-conv-id",
            agentName: "TestAgent");
    }

    private static FrontendPluginDefinition CreateTestPlugin(
        string name,
        FrontendToolDefinition[]? tools = null,
        bool startCollapsed = false)
    {
        return new FrontendPluginDefinition(
            Name: name,
            Description: $"Test plugin {name}",
            Tools: tools ?? new[] { CreateTestTool($"{name}_DefaultTool") },
            StartCollapsed: startCollapsed
        );
    }

    private static FrontendToolDefinition CreateTestTool(string name)
    {
        return new FrontendToolDefinition(
            Name: name,
            Description: $"Test tool {name}",
            ParametersSchema: JsonDocument.Parse("{}").RootElement
        );
    }
}

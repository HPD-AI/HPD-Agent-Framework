// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent;
using HPD.Agent.ClientTools;
using HPD.Agent.Middleware;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Xunit;

namespace HPD.Agent.Tests.ClientTools;

/// <summary>
/// Unit tests for ClientToolMiddleware.
/// Tests plugin registration, tool visibility, and tool invocation interception.
/// </summary>
public class ClientToolMiddlewareTests
{
    // ============================================
    // BeforeMessageTurnAsync - Plugin Registration Tests
    // ============================================

    [Fact]
    public async Task BeforeMessageTurn_NoAgentRunInput_DoesNothing()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateContext();

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert - state unchanged
        Assert.Null(context.State.MiddlewareState.ClientTool);
    }

    [Fact]
    public async Task BeforeMessageTurn_WithToolss_RegistersPlugins()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            ClientToolGroups = new[]
            {
                CreateTestPlugin("Plugin1", tools: new[] { CreateTestTool("Tool1") }),
                CreateTestPlugin("Plugin2", tools: new[] { CreateTestTool("Tool2") })
            }
        };
        context.RunOptions.ClientToolInput = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Equal(2, state.RegisteredToolGroups.Count);
        Assert.True(state.RegisteredToolGroups.ContainsKey("Plugin1"));
        Assert.True(state.RegisteredToolGroups.ContainsKey("Plugin2"));
    }

    [Fact]
    public async Task BeforeMessageTurn_WithExpandedContainers_MarksAsExpanded()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            ClientToolGroups = new[]
            {
                CreateTestPlugin("Plugin1", startCollapsed: true),
                CreateTestPlugin("Plugin2", startCollapsed: true)
            },
            ExpandedContainers = new HashSet<string> { "Plugin1" }
        };
        context.RunOptions.ClientToolInput = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Contains("Plugin1", state.ExpandedPlugins);
        Assert.DoesNotContain("Plugin2", state.ExpandedPlugins);
    }

    [Fact]
    public async Task BeforeMessageTurn_WithHiddenTools_MarksAsHidden()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            ClientToolGroups = new[]
            {
                CreateTestPlugin("Plugin1", tools: new[]
                {
                    CreateTestTool("Tool1"),
                    CreateTestTool("Tool2")
                })
            },
            HiddenTools = new HashSet<string> { "Tool1" }
        };
        context.RunOptions.ClientToolInput = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Contains("Tool1", state.HiddenTools);
        Assert.DoesNotContain("Tool2", state.HiddenTools);
    }

    [Fact]
    public async Task BeforeMessageTurn_WithContext_StoresContext()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateContext();
        var runInput = new AgentRunInput
        {
            ClientToolGroups = new[] { CreateTestPlugin("Plugin1") },
            Context = new[]
            {
                new ContextItem("User preferences", "dark-theme", "prefs"),
                new ContextItem("Current page", "/dashboard", "page")
            }
        };
        context.RunOptions.ClientToolInput = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Equal(2, state.Context.Count);
        Assert.True(state.Context.ContainsKey("prefs"));
        Assert.True(state.Context.ContainsKey("page"));
    }

    [Fact]
    public async Task BeforeMessageTurn_WithState_StoresState()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateContext();
        var appState = JsonSerializer.SerializeToElement(new { cartItems = 3, userId = "user123" });
        var runInput = new AgentRunInput
        {
            ClientToolGroups = new[] { CreateTestPlugin("Plugin1") },
            State = appState
        };
        context.RunOptions.ClientToolInput = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.NotNull(state.State);
    }

    [Fact]
    public async Task BeforeMessageTurn_ResetClientState_ClearsExistingState()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();

        // First call - register plugins
        var context1 = CreateContext();
        var runInput1 = new AgentRunInput
        {
            ClientToolGroups = new[] { CreateTestPlugin("OldPlugin") }
        };
        context1.RunOptions.ClientToolInput = runInput1;
        await middleware.BeforeMessageTurnAsync(context1, CancellationToken.None);

        // Second call with reset
        var context2 = CreateContext(context1.State);
        var runInput2 = new AgentRunInput
        {
            ClientToolGroups = new[] { CreateTestPlugin("NewPlugin") },
            ResetClientState = true
        };
        context2.RunOptions.ClientToolInput = runInput2;

        // Act
        await middleware.BeforeMessageTurnAsync(context2, CancellationToken.None);

        // Assert - only new plugin registered
        var state = context2.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Single(state.RegisteredToolGroups);
        Assert.True(state.RegisteredToolGroups.ContainsKey("NewPlugin"));
        Assert.False(state.RegisteredToolGroups.ContainsKey("OldPlugin"));
    }

    // ============================================
    // BeforeIterationAsync - Tool Visibility Tests
    // ============================================

    [Fact]
    public async Task BeforeIteration_NoState_DoesNothing()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateIterationContext();

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - no exception, tools unchanged
        Assert.Empty(context.Options.Tools);
    }

    [Fact]
    public async Task BeforeIteration_WithExpandedPlugin_AddsToolsToOptions()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();

        // Set up state with registered and expanded plugin
        var state = new ClientToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1", tools: new[]
            {
                CreateTestTool("Tool1"),
                CreateTestTool("Tool2")
            }))
            .WithExpandedPlugin("Plugin1");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithClientTool(state)
        };

        var context = CreateIterationContext(agentState);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - tools should be added
        Assert.True(context.Options.Tools.Count >= 2);
    }

    [Fact]
    public async Task BeforeIteration_WithHiddenTool_ExcludesTool()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();

        var state = new ClientToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1", tools: new[]
            {
                CreateTestTool("Tool1"),
                CreateTestTool("Tool2")
            }))
            .WithExpandedPlugin("Plugin1")
            .WithHiddenTool("Tool1");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithClientTool(state)
        };

        var context = CreateIterationContext(agentState);

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
        var middleware = new ClientToolMiddleware(new ClientToolConfig { ValidateSchemaOnRegistration = true });
        var context = CreateBeforeMessageTurnContext();

        // Create plugin with startCollapsed=true but no description
        var plugin = new ClientToolGroupDefinition(
            Name: "BadPlugin",
            Description: null, // No description!
            Tools: new[] { CreateTestTool("Tool1") },
            StartCollapsed: true
        );

        var runInput = new AgentRunInput { ClientToolGroups = new[] { plugin } };
        context.RunOptions.ClientToolInput = runInput;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await middleware.BeforeMessageTurnAsync(context, CancellationToken.None));
    }

    // ============================================
    // ClientToolAugmentation Tests
    // ============================================

    [Fact]
    public async Task BeforeIteration_WithPendingAugmentation_AppliesChanges()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();

        var augmentation = new ClientToolAugmentation
        {
            ExpandPlugins = new HashSet<string> { "Plugin2" },
            HideTools = new HashSet<string> { "Tool1" }
        };

        var state = new ClientToolStateData()
            .WithRegisteredPlugin(CreateTestPlugin("Plugin1", tools: new[] { CreateTestTool("Tool1") }))
            .WithRegisteredPlugin(CreateTestPlugin("Plugin2", tools: new[] { CreateTestTool("Tool2") }))
            .WithExpandedPlugin("Plugin1")
            .WithPendingAugmentation(augmentation);

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithClientTool(state)
        };

        var context = CreateIterationContext(agentState);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert
        var updatedState = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(updatedState);

        // Augmentation should have been applied
        Assert.Contains("Plugin2", updatedState.ExpandedPlugins);
        Assert.Contains("Tool1", updatedState.HiddenTools);

        // Augmentation should be cleared
        Assert.Null(updatedState.PendingAugmentation);
    }

    // ============================================
    // Client Skill Tests
    // ============================================

    [Fact]
    public async Task BeforeMessageTurn_WithSkills_RegistersSkills()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateBeforeMessageTurnContext();

        var skill = new ClientSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            SystemPrompt: "1. Verify cart\n2. Get payment\n3. Confirm order",
            References: new[] { new ClientSkillReference("AddToCart") }
        );

        var plugin = new ClientToolGroupDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput { ClientToolGroups = new[] { plugin } };
        context.RunOptions.ClientToolInput = runInput;

        // Act
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Single(state.RegisteredToolGroups);
        Assert.NotNull(state.RegisteredToolGroups["ECommerce"].Skills);
        Assert.Single(state.RegisteredToolGroups["ECommerce"].Skills!);
        Assert.Equal("CheckoutWorkflow", state.RegisteredToolGroups["ECommerce"].Skills![0].Name);
    }

    [Fact]
    public async Task BeforeIteration_WithSkills_AddsSkillsAsAIFunctions()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();

        var skill = new ClientSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            SystemPrompt: "1. Verify cart\n2. Get payment\n3. Confirm order"
        );

        var plugin = new ClientToolGroupDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var state = new ClientToolStateData()
            .WithRegisteredPlugin(plugin)
            .WithExpandedPlugin("ECommerce");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithClientTool(state)
        };

        var context = CreateIterationContext(agentState);

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
        var middleware = new ClientToolMiddleware();

        var skill = new ClientSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            SystemPrompt: "Follow these steps for checkout",
            Documents: new[]
            {
                new ClientSkillDocument("checkout-guide", "Detailed checkout documentation", Content: "# Checkout Guide\n..."),
                new ClientSkillDocument("payment-api", "Payment API reference", Content: "# Payment API\n...")
            }
        );

        var plugin = new ClientToolGroupDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var state = new ClientToolStateData()
            .WithRegisteredPlugin(plugin)
            .WithExpandedPlugin("ECommerce");

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithClientTool(state)
        };

        var context = CreateIterationContext(agentState);

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
        var middleware = new ClientToolMiddleware();
        var context = CreateBeforeMessageTurnContext();

        // Skill references a tool that doesn't exist in the plugin
        var skill = new ClientSkillDefinition(
            Name: "CheckoutWorkflow",
            Description: "Guides through checkout process",
            SystemPrompt: "Follow these steps",
            References: new[] { new ClientSkillReference("NonExistentTool") }
        );

        var plugin = new ClientToolGroupDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skill },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput { ClientToolGroups = new[] { plugin } };
        context.RunOptions.ClientToolInput = runInput;

        // Act & Assert - should throw because skill references non-existent tool
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await middleware.BeforeMessageTurnAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task BeforeMessageTurn_SkillWithCrossPluginReference_ValidatesCorrectly()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateBeforeMessageTurnContext();

        // Plugin A has a skill that references a tool in Plugin B
        var skillWithCrossRef = new ClientSkillDefinition(
            Name: "FullOrderWorkflow",
            Description: "Complete order workflow",
            SystemPrompt: "Use tools from both plugins",
            References: new[]
            {
                new ClientSkillReference("AddToCart"),  // Local tool
                new ClientSkillReference("ProcessPayment", "PaymentPlugin")  // Cross-plugin ref
            }
        );

        var ecommercePlugin = new ClientToolGroupDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart") },
            Skills: new[] { skillWithCrossRef },
            StartCollapsed: false
        );

        var paymentPlugin = new ClientToolGroupDefinition(
            Name: "PaymentPlugin",
            Description: "Payment tools",
            Tools: new[] { CreateTestTool("ProcessPayment") },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput
        {
            ClientToolGroups = new[] { ecommercePlugin, paymentPlugin }
        };
        context.RunOptions.ClientToolInput = runInput;

        // Act - should succeed because PaymentPlugin.ProcessPayment exists
        await middleware.BeforeMessageTurnAsync(context, CancellationToken.None);

        // Assert
        var state = context.State.MiddlewareState.ClientTool;
        Assert.NotNull(state);
        Assert.Equal(2, state.RegisteredToolGroups.Count);
    }

    [Fact]
    public async Task BeforeMessageTurn_SkillWithInvalidCrossPluginReference_ThrowsValidationError()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();
        var context = CreateBeforeMessageTurnContext();

        // Skill references a tool in a plugin that doesn't exist
        var skillWithBadRef = new ClientSkillDefinition(
            Name: "BadWorkflow",
            Description: "Workflow with invalid reference",
            SystemPrompt: "This will fail",
            References: new[]
            {
                new ClientSkillReference("SomeTool", "NonExistentPlugin")
            }
        );

        var plugin = new ClientToolGroupDefinition(
            Name: "MyPlugin",
            Description: "My tools",
            Tools: new[] { CreateTestTool("LocalTool") },
            Skills: new[] { skillWithBadRef },
            StartCollapsed: false
        );

        var runInput = new AgentRunInput { ClientToolGroups = new[] { plugin } };
        context.RunOptions.ClientToolInput = runInput;

        // Act & Assert - should throw because referenced plugin doesn't exist
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await middleware.BeforeMessageTurnAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task BeforeIteration_CollapsedPlugin_HasContainerAndSkills()
    {
        // Arrange
        var middleware = new ClientToolMiddleware();

        // When a plugin is collapsed:
        // - Container function (Client_ECommerce) is added
        // - Collapsed tools (with ParentPlugin metadata) are added for ToolCollapsingMiddleware
        // - Skills are ALWAYS added (they're entry points)
        var skill = new ClientSkillDefinition(
            Name: "QuickCheckout",
            Description: "Fast checkout process",
            SystemPrompt: "Use this for quick orders"
        );

        var plugin = new ClientToolGroupDefinition(
            Name: "ECommerce",
            Description: "E-commerce tools",
            Tools: new[] { CreateTestTool("AddToCart"), CreateTestTool("RemoveFromCart") },
            Skills: new[] { skill },
            StartCollapsed: true
        );

        // Plugin is NOT expanded
        var state = new ClientToolStateData()
            .WithRegisteredPlugin(plugin);
        // Note: NOT calling .WithExpandedPlugin()

        var agentState = CreateEmptyState() with
        {
            MiddlewareState = new MiddlewareState().WithClientTool(state)
        };

        var context = CreateIterationContext(agentState);

        // Act
        await middleware.BeforeIterationAsync(context, CancellationToken.None);

        // Assert - container and skill should be visible
        var functions = context.Options.Tools.OfType<AIFunction>().ToList();
        var functionNames = functions.Select(f => f.Name).ToList();

        // Container function exists
        Assert.Contains("Client_ECommerce", functionNames);

        // Skill is visible (skills are always available as entry points)
        Assert.Contains("QuickCheckout", functionNames);

        // Tools exist (with ParentPlugin metadata for ToolCollapsingMiddleware to filter)
        Assert.Contains("AddToCart", functionNames);
        Assert.Contains("RemoveFromCart", functionNames);

        // Verify tools have ParentPlugin metadata (for Collapsing middleware)
        var addToCart = functions.First(f => f.Name == "AddToCart");
        Assert.True(addToCart.AdditionalProperties?.ContainsKey("ParentPlugin") == true);
        Assert.Equal("Client_ECommerce", addToCart.AdditionalProperties!["ParentPlugin"]);
    }

    [Fact]
    public void SkillDefinition_Validation_RequiresName()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new ClientSkillDefinition(
                Name: "",
                Description: "Description",
                SystemPrompt: "Instructions"
            ).Validate());
    }

    [Fact]
    public void SkillDefinition_Validation_RequiresDescription()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new ClientSkillDefinition(
                Name: "Skill",
                Description: "",
                SystemPrompt: "Instructions"
            ).Validate());
    }

    [Fact]
    public void SkillDefinition_Validation_RequiresFunctionResultOrSystemPrompt()
    {
        // Act & Assert - at least one of FunctionResult or SystemPrompt must be provided
        Assert.Throws<ArgumentException>(() =>
            new ClientSkillDefinition(
                Name: "Skill",
                Description: "Description",
                FunctionResult: null,
                SystemPrompt: null
            ).Validate());
    }

    [Fact]
    public void SkillDocument_Validation_RequiresContentOrUrl()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new ClientSkillDocument(
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
        var doc = new ClientSkillDocument(
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
        var doc = new ClientSkillDocument(
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

    private static BeforeMessageTurnContext CreateContext(AgentLoopState? state = null)
    {
        var agentState = state ?? CreateEmptyState();

        var agentContext = new AgentContext(
            "TestAgent",
            "test-conv-id",
            agentState,
            new BidirectionalEventCoordinator(),
            CancellationToken.None);

        var userMessage = new ChatMessage(ChatRole.User, "test");
        return agentContext.AsBeforeMessageTurn(
            userMessage,
            new List<ChatMessage>(),
            new AgentRunOptions());
    }

    private static BeforeIterationContext CreateIterationContext(AgentLoopState? state = null)
    {
        var agentState = state ?? CreateEmptyState();

        var agentContext = new AgentContext(
            "TestAgent",
            "test-conv-id",
            agentState,
            new BidirectionalEventCoordinator(),
            CancellationToken.None);

        return agentContext.AsBeforeIteration(
            iteration: 0,
            messages: new List<ChatMessage>(),
            options: new ChatOptions { Tools = new List<AITool>() },
            runOptions: new AgentRunOptions());
    }

    private static AgentLoopState CreateEmptyState()
    {
        return AgentLoopState.Initial(
            messages: new List<ChatMessage>(),
            runId: Guid.NewGuid().ToString(),
            conversationId: "test-conv-id",
            agentName: "TestAgent");
    }

    private static ClientToolGroupDefinition CreateTestPlugin(
        string name,
        ClientToolDefinition[]? tools = null,
        bool startCollapsed = false)
    {
        return new ClientToolGroupDefinition(
            Name: name,
            Description: $"Test plugin {name}",
            Tools: tools ?? new[] { CreateTestTool($"{name}_DefaultTool") },
            StartCollapsed: startCollapsed
        );
    }

    private static ClientToolDefinition CreateTestTool(string name)
    {
        return new ClientToolDefinition(
            Name: name,
            Description: $"Test tool {name}",
            ParametersSchema: JsonDocument.Parse("{}").RootElement
        );
    }

    private static AgentContext CreateAgentContext(AgentLoopState? state = null)
    {
        var agentState = state ?? AgentLoopState.Initial(
            messages: Array.Empty<ChatMessage>(),
            runId: "test-run",
            conversationId: "test-conversation",
            agentName: "TestAgent");

        return new AgentContext(
            "TestAgent",
            "test-conversation",
            agentState,
            new BidirectionalEventCoordinator(),
            CancellationToken.None);
    }

    private static BeforeToolExecutionContext CreateBeforeToolExecutionContext(
        ChatMessage? response = null,
        List<FunctionCallContent>? toolCalls = null,
        AgentLoopState? state = null)
    {
        var agentContext = CreateAgentContext(state);
        response ??= new ChatMessage(ChatRole.Assistant, []);
        toolCalls ??= new List<FunctionCallContent>();
        return agentContext.AsBeforeToolExecution(response, toolCalls, new AgentRunOptions());
    }

    private static AfterMessageTurnContext CreateAfterMessageTurnContext(
        AgentLoopState? state = null,
        List<ChatMessage>? turnHistory = null)
    {
        var agentContext = CreateAgentContext(state);
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test response"));
        turnHistory ??= new List<ChatMessage>();
        return agentContext.AsAfterMessageTurn(finalResponse, turnHistory, new AgentRunOptions());
    }


    private static BeforeMessageTurnContext CreateBeforeMessageTurnContext(AgentLoopState? state = null)
    {
        var agentContext = CreateAgentContext(state);
        var userMessage = new ChatMessage(ChatRole.User, "Test message");
        var conversationHistory = new List<ChatMessage>();
        var runOptions = new AgentRunOptions();
        return agentContext.AsBeforeMessageTurn(userMessage, conversationHistory, runOptions);
    }

}

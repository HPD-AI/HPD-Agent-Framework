using HPD_Agent.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;
using FluentAssertions;
using HPD.Agent.Providers;

namespace HPD_Agent.Tests.Phase0_Characterization;

/// <summary>
/// Phase 0: Permission System Characterization Tests
///
/// These tests capture the CURRENT behavior of the permission system before refactoring.
/// They verify that permission requests, approvals, and denials work as expected.
/// </summary>
public class PermissionTests : AgentTestBase
{
    /// <summary>
    /// Helper to create agent with permission filtering enabled.
    /// </summary>
    private Agent CreateAgentWithPermissions(FakeChatClient client, params AIFunction[] tools)
    {
        var config = DefaultConfig();
        config.Provider ??= new ProviderConfig();
        config.Provider.ProviderKey = "test";
        config.Provider.ModelName = "test-model";
        config.Provider.DefaultChatOptions ??= new ChatOptions();
        config.Provider.DefaultChatOptions.Tools = tools.Cast<AITool>().ToList();

        var builder = new AgentBuilder(config, new TestProviderRegistry(client));
        builder.WithPermissions(); // Enable permission filtering
        return builder.Build();
    }

    /// <summary>
    /// Test 1: Permission denied blocks tool execution.
    /// Verifies that when permission is denied, the tool is not executed and agent receives denial message.
    /// </summary>
    [Fact]
    public async Task CurrentBehavior_PermissionDenied_BlocksToolExecution()
    {
        // Arrange
        var fakeLLM = new FakeChatClient();

        // LLM requests a tool that requires permission
        fakeLLM.EnqueueToolCall(
            functionName: "SensitiveTool",
            callId: "call_1",
            args: new Dictionary<string, object?> { ["action"] = "delete" });

        // LLM responds after permission denial
        fakeLLM.EnqueueTextResponse("I understand the permission was denied.");

        // Create a tool that requires permission using HPDAIFunctionFactory
        var options = new HPDAIFunctionFactoryOptions
        {
            Name = "SensitiveTool",
            Description = "A sensitive tool requiring permission",
            RequiresPermission = true
        };

        var sensitiveToolWithPermission = HPDAIFunctionFactory.Create(
            async (args, ct) => "Executed: delete",
            options);

        var agent = CreateAgentWithPermissions(fakeLLM, sensitiveToolWithPermission);
        var messages = CreateSimpleConversation("Use the sensitive tool");

        // Set up mock permission handler to deny permission
        var eventStream = agent.RunAgenticLoopAsync(messages, cancellationToken: TestCancellationToken);

        using var permissionHandler = new MockPermissionHandler(agent, eventStream)
            .AutoDenyAll("User denied permission");

        // Act - Wait for handler to complete (it consumes the event stream)
        await permissionHandler.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        // Get captured events from handler
        var capturedEvents = permissionHandler.CapturedEvents;

        // Assert - Permission request should be captured
        permissionHandler.CapturedRequests.Should().ContainSingle("permission should be requested once");

        var permissionRequest = permissionHandler.CapturedRequests[0];
        permissionRequest.FunctionName.Should().Be("SensitiveTool", "correct function should request permission");

        // Permission denied event should be emitted
        var permissionDenied = capturedEvents.OfType<InternalPermissionDeniedEvent>().ToList();
        permissionDenied.Should().ContainSingle("permission denial should be recorded");

        // CURRENT BEHAVIOR: Check what actually happens with tool execution
        var toolResults = capturedEvents.OfType<InternalToolCallResultEvent>().ToList();

        // The permission system may or may not generate a tool result event
        // Document what actually happens
        if (toolResults.Any())
        {
            // If there's a tool result, it should indicate denial or the tool was blocked
            var result = toolResults[0].Result;
            // Accept either denial message or empty result (both indicate blocking)
            (string.IsNullOrEmpty(result) || result.Contains("denied", StringComparison.OrdinalIgnoreCase))
                .Should().BeTrue($"tool result should be empty or contain denial message, but was: '{result}'");
        }

        // Final response should exist
        var textDeltas = capturedEvents.OfType<InternalTextDeltaEvent>().ToList();
        var finalText = string.Concat(textDeltas.Select(e => e.Text));
        finalText.Should().NotBeEmpty("agent should respond after permission denial");
    }

    /// <summary>
    /// Test 2: Permission approved allows tool execution.
    /// Verifies that when permission is approved, the tool executes successfully.
    /// </summary>
    [Fact]
    public async Task CurrentBehavior_PermissionApproved_AllowsToolExecution()
    {
        // Arrange
        var fakeLLM = new FakeChatClient();

        // LLM requests a tool that requires permission
        fakeLLM.EnqueueToolCall(
            functionName: "SensitiveTool",
            callId: "call_1",
            args: new Dictionary<string, object?> { ["action"] = "read" });

        // LLM responds after successful tool execution
        fakeLLM.EnqueueTextResponse("The tool executed successfully.");

        // Create a tool that requires permission
        var options = new HPDAIFunctionFactoryOptions
        {
            Name = "SensitiveTool",
            Description = "A sensitive tool requiring permission",
            RequiresPermission = true
        };

        var sensitiveToolWithPermission = HPDAIFunctionFactory.Create(
            async (args, ct) => "Successfully read data",
            options);

        var agent = CreateAgentWithPermissions(fakeLLM, sensitiveToolWithPermission);
        var messages = CreateSimpleConversation("Use the sensitive tool to read data");

        // Set up mock permission handler to approve permission
        var eventStream = agent.RunAgenticLoopAsync(messages, cancellationToken: TestCancellationToken);

        using var permissionHandler = new MockPermissionHandler(agent, eventStream)
            .AutoApproveAll();

        // Act - Wait for handler to complete (it consumes the event stream)
        await permissionHandler.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        // Get captured events from handler
        var capturedEvents = permissionHandler.CapturedEvents;

        // Assert - Permission request should be captured
        permissionHandler.CapturedRequests.Should().ContainSingle("permission should be requested once");

        // Permission approved event should be emitted
        var permissionApproved = capturedEvents.OfType<InternalPermissionApprovedEvent>().ToList();
        permissionApproved.Should().ContainSingle("permission approval should be recorded");

        // Tool SHOULD be executed successfully
        var toolResults = capturedEvents.OfType<InternalToolCallResultEvent>().ToList();
        toolResults.Should().ContainSingle("tool should execute after approval");
        toolResults[0].Result.Should().Contain("Successfully", "tool should return successful result");

        // Final response should acknowledge success
        var textDeltas = capturedEvents.OfType<InternalTextDeltaEvent>().ToList();
        var finalText = string.Concat(textDeltas.Select(e => e.Text));
        finalText.Should().Contain("successfully", "agent should acknowledge successful execution");
    }

    /// <summary>
    /// Test 3: Multiple permission requests handled sequentially.
    /// Verifies that multiple tools requiring permission are handled correctly.
    /// </summary>
    [Fact]
    public async Task CurrentBehavior_MultiplePermissions_HandledSequentially()
    {
        // Arrange
        var fakeLLM = new FakeChatClient();

        // LLM requests two tools requiring permission
        fakeLLM.EnqueueToolCall("Tool1", "call_1", new Dictionary<string, object?> { ["data"] = "a" });
        fakeLLM.EnqueueToolCall("Tool2", "call_2", new Dictionary<string, object?> { ["data"] = "b" });

        // Final response
        fakeLLM.EnqueueTextResponse("Both tools executed");

        // Create two tools requiring permission
        var tool1 = HPDAIFunctionFactory.Create(
            async (args, ct) => "Tool1 result",
            new HPDAIFunctionFactoryOptions
            {
                Name = "Tool1",
                Description = "First sensitive tool",
                RequiresPermission = true
            });

        var tool2 = HPDAIFunctionFactory.Create(
            async (args, ct) => "Tool2 result",
            new HPDAIFunctionFactoryOptions
            {
                Name = "Tool2",
                Description = "Second sensitive tool",
                RequiresPermission = true
            });

        var agent = CreateAgentWithPermissions(fakeLLM, tool1, tool2);
        var messages = CreateSimpleConversation("Use both tools");

        // Set up mock permission handler - approve first, deny second
        var eventStream = agent.RunAgenticLoopAsync(messages, cancellationToken: TestCancellationToken);

        using var permissionHandler = new MockPermissionHandler(agent, eventStream);
        permissionHandler.EnqueueResponse(approved: true); // Approve Tool1
        permissionHandler.EnqueueResponse(approved: false, denialReason: "User denied Tool2"); // Deny Tool2

        // Act - Wait for handler to complete (it consumes the event stream)
        await permissionHandler.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        // Get captured events from handler
        var capturedEvents = permissionHandler.CapturedEvents;

        // Assert - Two permission requests
        permissionHandler.CapturedRequests.Should().HaveCount(2, "both tools should request permission");

        // One approval, one denial
        capturedEvents.OfType<InternalPermissionApprovedEvent>().Should().ContainSingle("one approval");
        capturedEvents.OfType<InternalPermissionDeniedEvent>().Should().ContainSingle("one denial");

        // Tool results should reflect approval/denial
        var toolResults = capturedEvents.OfType<InternalToolCallResultEvent>().ToList();
        toolResults.Should().HaveCountGreaterOrEqualTo(1, "at least approved tool should have result");
    }
}

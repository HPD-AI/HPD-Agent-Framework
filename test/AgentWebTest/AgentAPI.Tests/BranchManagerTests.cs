using Xunit;
using HPD.Agent;
using HPD.Agent.Checkpointing;
using AgentAPI.Services;

namespace AgentAPI.Tests;

/// <summary>
/// Tests for application-level branching (BranchManager).
/// Key architecture: Each branch = separate threadId.
/// </summary>
public class BranchManagerTests : IDisposable
{
    private readonly InMemoryThreadStore _threadStore;
    private readonly BranchManager _branchManager;
    private readonly string _tempStorageDir;

    public BranchManagerTests()
    {
        _threadStore = new InMemoryThreadStore();
        _tempStorageDir = Path.Combine(Path.GetTempPath(), $"branch-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempStorageDir);
        _branchManager = new BranchManager(_threadStore, _tempStorageDir);
    }

    [Fact]
    public async Task InitializeConversation_CreatesMainBranch()
    {
        // Arrange
        var thread = new ConversationThread();
        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);

        // Act
        var state = await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        // Assert
        Assert.NotNull(state);
        Assert.Equal(conversationId, state.ConversationId);
        Assert.Equal("main", state.ActiveBranch);
        Assert.Single(state.Branches);
        Assert.True(state.Branches.ContainsKey("main"));

        var mainBranch = state.Branches["main"];
        Assert.Equal("main", mainBranch.Name);
        Assert.Equal(thread.Id, mainBranch.ThreadId);
        Assert.Null(mainBranch.ParentBranch);
    }

    [Fact]
    public async Task ForkAsync_CreatesSeparateThreadWithNewThreadId()
    {
        // Arrange
        var thread = new ConversationThread();
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "Message 1"));
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Response 1"));

        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        // Act: Fork from message 1 (should have 1 message in new branch)
        var (forkedThread, evt) = await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 1);

        // Assert: New thread has DIFFERENT threadId
        Assert.NotEqual(thread.Id, forkedThread.Id);
        Assert.Equal(1, forkedThread.MessageCount);
        Assert.Equal("Message 1", forkedThread.Messages[0].Text);

        // Assert: Event has correct data
        Assert.Equal(conversationId, evt.ConversationId);
        Assert.Equal("edit-1", evt.BranchName);
        Assert.Equal(forkedThread.Id, evt.ThreadId);
        Assert.Equal("main", evt.ParentBranch);
        Assert.Equal(1, evt.ForkMessageIndex);

        // Assert: Branch state updated
        var state = await _branchManager.GetBranchStateAsync(conversationId);
        Assert.NotNull(state);
        Assert.Equal(2, state.Branches.Count);
        Assert.True(state.Branches.ContainsKey("edit-1"));
        Assert.Equal("edit-1", state.ActiveBranch);

        var editBranch = state.Branches["edit-1"];
        Assert.Equal(forkedThread.Id, editBranch.ThreadId);
        Assert.Equal("main", editBranch.ParentBranch);
    }

    [Fact]
    public async Task ForkAsync_PreservesMessageUpToForkPoint()
    {
        // Arrange
        var thread = new ConversationThread();
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "Message 1"));
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Response 1"));
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "Message 2"));
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Response 2"));

        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        // Act: Fork from message 2 (should have 2 messages)
        var (forkedThread, evt) = await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 2);

        // Assert
        Assert.Equal(2, forkedThread.MessageCount);
        Assert.Equal("Message 1", forkedThread.Messages[0].Text);
        Assert.Equal("Response 1", forkedThread.Messages[1].Text);

        // Original thread still has all 4 messages
        var originalThread = await _threadStore.LoadThreadAsync(thread.Id);
        Assert.NotNull(originalThread);
        Assert.Equal(4, originalThread.MessageCount);
    }

    [Fact]
    public async Task SwitchBranch_LoadsDifferentThreadMessages()
    {
        // Arrange
        var thread = new ConversationThread();
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "Main message"));

        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        // Create edit branch
        var (editThread, _) = await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 1);

        // Add different message to edit branch
        editThread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "Edit message"));
        await _threadStore.SaveThreadAsync(editThread);

        // Act: Switch back to main branch
        var result = await _branchManager.SwitchBranchAsync(conversationId, "main");

        // Assert
        Assert.NotNull(result);
        var (mainThread, evt) = result.Value;

        Assert.Equal(thread.Id, mainThread.Id);
        Assert.Equal(1, mainThread.MessageCount);
        Assert.Equal("Main message", mainThread.Messages[0].Text);

        Assert.Equal("edit-1", evt.OldBranch);
        Assert.Equal("main", evt.NewBranch);
    }

    [Fact]
    public async Task DeleteBranch_RemovesThreadAndMetadata()
    {
        // Arrange
        var thread = new ConversationThread();
        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        var (editThread, _) = await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 0);

        // Switch back to main so we can delete edit-1
        await _branchManager.SwitchBranchAsync(conversationId, "main");

        // Act
        var evt = await _branchManager.DeleteBranchAsync(conversationId, "edit-1");

        // Assert
        Assert.NotNull(evt);
        Assert.Equal("edit-1", evt.BranchName);

        // Thread should be deleted
        var deletedThread = await _threadStore.LoadThreadAsync(editThread.Id);
        Assert.Null(deletedThread);

        // Branch metadata should be removed
        var state = await _branchManager.GetBranchStateAsync(conversationId);
        Assert.NotNull(state);
        Assert.Single(state.Branches);
        Assert.False(state.Branches.ContainsKey("edit-1"));
    }

    [Fact]
    public async Task DeleteBranch_CannotDeleteMainBranch()
    {
        // Arrange
        var thread = new ConversationThread();
        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _branchManager.DeleteBranchAsync(conversationId, "main"));
    }

    [Fact]
    public async Task DeleteBranch_CannotDeleteActiveBranch()
    {
        // Arrange
        var thread = new ConversationThread();
        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 0);

        // Act & Assert (edit-1 is now active)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _branchManager.DeleteBranchAsync(conversationId, "edit-1"));
    }

    [Fact]
    public async Task RenameBranch_UpdatesMetadata()
    {
        // Arrange
        var thread = new ConversationThread();
        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        var (editThread, _) = await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 0);

        // Act
        var evt = await _branchManager.RenameBranchAsync(conversationId, "edit-1", "feature-x");

        // Assert
        Assert.NotNull(evt);
        Assert.Equal("edit-1", evt.OldName);
        Assert.Equal("feature-x", evt.NewName);

        var state = await _branchManager.GetBranchStateAsync(conversationId);
        Assert.NotNull(state);
        Assert.False(state.Branches.ContainsKey("edit-1"));
        Assert.True(state.Branches.ContainsKey("feature-x"));

        var renamedBranch = state.Branches["feature-x"];
        Assert.Equal(editThread.Id, renamedBranch.ThreadId);
    }

    [Fact]
    public async Task RenameBranch_CannotRenameMainBranch()
    {
        // Arrange
        var thread = new ConversationThread();
        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _branchManager.RenameBranchAsync(conversationId, "main", "primary"));
    }

    [Fact]
    public async Task GetActiveBranchThread_ReturnsCorrectThread()
    {
        // Arrange
        var thread = new ConversationThread();
        thread.AddMessage(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "Main message"));

        var conversationId = thread.Id;
        await _threadStore.SaveThreadAsync(thread);
        await _branchManager.InitializeConversationAsync(conversationId, thread.Id);

        var (editThread, _) = await _branchManager.ForkAsync(conversationId, "edit-1", forkMessageIndex: 0);

        // Act: Get active branch (should be edit-1)
        var activeThread = await _branchManager.GetActiveBranchThreadAsync(conversationId);

        // Assert
        Assert.NotNull(activeThread);
        Assert.Equal(editThread.Id, activeThread.Id);
        Assert.Equal(0, activeThread.MessageCount);  // Forked at 0
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempStorageDir))
        {
            Directory.Delete(_tempStorageDir, recursive: true);
        }
    }
}

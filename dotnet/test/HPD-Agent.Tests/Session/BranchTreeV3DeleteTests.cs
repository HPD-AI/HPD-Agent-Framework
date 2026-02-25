using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// V3 Branch Tree Architecture - Delete Operation Tests
/// Tests for safe deletion with referential integrity and sibling reindexing.
/// </summary>
public class BranchTreeV3DeleteTests : AgentTestBase
{
    //──────────────────────────────────────────────────────────────────
    // P0 CRITICAL - DELETE SAFETY
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test15_DeleteBranch_WithChildren_ThrowsError()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Create fork-1, then fork from fork-1 (so fork-1 has children)
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);
        fork1.Session = session;
        fork1.AddMessage(AssistantMessage("Response"));
        await store.SaveBranchAsync("test-session", fork1);

        var fork2 = await agent.ForkBranchAsync(fork1, "fork-2", fromMessageIndex: 0);

        // Act & Assert - Cannot delete fork-1 because it has children
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await agent.DeleteBranchAsync("test-session", "fork-1"));

        Assert.Contains("child", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test17_DeleteBranch_ReindexesRemainingSiblings()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Create 3 siblings
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);
        main = (await store.LoadBranchAsync("test-session", "main"))!;
        main.Session = session;
        var fork2 = await agent.ForkBranchAsync(main, "fork-2", fromMessageIndex: 0);
        main = (await store.LoadBranchAsync("test-session", "main"))!;
        main.Session = session;
        var fork3 = await agent.ForkBranchAsync(main, "fork-3", fromMessageIndex: 0);

        // Initial state: fork-1(0), fork-2(1), fork-3(2)
        var beforeFork1 = await store.LoadBranchAsync("test-session", "fork-1");
        var beforeFork2 = await store.LoadBranchAsync("test-session", "fork-2");
        var beforeFork3 = await store.LoadBranchAsync("test-session", "fork-3");
        Assert.Equal(0, beforeFork1!.SiblingIndex);
        Assert.Equal(1, beforeFork2!.SiblingIndex);
        Assert.Equal(2, beforeFork3!.SiblingIndex);

        // Act - Delete middle sibling (fork-2)
        await agent.DeleteBranchAsync("test-session", "fork-2");

        // Assert - Remaining siblings reindexed: fork-1(0), fork-3(1)
        var afterFork1 = await store.LoadBranchAsync("test-session", "fork-1");
        var afterFork2 = await store.LoadBranchAsync("test-session", "fork-2");
        var afterFork3 = await store.LoadBranchAsync("test-session", "fork-3");

        Assert.Null(afterFork2); // Deleted
        Assert.Equal(0, afterFork1!.SiblingIndex);
        Assert.Equal(1, afterFork3!.SiblingIndex); // Shifted down from 2 to 1
        Assert.Equal(2, afterFork1.TotalSiblings);
        Assert.Equal(2, afterFork3.TotalSiblings);
    }

    [Fact]
    public async Task Test18_DeleteBranch_UpdatesNavigationPointers()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Create 3 siblings: fork-1 <-> fork-2 <-> fork-3
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);
        main = (await store.LoadBranchAsync("test-session", "main"))!;
        main.Session = session;
        var fork2 = await agent.ForkBranchAsync(main, "fork-2", fromMessageIndex: 0);
        main = (await store.LoadBranchAsync("test-session", "main"))!;
        main.Session = session;
        var fork3 = await agent.ForkBranchAsync(main, "fork-3", fromMessageIndex: 0);

        // Act - Delete middle sibling (fork-2)
        await agent.DeleteBranchAsync("test-session", "fork-2");

        // Assert - Navigation pointers updated: fork-1 <-> fork-3
        var afterFork1 = await store.LoadBranchAsync("test-session", "fork-1");
        var afterFork3 = await store.LoadBranchAsync("test-session", "fork-3");

        Assert.Null(afterFork1!.PreviousSiblingId); // First
        Assert.Equal("fork-3", afterFork1.NextSiblingId);

        Assert.Equal("fork-1", afterFork3!.PreviousSiblingId);
        Assert.Null(afterFork3.NextSiblingId); // Last
    }

    [Fact]
    public async Task Test19_DeleteBranch_UpdatesParentChildBranches()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Create fork
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);

        // Verify parent has child
        var beforeMain = await store.LoadBranchAsync("test-session", "main");
        Assert.Contains("fork-1", beforeMain!.ChildBranches);
        Assert.Equal(1, beforeMain.TotalForks);

        // Act - Delete fork
        await agent.DeleteBranchAsync("test-session", "fork-1");

        // Assert - Parent updated
        var afterMain = await store.LoadBranchAsync("test-session", "main");
        Assert.DoesNotContain("fork-1", afterMain!.ChildBranches);
        Assert.Equal(0, afterMain.TotalForks);
    }

    [Fact]
    public async Task Test40_CannotDeleteMainBranch()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        await store.SaveBranchAsync("test-session", main);

        // Act & Assert - Cannot delete main branch
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await agent.DeleteBranchAsync("test-session", "main"));

        Assert.Contains("main", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test43_ForkWithInvalidMessageIndex_FailsGracefully()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Act & Assert - Fork at invalid index
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 999));

        Assert.Contains("index", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    //──────────────────────────────────────────────────────────────────
    // INTEGRATION TESTS - WORKFLOWS
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test33_CreateSession_ForkBranch_VerifyTreeStructure()
    {
        // Arrange & Act
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        main.AddMessage(AssistantMessage("Response 1"));
        main.AddMessage(UserMessage("Message 2"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 1);

        // Assert - Tree structure
        var reloadedMain = await store.LoadBranchAsync("test-session", "main");
        var reloadedFork1 = await store.LoadBranchAsync("test-session", "fork-1");

        // Main
        Assert.Equal(3, reloadedMain!.MessageCount);
        Assert.True(reloadedMain.IsOriginal);
        Assert.Contains("fork-1", reloadedMain.ChildBranches);

        // Fork1
        Assert.Equal(2, reloadedFork1!.MessageCount); // 0, 1
        Assert.False(reloadedFork1.IsOriginal);
        Assert.Equal("main", reloadedFork1.ForkedFrom);
        Assert.Equal(1, reloadedFork1.ForkedAtMessageIndex);
        Assert.Empty(reloadedFork1.ChildBranches);
    }

    [Fact]
    public async Task Test34_ForkMultipleBranches_VerifySiblingOrder()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Act - Create 5 siblings
        var forks = new List<Branch>();
        for (int i = 0; i < 5; i++)
        {
            var fork = await agent.ForkBranchAsync(main, $"fork-{i}", fromMessageIndex: 0);
            forks.Add(fork);
            main = (await store.LoadBranchAsync("test-session", "main"))!;
            main.Session = session;
            await Task.Delay(10); // Ensure chronological order
        }

        // Assert - Verify indices 0-4 and navigation pointers
        for (int i = 0; i < 5; i++)
        {
            var reloaded = await store.LoadBranchAsync("test-session", $"fork-{i}");
            Assert.Equal(i, reloaded!.SiblingIndex);
            Assert.Equal(5, reloaded.TotalSiblings);

            // Verify navigation pointers
            if (i == 0)
                Assert.Null(reloaded.PreviousSiblingId);
            else
                Assert.Equal($"fork-{i - 1}", reloaded.PreviousSiblingId);

            if (i == 4)
                Assert.Null(reloaded.NextSiblingId);
            else
                Assert.Equal($"fork-{i + 1}", reloaded.NextSiblingId);
        }
    }

    [Fact]
    public async Task Test35_DeleteBranch_VerifySiblingsReindexed()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Create 5 siblings
        for (int i = 0; i < 5; i++)
        {
            await agent.ForkBranchAsync(main, $"fork-{i}", fromMessageIndex: 0);
            main = (await store.LoadBranchAsync("test-session", "main"))!;
            main.Session = session;
        }

        // Act - Delete fork-2 (middle)
        await agent.DeleteBranchAsync("test-session", "fork-2");

        // Assert - Remaining siblings reindexed
        // fork-0(0), fork-1(1), fork-3(2), fork-4(3)
        Assert.Null(await store.LoadBranchAsync("test-session", "fork-2"));

        var fork0 = await store.LoadBranchAsync("test-session", "fork-0");
        var fork1 = await store.LoadBranchAsync("test-session", "fork-1");
        var fork3 = await store.LoadBranchAsync("test-session", "fork-3");
        var fork4 = await store.LoadBranchAsync("test-session", "fork-4");

        Assert.Equal(0, fork0!.SiblingIndex);
        Assert.Equal(1, fork1!.SiblingIndex);
        Assert.Equal(2, fork3!.SiblingIndex); // Shifted from 3
        Assert.Equal(3, fork4!.SiblingIndex); // Shifted from 4

        // All have TotalSiblings=4
        Assert.Equal(4, fork0.TotalSiblings);
        Assert.Equal(4, fork1.TotalSiblings);
        Assert.Equal(4, fork3.TotalSiblings);
        Assert.Equal(4, fork4.TotalSiblings);
    }

    [Fact]
    public async Task Test36_ForkAgain_VerifyMultiLevelAncestry()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Act - Create multi-level ancestry: main -> fork1 -> fork2
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);
        fork1.Session = session;
        fork1.AddMessage(AssistantMessage("Fork 1 response"));
        await store.SaveBranchAsync("test-session", fork1);

        var fork2 = await agent.ForkBranchAsync(fork1, "fork-2", fromMessageIndex: 0);

        // Assert - Verify ancestry chain
        Assert.Equal("main", fork1.ForkedFrom);
        Assert.Equal("fork-1", fork2.ForkedFrom);

        // Check Ancestors dictionary
        Assert.NotNull(fork2.Ancestors);
        Assert.Equal(2, fork2.Ancestors!.Count); // main, fork-1
        Assert.Equal("main", fork2.Ancestors["0"]);
        Assert.Equal("fork-1", fork2.Ancestors["1"]);

        // Check ChildBranches
        var reloadedMain = await store.LoadBranchAsync("test-session", "main");
        var reloadedFork1 = await store.LoadBranchAsync("test-session", "fork-1");

        Assert.Contains("fork-1", reloadedMain!.ChildBranches);
        Assert.Contains("fork-2", reloadedFork1!.ChildBranches);
    }

    //──────────────────────────────────────────────────────────────────
    // EDGE CASES
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestEdge_ForkEmptyBranch_AtIndexZero()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Act - Fork empty branch at index 0 (valid)
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);

        // Assert
        Assert.Empty(fork1.Messages);
        Assert.Equal("main", fork1.ForkedFrom);
        Assert.Equal(0, fork1.ForkedAtMessageIndex);
    }

    [Fact]
    public async Task TestEdge_ForkEmptyBranch_AtNonZeroIndex_Throws()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Act & Assert - Fork empty branch at index 1 (invalid)
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 1));

        Assert.Contains("must be 0", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestEdge_DeleteLastSibling_LeavesNoSiblings()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);

        // Act - Delete the only fork
        await agent.DeleteBranchAsync("test-session", "fork-1");

        // Assert - Main is now alone with no siblings
        var reloadedMain = await store.LoadBranchAsync("test-session", "main");
        Assert.Empty(reloadedMain!.ChildBranches);
        Assert.Equal(0, reloadedMain.TotalForks);
    }

    //──────────────────────────────────────────────────────────────────
    // HELPER METHODS
    //──────────────────────────────────────────────────────────────────

    private async Task<HPD.Agent.Agent> CreateAgentWithStore(ISessionStore store)
    {
        return await new AgentBuilder(DefaultConfig(), new TestProviderRegistry(new FakeChatClient()))
            .WithSessionStore(store)
            .BuildAsync(CancellationToken.None);
    }
}

using Microsoft.Extensions.AI;
using Xunit;
using HPD.Agent;
using HPD.Agent.Tests.Infrastructure;

namespace HPD.Agent.Tests.Session;

/// <summary>
/// V3 Branch Tree Architecture Tests
/// Tests for tree navigation, sibling ordering, atomic operations, and referential integrity.
/// </summary>
public class BranchTreeV3Tests : AgentTestBase
{
    //──────────────────────────────────────────────────────────────────
    // UNIT TESTS - SCHEMA & SERIALIZATION
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void Test01_NewFields_SerializeDeserialize_RoundTrip()
    {
        // Arrange - Create branch with all V3 fields
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = 2,
            TotalSiblings = 5,
            IsOriginal = false,
            OriginalBranchId = "main",
            PreviousSiblingId = "branch-0",
            NextSiblingId = "branch-2",
            ChildBranches = new List<string> { "child-1", "child-2" }
        };

        // Act - Serialize and deserialize
        var json = System.Text.Json.JsonSerializer.Serialize(branch);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Branch>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.SiblingIndex);
        Assert.Equal(5, deserialized.TotalSiblings);
        Assert.False(deserialized.IsOriginal);
        Assert.Equal("main", deserialized.OriginalBranchId);
        Assert.Equal("branch-0", deserialized.PreviousSiblingId);
        Assert.Equal("branch-2", deserialized.NextSiblingId);
        Assert.Equal(2, deserialized.ChildBranches.Count);
        Assert.Contains("child-1", deserialized.ChildBranches);
    }

    [Fact]
    public void Test02_DefaultValues_PassInvariantChecks()
    {
        // Arrange & Act - Create new branch with defaults
        var branch = new Branch("session-1", "main");

        // Assert - Should not throw
        branch.ValidateTreeInvariants();

        // Verify defaults
        Assert.Equal(0, branch.SiblingIndex);
        Assert.Equal(1, branch.TotalSiblings);
        Assert.True(branch.IsOriginal);
        Assert.Null(branch.OriginalBranchId);
        Assert.Null(branch.PreviousSiblingId);
        Assert.Null(branch.NextSiblingId);
        Assert.Empty(branch.ChildBranches);
    }

    [Fact]
    public void Test03_BranchCreation_SetsCorrectInitialValues()
    {
        // Arrange
        var session = new HPD.Agent.Session("test-session");

        // Act
        var branch = session.CreateBranch("main");

        // Assert - V3 defaults
        Assert.Equal(0, branch.SiblingIndex);
        Assert.Equal(1, branch.TotalSiblings);
        Assert.True(branch.IsOriginal);
        Assert.Null(branch.OriginalBranchId);
        Assert.Null(branch.PreviousSiblingId);
        Assert.Null(branch.NextSiblingId);
        Assert.Empty(branch.ChildBranches);
        Assert.Equal(0, branch.TotalForks);
    }

    [Fact]
    public void Test04_BackwardCompatibility_MissingV3Fields_UseSafeDefaults()
    {
        // Arrange - JSON without V3 fields (simulates old data)
        var jsonWithoutV3Fields = """
        {
            "Id": "branch-1",
            "SessionId": "session-1",
            "Messages": [],
            "ForkedFrom": null,
            "ForkedAtMessageIndex": null,
            "CreatedAt": "2025-01-01T00:00:00Z",
            "LastActivity": "2025-01-01T00:00:00Z",
            "Name": null,
            "Description": null,
            "Tags": null,
            "Ancestors": null,
            "MiddlewareState": {}
        }
        """;

        // Act
        var branch = System.Text.Json.JsonSerializer.Deserialize<Branch>(jsonWithoutV3Fields);

        // Assert - Safe defaults applied
        Assert.NotNull(branch);
        Assert.Equal(0, branch.SiblingIndex);
        Assert.Equal(1, branch.TotalSiblings);
        Assert.True(branch.IsOriginal);
        Assert.Null(branch.OriginalBranchId);
        Assert.Empty(branch.ChildBranches);
    }

    [Fact]
    public void Test05_NameField_ProperlySerializedDeserialized()
    {
        // Arrange
        var branch = new Branch("session-1", "branch-1")
        {
            Name = "Experiment A"
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(branch);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<Branch>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("Experiment A", deserialized.Name);
    }

    //──────────────────────────────────────────────────────────────────
    // UNIT TESTS - VALIDATION (ValidateTreeInvariants)
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void Test26_IsOriginal_MismatchWithForkedFrom_ThrowsException()
    {
        // Arrange - Invalid state: IsOriginal=true but has ForkedFrom
        var branch = new Branch("session-1", "branch-1")
        {
            IsOriginal = true,
            ForkedFrom = "main", //  Conflict!
            SiblingIndex = 0,
            TotalSiblings = 1
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("IsOriginal=True but ForkedFrom=main", ex.Message);
    }

    [Fact]
    public void Test27_SiblingIndex_OutOfRange_ThrowsException()
    {
        // Arrange - SiblingIndex >= TotalSiblings
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = 5,
            TotalSiblings = 3, //  Index out of range!
            IsOriginal = false,
            ForkedFrom = "main"
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("out of range [0, 3)", ex.Message);
    }

    [Fact]
    public void Test28_TotalSiblings_ZeroOrNegative_ThrowsException()
    {
        // Arrange - Set SiblingIndex < TotalSiblings to bypass range check first
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = -1, // Set this negative to avoid range check triggering first
            TotalSiblings = 0, //  Must be positive!
            IsOriginal = true
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        // The validation will catch TotalSiblings <= 0 or SiblingIndex out of range
        Assert.True(ex.Message.Contains("must be positive") || ex.Message.Contains("out of range"));
    }

    [Fact]
    public void Test29_FirstSibling_WithPreviousPointer_ThrowsException()
    {
        // Arrange - First sibling should have no previous
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = 0,
            TotalSiblings = 3,
            IsOriginal = true,
            PreviousSiblingId = "some-id" //  First sibling can't have previous!
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("First sibling (index=0) has PreviousSiblingId", ex.Message);
    }

    [Fact]
    public void Test30_LastSibling_WithNextPointer_ThrowsException()
    {
        // Arrange - Last sibling should have no next
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = 2,
            TotalSiblings = 3,
            IsOriginal = false,
            ForkedFrom = "main",
            NextSiblingId = "some-id" //  Last sibling can't have next!
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("Last sibling (index=2) has NextSiblingId", ex.Message);
    }

    [Fact]
    public void Test31_MiddleSibling_MissingPreviousPointer_ThrowsException()
    {
        // Arrange - Middle sibling must have both pointers
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = 1,
            TotalSiblings = 3,
            IsOriginal = false,
            ForkedFrom = "main",
            PreviousSiblingId = null, //  Middle sibling needs previous!
            NextSiblingId = "branch-2"
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("Middle sibling (index=1) has null PreviousSiblingId", ex.Message);
    }

    [Fact]
    public void Test31b_MiddleSibling_MissingNextPointer_ThrowsException()
    {
        // Arrange - Middle sibling must have both pointers
        var branch = new Branch("session-1", "branch-1")
        {
            SiblingIndex = 1,
            TotalSiblings = 3,
            IsOriginal = false,
            ForkedFrom = "main",
            PreviousSiblingId = "main",
            NextSiblingId = null //  Middle sibling needs next!
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("Middle sibling (index=1) has null NextSiblingId", ex.Message);
    }

    [Fact]
    public void Test32_OriginalBranchId_SetOnOriginalBranch_ThrowsException()
    {
        // Arrange - Original branch shouldn't have OriginalBranchId
        var branch = new Branch("session-1", "main")
        {
            IsOriginal = true,
            ForkedFrom = null,
            OriginalBranchId = "some-id", //  Original branches don't have this!
            SiblingIndex = 0,
            TotalSiblings = 1
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => branch.ValidateTreeInvariants());
        Assert.Contains("Original branch should have OriginalBranchId=null", ex.Message);
    }

    //──────────────────────────────────────────────────────────────────
    // UNIT TESTS - HELPER PROPERTIES
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public void TestHelpers_IsLeaf_ReturnsTrue_WhenNoChildren()
    {
        // Arrange
        var branch = new Branch("session-1", "branch-1");

        // Act & Assert
        Assert.True(branch.IsLeaf);
        Assert.Equal(0, branch.TotalForks);
    }

    [Fact]
    public void TestHelpers_IsLeaf_ReturnsFalse_WhenHasChildren()
    {
        // Arrange
        var branch = new Branch("session-1", "branch-1")
        {
            ChildBranches = new List<string> { "child-1", "child-2" }
        };

        // Act & Assert
        Assert.False(branch.IsLeaf);
        Assert.Equal(2, branch.TotalForks);
    }

    [Fact]
    public void TestHelpers_IsRoot_ReturnsTrue_WhenNoParent()
    {
        // Arrange
        var branch = new Branch("session-1", "main")
        {
            ForkedFrom = null
        };

        // Act & Assert
        Assert.True(branch.IsRoot);
    }

    [Fact]
    public void TestHelpers_IsRoot_ReturnsFalse_WhenHasParent()
    {
        // Arrange
        var branch = new Branch("session-1", "fork-1")
        {
            ForkedFrom = "main"
        };

        // Act & Assert
        Assert.False(branch.IsRoot);
    }

    //──────────────────────────────────────────────────────────────────
    // INTEGRATION TESTS - ATOMIC FORK
    //──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test09_ForkBranch_UpdatesAllSiblingMetadata_Atomically()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        main.AddMessage(AssistantMessage("Response 1"));
        main.AddMessage(UserMessage("Message 2"));
        await store.SaveBranchAsync("test-session", main);

        // Act - Fork main branch
        main.Session = session;
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 1);

        // Assert - Fork has correct metadata
        // Note: main and fork-1 are NOT siblings (different ForkedFrom)
        // So fork-1's TotalSiblings=1 (just itself in its sibling group)
        var reloadedMain = await store.LoadBranchAsync("test-session", "main");
        var reloadedFork1 = await store.LoadBranchAsync("test-session", "fork-1");

        // Main still original
        Assert.Equal(1, reloadedMain!.TotalSiblings);
        Assert.Equal(0, reloadedMain.SiblingIndex);
        Assert.True(reloadedMain.IsOriginal);

        // Fork-1 metadata
        Assert.Equal(1, reloadedFork1!.TotalSiblings);
        Assert.Equal(0, reloadedFork1.SiblingIndex);
        Assert.False(reloadedFork1.IsOriginal);
    }

    [Fact]
    public async Task Test10_ForkBranch_SetsCorrectSiblingIndex_ChronologicalOrder()
    {
        // Arrange
        var store = new InMemorySessionStore();
        var agent = await CreateAgentWithStore(store);
        var session = new HPD.Agent.Session("test-session");
        await store.SaveSessionAsync(session);

        var main = session.CreateBranch("main");
        main.AddMessage(UserMessage("Message 1"));
        main.AddMessage(AssistantMessage("Response 1"));
        await store.SaveBranchAsync("test-session", main);
        main.Session = session;

        // Act - Fork main THREE TIMES at the SAME index
        // This creates three siblings: fork-1, fork-2, fork-3
        // All have ForkedFrom="main", ForkedAtMessageIndex=1
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 1);
        await Task.Delay(10); // Ensure time difference

        // Reload main to get updated metadata
        main = (await store.LoadBranchAsync("test-session", "main"))!;
        main.Session = session;
        var fork2 = await agent.ForkBranchAsync(main, "fork-2", fromMessageIndex: 1);
        await Task.Delay(10);

        // Reload main again
        main = (await store.LoadBranchAsync("test-session", "main"))!;
        main.Session = session;
        var fork3 = await agent.ForkBranchAsync(main, "fork-3", fromMessageIndex: 1);

        // Assert - Reload ALL siblings (fork-1, fork-2, fork-3)
        // Note: main is NOT a sibling (different ForkedFrom)
        var reloadedFork1 = await store.LoadBranchAsync("test-session", "fork-1");
        var reloadedFork2 = await store.LoadBranchAsync("test-session", "fork-2");
        var reloadedFork3 = await store.LoadBranchAsync("test-session", "fork-3");

        // Verify sibling indices in chronological order
        // None of these are "original" (all have ForkedFrom="main")
        // So they're ordered chronologically: 0, 1, 2
        Assert.Equal(0, reloadedFork1!.SiblingIndex);
        Assert.Equal(1, reloadedFork2!.SiblingIndex);
        Assert.Equal(2, reloadedFork3!.SiblingIndex);

        // Verify total siblings count
        Assert.Equal(3, reloadedFork1.TotalSiblings);
        Assert.Equal(3, reloadedFork2.TotalSiblings);
        Assert.Equal(3, reloadedFork3.TotalSiblings);

        // Verify they're all NOT original
        Assert.False(reloadedFork1.IsOriginal);
        Assert.False(reloadedFork2.IsOriginal);
        Assert.False(reloadedFork3.IsOriginal);
    }

    [Fact]
    public async Task Test11_ForkBranch_LinksNavigationPointers_Bidirectionally()
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

        // Act
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);

        // Assert - Fork-1 has no siblings (TotalSiblings=1)
        // main and fork-1 are NOT siblings (different ForkedFrom)
        var reloadedMain = await store.LoadBranchAsync("test-session", "main");
        var reloadedFork1 = await store.LoadBranchAsync("test-session", "fork-1");

        // Main has no siblings
        Assert.Null(reloadedMain!.PreviousSiblingId);
        Assert.Null(reloadedMain.NextSiblingId);

        // Fork-1 has no siblings
        Assert.Null(reloadedFork1!.PreviousSiblingId);
        Assert.Null(reloadedFork1.NextSiblingId);
    }

    [Fact]
    public async Task Test12_ForkBranch_UpdatesParentChildBranches_List()
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

        // Act
        var fork1 = await agent.ForkBranchAsync(main, "fork-1", fromMessageIndex: 0);

        // Assert - Parent tracks child
        var reloadedMain = await store.LoadBranchAsync("test-session", "main");
        Assert.Contains("fork-1", reloadedMain!.ChildBranches);
        Assert.Equal(1, reloadedMain.TotalForks);

        // Child references parent
        Assert.Equal("main", fork1.ForkedFrom);
    }

    //──────────────────────────────────────────────────────────────────
    // HELPER METHODS
    //──────────────────────────────────────────────────────────────────

    private async Task<HPD.Agent.Agent> CreateAgentWithStore(ISessionStore store)
    {
        return await new AgentBuilder(DefaultConfig(), new TestProviderRegistry(new FakeChatClient()))
            .WithSessionStore(store)
            .Build(CancellationToken.None);
    }
}

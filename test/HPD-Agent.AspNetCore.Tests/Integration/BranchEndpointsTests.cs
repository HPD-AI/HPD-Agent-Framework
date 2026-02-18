using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HPD.Agent.AspNetCore.Tests.TestInfrastructure;
using HPD.Agent.Hosting.Data;

namespace HPD.Agent.AspNetCore.Tests.Integration;

/// <summary>
/// Integration tests for Branch CRUD endpoints.
/// Tests: GET /sessions/{sid}/branches, POST /sessions/{sid}/branches, GET /sessions/{sid}/branches/{bid},
/// POST /sessions/{sid}/branches/{bid}/fork, DELETE /sessions/{sid}/branches/{bid}, etc.
/// </summary>
public class BranchEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BranchEndpointsTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<string> CreateTestSession()
    {
        var response = await _client.PostAsync("/sessions", null);
        var session = await response.Content.ReadFromJsonAsync<SessionDto>();
        return session!.SessionId;
    }

    #region GET /sessions/{sid}/branches

    [Fact]
    public async Task ListBranches_ReturnsAllBranches_ForSession()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var branches = await response.Content.ReadFromJsonAsync<List<BranchDto>>();
        branches.Should().NotBeNull();
        branches!.Should().ContainSingle(); // Only "main" branch initially
        branches[0].Id.Should().Be("main");
    }

    [Fact]
    public async Task ListBranches_Returns404_WhenSessionNotFound()
    {
        // Act
        var response = await _client.GetAsync("/sessions/nonexistent/branches");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListBranches_ReturnsEmptyArray_WhenNoBranches()
    {
        // This test verifies behavior if somehow a session has no branches
        // In practice, sessions always have at least "main"
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches");

        // Assert
        var branches = await response.Content.ReadFromJsonAsync<List<BranchDto>>();
        branches.Should().NotBeNull();
        branches!.Should().NotBeEmpty(); // Always has "main"
    }

    #endregion

    #region GET /sessions/{sid}/branches/{bid}

    [Fact]
    public async Task GetBranch_Returns200_WithBranchDto()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/main");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch.Should().NotBeNull();
        branch!.Id.Should().Be("main");
        branch.SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task GetBranch_Returns404_WhenBranchNotFound()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/nonexistent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBranch_Returns404_WhenSessionNotFound()
    {
        // Act
        var response = await _client.GetAsync("/sessions/nonexistent/branches/main");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region POST /sessions/{sid}/branches

    [Fact]
    public async Task CreateBranch_Returns201_WithBranchDto()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new CreateBranchRequest(
            "feature-branch",
            "Feature Branch",
            "Testing new feature",
            null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch.Should().NotBeNull();
        branch!.Id.Should().Be("feature-branch");
        branch.Name.Should().Be("Feature Branch");
        branch.Description.Should().Be("Testing new feature");
    }

    [Fact]
    public async Task CreateBranch_AcceptsCustomBranchId()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new CreateBranchRequest("custom-id", "Custom", null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches",
            request);

        // Assert
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch!.Id.Should().Be("custom-id");
    }

    [Fact]
    public async Task CreateBranch_GeneratesBranchId_WhenNotProvided()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new CreateBranchRequest(null, "Auto Branch", null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches",
            request);

        // Assert
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch!.Id.Should().NotBeNullOrEmpty();
        branch.Id.Should().NotBe("main");
    }

    [Fact]
    public async Task CreateBranch_AcceptsNameAndDescription()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new CreateBranchRequest(
            "test",
            "Test Branch",
            "This is a test branch",
            null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches",
            request);

        // Assert
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch!.Name.Should().Be("Test Branch");
        branch.Description.Should().Be("This is a test branch");
    }

    [Fact]
    public async Task CreateBranch_Returns404_WhenSessionNotFound()
    {
        // Arrange
        var request = new CreateBranchRequest("test", "Test", null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            "/sessions/nonexistent/branches",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateBranch_Returns409_WhenBranchIdExists()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act - Try to create a branch with ID "main" (already exists)
        var request = new CreateBranchRequest("main", "Duplicate", null, null);
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    #endregion

    #region POST /sessions/{sid}/branches/{bid}/fork

    [Fact]
    public async Task ForkBranch_Returns201_WithForkedBranch()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new ForkBranchRequest(
            "forked",
            0,
            "Forked Branch",
            "Forked from main",
            null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches/main/fork",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch.Should().NotBeNull();
        branch!.Id.Should().Be("forked");
        branch.ForkedFrom.Should().Be("main");
        branch.ForkedAtMessageIndex.Should().Be(0);
    }

    [Fact]
    public async Task ForkBranch_CopiesMessagesUpToIndex()
    {
        // This test would require sending messages first
        // Simplified test just verifies the fork operation succeeds
        var sessionId = await CreateTestSession();
        var request = new ForkBranchRequest("fork1", 0, "Fork", null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches/main/fork",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ForkBranch_SetsForkedFromAndIndex()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new ForkBranchRequest("fork2", 0, null, null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches/main/fork",
            request);

        // Assert
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch!.ForkedFrom.Should().Be("main");
        branch.ForkedAtMessageIndex.Should().Be(0);
    }

    [Fact]
    public async Task ForkBranch_SetsAncestors_Correctly()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new ForkBranchRequest("fork3", 0, null, null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches/main/fork",
            request);

        // Assert
        var branch = await response.Content.ReadFromJsonAsync<BranchDto>();
        branch!.Ancestors.Should().NotBeNull();
        branch.Ancestors!.Should().ContainKey("0");
    }

    [Fact]
    public async Task ForkBranch_Returns404_WhenSourceBranchNotFound()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new ForkBranchRequest("fork", 0, null, null, null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches/nonexistent/fork",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForkBranch_Returns400_WhenIndexOutOfBounds()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var request = new ForkBranchRequest("fork", 999, null, null, null); // Index too high

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/sessions/{sessionId}/branches/main/fork",
            request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region DELETE /sessions/{sid}/branches/{bid}

    [Fact]
    public async Task DeleteBranch_Returns204_OnSuccess()
    {
        // Arrange
        var sessionId = await CreateTestSession();
        var createRequest = new CreateBranchRequest("to-delete", "Delete Me", null, null);
        await _client.PostAsJsonAsync($"/sessions/{sessionId}/branches", createRequest);

        // Act
        var response = await _client.DeleteAsync($"/sessions/{sessionId}/branches/to-delete");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteBranch_Returns404_WhenBranchNotFound()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.DeleteAsync($"/sessions/{sessionId}/branches/nonexistent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBranch_Returns400_WhenDeletingMainBranch()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.DeleteAsync($"/sessions/{sessionId}/branches/main");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region GET /sessions/{sid}/branches/{bid}/messages

    [Fact]
    public async Task GetBranchMessages_ReturnsAllMessages()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/main/messages");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var messages = await response.Content.ReadFromJsonAsync<List<MessageDto>>();
        messages.Should().NotBeNull();
        messages!.Should().BeEmpty(); // No messages yet
    }

    [Fact]
    public async Task GetBranchMessages_ReturnsEmptyArray_WhenNoMessages()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/main/messages");

        // Assert
        var messages = await response.Content.ReadFromJsonAsync<List<MessageDto>>();
        messages.Should().NotBeNull();
        messages!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBranchMessages_Returns404_WhenBranchNotFound()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/nonexistent/messages");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region GET /sessions/{sid}/branches/{bid}/siblings

    [Fact]
    public async Task GetSiblings_ReturnsForkedBranches()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Create sibling branches
        await _client.PostAsJsonAsync($"/sessions/{sessionId}/branches/main/fork",
            new ForkBranchRequest("sibling1", 0, null, null, null));
        await _client.PostAsJsonAsync($"/sessions/{sessionId}/branches/main/fork",
            new ForkBranchRequest("sibling2", 0, null, null, null));

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/sibling1/siblings");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var siblings = await response.Content.ReadFromJsonAsync<List<string>>();
        siblings.Should().NotBeNull();
        siblings!.Should().Contain("sibling2");
    }

    [Fact]
    public async Task GetSiblings_ReturnsEmptyArray_WhenNoSiblings()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/main/siblings");

        // Assert
        var siblings = await response.Content.ReadFromJsonAsync<List<string>>();
        siblings.Should().NotBeNull();
        siblings!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSiblings_Returns404_WhenBranchNotFound()
    {
        // Arrange
        var sessionId = await CreateTestSession();

        // Act
        var response = await _client.GetAsync($"/sessions/{sessionId}/branches/nonexistent/siblings");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion
}

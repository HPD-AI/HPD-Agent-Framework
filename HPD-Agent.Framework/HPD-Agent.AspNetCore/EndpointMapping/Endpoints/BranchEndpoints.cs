using HPD.Agent;
using HPD.Agent.AspNetCore.Lifecycle;
using HPD.Agent.Hosting.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace HPD.Agent.AspNetCore.EndpointMapping.Endpoints;

/// <summary>
/// Branch CRUD endpoints for the HPD-Agent API.
/// </summary>
internal static class BranchEndpoints
{
    /// <summary>
    /// Maps all branch-related endpoints.
    /// </summary>
    internal static void Map(IEndpointRouteBuilder endpoints, AspNetCoreSessionManager manager)
    {
        // GET /sessions/{sid}/branches - List branches
        endpoints.MapGet("/sessions/{sid}/branches", (string sid, CancellationToken ct) =>
                ListBranches(sid, manager, ct))
            .WithName("ListBranches")
            .WithSummary("List all branches in a session");

        // GET /sessions/{sid}/branches/{bid} - Get branch metadata
        endpoints.MapGet("/sessions/{sid}/branches/{bid}", (string sid, string bid, CancellationToken ct) =>
                GetBranch(sid, bid, manager, ct))
            .WithName("GetBranch")
            .WithSummary("Get branch metadata by ID");

        // POST /sessions/{sid}/branches - Create new branch
        endpoints.MapPost("/sessions/{sid}/branches", (string sid, CreateBranchRequest request, CancellationToken ct) =>
                CreateBranch(sid, request, manager, ct))
            .WithName("CreateBranch")
            .WithSummary("Create a new branch in a session");

        // POST /sessions/{sid}/branches/{bid}/fork - Fork at message index
        endpoints.MapPost("/sessions/{sid}/branches/{bid}/fork", (string sid, string bid, ForkBranchRequest request, CancellationToken ct) =>
                ForkBranch(sid, bid, request, manager, ct))
            .WithName("ForkBranch")
            .WithSummary("Fork an existing branch at a specific message index");

        // DELETE /sessions/{sid}/branches/{bid} - Delete branch
        endpoints.MapDelete("/sessions/{sid}/branches/{bid}", (string sid, string bid, CancellationToken ct) =>
                DeleteBranch(sid, bid, manager, ct))
            .WithName("DeleteBranch")
            .WithSummary("Delete a branch");

        // GET /sessions/{sid}/branches/{bid}/messages - Get branch messages
        endpoints.MapGet("/sessions/{sid}/branches/{bid}/messages", (string sid, string bid, CancellationToken ct) =>
                GetMessages(sid, bid, manager, ct))
            .WithName("GetBranchMessages")
            .WithSummary("Get all messages in a branch");

        // GET /sessions/{sid}/branches/{bid}/siblings - Get sibling branch IDs
        endpoints.MapGet("/sessions/{sid}/branches/{bid}/siblings", (string sid, string bid, CancellationToken ct) =>
                GetSiblings(sid, bid, manager, ct))
            .WithName("GetSiblingBranches")
            .WithSummary("Get sibling branch IDs (branches that share the same parent)");
    }

    private static async Task<IResult> ListBranches(
        string sid,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        try
        {
            var session = await manager.Store.LoadSessionAsync(sid, ct);
            if (session == null)
            {
                return ErrorResponses.NotFound();
            }

            var branchIds = await manager.Store.ListBranchIdsAsync(sid, ct);
            var dtos = new List<BranchDto>();

            foreach (var branchId in branchIds)
            {
                var branch = await manager.Store.LoadBranchAsync(sid, branchId, ct);
                if (branch != null)
                {
                    dtos.Add(ToBranchDto(branch, sid));
                }
            }

            return ErrorResponses.Json(dtos);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ListBranchesError"] = [ex.Message]
            });
        }
    }

    private static async Task<IResult> GetBranch(
        string sid,
        string bid,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        try
        {
            var branch = await manager.Store.LoadBranchAsync(sid, bid, ct);
            if (branch == null)
            {
                return ErrorResponses.NotFound();
            }

            var dto = ToBranchDto(branch, sid);
            return ErrorResponses.Json(dto);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["GetBranchError"] = [ex.Message]
            });
        }
    }

    private static async Task<IResult> CreateBranch(
        string sid,
        CreateBranchRequest request,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        try
        {
            var session = await manager.Store.LoadSessionAsync(sid, ct);
            if (session == null)
            {
                return ErrorResponses.NotFound();
            }

            session.Store = manager.Store;

            // Generate branch ID if not provided
            var branchId = string.IsNullOrWhiteSpace(request.BranchId)
                ? Guid.NewGuid().ToString()
                : request.BranchId;

            // Check if branch already exists (return conflict)
            var existingBranch = await manager.Store.LoadBranchAsync(sid, branchId, ct);
            if (existingBranch != null)
            {
                return ErrorResponses.Conflict();
            }

            // Use agent to create branch (Agent has access to internal constructors)
            var agent = await manager.GetOrCreateAgentAsync(sid, ct);
            var (_, branch) = await agent.LoadSessionAndBranchAsync(sid, branchId, ct);

            // Set Name and Description from request
            if (!string.IsNullOrEmpty(request.Name))
            {
                branch.Name = request.Name;
            }

            if (!string.IsNullOrEmpty(request.Description))
            {
                branch.Description = request.Description;
            }

            if (request.Tags != null && request.Tags.Count > 0)
            {
                branch.Tags = request.Tags;
            }

            await manager.Store.SaveBranchAsync(sid, branch, ct);
            await manager.Store.SaveSessionAsync(session, ct);

            var dto = ToBranchDto(branch, sid);
            return ErrorResponses.Created($"/sessions/{sid}/branches/{branch.Id}", dto);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["CreateBranchError"] = [ex.Message]
            });
        }
    }

    private static async Task<IResult> ForkBranch(
        string sid,
        string bid,
        ForkBranchRequest request,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        try
        {
            // V3: Use session-level lock for atomic sibling updates
            return await manager.WithSessionLockAsync(sid, async () =>
            {
                var session = await manager.Store.LoadSessionAsync(sid, ct);
                if (session == null)
                {
                    return ErrorResponses.NotFound();
                }

                var sourceBranch = await manager.Store.LoadBranchAsync(sid, bid, ct);
                if (sourceBranch == null)
                {
                    return ErrorResponses.NotFound();
                }

                session.Store = manager.Store;
                sourceBranch.Session = session;

                // Use agent to fork branch (Agent has access to internal methods)
                var agent = await manager.GetOrCreateAgentAsync(sid, ct);
                var newBranch = await agent.ForkBranchAsync(sourceBranch, request.NewBranchId, request.FromMessageIndex, ct);

                // Set Name and Description from request
                if (!string.IsNullOrEmpty(request.Name))
                {
                    newBranch.Name = request.Name;
                }

                if (!string.IsNullOrEmpty(request.Description))
                {
                    newBranch.Description = request.Description;
                }

                if (request.Tags != null && request.Tags.Count > 0)
                {
                    newBranch.Tags = request.Tags;
                }

                // Note: newBranch is already saved by ForkBranchAsync
                var dto = ToBranchDto(newBranch, sid);
                return ErrorResponses.Created($"/sessions/{sid}/branches/{newBranch.Id}", dto);

            }, ct);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ForkBranchError"] = [ex.Message]
            });
        }
    }

    private static async Task<IResult> DeleteBranch(
        string sid,
        string bid,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        // 1. Protect "main" branch from deletion
        if (bid == "main")
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ProtectedBranch"] = ["Cannot delete the 'main' branch."]
            });
        }

        // 2. Load the branch to delete
        var branch = await manager.Store.LoadBranchAsync(sid, bid, ct);
        if (branch == null)
        {
            return ErrorResponses.NotFound();
        }

        // 3. V3: Prevent deletion if branch has children (referential integrity)
        if (branch.ChildBranches.Count > 0)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["HasChildren"] = [
                    $"Cannot delete branch with {branch.ChildBranches.Count} child branches. " +
                    $"Delete children first: {string.Join(", ", branch.ChildBranches)}"
                ]
            });
        }

        // 4. Check if branch is actively streaming (AspNetCore: check stream lock)
        // Try to acquire the stream lock - if we can't, the branch is actively streaming
        if (!manager.TryAcquireStreamLock(sid, bid))
        {
            return ErrorResponses.Conflict(
                "Branch is actively streaming and cannot be deleted. Try again later.");
        }

        // Release immediately - we just needed to check
        manager.ReleaseStreamLock(sid, bid);

        // 5. V3: Perform atomic deletion with sibling reindexing
        try
        {
            return await manager.WithSessionLockAsync(sid, async () =>
            {
                // 5a. Remove from parent's ChildBranches list
                if (branch.ForkedFrom != null)
                {
                    var parent = await manager.Store.LoadBranchAsync(sid, branch.ForkedFrom, ct);
                    if (parent != null && parent.ChildBranches.Contains(bid))
                    {
                        parent.ChildBranches.Remove(bid);
                        parent.LastActivity = DateTime.UtcNow;
                        await manager.Store.SaveBranchAsync(sid, parent, ct);
                    }
                }

                // 5b. Get all remaining siblings
                var branchIds = await manager.Store.ListBranchIdsAsync(sid, ct);
                var remainingSiblings = new List<Branch>();

                foreach (var branchId in branchIds)
                {
                    if (branchId == bid) continue; // Skip branch being deleted

                    var sibling = await manager.Store.LoadBranchAsync(sid, branchId, ct);
                    if (sibling != null &&
                        sibling.ForkedFrom == branch.ForkedFrom &&
                        sibling.ForkedAtMessageIndex == branch.ForkedAtMessageIndex)
                    {
                        remainingSiblings.Add(sibling);
                    }
                }

                // 5c. Sort siblings by current index (to maintain order)
                remainingSiblings = remainingSiblings
                    .OrderBy(b => b.SiblingIndex)
                    .ToList();

                // 5d. Reindex siblings (shift indices down)
                for (int i = 0; i < remainingSiblings.Count; i++)
                {
                    var sibling = remainingSiblings[i];

                    // Update sibling metadata
                    sibling.SiblingIndex = i;
                    sibling.TotalSiblings = remainingSiblings.Count;
                    sibling.LastActivity = DateTime.UtcNow;

                    // Update navigation pointers
                    sibling.PreviousSiblingId = i > 0
                        ? remainingSiblings[i - 1].Id
                        : null;

                    sibling.NextSiblingId = i < remainingSiblings.Count - 1
                        ? remainingSiblings[i + 1].Id
                        : null;

                    await manager.Store.SaveBranchAsync(sid, sibling, ct);
                }

                // 5e. Update session's LastActivity
                var session = await manager.Store.LoadSessionAsync(sid, ct);
                if (session != null)
                {
                    session.LastActivity = DateTime.UtcNow;
                    await manager.Store.SaveSessionAsync(session, ct);
                }

                // 5f. Delete the branch (after all updates complete)
                await manager.Store.DeleteBranchAsync(sid, bid, ct);

                return ErrorResponses.NoContent();

            }, ct);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["DeleteBranchError"] = [ex.Message]
            });
        }
    }

    private static async Task<IResult> GetMessages(
        string sid,
        string bid,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        try
        {
            var branch = await manager.Store.LoadBranchAsync(sid, bid, ct);
            if (branch == null)
            {
                return ErrorResponses.NotFound();
            }

            var messages = new List<MessageDto>();
            for (int i = 0; i < branch.MessageCount; i++)
            {
                var message = branch.Messages[i];
                messages.Add(new MessageDto(
                    $"msg-{i}",
                    message.Role.Value,
                    message.Text ?? "",
                    DateTime.UtcNow.ToString("O"))); // Note: Message timestamps not currently tracked
            }

            return ErrorResponses.Json(messages);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["GetMessagesError"] = [ex.Message]
            });
        }
    }

    /// <summary>
    /// V3: Get sibling branch metadata with full navigation info.
    /// Returns siblings sorted by SiblingIndex (deterministic ordering).
    /// </summary>
    private static async Task<IResult> GetSiblings(
        string sid,
        string bid,
        AspNetCoreSessionManager manager,
        CancellationToken ct = default)
    {
        try
        {
            // Load target branch
            var targetBranch = await manager.Store.LoadBranchAsync(sid, bid, ct);
            if (targetBranch == null)
            {
                return ErrorResponses.NotFound();
            }

            // Get all branches in session
            var branchIds = await manager.Store.ListBranchIdsAsync(sid, ct);
            var siblingDtos = new List<SiblingBranchDto>();

            // Filter siblings (same ForkedFrom + ForkedAtMessageIndex)
            foreach (var branchId in branchIds)
            {
                var branch = await manager.Store.LoadBranchAsync(sid, branchId, ct);
                if (branch == null) continue;

                // V3: CRITICAL - Check BOTH ForkedFrom AND ForkedAtMessageIndex
                bool isSibling = branch.ForkedFrom == targetBranch.ForkedFrom &&
                                 branch.ForkedAtMessageIndex == targetBranch.ForkedAtMessageIndex;

                if (isSibling)
                {
                    siblingDtos.Add(new SiblingBranchDto(
                        BranchId: branch.Id,
                        Name: branch.GetDisplayName(),
                        SiblingIndex: branch.SiblingIndex,
                        TotalSiblings: branch.TotalSiblings,
                        IsOriginal: branch.IsOriginal,
                        MessageCount: branch.MessageCount,
                        CreatedAt: branch.CreatedAt,
                        LastActivity: branch.LastActivity
                    ));
                }
            }

            // Sort by SiblingIndex (should already be correct, but guarantee it)
            siblingDtos = siblingDtos
                .OrderBy(s => s.SiblingIndex)
                .ToList();

            return ErrorResponses.Json(siblingDtos);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["GetSiblingsError"] = [ex.Message]
            });
        }
    }

    private static BranchDto ToBranchDto(Branch branch, string sessionId)
    {
        return new BranchDto(
            branch.Id,
            sessionId,
            branch.GetDisplayName(),
            branch.Description,
            branch.ForkedFrom,
            branch.ForkedAtMessageIndex,
            branch.CreatedAt,
            branch.LastActivity,
            branch.MessageCount,
            branch.Tags,
            branch.Ancestors,
            // V3: Tree navigation metadata
            branch.SiblingIndex,
            branch.TotalSiblings,
            branch.IsOriginal,
            branch.OriginalBranchId,
            branch.PreviousSiblingId,
            branch.NextSiblingId,
            branch.TotalForks);
    }
}

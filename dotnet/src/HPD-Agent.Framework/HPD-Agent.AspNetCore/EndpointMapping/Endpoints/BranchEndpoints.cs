using System.Text.Json;
using HPD.Agent;
using HPD.Agent.AspNetCore.Lifecycle;
using HPD.Agent.Hosting.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;

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

        // PATCH /sessions/{sid}/branches/{bid} - Update branch metadata
        endpoints.MapPatch("/sessions/{sid}/branches/{bid}", (string sid, string bid, UpdateBranchRequest request, CancellationToken ct) =>
                UpdateBranch(sid, bid, request, manager, ct))
            .WithName("UpdateBranch")
            .WithSummary("Update branch name, description, or tags");

        // DELETE /sessions/{sid}/branches/{bid} - Delete branch
        // Optional query param: ?recursive=true to delete the entire subtree
        endpoints.MapDelete("/sessions/{sid}/branches/{bid}", (string sid, string bid, bool recursive = false, CancellationToken ct = default) =>
                DeleteBranch(sid, bid, recursive, manager, ct))
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

                // Use agent to fork branch (Agent has access to internal methods)
                var agent = await manager.GetOrCreateAgentAsync(sid, ct);
                var newBranchId = string.IsNullOrWhiteSpace(request.NewBranchId)
                    ? Guid.NewGuid().ToString()
                    : request.NewBranchId;
                var newBranch = await agent.ForkBranchAsync(sourceBranch, newBranchId, request.FromMessageIndex, ct);

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

    private static async Task<IResult> UpdateBranch(
        string sid,
        string bid,
        UpdateBranchRequest request,
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

            return await manager.WithSessionLockAsync(sid, async () =>
            {
                if (request.Name != null) branch.Name = request.Name;
                if (request.Description != null) branch.Description = request.Description;
                if (request.Tags != null) branch.Tags = request.Tags;
                branch.LastActivity = DateTime.UtcNow;

                await manager.Store.SaveBranchAsync(sid, branch, ct);
                return ErrorResponses.Json(ToBranchDto(branch, sid));
            }, ct);
        }
        catch (Exception ex)
        {
            return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
            {
                ["UpdateBranchError"] = [ex.Message]
            });
        }
    }

    private static async Task<IResult> DeleteBranch(
        string sid,
        string bid,
        bool recursive,
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

        // 3. V3: Guard children — reject unless recursive is explicitly requested and permitted
        if (branch.ChildBranches.Count > 0)
        {
            if (!recursive)
            {
                return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["HasChildren"] = [
                        $"Cannot delete branch with {branch.ChildBranches.Count} child branches. " +
                        $"Use ?recursive=true to delete the entire subtree, or delete children first: " +
                        $"{string.Join(", ", branch.ChildBranches)}"
                    ]
                });
            }

            if (!manager.AllowRecursiveBranchDelete)
            {
                return ErrorResponses.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["RecursiveDeleteDisabled"] = [
                        "Recursive branch deletion is not enabled on this server. " +
                        "Set AllowRecursiveBranchDelete = true in HPDAgentConfig to enable it."
                    ]
                });
            }
        }

        // 4. Check if branch is actively streaming — acquire and HOLD the stream lock
        if (!manager.TryAcquireStreamLock(sid, bid))
        {
            return ErrorResponses.Conflict("StreamingInProgress",
                "Branch is actively streaming and cannot be deleted. Try again later.");
        }

        // 5. V3: Perform atomic deletion with sibling reindexing (stream lock held throughout)
        try
        {
            return await manager.WithSessionLockAsync(sid, async () =>
            {
                // 5a. Recursively delete all descendants first (if requested)
                if (recursive)
                {
                    foreach (var childId in branch.ChildBranches.ToList())
                        await DeleteSubtreeAsync(sid, childId, manager, ct);
                }

                // 5b. Reindex siblings and remove this branch from parent's ChildBranches
                await ReindexSiblingsAfterDeleteAsync(sid, bid, branch, manager, ct);

                // 5c. Update session's LastActivity
                var session = await manager.Store.LoadSessionAsync(sid, ct);
                if (session != null)
                {
                    session.LastActivity = DateTime.UtcNow;
                    await manager.Store.SaveSessionAsync(session, ct);
                }

                // 5d. Delete the branch (after all updates complete)
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
        finally
        {
            manager.ReleaseStreamLock(sid, bid);
            manager.RemoveBranchStreamLock(sid, bid);
        }
    }

    /// <summary>
    /// Depth-first recursive delete of a branch subtree.
    /// Deletes all descendants before deleting the given branch node.
    /// Caller is responsible for holding the session lock.
    /// </summary>
    private static async Task DeleteSubtreeAsync(
        string sid,
        string bid,
        AspNetCoreSessionManager manager,
        CancellationToken ct)
    {
        var branch = await manager.Store.LoadBranchAsync(sid, bid, ct);
        if (branch == null) return;

        // Depth-first: delete all children before this node
        foreach (var childId in branch.ChildBranches.ToList())
            await DeleteSubtreeAsync(sid, childId, manager, ct);

        // Reindex siblings and remove from parent pointer
        await ReindexSiblingsAfterDeleteAsync(sid, bid, branch, manager, ct);

        await manager.Store.DeleteBranchAsync(sid, bid, ct);
    }

    /// <summary>
    /// Removes a branch from its parent's ChildBranches list and reindexes
    /// the remaining siblings (SiblingIndex, TotalSiblings, navigation pointers).
    /// Caller is responsible for holding the session lock.
    /// </summary>
    private static async Task ReindexSiblingsAfterDeleteAsync(
        string sid,
        string bid,
        Branch branch,
        AspNetCoreSessionManager manager,
        CancellationToken ct)
    {
        // Remove from parent's ChildBranches list
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

        // Load remaining siblings (same forkedFrom + same forkedAtMessageIndex)
        var allBranchIds = await manager.Store.ListBranchIdsAsync(sid, ct);
        var remainingSiblings = new List<Branch>();

        foreach (var branchId in allBranchIds)
        {
            if (branchId == bid) continue;

            var sibling = await manager.Store.LoadBranchAsync(sid, branchId, ct);
            if (sibling != null &&
                sibling.ForkedFrom == branch.ForkedFrom &&
                sibling.ForkedAtMessageIndex == branch.ForkedAtMessageIndex)
            {
                remainingSiblings.Add(sibling);
            }
        }

        remainingSiblings = remainingSiblings.OrderBy(b => b.SiblingIndex).ToList();

        for (int i = 0; i < remainingSiblings.Count; i++)
        {
            var sibling = remainingSiblings[i];
            sibling.SiblingIndex = i;
            sibling.TotalSiblings = remainingSiblings.Count;
            sibling.PreviousSiblingId = i > 0 ? remainingSiblings[i - 1].Id : null;
            sibling.NextSiblingId = i < remainingSiblings.Count - 1 ? remainingSiblings[i + 1].Id : null;
            sibling.LastActivity = DateTime.UtcNow;
            await manager.Store.SaveBranchAsync(sid, sibling, ct);
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
                // Exclude UsageContent — billing metadata, not conversation content
                var contents = message.Contents
                    .Where(c => c is not UsageContent)
                    .ToList();
                messages.Add(new MessageDto(
                    message.MessageId ?? $"msg-{i}",
                    message.Role.Value,
                    contents,
                    message.AuthorName,
                    message.CreatedAt?.ToString("O") ?? DateTime.UtcNow.ToString("O")));
            }

            // Must use SessionJsonContext.CombinedOptions — plain JsonSerializerOptions lacks
            // the M.E.AI type resolver chain needed for AIContent $type polymorphism.
            var json = JsonSerializer.Serialize(messages, SessionJsonContext.CombinedOptions);
            return Results.Content(json, "application/json");
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
                        Id: branch.Id,
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

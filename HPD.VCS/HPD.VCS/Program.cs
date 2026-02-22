using System;
using System.IO;
using System.Threading.Tasks;
using HPD.VCS;
using HPD.VCS.Core;
using HPD.VCS.WorkingCopy;

namespace HPD.VCS.CLI;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            ShowUsage();
            return;
        }

        var command = args[0].ToLowerInvariant();
        var currentDir = Directory.GetCurrentDirectory();

        try
        {
            switch (command)
            {
                case "init":
                    await InitRepository(currentDir);
                    break;
                case "commit":
                    var message = args.Length > 1 ? args[1] : "Default commit message";
                    if (args.Length > 2 && args[1].ToLowerInvariant() == "-m")
                    {
                        message = args[2];
                    }
                    else if (args.Length > 1 && args[1].ToLowerInvariant() != "-m")
                    {
                        message = args[1];
                    }
                    else
                    {
                        message = "Default commit message";
                    }
                    await CommitChanges(currentDir, message);
                    break;
                case "log":
                    var limit = args.Length > 1 && int.TryParse(args[1], out var l) ? l : 10;
                    await ShowLog(currentDir, limit);
                    break;
                case "status":
                    await ShowStatus(currentDir);
                    break;
                case "checkout":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: checkout requires a commit ID");
                        return;
                    }
                    await CheckoutCommit(currentDir, args[1]);
                    break;                case "undo":
                    await UndoLastOperation(currentDir);
                    break;
                case "branch":
                    await HandleBranchCommand(currentDir, args);
                    break;
                case "merge":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: merge requires a branch or commit ID");
                        return;
                    }
                    await MergeBranch(currentDir, args[1]);
                    break;
                case "diff":
                    await ShowDiff(currentDir, args);
                    break;
                case "squash":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: squash requires <start-commit> <end-commit>");
                        return;
                    }
                    await SquashCommits(currentDir, args[1], args[2]);
                    break;
                case "describe":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: describe requires <commit-id> <new-message>");
                        return;
                    }
                    await DescribeCommit(currentDir, args[1], args[2]);
                    break;
                case "graph":
                    var graphLimit = args.Length > 1 && int.TryParse(args[1], out var gl) ? gl : 20;
                    await ShowGraph(currentDir, graphLimit);
                    break;
                case "resolve":
                    await ResolveConflicts(currentDir);
                    break;
                default:
                    Console.WriteLine($"Unknown command: {command}");
                    ShowUsage();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }    static void ShowUsage()
    {
        Console.WriteLine("VCS - Simple Version Control System");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  vcs init                              - Initialize a new repository");
        Console.WriteLine("  vcs commit [message]                  - Commit current changes");
        Console.WriteLine("  vcs log [limit]                       - Show commit history");
        Console.WriteLine("  vcs status                            - Show working copy status");
        Console.WriteLine("  vcs checkout <commit-id>              - Checkout a specific commit");
        Console.WriteLine("  vcs undo                              - Undo the last operation");
        Console.WriteLine();
        Console.WriteLine("Branch Management:");
        Console.WriteLine("  vcs branch                            - List all branches");
        Console.WriteLine("  vcs branch create <branch-name>       - Create a new branch");
        Console.WriteLine("  vcs branch delete <branch-name>       - Delete a branch");
        Console.WriteLine("  vcs branch switch <branch-name>       - Switch to a branch");
        Console.WriteLine();
        Console.WriteLine("Advanced Operations:");
        Console.WriteLine("  vcs merge <branch-or-commit>          - Merge a branch or commit");
        Console.WriteLine("  vcs diff <commit-id>                  - Show differences for a commit");
        Console.WriteLine("  vcs squash <start-commit> <end-commit> - Squash commits into one");
        Console.WriteLine("  vcs describe <commit-id> <new-message> - Update commit description");
        Console.WriteLine("  vcs graph [limit]                     - Show commit graph visualization");
        Console.WriteLine("  vcs resolve                           - Resolve merge conflicts");
    }

    static async Task InitRepository(string repoPath)
    {
        Console.WriteLine($"Initializing repository at {repoPath}...");
        
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        var repo = await Repository.InitializeAsync(repoPath, userSettings);
        
        Console.WriteLine("Repository initialized successfully!");
        Console.WriteLine($"Repository path: {repo.RepoPath}");
        
        repo.Dispose();
    }

    static async Task CommitChanges(string repoPath, string message)
    {
        Console.WriteLine($"Committing changes with message: '{message}'");
        
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        var snapshotOptions = new SnapshotOptions();
        
        var commitId = await repo.CommitAsync(message, userSettings, snapshotOptions);
        
        if (commitId.HasValue)
        {
            Console.WriteLine($"Commit created: {commitId.Value}");
        }
        else
        {
            Console.WriteLine("No changes to commit");
        }
        
        repo.Dispose();
    }

    static async Task ShowLog(string repoPath, int limit)
    {
        Console.WriteLine($"Showing last {limit} commits:");
        
        var repo = await Repository.LoadAsync(repoPath);
        var commits = await repo.LogAsync(limit);
        
        if (commits.Count == 0)
        {
            Console.WriteLine("No commits found");
        }        else
        {            foreach (var commit in commits)
            {
                var commitId = ObjectHasher.ComputeCommitId(commit);
                Console.WriteLine($"Commit: {commitId.ToHexString()}");
                Console.WriteLine($"Author: {commit.Author.Name} <{commit.Author.Email}>");
                Console.WriteLine($"Date: {commit.Author.Timestamp}");
                Console.WriteLine($"Message: {commit.Description}");
                Console.WriteLine();
            }
        }
        
        repo.Dispose();
    }

    static async Task ShowStatus(string repoPath)
    {
        Console.WriteLine("Working copy status:");
          var repo = await Repository.LoadAsync(repoPath);
        var status = await repo.GetStatusAsync();
        
        Console.WriteLine($"Current operation: {repo.CurrentOperationId}");
        
        if (repo.CurrentViewData.WorkspaceCommitIds.TryGetValue("default", out var workspaceCommit))
        {
            Console.WriteLine($"Current workspace commit: {workspaceCommit}");
        }
          if (status.ModifiedFiles.Count > 0)
        {
            Console.WriteLine("\nModified files:");
            foreach (var file in status.ModifiedFiles)
            {
                Console.WriteLine($"  M {file}");
            }
        }
        
        if (status.UntrackedFiles.Count > 0)
        {
            Console.WriteLine("\nUntracked files:");
            foreach (var file in status.UntrackedFiles)
            {
                Console.WriteLine($"  ? {file}");
            }
        }
        
        if (status.RemovedFiles.Count > 0)
        {
            Console.WriteLine("\nDeleted files:");
            foreach (var file in status.RemovedFiles)
            {
                Console.WriteLine($"  D {file}");
            }
        }
          if (status.ModifiedFiles.Count == 0 && status.UntrackedFiles.Count == 0 && status.RemovedFiles.Count == 0)
        {
            Console.WriteLine("Working copy is clean");
        }
        
        repo.Dispose();
    }

    static async Task CheckoutCommit(string repoPath, string commitIdString)
    {
        Console.WriteLine($"Checking out commit: {commitIdString}");
        
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        var checkoutOptions = new CheckoutOptions();
          // Parse the commit ID from hex string
        CommitId commitId;
        try
        {
            commitId = ObjectIdBase.FromHexString<CommitId>(commitIdString);
        }
        catch (Exception)
        {
            Console.WriteLine("Error: Invalid commit ID format");
            repo.Dispose();
            return;
        }
        var stats = await repo.CheckoutAsync(commitId, checkoutOptions, userSettings);
        
        Console.WriteLine($"Checkout completed:");
        Console.WriteLine($"Files updated: {stats.FilesUpdated}");
        Console.WriteLine($"Files added: {stats.FilesAdded}");
        Console.WriteLine($"Files removed: {stats.FilesRemoved}");
        
        repo.Dispose();
    }

    static async Task UndoLastOperation(string repoPath)
    {
        Console.WriteLine("Undoing last operation...");
        
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        
        var undoOperationId = await repo.UndoOperationAsync(userSettings);
        
        Console.WriteLine($"Undo operation created: {undoOperationId}");
        Console.WriteLine("Last operation has been undone");
        
        repo.Dispose();
    }    static async Task HandleBranchCommand(string repoPath, string[] args)
    {
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");

        if (args.Length < 2)
        {
            // List all branches
            Console.WriteLine("Branches:");
            foreach (var branch in repo.CurrentViewData.Branches)
            {
                // Check if this is the current branch by comparing with workspace commit
                var isCurrentBranch = repo.CurrentViewData.WorkspaceCommitIds.TryGetValue("default", out var workspaceCommitId) &&
                                    workspaceCommitId.Equals(branch.Value);
                var marker = isCurrentBranch ? "* " : "  ";
                Console.WriteLine($"{marker}{branch.Key}");
            }
        }
        else
        {
            var subCommand = args[1].ToLowerInvariant();
            switch (subCommand)
            {
                case "create":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: branch create requires a branch name");
                        break;
                    }
                    var newBranchName = args[2];
                    // Use current workspace commit as the starting point for the new branch
                    var currentCommitId = repo.CurrentViewData.WorkspaceCommitIds["default"];
                    await repo.CreateBranchAsync(newBranchName, currentCommitId);
                    Console.WriteLine($"Branch '{newBranchName}' created successfully");
                    break;

                case "delete":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: branch delete requires a branch name");
                        break;
                    }
                    var deleteBranchName = args[2];
                    await repo.DeleteBranchAsync(deleteBranchName);
                    Console.WriteLine($"Branch '{deleteBranchName}' deleted successfully");
                    break;

                case "switch":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: branch switch requires a branch name");
                        break;
                    }
                    var switchBranchName = args[2];
                    var branchCommitId = repo.GetBranch(switchBranchName);
                    if (branchCommitId.HasValue)
                    {
                        var checkoutOptions = new CheckoutOptions();
                        await repo.CheckoutAsync(branchCommitId.Value, checkoutOptions, userSettings);
                        Console.WriteLine($"Switched to branch '{switchBranchName}'");
                    }
                    else
                    {
                        Console.WriteLine($"Error: Branch '{switchBranchName}' not found");
                    }
                    break;

                default:
                    Console.WriteLine($"Unknown branch subcommand: {subCommand}");
                    Console.WriteLine("Usage: vcs branch [create|delete|switch] <branch-name>");
                    break;
            }
        }

        repo.Dispose();
    }

    static async Task MergeBranch(string repoPath, string branchName)
    {
        Console.WriteLine($"Merging branch '{branchName}' into current branch...");
        
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        
        try
        {
            var mergeCommitId = await repo.MergeAsync(branchName, userSettings);
            Console.WriteLine($"Merge completed successfully! Merge commit: {mergeCommitId}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("conflict"))
        {
            Console.WriteLine("Merge conflicts detected! Use 'vcs resolve' to resolve conflicts and complete the merge.");
        }
        
        repo.Dispose();
    }

    static async Task ShowDiff(string repoPath, string[] args)
    {
        var repo = await Repository.LoadAsync(repoPath);
        
        if (args.Length < 2)
        {
            Console.WriteLine("Error: diff requires a commit ID");
            repo.Dispose();
            return;
        }

        try
        {
            CommitId commitId = ObjectIdBase.FromHexString<CommitId>(args[1]);
            var diff = await repo.GetCommitDiffAsync(commitId);
            
            if (diff.Count == 0)
            {
                Console.WriteLine("No differences found");
            }
            else
            {
                Console.WriteLine($"Diff for commit {commitId}:");
                Console.WriteLine();
                
                foreach (var fileDiff in diff)
                {
                    Console.WriteLine($"--- {fileDiff.Key}");
                    Console.WriteLine(fileDiff.Value);
                    Console.WriteLine();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating diff: {ex.Message}");
        }
        
        repo.Dispose();
    }

    static async Task SquashCommits(string repoPath, string startCommit, string endCommit)
    {
        Console.WriteLine($"Squashing commits from {startCommit} to {endCommit}...");
        
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        
        try
        {
            CommitId targetCommitId = ObjectIdBase.FromHexString<CommitId>(endCommit);
            var operationId = await repo.SquashAsync(targetCommitId, userSettings);
            Console.WriteLine($"Commits squashed successfully! Operation: {operationId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error squashing commits: {ex.Message}");
        }
        
        repo.Dispose();
    }

    static async Task DescribeCommit(string repoPath, string commitIdString, string newMessage)
    {
        Console.WriteLine($"Updating description for commit {commitIdString}...");
        
        var repo = await Repository.LoadAsync(repoPath);
        var userSettings = new UserSettings("Demo User", "demo@example.com");
        
        try
        {
            CommitId commitId = ObjectIdBase.FromHexString<CommitId>(commitIdString);
            var operationId = await repo.DescribeAsync(commitId, newMessage, userSettings);
            Console.WriteLine($"Commit description updated successfully! Operation: {operationId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating commit description: {ex.Message}");
        }
        
        repo.Dispose();
    }

    static async Task ShowGraph(string repoPath, int limit)
    {
        Console.WriteLine($"Showing commit graph (last {limit} commits):");
        
        var repo = await Repository.LoadAsync(repoPath);
        
        try
        {
            var graphLog = await repo.GetGraphLogAsync(limit);
            
            if (graphLog.Count == 0)
            {
                Console.WriteLine("No commits found");
            }
            else
            {
                foreach (var (commit, edges) in graphLog)
                {
                    // Display commit info
                    var shortId = commit.RootTreeId.ToString()[..8];
                    Console.Write($"{shortId} ");
                      // Display graph edges
                    foreach (var edge in edges)
                    {
                        var edgeSymbol = edge.Type switch
                        {
                            VCS.Graphing.GraphEdgeType.Direct => "─",
                            VCS.Graphing.GraphEdgeType.Indirect => "┈",
                            VCS.Graphing.GraphEdgeType.Missing => "✗",
                            _ => "?"
                        };
                        Console.Write(edgeSymbol);
                    }
                    
                    Console.Write($" {commit.Description}");
                    Console.WriteLine($" ({commit.Author.Name}, {commit.Author.Timestamp:yyyy-MM-dd HH:mm})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating graph: {ex.Message}");
        }
        
        repo.Dispose();
    }

    static async Task ResolveConflicts(string repoPath)
    {
        Console.WriteLine("Conflict resolution is not yet implemented in this CLI demo.");
        Console.WriteLine("In a full implementation, this would:");
        Console.WriteLine("1. Detect merge conflicts in the working copy");
        Console.WriteLine("2. Present conflict markers to the user");
        Console.WriteLine("3. Allow manual or automated conflict resolution");
        Console.WriteLine("4. Complete the merge after conflicts are resolved");
        
        // For demonstration, we'll just show the current status
        await ShowStatus(repoPath);
    }
}
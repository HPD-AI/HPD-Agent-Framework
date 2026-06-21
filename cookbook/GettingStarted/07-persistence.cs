#:package HPD-Agent.Framework@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample uses one file-backed workspace for sessions, agent definitions, and content.

using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

// Put every durable artifact under one local folder so the sample can be
// deleted or inspected easily after it runs.
var dataRoot = Path.Combine(Directory.GetCurrentDirectory(), ".hpd-persistence");

// JsonWorkspaceStore is the shared file-backed backing store. The typed
// repositories below give the agent separate views for sessions, definitions,
// and content while still using the same workspace root.
var workspace = new JsonWorkspaceStore(Path.Combine(dataRoot, "workspace"));
var sessionRepository = new WorkspaceSessionRepository(workspace);
var agentRepository = new WorkspaceAgentRepository(workspace);
var contentStore = new WorkspaceContentStore(workspace);

// Seed a scoped knowledge item. The content store can later be queried by
// scope, role, path, or other metadata.
var note = await contentStore.WriteTextAsync(
    scope: "cookbook-persistence-agent",
    text: "HPD Agent can persist sessions, stored agent definitions, and scoped content.",
    metadata: new ContentMetadata
    {
        Name = "persistence-note.txt",
        ContentType = "text/plain",
        Origin = ContentSource.System,
        Role = WorkspaceContentStore.KnowledgeRole,
        PathHint = "/knowledge"
    });

// The agent id is the stable key for the stored definition. Persist-on-build
// writes the final serializable config into the agent repository.
var agent = await new AgentBuilder()
                    .WithAgentId("cookbook-persistence-agent")
                    .WithAgentRepository(agentRepository, persistOnBuild: true)
                    .WithSessionRepository(sessionRepository, persistAfterTurn: true)
                    .WithContentStore(contentStore)
                    .WithInstructions("You are a concise assistant.")
                    .WithOpenAI("gpt-5-mini")
                    .BuildAsync();

// Re-running the sample should continue using the same persisted session
// instead of failing on duplicate session creation.
if (await sessionRepository.LoadSessionAsync("cookbook-persistence") is null)
{
    await agent.CreateSessionAsync("cookbook-persistence");
}

// Because persistAfterTurn is enabled, this turn is saved after the run
// completes and can be loaded by the next process.
var result = await agent.RunAsync(
    "Remember that my release target is HPD Agent 0.5.5.",
    sessionId: "cookbook-persistence",
    threadId: "main");

Console.WriteLine(result.Text);
Console.WriteLine();

// Read the persisted indexes back directly to show what the workspace captured.
var sessionIds = await sessionRepository.ListSessionIdsAsync();
var threadIds = await sessionRepository.ListThreadIdsAsync("cookbook-persistence");
var agentIds = await agentRepository.ListIdsAsync();
var knowledge = await contentStore.QueryAsync(
    scope: "cookbook-persistence-agent",
    query: new ContentQuery
    {
        Role = WorkspaceContentStore.KnowledgeRole,
        PathHint = "/knowledge"
    });

Console.WriteLine($"Sessions: {string.Join(", ", sessionIds)}");
Console.WriteLine($"Threads: {string.Join(", ", threadIds)}");
Console.WriteLine($"Stored agents: {string.Join(", ", agentIds)}");
Console.WriteLine($"Knowledge items: {knowledge.Count}");
Console.WriteLine($"Latest content item: {note.Name}");
Console.WriteLine(await contentStore.ReadTextAsync("cookbook-persistence-agent", note.Id));

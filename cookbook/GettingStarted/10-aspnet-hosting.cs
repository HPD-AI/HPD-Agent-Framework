#:sdk Microsoft.NET.Sdk.Web
#:package HPD-Agent.AspNetCore@0.5.5
#:package HPD-Agent.Providers.OpenAI@0.5.5
#:property TargetFramework=net10.0

// This sample hosts an HPD Agent as an ASP.NET Core API.

using HPD.Agent;
using HPD.Agent.AspNetCore;
using HPD.Agent.Hosting.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRouting();

// Register one named hosted agent. The name selects this hosting configuration
// when routes call MapHPDAgentApi("cookbook-agent").
builder.Services.AddHPDAgent("cookbook-agent", config =>
{
    // Keep server state next to the sample so sessions and stored agent
    // definitions survive app restarts but remain easy to delete.
    var dataRoot = Path.Combine(Directory.GetCurrentDirectory(), ".hpd-hosting");

    // The hosting layer owns persistence. It passes the same session store to
    // agent runtimes when streaming requests arrive.
    config.SessionStorePath = Path.Combine(dataRoot, "sessions");
    config.AgentStore = new JsonAgentStore(Path.Combine(dataRoot, "agents"));
    config.PersistAfterTurn = true;

    // DefaultAgent is the serializable fallback definition used when a client
    // talks to an agent id that does not already exist in the agent store.
    config.DefaultAgent = new AgentConfig
    {
        Name = "Cookbook Agent",
        SystemInstructions = "You are a hosted HPD Agent. Be concise and helpful.",
        Clients = new AgentClientConfig
        {
            Chat = new ClientProviderConfig
            {
                ProviderKey = "openai",
                ModelName = "gpt-5-mini"
            }
        }
    };
});

var app = builder.Build();

app.MapGet("/", () => "HPD Agent is hosted. Try POST /hpd/sessions or the streaming endpoints.");

// Map the built-in HPD Agent API under /hpd: sessions, threads, content,
// streaming, middleware responses, and agent definition CRUD.
app.MapGroup("/hpd").MapHPDAgentApi("cookbook-agent", options =>
{
    // Keep the getting-started surface focused on chat/runtime hosting.
    // Evaluation endpoints can be enabled when you are ready to expose eval data.
    options.MapEvals = false;
});

app.Run();

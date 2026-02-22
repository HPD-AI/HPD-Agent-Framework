#:sdk Aspire.AppHost.Sdk@13.1.0
#:package Aspire.Hosting.JavaScript@13.1.0
#:property ManagePackageVersionsCentrally=false

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject("agent-api", "./AgentAPI/AgentAPI.csproj");

builder.AddViteApp("frontend", "./Frontend/frontend", "dev")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();

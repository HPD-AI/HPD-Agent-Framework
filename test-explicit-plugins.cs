#!/usr/bin/env dotnet-script
// Quick test to check explicitly registered plugins

var builder = new AgentBuilder(new AgentConfig { Name = "Test" });
builder.WithPlugin<FinancialAnalysisPlugin>();
builder.WithPlugin<FinancialAnalysisSkills>();

Console.WriteLine($"✨ Explicitly registered plugins: {string.Join(", ", builder._explicitlyRegisteredPlugins)}");
Console.WriteLine($"Count: {builder._explicitlyRegisteredPlugins.Count}");

#!/usr/bin/env dotnet-script
// Quick script to dump all tool schemas for debugging
// Usage: dotnet script dump_schemas.csx

using System;
using System.Text.Json;
using Microsoft.Extensions.AI;

// This would need to be run in context of your app
// For now, create a manual test that dumps schemas

Console.WriteLine("Schema dumper - integrate this into your Program.cs for debugging");
Console.WriteLine(@"
// Add this before calling the agent:
if (options?.Tools != null)
{
    for (int i = 0; i < options.Tools.Count; i++)
    {
        if (options.Tools[i] is AIFunction func)
        {
            Console.WriteLine($""Tool [{i}]: {func.Name}"");
            var schema = func.JsonSchema;
            Console.WriteLine(JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
        }
    }
}
");

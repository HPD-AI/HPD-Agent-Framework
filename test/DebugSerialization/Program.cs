using System;
using System.Text.Json;
using HPD.Agent;
using Microsoft.Extensions.AI;

var container = new MiddlewareStateContainer()
    .WithErrorTracking(new ErrorTrackingStateData { ConsecutiveFailures = 5 });

Console.WriteLine("=== Container Serialization Test ===");
var json = JsonSerializer.Serialize(container, AIJsonUtilities.DefaultOptions);
Console.WriteLine(json);
Console.WriteLine();

var state = AgentLoopState.Initial(
    messages: Array.Empty<ChatMessage>(),
    runId: "test",
    conversationId: "test",
    agentName: "Test")
    with { MiddlewareState = container };

Console.WriteLine("=== AgentLoopState Serialization Test ===");
var stateJson = JsonSerializer.Serialize(state, AIJsonUtilities.DefaultOptions);
if (stateJson.Contains("MiddlewareState"))
{
    Console.WriteLine("✅ Found 'MiddlewareState' in JSON");
}
else
{
    Console.WriteLine("❌ 'MiddlewareState' NOT FOUND in JSON");
}

Console.WriteLine();
Console.WriteLine("First 1000 chars:");
Console.WriteLine(stateJson.Substring(0, Math.Min(1000, stateJson.Length)));

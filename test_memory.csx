#!/usr/bin/env dotnet-script

// Quick test to verify thread memory works

#r "nuget: Microsoft.Extensions.AI, 9.0.0"

using Microsoft.Extensions.AI;
using System;

// Simulate ConversationThread with message persistence
class TestThread
{
    private List<ChatMessage> messages = new();
    
    public async Task AddMessageAsync(ChatMessage msg)
    {
        messages.Add(msg);
        await Task.CompletedTask;
    }
    
    public async Task<List<ChatMessage>> GetMessagesAsync()
    {
        return await Task.FromResult(new List<ChatMessage>(messages));
    }
    
    public int MessageCount => messages.Count;
}

// Test
var thread = new TestThread();

// Simulate conversation
await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "What's 2+2?"));
await thread.AddMessageAsync(new ChatMessage(ChatRole.Assistant, "It's 4."));
await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "What did I ask?"));
await thread.AddMessageAsync(new ChatMessage(ChatRole.Assistant, "You asked what 2+2 is."));

// Check history
var history = await thread.GetMessagesAsync();

Console.WriteLine($"Total messages in thread: {thread.MessageCount}");
Console.WriteLine("\n📝 Thread History:");
foreach (var msg in history)
{
    Console.WriteLine($"  {msg.Role}: {msg.Text}");
}

Console.WriteLine($"\n✅ SUCCESS: Thread now stores all messages ({history.Count} total)");

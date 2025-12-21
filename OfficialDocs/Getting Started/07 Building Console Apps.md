# Building Console Apps

> Get a console CLI running in under 2 minutes

HPD-Agent works natively in .NET console applications with no additional dependencies. Use `await foreach` to consume events and build interactive command-line tools.

## Quick Start

### 1. Install the Package

```bash
dotnet add package HPD.Agent
```

### 2. Create a Minimal Console App

```csharp
using HPD.Agent;
using HPD.Agent.Events;

// Configure the agent
var agent = new AgentBuilder()
    .WithProvider("anthropic", "claude-sonnet-4-5")
    .WithSystemInstructions("You are a helpful assistant.")
    .Build();

// Start conversation
var messages = new List<ChatMessage>();

while (true)
{
    // Get user input
    Console.Write("You: ");
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) break;

    messages.Add(new ChatMessage { Role = "user", Content = input });

    // Stream agent response
    Console.Write("Agent: ");
    await foreach (var evt in agent.RunAsync(messages))
    {
        switch (evt)
        {
            case TextDeltaEvent delta:
                Console.Write(delta.Text);
                break;

            case MessageTurnFinishedEvent:
                Console.WriteLine("\n");
                break;
        }
    }
}
```

### 3. Run It

```bash
dotnet run
```

That's it! You now have a working console agent.

## Next Steps

This basic example gets you started, but production console apps need:
- Tool execution indicators
- Permission prompts
- Error handling
- Ctrl+C cancellation
- Multi-turn conversation management

For complete patterns and best practices, see:

- [**Console Quick Start**](../Platform%20Guides/Console%20Apps/Console%20Quick%20Start.md) - Full-featured console app with all essential patterns
- [**User Prompts**](../Platform%20Guides/Console%20Apps/User%20Prompts.md) - Handling permissions and clarifications
- [**Event Handling Patterns**](../Platform%20Guides/Console%20Apps/Event%20Handling%20Patterns.md) - Advanced event consumption patterns
- [**Cancellation**](../Platform%20Guides/Console%20Apps/Cancellation.md) - Ctrl+C handling and graceful shutdown

## See Also

- [**Event Handling**](05%20Event%20Handling.md) - Understanding the event stream
- [**Building Web Apps**](08%20Building%20Web%20Apps.md) - SSE streaming for web/mobile

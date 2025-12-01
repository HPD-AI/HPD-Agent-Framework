// Simple test to verify middleware registration fix
using HPD.Agent;
using HPD.Agent.Middleware;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

Console.WriteLine("🧪 Testing Middleware Registration Fix...\n");

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("❌ OPENAI_API_KEY not set. Using dummy key for structural test.");
    apiKey = "sk-dummy-key-for-structure-test";
}

try
{
    var agent = new AgentBuilder()
        .WithProvider("openai", "gpt-4o-mini", apiKey)
        .WithLogging(loggerFactory) // Uses unified LoggingMiddleware
        .Build();

    Console.WriteLine("✅ Agent created successfully!");

    // Check if middlewares are registered
    var middlewares = agent.AgentMiddlewares;
    Console.WriteLine($"\n📊 Agent middlewares registered: {middlewares.Count}");

    if (middlewares.Count == 0)
    {
        Console.WriteLine("❌ NO agent middlewares registered! The fix didn't work.");
        return 1;
    }

    foreach (var middleware in middlewares)
    {
        Console.WriteLine($"   ✓ {middleware.GetType().Name}");
    }

    // Check if LoggingMiddleware is present
    bool hasLoggingMiddleware = middlewares.Any(m =>
        m.GetType().Name.Contains("Logging"));

    if (hasLoggingMiddleware)
    {
        Console.WriteLine("\n✅ SUCCESS! LoggingMiddleware is registered!");
        Console.WriteLine("✅ The unified middleware fix is working correctly!");
        return 0;
    }
    else
    {
        Console.WriteLine("\n❌ FAILED! LoggingMiddleware was not found!");
        return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ Test FAILED with exception: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    return 1;
}

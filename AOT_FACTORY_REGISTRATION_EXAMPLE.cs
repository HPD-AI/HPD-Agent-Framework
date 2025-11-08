// ==============================================================================
// AOT Factory Registration Example
// ==============================================================================
// This file shows how to register message store factories for Native AOT support
// Place this registration code at the very beginning of your application startup.
// ==============================================================================

using System;

namespace HPD_Agent_AOT_Example
{
    public class Startup
    {
        /// <summary>
        /// Call this FIRST in your application's Main() or startup method.
        /// This must be done before any ConversationThread deserialization occurs.
        /// </summary>
        public static void RegisterAOTFactories()
        {
            // Register the in-memory store factory
            // This enables AOT-friendly deserialization without reflection
            ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());

            // When you add more store types (e.g., DatabaseConversationMessageStore),
            // register their factories here too:
            // ConversationThread.RegisterStoreFactory(new DatabaseConversationMessageStoreFactory());
            // ConversationThread.RegisterStoreFactory(new RedisConversationMessageStoreFactory());
        }
    }

    // ==============================================================================
    // Example: Console Application
    // ==============================================================================
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // ✅ STEP 1: Register factories FIRST (required for Native AOT)
            Startup.RegisterAOTFactories();

            // ✅ STEP 2: Now you can use ConversationThread normally
            var thread = new ConversationThread();
            thread.DisplayName = "My Conversation";

            // Add messages
            await thread.AddMessageAsync(new ChatMessage(ChatRole.User, "Hello!"));

            // Serialize
            var snapshot = thread.Serialize();

            // Deserialize (uses registered factory - AOT-friendly!)
            var restored = ConversationThread.Deserialize(
                JsonSerializer.Deserialize<ConversationThreadSnapshot>(snapshot.GetRawText())!
            );

            Console.WriteLine($"Restored thread: {restored.DisplayName}");
            Console.WriteLine($"Message count: {await restored.GetMessageCountAsync()}");
        }
    }

    // ==============================================================================
    // Example: ASP.NET Core Application
    // ==============================================================================
    public class AspNetCoreStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            // ✅ Register factories in ConfigureServices or Program.cs
            ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());

            // ... rest of service configuration
        }
    }

    // ==============================================================================
    // Example: Worker Service / Background Service
    // ==============================================================================
    public class WorkerStartup
    {
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    // ✅ Register factories
                    ConversationThread.RegisterStoreFactory(new InMemoryConversationMessageStoreFactory());

                    services.AddHostedService<Worker>();
                });
    }
}

// ==============================================================================
// Why Is This Needed?
// ==============================================================================
//
// Native AOT compilation removes reflection capabilities to reduce binary size
// and improve startup time. The old deserialization code used:
//
//   Type.GetType(typeName)  // ❌ Fails in AOT
//   Activator.CreateInstance(type, args)  // ❌ Fails in AOT
//
// The factory pattern replaces reflection with explicit registration:
//
//   1. You register factories at startup (once)
//   2. Deserialization looks up the factory by type name
//   3. Factory creates instance directly (no reflection)
//
// ==============================================================================
// Fallback Behavior
// ==============================================================================
//
// If you DON'T register factories:
// - Non-AOT builds: Falls back to reflection (works, but slower)
// - Native AOT builds: Throws helpful error message:
//   "Cannot find message store type: X. For Native AOT, register a factory
//    via ConversationThread.RegisterStoreFactory() before deserializing."
//
// ==============================================================================
// Testing AOT Compatibility
// ==============================================================================
//
// To test Native AOT compilation:
//
// 1. Add to your .csproj:
//    <PublishAot>true</PublishAot>
//
// 2. Publish:
//    dotnet publish -c Release -r osx-arm64
//
// 3. Run the binary:
//    ./bin/Release/net9.0/osx-arm64/publish/YourApp
//
// If factories aren't registered, you'll get a runtime error on deserialization.
//
// ==============================================================================

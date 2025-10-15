# HPD-Agent Developer Documentation

## 📋 Table of Contents
1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Core Components](#core-components)
4. [Configuration System](#configuration-system)
5. [Permission System](#permission-system)
6. [AGUI Event System](#agui-event-system)
7. [Provider Integration](#provider-integration)
8. [Error Handling](#error-handling)
9. [Usage Examples](#usage-examples)
10. [Best Practices](#best-practices)

---

## Overview

The HPD-Agent framework is a sophisticated, Microsoft.Extensions.AI-compliant agent system that provides enterprise-grade AI capabilities with comprehensive configuration, permission management, and multi-provider support.

### Key Features
- ✅ **Full Microsoft.Extensions.AI Compliance**: Implements `IChatClient` interface with complete service discovery
- 🔐 **Advanced Permission System**: Fine-grained control over tool/function execution
- 🌐 **Multi-Provider Support**: OpenAI, Azure OpenAI, OpenRouter, Ollama
- 📊 **Statistics & Telemetry**: Built-in tracking and observability
- 🎯 **AGUI Streaming**: Real-time event streaming for interactive UIs
- 🛡️ **Error Normalization**: Consistent error handling across providers
- ⚙️ **Configuration Validation**: Comprehensive validation with FluentValidation

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        Agent (IChatClient)                   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │              Microsoft.Extensions.AI                 │   │
│  │  • ChatClientMetadata  • Service Discovery          │   │
│  │  • Statistics Tracking • Error Handling Policy      │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │Message       │  │Function Call │  │AGUI Event    │     │
│  │Processor     │  │Processor     │  │Handler       │     │
│  └──────────────┘  └──────────────┘  └──────────────┘     │
│                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │Permission    │  │Tool          │  │Capability    │     │
│  │Filters       │  │Scheduler     │  │Manager       │     │
│  └──────────────┘  └──────────────┘  └──────────────┘     │
└─────────────────────────────────────────────────────────────┘
```

---

## Core Components

### 1. Agent Class (`Agent.cs`)

The central facade that implements `IChatClient` and orchestrates all agent capabilities.

#### Key Properties

```csharp
public class Agent : IChatClient
{
    // Configuration
    public AgentConfig? Config { get; }

    // Microsoft.Extensions.AI Compliance
    public ChatClientMetadata Metadata { get; }
    public AgentStatistics Statistics { get; }
    public ErrorHandlingPolicy ErrorPolicy { get; }

    // Provider Information
    public ChatProvider Provider { get; }
    public string? ModelId { get; }
    public string? ConversationId { get; }

    // Default chat options
    public ChatOptions? DefaultOptions { get; }
}
```

#### Service Discovery

The Agent supports Microsoft.Extensions.AI service discovery pattern:

```csharp
// Get services from the agent
var metadata = agent.GetService<ChatClientMetadata>();
var statistics = agent.GetService<AgentStatistics>();
var config = agent.GetService<AgentConfig>();
var errorPolicy = agent.GetService<ErrorHandlingPolicy>();
```

#### Main Methods

- `GetResponseAsync()` - Get a single response (non-streaming)
- `GetStreamingResponseAsync()` - Get streaming responses
- `ExecuteStreamingTurnAsync()` - Execute a full turn with tool calls
- `RegisterCapability()` - Add custom capabilities
- `ResetStatistics()` - Reset telemetry counters

### 2. AgentBuilder Class (`AgentBuilder.cs`)

Fluent builder pattern for constructing agents with sophisticated configuration.

#### Basic Usage

```csharp
var agent = new AgentBuilder(agentConfig)
    .WithAPIConfiguration(configuration)  // IConfiguration for API keys
    .WithProvider(ChatProvider.OpenAI, "gpt-4o")
    .WithDefaultOptions(chatOptions)
    .WithPlugin<WeatherPlugin>()         // Add plugins
    .WithTavilyWebSearch()               // Add web search
    .WithInjectedMemory(opts => opts
        .WithStorageDirectory("./memory")
        .WithMaxTokens(6000))
    .WithConsolePermissions()            // Interactive permissions
    .WithMCP("./MCP.json")              // Model Context Protocol
    .Build();
```

#### Builder Methods

- **Provider Configuration**
  - `WithProvider()` - Set the AI provider and model
  - `WithAPIConfiguration()` - Load API keys from IConfiguration
  - `WithDefaultOptions()` - Set default ChatOptions

- **Plugin Management**
  - `WithPlugin<T>()` - Register typed plugins
  - `WithPluginDirectory()` - Load plugins from directory
  - `WithFunction()` - Add individual functions

- **Capabilities**
  - `WithInjectedMemory()` - Add persistent memory
  - `WithTavilyWebSearch()` - Enable web search
  - `WithMCP()` - Enable Model Context Protocol

- **Filters & Permissions**
  - `WithConsolePermissions()` - Interactive permission prompts
  - `WithAGUIPermissions()` - GUI-based permissions
  - `WithAutoApprove()` - Auto-approve specific functions
  - `WithPromptFilter()` - Add prompt transformation filters
  - `WithAiFunctionFilter()` - Add function execution filters

- **Observability**
  - `WithLogging()` - Enable logging
  - `WithOpenTelemetry()` - Add telemetry
  - `WithDistributedCache()` - Enable caching

---

## Configuration System

### AgentConfig Class (`AgentConfig.cs`)

Central configuration object that can be serialized/deserialized from JSON.

```csharp
public class AgentConfig
{
    // Core Settings
    public string Name { get; set; }
    public string? SystemInstructions { get; set; }
    public int MaxFunctionCallTurns { get; set; } = 10;
    public int MaxConversationHistory { get; set; } = 100;

    // Provider Configuration
    public ProviderConfig? Provider { get; set; }

    // Feature Configurations
    public InjectedMemoryConfig? InjectedMemory { get; set; }
    public McpConfig? Mcp { get; set; }
    public WebSearchConfig? WebSearch { get; set; }
    public ErrorHandlingConfig? ErrorHandling { get; set; }
}
```

### Provider Configuration

```csharp
public class ProviderConfig
{
    public ChatProvider Provider { get; set; }
    public string ModelName { get; set; }
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public ChatOptions? DefaultChatOptions { get; set; }
}
```

### Provider-Specific Settings

Each provider can have specific settings accessible via `AdditionalProperties`:

```csharp
// OpenAI-specific
public class OpenAISettings
{
    public string? Organization { get; set; }
    public bool? StrictJsonSchema { get; set; }
    public string? ImageDetail { get; set; }  // for vision models
    public string? AudioVoice { get; set; }    // for audio models
}

// Azure OpenAI-specific
public class AzureOpenAISettings
{
    public string? ResourceName { get; set; }
    public string? DeploymentName { get; set; }
    public string ApiVersion { get; set; } = "2024-08-01-preview";
    public bool UseEntraId { get; set; }
}

// Ollama-specific
public class OllamaSettings
{
    public int? NumCtx { get; set; }          // Context window
    public string? KeepAlive { get; set; }    // Model keep-alive time
    public bool? UseMlock { get; set; }       // Memory locking
}

// OpenRouter-specific
public class OpenRouterSettings
{
    public string? HttpReferer { get; set; }
    public string? AppName { get; set; }
    public OpenRouterReasoningConfig? Reasoning { get; set; }
}
```

### Configuration from JSON

```json
{
  "Name": "ProductionAgent",
  "SystemInstructions": "You are a helpful assistant.",
  "MaxFunctionCallTurns": 10,
  "Provider": {
    "Provider": "OpenAI",
    "ModelName": "gpt-4o",
    "DefaultChatOptions": {
      "Temperature": 0.7,
      "MaxOutputTokens": 2048
    }
  },
  "InjectedMemory": {
    "StorageDirectory": "./agent-memory",
    "MaxTokens": 6000,
    "EnableAutoEviction": true,
    "AutoEvictionThreshold": 85
  },
  "ErrorHandling": {
    "NormalizeErrors": true,
    "MaxRetries": 3,
    "IncludeProviderDetails": false
  }
}
```

### Configuration Validation

The framework includes comprehensive validation using FluentValidation:

```csharp
public class AgentConfigValidator : AbstractValidator<AgentConfig>
{
    // Validates:
    // - Required fields (Name, Provider, ModelName)
    // - Value ranges (MaxFunctionCalls: 1-50)
    // - Provider-specific requirements (Azure needs endpoint)
    // - Cross-configuration compatibility
    // - Resource limit safety checks
}
```

---

## Permission System

The permission system provides fine-grained control over tool/function execution with multiple implementation options.

### IPermissionFilter Interface

```csharp
public interface IPermissionFilter : IAiFunctionFilter
{
    // Inherits IAiFunctionFilter for pipeline integration
    Task InvokeAsync(AiFunctionContext context, Func<AiFunctionContext, Task> next);
}
```

### Available Permission Filters

#### 1. ConsolePermissionFilter
Interactive console prompts for function approval:

```csharp
builder.WithConsolePermissions();

// Runtime behavior:
// [PERMISSION REQUIRED]
// Function: WebSearch
// Description: Search the web using Tavily
// Arguments:
//   query: "latest AI news"
//
// Choose an option:
//   [A]llow once
//   [D]eny once
//   [Y] Always allow (Global)
//   [N] Never allow (Global)
```

#### 2. AGUIPermissionFilter
GUI-based permission system with streaming events:

```csharp
builder.WithAGUIPermissions(channel);

// Sends permission events through AGUI channel
// Frontend can approve/deny through UI
```

#### 3. AutoApprovePermissionFilter
Automatically approve specific functions:

```csharp
builder.WithAutoApprove("WebSearch", "GetWeather");
// These functions will be auto-approved
```

### Permission Storage

Permissions can be persisted using `IPermissionStorage`:

```csharp
public interface IPermissionStorage
{
    Task<PermissionDecision> GetPermissionAsync(
        string conversationId,
        string functionName);

    Task SavePermissionAsync(
        string conversationId,
        string functionName,
        PermissionDecision decision);
}
```

---

## AGUI Event System

The AGUI (Agent GUI) system provides real-time streaming events for interactive UIs.

### Event Types

```csharp
// Base event
public abstract class BaseEvent
{
    public string Type { get; set; }
    public long Timestamp { get; set; }
}

// Specific events
public class MessageStartEvent : BaseEvent
public class MessageDeltaEvent : BaseEvent
public class ToolCallStartEvent : BaseEvent
public class ToolCallResultEvent : BaseEvent
public class PermissionRequestEvent : BaseEvent
public class ErrorEvent : BaseEvent
public class TurnCompleteEvent : BaseEvent
```

### AGUI Streaming

```csharp
// Execute with AGUI streaming
var channel = Channel.CreateUnbounded<BaseEvent>();
var turnResult = await agent.ExecuteStreamingTurnAsync(
    messages,
    options,
    aguiChannel: channel);

// Consume events
await foreach (var evt in channel.Reader.ReadAllAsync())
{
    switch (evt)
    {
        case MessageStartEvent msg:
            // Handle message start
            break;
        case MessageDeltaEvent delta:
            // Handle content delta
            break;
        case ToolCallStartEvent tool:
            // Handle tool call
            break;
    }
}
```

### Event Serialization

Events use System.Text.Json source generation for AOT compatibility:

```csharp
var json = EventSerialization.SerializeEvent(aguiEvent);
// Properly serialized with polymorphic type handling
```

---

## Provider Integration

### Supported Providers

1. **OpenAI** - GPT-4, GPT-3.5, o1 models
2. **Azure OpenAI** - Deployed models on Azure
3. **OpenRouter** - Access to 100+ models
4. **Ollama** - Local model execution

### Provider Resolution

```csharp
// API keys resolved in order:
// 1. Explicit in ProviderConfig.ApiKey
// 2. From IConfiguration (appsettings.json)
// 3. From environment variables

var agent = new AgentBuilder(config)
    .WithAPIConfiguration(configuration) // Uses IConfiguration
    .Build();
```

### Provider URIs

The framework automatically resolves provider URIs:

```csharp
private static Uri? ResolveProviderUri(ProviderConfig? provider)
{
    return provider?.Provider switch
    {
        ChatProvider.OpenAI => new Uri("https://api.openai.com"),
        ChatProvider.OpenRouter => new Uri("https://openrouter.ai/api"),
        ChatProvider.Ollama => new Uri("http://localhost:11434"),
        ChatProvider.AzureOpenAI => new Uri(provider.Endpoint), // Required
        _ => null
    };
}
```

---

## Error Handling

### ErrorHandlingPolicy

Normalizes provider-specific errors into consistent formats:

```csharp
public class ErrorHandlingPolicy
{
    public bool NormalizeProviderErrors { get; set; }
    public bool IncludeProviderDetails { get; set; }
    public int MaxRetries { get; set; }

    public ErrorContent NormalizeError(Exception ex, ChatProvider provider);
    public bool IsTransientError(Exception ex, ChatProvider provider);
}
```

### Error Normalization Examples

```csharp
// OpenAI rate limit → Normalized
"Rate limit exceeded. Please try again later." (ErrorCode: "RateLimit")

// Azure unauthorized → Normalized
"Authentication failed. Check your API key." (ErrorCode: "AuthenticationFailed")

// Ollama model not found → Normalized
"Model not found. Please pull the model first." (ErrorCode: "ModelNotFound")
```

---

## Usage Examples

### Basic Chat

```csharp
// Simple configuration
var config = new AgentConfig
{
    Name = "Assistant",
    Provider = new ProviderConfig
    {
        Provider = ChatProvider.OpenAI,
        ModelName = "gpt-4o"
    }
};

// Build agent
var agent = new AgentBuilder(config)
    .WithAPIConfiguration(configuration)
    .Build();

// Use agent
var response = await agent.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Hello!")]);

Console.WriteLine(response.Message.Text);
```

### Advanced with Tools and Permissions

```csharp
var agent = new AgentBuilder(config)
    .WithAPIConfiguration(configuration)
    .WithPlugin<WeatherPlugin>()
    .WithPlugin<MathPlugin>()
    .WithTavilyWebSearch()
    .WithConsolePermissions()  // Interactive permissions
    .WithInjectedMemory(opts => opts
        .WithStorageDirectory("./memory")
        .WithMaxTokens(6000))
    .Build();

// Tools will request permission before execution
var response = await agent.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "What's the weather in NYC?")]);
```

### Streaming with AGUI

```csharp
// Create AGUI channel
var channel = Channel.CreateUnbounded<BaseEvent>();

// Execute streaming turn
var turnResult = await agent.ExecuteStreamingTurnAsync(
    messages,
    options,
    aguiChannel: channel);

// Process events in real-time
await foreach (var evt in channel.Reader.ReadAllAsync())
{
    if (evt is MessageDeltaEvent delta)
    {
        Console.Write(delta.Delta);
    }
}

// Get final result
var finalMessages = await turnResult.FinalHistory;
```

### With Statistics Tracking

```csharp
// Execute requests
await agent.GetResponseAsync(messages);

// Check statistics
var stats = agent.Statistics;
Console.WriteLine($"Total Requests: {stats.TotalRequests}");
Console.WriteLine($"Tokens Used: {stats.TotalTokensUsed}");
Console.WriteLine($"Tool Calls: {stats.TotalToolCalls}");
Console.WriteLine($"Average Processing Time: {stats.AverageProcessingTime}");

// Reset if needed
agent.ResetStatistics();
```

---

## Best Practices

### 1. Configuration Management

✅ **DO:**
- Store configuration in `appsettings.json` or environment variables
- Use configuration validation before building agents
- Keep API keys secure (never commit to source control)

❌ **DON'T:**
- Hardcode API keys or sensitive configuration
- Skip validation for user-provided configurations

### 2. Permission Handling

✅ **DO:**
- Use appropriate permission filters for your environment
- Implement custom `IPermissionStorage` for persistence
- Consider user experience when choosing permission strategy

❌ **DON'T:**
- Auto-approve all functions in production
- Ignore permission denied responses

### 3. Error Handling

✅ **DO:**
- Enable error normalization for consistent handling
- Implement retry logic for transient errors
- Log errors for debugging

❌ **DON'T:**
- Expose raw provider errors to end users
- Retry non-transient errors

### 4. Resource Management

✅ **DO:**
- Set reasonable limits for `MaxFunctionCallTurns`
- Monitor token usage via Statistics
- Use memory management for long conversations

❌ **DON'T:**
- Allow unlimited function call recursion
- Ignore token limits

### 5. Streaming

✅ **DO:**
- Use streaming for better user experience
- Process AGUI events asynchronously
- Handle partial responses gracefully

❌ **DON'T:**
- Block on streaming operations
- Ignore error events in the stream

### 6. Testing

✅ **DO:**
- Test with different providers
- Validate configuration before deployment
- Test permission flows

❌ **DON'T:**
- Assume all providers behave identically
- Skip testing error scenarios

---

## Advanced Topics

### Custom Capabilities

```csharp
public interface ICapability
{
    string Name { get; }
    Task<object?> ExecuteAsync(object? input);
}

// Register custom capability
agent.RegisterCapability("custom", new CustomCapability());
```

### Custom Filters

```csharp
public class CustomFilter : IPromptFilter
{
    public Task TransformAsync(PromptFilterContext context)
    {
        // Transform prompts before sending
        context.SystemInstructions += "\nAlways be polite.";
        return Task.CompletedTask;
    }
}

builder.WithPromptFilter(new CustomFilter());
```

### Service Extensions

```csharp
// Get internal services
var scopedFilterManager = agent.GetService<ScopedFilterManager>();
var capabilityManager = agent.GetService<CapabilityManager>();

// Extend functionality
scopedFilterManager.AddFilter("custom", customFilter);
```

---

## Troubleshooting

### Common Issues

1. **"Model not found"**
   - Verify model name is correct for the provider
   - Check if you have access to the model
   - For OpenAI, ensure organization is verified

2. **"Rate limit exceeded"**
   - Enable retry logic in ErrorHandlingConfig
   - Implement exponential backoff
   - Consider using multiple API keys

3. **"Context length exceeded"**
   - Use InjectedMemory with auto-eviction
   - Reduce MaxConversationHistory
   - Summarize long conversations

4. **Permission denied loops**
   - Check IPermissionStorage implementation
   - Verify permission filter ordering
   - Clear stored permissions if needed

---

## Migration Guide

### From Raw IChatClient

```csharp
// Before
IChatClient client = new ChatClient("gpt-4", apiKey);
var response = await client.GetResponseAsync(messages);

// After
var agent = new AgentBuilder(new AgentConfig
{
    Name = "Assistant",
    Provider = new ProviderConfig
    {
        Provider = ChatProvider.OpenAI,
        ModelName = "gpt-4"
    }
})
.WithAPIConfiguration(configuration)
.Build();

var response = await agent.GetResponseAsync(messages);
// Plus: statistics, permissions, error handling, etc.
```

### From Semantic Kernel

```csharp
// Semantic Kernel
var kernel = Kernel.CreateBuilder()
    .AddOpenAIChatCompletion("gpt-4", apiKey)
    .Build();

// HPD-Agent (similar builder pattern)
var agent = new AgentBuilder(config)
    .WithProvider(ChatProvider.OpenAI, "gpt-4")
    .WithPlugin<MyPlugin>()
    .Build();
```

---

## Contributing

When extending the Agent framework:

1. Follow existing patterns (delegation, separation of concerns)
2. Maintain Microsoft.Extensions.AI compatibility
3. Add appropriate unit tests
4. Update documentation
5. Consider AOT compatibility (avoid reflection where possible)

---

## Support

For issues or questions:
- Check the troubleshooting section
- Review the examples
- Examine the unit tests for usage patterns
- File an issue with detailed reproduction steps

---

*This documentation covers HPD-Agent v1.0 with Microsoft.Extensions.AI compliance.*
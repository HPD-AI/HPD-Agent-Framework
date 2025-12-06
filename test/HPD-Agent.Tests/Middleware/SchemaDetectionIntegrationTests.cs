// Copyright (c) 2025 Einstein Essibu. All rights reserved.

using HPD.Agent;
using HPD.Agent.Checkpointing;
using HPD.Agent.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using Xunit;

namespace HPD.Agent.Tests.Middleware;

/// <summary>
/// Integration tests for middleware state schema detection during checkpoint resume.
/// Tests the runtime validation logic in Agent.ValidateAndMigrateSchema().
/// </summary>
public class SchemaDetectionIntegrationTests : AgentTestBase
{
    private readonly TestLoggerProvider _loggerProvider = new();
    private readonly TestEventObserver _eventObserver = new();

    [Fact]
    public async Task Resume_WithPreVersioningCheckpoint_UpgradesSchema()
    {
        // Arrange: Create checkpoint without schema metadata (simulate old version)
        var preVersioningState = CreatePreVersioningCheckpoint();
        var thread = CreateThreadWithCheckpoint(preVersioningState);
        var agent = CreateTestAgentWithLogging();

        // Act: Resume from pre-versioning checkpoint
        await foreach (var evt in agent.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        // Wait a bit for async observer notifications to complete
        await Task.Delay(100);

        // Assert: Schema upgraded and logged
        var logs = _loggerProvider.GetLogs();
        Assert.Contains(logs, log =>
            log.LogLevel == LogLevel.Information &&
            log.Message.Contains("before schema versioning"));

        // Assert: SchemaChangedEvent emitted
        var schemaEvents = _eventObserver.GetEvents<SchemaChangedEvent>();
        Assert.Single(schemaEvents);
        var schemaEvent = schemaEvents[0];
        Assert.Null(schemaEvent.OldSignature);
        Assert.NotNull(schemaEvent.NewSignature);
        Assert.True(schemaEvent.IsUpgrade);
    }

    [Fact]
    public async Task Resume_WithRemovedMiddleware_LogsWarning()
    {
        // Arrange: Checkpoint with middleware that no longer exists
        var checkpointWithOldMiddleware = CreateCheckpointWithRemovedMiddleware();
        var thread = CreateThreadWithCheckpoint(checkpointWithOldMiddleware);

        var agent = CreateTestAgentWithLogging();

        // Act: Resume
        await foreach (var evt in agent.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        // Assert: Warning logged
        var logs = _loggerProvider.GetLogs();
        Assert.Contains(logs, log =>
            log.LogLevel == LogLevel.Warning &&
            log.Message.Contains("no longer exist") &&
            log.Message.Contains("discarded"));

        // Assert: SchemaChangedEvent emitted with removed types
        var schemaEvents = _eventObserver.GetEvents<SchemaChangedEvent>();
        Assert.Single(schemaEvents);
        var schemaEvent = schemaEvents[0];
        Assert.NotEmpty(schemaEvent.RemovedTypes);
        Assert.False(schemaEvent.IsUpgrade);
    }

    [Fact]
    public async Task Resume_WithAddedMiddleware_LogsInfo()
    {
        // Arrange: Checkpoint without new middleware
        var checkpointBeforeNewMiddleware = CreateCheckpointWithFewerMiddleware();
        var thread = CreateThreadWithCheckpoint(checkpointBeforeNewMiddleware);

        var agent = CreateTestAgentWithLogging();

        // Act: Resume (agent now has more middleware)
        await foreach (var evt in agent.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        // Assert: Info logged
        var logs = _loggerProvider.GetLogs();
        Assert.Contains(logs, log =>
            log.LogLevel == LogLevel.Information &&
            log.Message.Contains("new middleware") &&
            log.Message.Contains("defaults"));

        // Assert: SchemaChangedEvent emitted with added types
        var schemaEvents = _eventObserver.GetEvents<SchemaChangedEvent>();
        Assert.Single(schemaEvents);
        var schemaEvent = schemaEvents[0];
        Assert.NotEmpty(schemaEvent.AddedTypes);
        Assert.False(schemaEvent.IsUpgrade);
    }

    [Fact]
    public async Task Resume_WithUnchangedSchema_NoLogging()
    {
        // Arrange: Checkpoint with current schema
        var currentCheckpoint = CreateCheckpointWithCurrentSchema();
        var thread = CreateThreadWithCheckpoint(currentCheckpoint);

        var agent = CreateTestAgentWithLogging();

        // Act: Resume
        await foreach (var evt in agent.RunAsync(
            Array.Empty<ChatMessage>(),
            options: null,
            thread: thread,
            cancellationToken: TestCancellationToken))
        {
            // Consume events
        }

        // Assert: No schema-related logs
        var logs = _loggerProvider.GetLogs();
        Assert.DoesNotContain(logs, log =>
            log.Message.Contains("schema", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("middleware", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("versioning", StringComparison.OrdinalIgnoreCase));

        // Assert: No SchemaChangedEvent emitted
        var schemaEvents = _eventObserver.GetEvents<SchemaChangedEvent>();
        Assert.Empty(schemaEvents);
    }

    //      
    // HELPER METHODS
    //      

    private Agent CreateTestAgentWithLogging()
    {
        var client = new FakeChatClient();
        client.EnqueueTextResponse("Test response");

        var config = DefaultConfig();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_loggerProvider));
        var providerRegistry = new TestProviderRegistry(client);

        // Create service provider with ILoggerFactory registered
        // This is required because Agent retrieves ILoggerFactory from the service provider
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        var serviceProvider = services.BuildServiceProvider();

        var agent = new AgentBuilder(config, providerRegistry)
            .WithObserver(_eventObserver)
            .WithServiceProvider(serviceProvider)
            .Build(CancellationToken.None).GetAwaiter().GetResult();

        return agent;
    }

    private ConversationThread CreateThreadWithCheckpoint(AgentLoopState checkpoint)
    {
        var thread = new ConversationThread()
        {
            ExecutionState = checkpoint
        };

        return thread;
    }

    private AgentLoopState CreatePreVersioningCheckpoint()
    {
        // Create a checkpoint without schema metadata (SchemaSignature = null)
        var middlewareState = new MiddlewareState
        {
            States = ImmutableDictionary<string, object?>.Empty,
            SchemaSignature = null,  // Pre-versioning
            SchemaVersion = 0,
            StateVersions = null
        };

        return new AgentLoopState
        {
            RunId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            AgentName = "test-agent",
            StartTime = DateTime.UtcNow,
            CurrentMessages = new List<ChatMessage>().AsReadOnly(),
            TurnHistory = ImmutableList<ChatMessage>.Empty,
            Iteration = 1,
            IsTerminated = false,
            CompletedFunctions = ImmutableHashSet<string>.Empty,
            InnerClientTracksHistory = false,
            MessagesSentToInnerClient = 0,
            LastAssistantMessageId = null,
            ResponseUpdates = ImmutableList<ChatResponseUpdate>.Empty,
            MiddlewareState = middlewareState,
            Version = 1,
            Metadata = new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1 }
        };
    }

    private AgentLoopState CreateCheckpointWithRemovedMiddleware()
    {
        // Create a checkpoint with a fake middleware that doesn't exist in current schema
        var currentSignature = MiddlewareState.CompiledSchemaSignature;
        var fakeOldSignature = currentSignature + ",HPD.Agent.ObsoleteMiddlewareStateData";

        var middlewareState = new MiddlewareState
        {
            States = ImmutableDictionary<string, object?>.Empty
                .Add("HPD.Agent.ObsoleteMiddlewareStateData", new { }),
            SchemaSignature = fakeOldSignature,
            SchemaVersion = 1,
            StateVersions = MiddlewareState.CompiledStateVersions
                .Add("HPD.Agent.ObsoleteMiddlewareStateData", 1)
        };

        return new AgentLoopState
        {
            RunId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            AgentName = "test-agent",
            StartTime = DateTime.UtcNow,
            CurrentMessages = new List<ChatMessage>().AsReadOnly(),
            TurnHistory = ImmutableList<ChatMessage>.Empty,
            Iteration = 1,
            IsTerminated = false,
            CompletedFunctions = ImmutableHashSet<string>.Empty,
            InnerClientTracksHistory = false,
            MessagesSentToInnerClient = 0,
            LastAssistantMessageId = null,
            ResponseUpdates = ImmutableList<ChatResponseUpdate>.Empty,
            MiddlewareState = middlewareState,
            Version = 1,
            Metadata = new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1 }
        };
    }

    private AgentLoopState CreateCheckpointWithFewerMiddleware()
    {
        // Create a checkpoint with fewer middleware than current schema
        var currentTypes = MiddlewareState.CompiledSchemaSignature.Split(',');
        var fewerTypes = currentTypes.Take(Math.Max(1, currentTypes.Length - 1)).ToArray();
        var olderSignature = string.Join(",", fewerTypes);

        var middlewareState = new MiddlewareState
        {
            States = ImmutableDictionary<string, object?>.Empty,
            SchemaSignature = olderSignature,
            SchemaVersion = 1,
            StateVersions = ImmutableDictionary<string, int>.Empty
        };

        return new AgentLoopState
        {
            RunId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            AgentName = "test-agent",
            StartTime = DateTime.UtcNow,
            CurrentMessages = new List<ChatMessage>().AsReadOnly(),
            TurnHistory = ImmutableList<ChatMessage>.Empty,
            Iteration = 1,
            IsTerminated = false,
            CompletedFunctions = ImmutableHashSet<string>.Empty,
            InnerClientTracksHistory = false,
            MessagesSentToInnerClient = 0,
            LastAssistantMessageId = null,
            ResponseUpdates = ImmutableList<ChatResponseUpdate>.Empty,
            MiddlewareState = middlewareState,
            Version = 1,
            Metadata = new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1 }
        };
    }

    private AgentLoopState CreateCheckpointWithCurrentSchema()
    {
        // Create a checkpoint with the current schema (no changes)
        var middlewareState = new MiddlewareState();  // Uses current schema

        return new AgentLoopState
        {
            RunId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString(),
            AgentName = "test-agent",
            StartTime = DateTime.UtcNow,
            CurrentMessages = new List<ChatMessage>().AsReadOnly(),
            TurnHistory = ImmutableList<ChatMessage>.Empty,
            Iteration = 1,
            IsTerminated = false,
            CompletedFunctions = ImmutableHashSet<string>.Empty,
            InnerClientTracksHistory = false,
            MessagesSentToInnerClient = 0,
            LastAssistantMessageId = null,
            ResponseUpdates = ImmutableList<ChatResponseUpdate>.Empty,
            MiddlewareState = middlewareState,
            Version = 1,
            Metadata = new CheckpointMetadata { Source = CheckpointSource.Loop, Step = 1 }
        };
    }
}

/// <summary>
/// Test event observer that captures events for assertions.
/// </summary>
internal class TestEventObserver : IAgentEventObserver
{
    private readonly List<AgentEvent> _events = new();

    public Task OnEventAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        lock (_events)
        {
            _events.Add(evt);
        }
        return Task.CompletedTask;
    }

    public List<T> GetEvents<T>() where T : AgentEvent
    {
        lock (_events)
        {
            return _events.OfType<T>().ToList();
        }
    }

    public void Clear()
    {
        lock (_events)
        {
            _events.Clear();
        }
    }
}

/// <summary>
/// Test logger provider that captures logs for assertions.
/// </summary>
internal class TestLoggerProvider : ILoggerProvider
{
    private readonly TestLogger _logger = new();

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose() { }

    public List<LogEntry> GetLogs() => _logger.GetLogs();
}

/// <summary>
/// Test logger that captures log entries.
/// </summary>
internal class TestLogger : ILogger
{
    private readonly List<LogEntry> _logs = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _logs.Add(new LogEntry
        {
            LogLevel = logLevel,
            Message = formatter(state, exception),
            Exception = exception
        });
    }

    public List<LogEntry> GetLogs() => _logs;
}

/// <summary>
/// Captured log entry for test assertions.
/// </summary>
internal class LogEntry
{
    public LogLevel LogLevel { get; set; }
    public string Message { get; set; } = string.Empty;
    public Exception? Exception { get; set; }
}

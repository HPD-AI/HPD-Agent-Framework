using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Search;

/// <summary>
/// Generic overloads for registering custom RAG components via the builder DSL.
/// </summary>
public static class CustomRAGExtensions
{
    // AgentMemoryBuilder extensions
    public static AgentMemoryBuilder WithCustomRetrieval<T>(this AgentMemoryBuilder builder)
        where T : IMemoryDb, new()
    {
        return builder.WithCustomRetrieval(new T());
    }

    public static AgentMemoryBuilder WithCustomSearchClient<T>(this AgentMemoryBuilder builder)
        where T : ISearchClient, new()
    {
        return builder.WithCustomSearchClient(new T());
    }

    public static AgentMemoryBuilder WithCustomRAGStrategy<TSearch, TMemory>(this AgentMemoryBuilder builder)
        where TSearch : ISearchClient, new()
        where TMemory : IMemoryDb, new()
    {
        return builder.WithCustomRAGStrategy(new TSearch(), new TMemory());
    }

    // ConversationMemoryBuilder extensions
    public static ConversationMemoryBuilder WithCustomRetrieval<T>(this ConversationMemoryBuilder builder)
        where T : IMemoryDb, new()
    {
        return builder.WithCustomRetrieval(new T());
    }

    public static ConversationMemoryBuilder WithCustomSearchClient<T>(this ConversationMemoryBuilder builder)
        where T : ISearchClient, new()
    {
        return builder.WithCustomSearchClient(new T());
    }

    public static ConversationMemoryBuilder WithCustomRAGStrategy<TSearch, TMemory>(this ConversationMemoryBuilder builder)
        where TSearch : ISearchClient, new()
        where TMemory : IMemoryDb, new()
    {
        return builder.WithCustomRAGStrategy(new TSearch(), new TMemory());
    }

    // ProjectMemoryBuilder extensions
    public static ProjectMemoryBuilder WithCustomRetrieval<T>(this ProjectMemoryBuilder builder)
        where T : IMemoryDb, new()
    {
        return builder.WithCustomRetrieval(new T());
    }

    public static ProjectMemoryBuilder WithCustomSearchClient<T>(this ProjectMemoryBuilder builder)
        where T : ISearchClient, new()
    {
        return builder.WithCustomSearchClient(new T());
    }

    public static ProjectMemoryBuilder WithCustomRAGStrategy<TSearch, TMemory>(this ProjectMemoryBuilder builder)
        where TSearch : ISearchClient, new()
        where TMemory : IMemoryDb, new()
    {
        return builder.WithCustomRAGStrategy(new TSearch(), new TMemory());
    }
}

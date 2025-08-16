using System.Collections.Generic;
using Microsoft.KernelMemory;
using HPD_Agent.MemoryRAG;



public class RAGContext
{
    public Dictionary<string, IKernelMemory?> AgentMemories { get; init; } = new();
    public IKernelMemory? ConversationMemory { get; init; }
    public IKernelMemory? ProjectMemory { get; init; }  // Nullable!

    public RAGConfiguration Configuration { get; init; } = new();

    public static RAGContext CreateWithProjectMemory(
    string projectId,
    Dictionary<string, IKernelMemory?> agentMemories,
    IKernelMemory? conversationMemory,
    RAGConfiguration? configuration = null)
    {
        var projectMemory = new ProjectMemoryBuilder(projectId).Build();
        
        return new RAGContext
        {
            AgentMemories = agentMemories,
            ConversationMemory = conversationMemory,
            ProjectMemory = projectMemory,
            Configuration = configuration ?? new RAGConfiguration()
        };
    }

    public static RAGContext CreateWithoutProjectMemory(
    Dictionary<string, IKernelMemory?> agentMemories,
    IKernelMemory? conversationMemory,
    RAGConfiguration? configuration = null)
    {
        return new RAGContext
        {
            AgentMemories = agentMemories,
            ConversationMemory = conversationMemory,
            ProjectMemory = null,
            Configuration = configuration ?? new RAGConfiguration()
        };
    }
}



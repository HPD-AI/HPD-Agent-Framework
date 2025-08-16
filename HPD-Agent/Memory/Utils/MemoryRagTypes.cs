using System.Collections.Generic;

namespace HPD_Agent.MemoryRAG
{
    public class VoyageAIResponse
    {
        public List<EmbeddingItem> data { get; set; } = new();
    }

    public class EmbeddingItem
    {
        public object? embedding { get; set; }
    }

    public class OpenRouterResponse
    {
        public List<OpenRouterChoice> choices { get; set; } = new();
        public Usage usage { get; set; } = new();
    }

    public class OpenRouterChoice
    {
        public OpenRouterMessage message { get; set; } = new();
    }

    public class OpenRouterMessage
    {
        public string content { get; set; } = string.Empty;
    }

    public class Usage
    {
        public int prompt_tokens { get; set; }
        public int completion_tokens { get; set; }
        public int total_tokens { get; set; }
    }
}

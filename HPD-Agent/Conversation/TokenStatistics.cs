namespace HPD.Agent
{
    /// <summary>
    /// Token usage statistics for a message store.
    /// Used for monitoring and debugging token-aware reduction.
    /// </summary>
    public record TokenStatistics
    {
        public int TotalMessages { get; init; }
        public int TotalTokens { get; init; }
        public int SystemMessageCount { get; init; }
        public int SystemMessageTokens { get; init; }
    }
}

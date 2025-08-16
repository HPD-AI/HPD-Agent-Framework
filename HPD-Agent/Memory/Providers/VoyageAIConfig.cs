public class VoyageAIConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public int MaxTokenLimit { get; set; } = 8192;
    public string? InputType { get; set; }
    public int? Truncation { get; set; }
    public int? OutputDimension { get; set; }
    public string? OutputDataType { get; set; }
}

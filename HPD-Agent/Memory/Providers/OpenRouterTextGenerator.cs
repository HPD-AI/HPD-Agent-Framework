using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using HPD_Agent.MemoryRAG;

// Uses the OpenRouterConfig from Config/ProviderConfig.cs

public class OpenRouterTextGenerator : ITextGenerator
{
private readonly OpenRouterConfig _config;
private readonly HttpClient _httpClient;
private readonly ILogger<OpenRouterTextGenerator> _logger;

public int MaxTokenTotal => _config.DefaultMaxTokenTotal;

public OpenRouterTextGenerator(OpenRouterConfig config, HttpClient httpClient, ILogger<OpenRouterTextGenerator> logger)
{
    _config = config;
    _httpClient = httpClient;
    _logger = logger;
}

public int CountTokens(string text) => text.Length / 4; // Replace with accurate tokenizer later

public IReadOnlyList<string> GetTokens(string text) => new[] { text };

public async IAsyncEnumerable<GeneratedTextContent> GenerateTextAsync(
    string prompt,
    TextGenerationOptions options,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var messages = new[]
    {
        new { role = "system", content = "You are a helpful AI assistant." },
        new { role = "user", content = prompt }
    };

    var payload = new Dictionary<string, object>
    {
        ["model"] = _config.ModelName,
        ["messages"] = messages,
        ["temperature"] = options?.Temperature ?? _config.Temperature,
        ["max_tokens"] = options?.MaxTokens ?? _config.MaxTokens
    };

    var payloadJson = JsonSerializer.Serialize<Dictionary<string, object>>(
        payload,
        MemoryRagJsonContext.Default.DictionaryStringObject);
    var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

    var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint)
    {
        Content = content
    };

    request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
    request.Headers.Add("HTTP-Referer", _config.HttpReferer);
    request.Headers.Add("X-Title", _config.AppName);

    var response = await _httpClient.SendAsync(request, cancellationToken);

    if (response == null)
    {
        _logger.LogError("No response from OpenRouter API.");
        throw new InvalidOperationException("No response from OpenRouter API.");
    }

    if (!response.IsSuccessStatusCode)
    {
        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("OpenRouter error: {StatusCode} {Body}", response.StatusCode, error);
        throw new HttpRequestException($"OpenRouter API failed: {response.StatusCode}");
    }

    var json = await response.Content.ReadAsStringAsync(cancellationToken);
    using var doc = JsonDocument.Parse(json);
    var result = JsonSerializer.Deserialize(doc, MemoryRagJsonContext.Default.OpenRouterResponse)!;
    if (result == null)
    {
        _logger.LogError("Failed to deserialize OpenRouter API response. JSON: {Json}", json);
        throw new InvalidOperationException("Failed to deserialize OpenRouter API response.");
    }
    if (result.Choices == null || result.Choices.Count == 0)
        throw new InvalidOperationException("No choices returned from OpenRouter.");

    yield return new GeneratedTextContent(
        result.Choices[0].Message?.Content ?? string.Empty,
        new TokenUsage
        {
            Timestamp = DateTimeOffset.UtcNow,
            ServiceType = "OpenRouter",
            ModelType = "TextGeneration",
            ModelName = _config.ModelName,
            ServiceTokensIn = result.Usage?.PromptTokens ?? 0,
            ServiceTokensOut = result.Usage?.CompletionTokens ?? 0
        });
}

}


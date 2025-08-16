using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;


public class VoyageAITextEmbeddingGenerator : ITextEmbeddingGenerator
{
    private readonly VoyageAIConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<VoyageAITextEmbeddingGenerator> _logger;

    public VoyageAITextEmbeddingGenerator(VoyageAIConfig config, HttpClient httpClient, ILogger<VoyageAITextEmbeddingGenerator> logger)
    {
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Name => "VoyageAI";
    public int MaxTokens => _config.MaxTokenLimit;

    public int CountTokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

    public IReadOnlyList<string> GetTokens(string text)
    {
        var tokens = new List<string>();
        for (int i = 0; i < text.Length; i += 4)
        {
            tokens.Add(text.Substring(i, Math.Min(4, text.Length - i)));
        }
        return tokens.AsReadOnly();
    }

    public async Task<Embedding> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var preview = string.IsNullOrEmpty(text) ? "" : text[..Math.Min(50, text.Length)];
        _logger.LogInformation("Generating embedding for text: {Preview}", preview);
        var apiKeyPreview = _config.ApiKey?.Length > 8 ? _config.ApiKey.Substring(0, 8) + "***" : (_config.ApiKey != null ? "***" : "Not Set");
        _logger.LogInformation("VoyageAI config: ModelName={ModelName}, Endpoint={Endpoint}, ApiKey={ApiKey}", _config.ModelName, _config.Endpoint, apiKeyPreview);
        var payload = BuildPayload(text);
        var payloadJson = JsonSerializer.Serialize(payload, MemoryRagJsonContext.Default.DictionaryStringObject);
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint)
        {
            Content = content
        };
        request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("VoyageAI error: {StatusCode}, {Error}", response.StatusCode, json);
                throw new HttpRequestException($"VoyageAI failed: {response.StatusCode}");
            }
            _logger.LogDebug("VoyageAI response body: {Body}", json);
            using (var doc = JsonDocument.Parse(json))
            {
                var result = JsonSerializer.Deserialize(doc, MemoryRagJsonContext.Default.VoyageAIResponse)!;
                if (result?.data == null || result.data.Count == 0)
                {
                    _logger.LogError("VoyageAI API returned an invalid response format");
                    throw new InvalidOperationException("VoyageAI API returned an invalid response format");
                }
                return new Embedding(ConvertToFloatArray(result.data.First().embedding));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EXCEPTION in GenerateEmbeddingAsync: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<IList<Embedding>> GenerateEmbeddingsAsync(IList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count > 1000)
            throw new ArgumentException("VoyageAI supports a maximum of 1000 texts per request");
        _logger.LogInformation("Generating embeddings for {Count} texts", texts.Count);
        var apiKeyPreview = _config.ApiKey?.Length > 8 ? _config.ApiKey.Substring(0, 8) + "***" : (_config.ApiKey != null ? "***" : "Not Set");
        _logger.LogInformation("VoyageAI config: ModelName={ModelName}, Endpoint={Endpoint}, ApiKey={ApiKey}", _config.ModelName, _config.Endpoint, apiKeyPreview);
        var payload = BuildPayload(texts);
        var payloadJson = JsonSerializer.Serialize(payload, MemoryRagJsonContext.Default.DictionaryStringObject);
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint)
        {
            Content = content
        };
        request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            _logger.LogInformation("Received HTTP response: {StatusCode}", response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("VoyageAI error: {StatusCode}, {Error}", response.StatusCode, json);
                throw new HttpRequestException($"VoyageAI failed: {response.StatusCode}");
            }
            _logger.LogDebug("VoyageAI response body: {Body}", json);
            using (var doc = JsonDocument.Parse(json))
            {
                var result = JsonSerializer.Deserialize(doc, MemoryRagJsonContext.Default.VoyageAIResponse)!;
                if (result?.data == null)
                {
                    _logger.LogError("VoyageAI API returned an invalid response format for batch request");
                    throw new InvalidOperationException("VoyageAI API returned an invalid response format");
                }
                return result.data.Select(d => new Embedding(ConvertToFloatArray(d.embedding))).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EXCEPTION in GenerateEmbeddingsAsync: {Message}", ex.Message);
            throw;
        }
    }

    private Dictionary<string, object> BuildPayload(object input)
    {
        var payload = new Dictionary<string, object>
        {
            ["input"] = input,
            ["model"] = _config.ModelName
        };

        if (!string.IsNullOrWhiteSpace(_config.InputType))
            payload["input_type"] = _config.InputType;

        if (_config.Truncation.HasValue)
            payload["truncation"] = _config.Truncation.Value;

        if (_config.OutputDimension.HasValue)
            payload["output_dimension"] = _config.OutputDimension;

        if (!string.IsNullOrWhiteSpace(_config.OutputDataType))
            payload["output_dtype"] = _config.OutputDataType;

        return payload;
    }

    private float[] ConvertToFloatArray(object? source)
    {
        if (source is JsonElement element)
        {
            var length = element.GetArrayLength();
            var result = new float[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = element[i].GetSingle();
            }
            return result;
        }

        if (source is float[] fArray)
            return fArray;

        throw new InvalidOperationException("Unexpected embedding format returned.");
    }

}

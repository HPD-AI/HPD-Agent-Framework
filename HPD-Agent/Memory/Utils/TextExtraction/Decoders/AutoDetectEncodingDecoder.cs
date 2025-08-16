using System.Text;
using UtfUnknown; // Required for AutoDetectEncodingDecoder

// --- AutoDetectEncodingDecoder ---
public sealed class AutoDetectEncodingDecoder : ITextDecoder
{
    public string Description => "Auto-Detect Encoding (UTF.Unknown library, fallback to System ANSI)";
    private static readonly UTF8Encoding Utf8NoBomStrict = new UTF8Encoding(false, true);
    private const float MinConfidence = 0.5f; 
    private readonly int _sampleSize;
    
    public AutoDetectEncodingDecoder() : this(16 * 1024) {} 
    public AutoDetectEncodingDecoder(int sampleSize) { _sampleSize = Math.Max(1024, sampleSize); }
    
    public async Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        if (TextExtractionUtility.IsUrl(filePath))
        {
            throw new NotSupportedException($"AutoDetectEncodingDecoder does not support direct URL input. URL: {filePath}");
        }
        
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var data = BinaryData.WrapStream(fs, true);
        byte[] sample = await data.GetSampleAsync(_sampleSize, ct).ConfigureAwait(false);
        if (sample.Length == 0) return string.Empty;
        
        Encoding? detectedEnc = null; 
        string? detectedName = null;
        try
        {
            var res = CharsetDetector.DetectFromBytes(sample);
            if (res.Detected != null && res.Detected.Confidence >= MinConfidence && res.Detected.Encoding != null)
            { 
                detectedEnc = res.Detected.Encoding; 
                detectedName = res.Detected.EncodingName; 
            }
        } 
        catch { /* Log */ }
        
        ReadOnlyMemory<byte> fullMem;
        if (detectedEnc != null) 
        { 
            try 
            { 
                fullMem = await data.ToMemoryAsync(ct).ConfigureAwait(false); 
                return detectedEnc.GetString(fullMem.Span); 
            } 
            catch { /* Log */ } 
        }
        
        if (detectedEnc == null || detectedName?.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) == false)
        { 
            try 
            { 
                fullMem = await data.ToMemoryAsync(ct).ConfigureAwait(false); 
                return Utf8NoBomStrict.GetString(fullMem.Span); 
            } 
            catch { /* Log */ } 
        }
        
        fullMem = await data.ToMemoryAsync(ct).ConfigureAwait(false); 
        return Encoding.Default.GetString(fullMem.Span);
    }
    
    public void Dispose() { } 
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

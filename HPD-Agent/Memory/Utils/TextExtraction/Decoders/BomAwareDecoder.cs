using System.Text;

// --- BomAwareDecoder ---
public sealed class BomAwareDecoder : ITextDecoder
{
    public string Description => "BOM-Aware Decoder (UTF-8, UTF-16LE, UTF-16BE BOMs)";
    private static readonly byte[] Utf8BomBytes = { 0xEF, 0xBB, 0xBF }; 
    private static readonly byte[] Utf16LeBomBytes = { 0xFF, 0xFE }; 
    private static readonly byte[] Utf16BeBomBytes = { 0xFE, 0xFF };
    
    public async Task<string> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        if (TextExtractionUtility.IsUrl(filePath))
        {
            throw new NotSupportedException($"BomAwareDecoder does not support direct URL input. URL: {filePath}");
        }
        
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var data = BinaryData.WrapStream(fs, true);
        int maxBomLen = Math.Max(Utf8BomBytes.Length, Math.Max(Utf16LeBomBytes.Length, Utf16BeBomBytes.Length));
        byte[] sample = await data.GetSampleAsync(maxBomLen, ct).ConfigureAwait(false);
        
        Encoding? enc = null; 
        int bomLen = 0;
        if (sample.AsSpan().StartsWith(Utf8BomBytes)) { enc = Encoding.UTF8; bomLen = Utf8BomBytes.Length; }
        else if (sample.AsSpan().StartsWith(Utf16LeBomBytes)) { enc = Encoding.Unicode; bomLen = Utf16LeBomBytes.Length; }
        else if (sample.AsSpan().StartsWith(Utf16BeBomBytes)) { enc = Encoding.BigEndianUnicode; bomLen = Utf16BeBomBytes.Length; }
        
        if (enc != null) 
        { 
            var full = await data.ToMemoryAsync(ct).ConfigureAwait(false); 
            return enc.GetString(full.Span.Slice(bomLen)); 
        }
        
        throw new InvalidDataException($"File '{Path.GetFileName(filePath)}' has no known BOM.");
    }
    
    public void Dispose() { } 
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

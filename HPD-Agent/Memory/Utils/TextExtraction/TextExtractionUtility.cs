using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// --- ITextDecoder Interface ---
public interface ITextDecoder : IAsyncDisposable, IDisposable
{
    Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken = default);
    string Description { get; }
}

/// <summary>
/// Represents binary data for our utility, potentially wrapping a stream for deferred reading.
/// Note: BinaryData instances are not thread-safe if wrapping a shared stream that is not thread-safe itself.
/// </summary>
public sealed class BinaryData : IDisposable
{
    private ReadOnlyMemory<byte> _materializedMemory;
    private Stream? _stream;
    private bool _leaveOpen;

    private BinaryData(Stream stream, bool leaveOpen)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        _leaveOpen = leaveOpen;
        _materializedMemory = ReadOnlyMemory<byte>.Empty;
    }
    private BinaryData(ReadOnlyMemory<byte> memory) { _materializedMemory = memory; _stream = null; _leaveOpen = true; }
    
    public static BinaryData FromBytes(byte[] bytes) 
    { 
        if (bytes == null) throw new ArgumentNullException(nameof(bytes)); 
        return new BinaryData(new ReadOnlyMemory<byte>(bytes)); 
    }
    
    public static BinaryData FromMemory(ReadOnlyMemory<byte> memory) 
    { 
        return new BinaryData(memory); 
    }
    
    public static BinaryData WrapStream(Stream stream, bool leaveOpen = false) 
    { 
        return new BinaryData(stream, leaveOpen); 
    }
    
    [Obsolete("Use WrapStream and ToMemoryAsync/GetSampleAsync.")]
    public static BinaryData FromStream(Stream stream) 
    { 
        using var ms = new MemoryStream(); 
        if (stream.CanSeek) stream.Position = 0; 
        stream.CopyTo(ms); 
        return new BinaryData(new ReadOnlyMemory<byte>(ms.ToArray())); 
    }
    
    public async ValueTask<ReadOnlyMemory<byte>> ToMemoryAsync(CancellationToken ct = default) 
    { 
        if (!_materializedMemory.IsEmpty || _stream == null) return _materializedMemory; 
        using var ms = new MemoryStream(); 
        if (_stream.CanSeek) _stream.Position = 0; 
        await _stream.CopyToAsync(ms, 81920, ct).ConfigureAwait(false); 
        _materializedMemory = new ReadOnlyMemory<byte>(ms.ToArray()); 
        if (!_leaveOpen) 
        { 
            await _stream.DisposeAsync().ConfigureAwait(false); 
            _stream = null; 
        } 
        return _materializedMemory; 
    }
    
    public ReadOnlyMemory<byte> ToMemory() 
    { 
        if (!_materializedMemory.IsEmpty || _stream == null) return _materializedMemory; 
        return ToMemoryAsync().GetAwaiter().GetResult(); 
    }
    
    public async ValueTask<byte[]> GetSampleAsync(int size, CancellationToken ct = default) 
    { 
        if (size <= 0) return Array.Empty<byte>(); 
        if (!_materializedMemory.IsEmpty) 
            return _materializedMemory.Length > size ? _materializedMemory.Slice(0, size).ToArray() : _materializedMemory.ToArray(); 
        
        if (_stream != null) 
        { 
            byte[] buf = new byte[size]; 
            int read; 
            if (_stream.CanSeek) 
            { 
                _stream.Position = 0; 
                read = await _stream.ReadAsync(buf, 0, size, ct).ConfigureAwait(false); 
                _stream.Position = 0; 
            } 
            else 
            { 
                read = await _stream.ReadAsync(buf, 0, size, ct).ConfigureAwait(false); 
                using var ms = new MemoryStream(); 
                await _stream.CopyToAsync(ms, 81920, ct).ConfigureAwait(false); 
                byte[] rest = ms.ToArray(); 
                var combined = new byte[read + rest.Length]; 
                Buffer.BlockCopy(buf, 0, combined, 0, read); 
                Buffer.BlockCopy(rest, 0, combined, read, rest.Length); 
                _materializedMemory = new ReadOnlyMemory<byte>(combined); 
                if (!_leaveOpen) await _stream.DisposeAsync().ConfigureAwait(false); 
                _stream = null; 
            } 
            if (read < size) Array.Resize(ref buf, read); 
            return buf; 
        } 
        return Array.Empty<byte>(); 
    }
    
    public void Dispose() 
    { 
        if (!_leaveOpen && _stream != null) 
        { 
            _stream.Dispose(); 
            _stream = null; 
        } 
    }
}

// --- DecoderInfo, IDecoderFactory, DefaultDecoderFactory ---
public sealed class DecoderInfo 
{ 
    public string DecoderType { get; init; } = string.Empty; 
    public string Description { get; init; } = string.Empty; 
    public string[] FileExtensions { get; init; } = Array.Empty<string>(); 
    public string[] MimeTypes { get; init; } = Array.Empty<string>(); 
    public int Priority { get; init; } 
}

public interface IDecoderFactory 
{ 
    TDecoder CreateDecoder<TDecoder>() where TDecoder : ITextDecoder, new(); 
}

public sealed class DefaultDecoderFactory : IDecoderFactory 
{ 
    private readonly ILoggerFactory? _lf; 
    public DefaultDecoderFactory(ILoggerFactory? lf = null) { _lf = lf; } 
    public TDecoder CreateDecoder<TDecoder>() where TDecoder : ITextDecoder, new() { return new TDecoder(); } 
}

// --- Our MimeTypes class ---
public static class MimeTypes 
{ 
    public const string MsWordX = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"; 
    public const string MsWord = "application/msword"; 
    public const string MsPowerPointX = "application/vnd.openxmlformats-officedocument.presentationml.presentation"; 
    public const string MsPowerPoint = "application/vnd.ms-powerpoint"; 
    public const string MsExcelX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; 
    public const string MsExcel = "application/vnd.ms-excel"; 
    public const string Pdf = "application/pdf"; 
    public const string ImageJpeg = "image/jpeg"; 
    public const string ImagePng = "image/png"; 
    public const string ImageTiff = "image/tiff"; 
    public const string PlainText = "text/plain"; 
    public const string Json = "application/json"; 
    public const string MarkDown = "text/markdown"; 
    public const string Xml = "application/xml"; 
    public const string Csv = "text/csv"; 
    public const string Html = "text/html"; 
    public const string WebPageUrl = "text/x-web-page-url"; 
}


// --- IDecoderRegistry & DecoderRegistry ---
public interface IDecoderRegistry : IAsyncDisposable 
{ 
    void RegisterDecoder<TDecoder>(string[] extensions, string[] mimeTypes, int priority = 0) where TDecoder : ITextDecoder, new(); 
    void RegisterDecoder(ITextDecoder decoder, string[] extensions, string[] mimeTypes, int priority = 0); 
    void RegisterDecoderFactory(Func<ITextDecoder> factory, Type decoderType, string[] extensions, string[] mimeTypes, int priority = 0);
    List<Func<ITextDecoder>> GetDecoderFactories(string extension);
    IEnumerable<DecoderInfo> GetRegisteredFormats(); 
}

public sealed class DecoderRegistry : IDecoderRegistry
{
    private readonly IDecoderFactory _factory; 
    private readonly List<DecoderRegistration> _registrations = new(); 
    private bool _disposed;
    
    public DecoderRegistry(IDecoderFactory factory) 
    { 
        _factory = factory ?? throw new ArgumentNullException(nameof(factory)); 
    }
    
    public void RegisterDecoder<TDecoder>(string[] e, string[] m, int p = 0) where TDecoder : ITextDecoder, new() 
        => RegisterDecoderInternal(() => _factory.CreateDecoder<TDecoder>(), typeof(TDecoder), e, m, p);
    
    public void RegisterDecoder(ITextDecoder d, string[] e, string[] m, int p = 0) 
    { 
        if (d == null) throw new ArgumentNullException(nameof(d)); 
        RegisterDecoderInternal(() => d, d.GetType(), e, m, p); 
    }

    public void RegisterDecoderFactory(Func<ITextDecoder> factory, Type decoderType, string[] extensions, string[] mimeTypes, int priority = 0)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (decoderType == null) throw new ArgumentNullException(nameof(decoderType));
        if (extensions == null) throw new ArgumentNullException(nameof(extensions));
        if (mimeTypes == null) throw new ArgumentNullException(nameof(mimeTypes));
        RegisterDecoderInternal(factory, decoderType, extensions, mimeTypes, priority);
    }
    
    public List<Func<ITextDecoder>> GetDecoderFactories(string extension)
    {
        var key = extension.ToLowerInvariant(); 
        var yieldedFactoryTypes = new HashSet<Type>();
        var factories = new List<Func<ITextDecoder>>();

        lock (_registrations)
        {
            var matchingRegistrations = _registrations.Where(r => r.Extensions.Contains(key)).OrderByDescending(r => r.Priority);
            foreach (var reg in matchingRegistrations) 
            { 
                if (yieldedFactoryTypes.Add(reg.DecoderType)) 
                { 
                    factories.Add(reg.Factory);
                } 
            }
        }
        
        if (yieldedFactoryTypes.Add(typeof(AutoDetectEncodingDecoder))) 
        { 
            factories.Add(() => _factory.CreateDecoder<AutoDetectEncodingDecoder>());
        }
        return factories;
    }
    
    public IEnumerable<DecoderInfo> GetRegisteredFormats() 
    { 
        lock (_registrations) 
        { 
            return _registrations.Select(r => 
            { 
                string d = $"Err {r.DecoderType.Name}"; 
                try 
                { 
                    using var t = r.Factory(); 
                    d = t.Description; 
                } 
                catch { } 
                return new DecoderInfo 
                { 
                    DecoderType = r.DecoderType.Name, 
                    Description = d, 
                    FileExtensions = r.Extensions, 
                    MimeTypes = r.MimeTypes, 
                    Priority = r.Priority 
                }; 
            }).ToList(); 
        } 
    }
    
    public async ValueTask DisposeAsync() 
    { 
        if (_disposed) return; 
        _disposed = true; 
        await Task.CompletedTask; 
    }
    
    private void RegisterDecoderInternal(Func<ITextDecoder> f, Type t, string[] ext, string[] mime, int pri) 
    { 
        lock (_registrations) 
        { 
            _registrations.Add(new DecoderRegistration(t, ext.Select(e => e.ToLowerInvariant()).ToArray(), mime, pri, f)); 
        } 
    }
    
    private sealed record DecoderRegistration(Type DecoderType, string[] Extensions, string[] MimeTypes, int Priority, Func<ITextDecoder> Factory);
}

// --- Main TextExtractionUtility Class ---
public sealed class TextExtractionUtility : IAsyncDisposable
{
    private readonly ILogger<TextExtractionUtility> _log; 
    private readonly IDecoderRegistry _registry; 
    private readonly BatchOptions _batchOptions; 
    private readonly ILoggerFactory _loggerFactory;
    
    public TextExtractionUtility(ILoggerFactory? loggerFactory = null, IDecoderRegistry? decoderRegistry = null, BatchOptions? batchOptions = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance; 
        _log = _loggerFactory.CreateLogger<TextExtractionUtility>();
        _batchOptions = batchOptions ?? new BatchOptions();
        _registry = decoderRegistry ?? new DecoderRegistry(new DefaultDecoderFactory(_loggerFactory));
        RegisterBuiltInDecoders();
        _log.LogInformation("TextExtractionUtility initialised with {Count} registered decoder formats. Web requests timeout: {WebTimeout}s, Max redirects: {MaxRedirects}", 
            _registry.GetRegisteredFormats().Count(), _batchOptions.WebRequestTimeout.TotalSeconds, _batchOptions.MaxRedirects);
    }

    public static bool IsUrl(string input)
    {
        return Uri.TryCreate(input, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
    
    public void RegisterDecoder<TDecoder>(string[] e, string[] m, int p = 0) where TDecoder : ITextDecoder, new() 
        => _registry.RegisterDecoder<TDecoder>(e, m, p);
    
    public void RegisterDecoder(ITextDecoder d, string[] e, string[] m, int p = 0) 
        => _registry.RegisterDecoder(d, e, m, p);

    public void RegisterDecoderFactory(Func<ITextDecoder> factory, Type decoderType, string[] extensions, string[] mimeTypes, int priority = 0)
        => _registry.RegisterDecoderFactory(factory, decoderType, extensions, mimeTypes, priority);

    public async Task<TextExtractionResult> ExtractTextAsync(string urlOrFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlOrFilePath); 
        
        bool isUrl = IsUrl(urlOrFilePath);
        string fileName, resolvedExtensionLookupKey, mimeType;
        long fileSize = 0;
        
        if (isUrl)
        {
            var uri = new Uri(urlOrFilePath);
            fileName = Path.GetFileName(uri.LocalPath) ?? uri.Host;
            resolvedExtensionLookupKey = ".html"; 
            mimeType = MimeTypes.WebPageUrl;
            _log.LogDebug("Processing URL: {Url}. Using lookup key: '{LookupKey}' for decoders.", urlOrFilePath, resolvedExtensionLookupKey);
        }
        else
        {
            var fi = new FileInfo(urlOrFilePath);
            if (!fi.Exists) 
            { 
                _log.LogWarning("File not found: {FilePath}", urlOrFilePath); 
                return Failure(fi.Name, urlOrFilePath, $"File not found: {urlOrFilePath}"); 
            }
            
            fileName = fi.Name;
            resolvedExtensionLookupKey = fi.Extension.ToLowerInvariant();
            mimeType = GetMimeType(resolvedExtensionLookupKey);
            fileSize = fi.Length;
            _log.LogDebug("Processing file: {FilePath} ({FileSize} bytes). Using lookup key: '{LookupKey}' for decoders.", urlOrFilePath, fileSize, resolvedExtensionLookupKey);
        }
        
        ct.ThrowIfCancellationRequested();

        _log.LogDebug("Attempting to find decoders for lookup key: '{LookupKey}' (isUrl: {IsUrlInput})", resolvedExtensionLookupKey, isUrl);
        List<Func<ITextDecoder>> decoderFactories = _registry.GetDecoderFactories(resolvedExtensionLookupKey);
        
        if (!decoderFactories.Any())
        {
            _log.LogError("No decoder factories found for lookup key '{LookupKey}' (input: '{Input}')", resolvedExtensionLookupKey, fileName);
            return Failure(fileName, urlOrFilePath, $"No decoder configuration found for lookup key '{resolvedExtensionLookupKey}'.");
        }

        _log.LogDebug("Found {Count} decoder factories for '{Input}'. Types (in order of attempt): {FactoryTypes}", 
            decoderFactories.Count, fileName,
            string.Join(", ", decoderFactories.Select(f => { try { using var d = f(); return d.GetType().Name; } catch { return "[ErrorInstantiatingFactory]"; } })));

        var triedDecoderDescriptions = new List<string>(); 
        var decoderErrorMessages = new List<string>();

        foreach (var factory in decoderFactories)
        {
            await using var decoder = factory();
            _log.LogDebug("Considering decoder: '{DecoderDescription}' for '{InputName}'", decoder.Description, fileName);
            
            if (isUrl && !(decoder is CustomWebDecoder))
            {
                _log.LogDebug("Skipping non-web decoder '{DecoderDescription}' for URL '{Url}'.", decoder.Description, urlOrFilePath);
                triedDecoderDescriptions.Add(decoder.Description + " (SkippedForUrl)");
                decoderErrorMessages.Add("Skipped: Not the designated web page decoder.");
                continue;
            }

            _log.LogInformation("Attempting to use decoder: '{DecoderDescription}' for '{InputName}'", decoder.Description, fileName);
            try
            {
                string extracted = await decoder.ExtractTextAsync(urlOrFilePath, ct).ConfigureAwait(false);
                
                if (isUrl && fileSize == 0 && !string.IsNullOrEmpty(extracted))
                {
                    fileSize = Encoding.UTF8.GetByteCount(extracted);
                }
                
                _log.LogInformation("{Type} '{Name}' ({FileSize} bytes): Successfully extracted text using decoder '{DecoderDescription}'.", 
                    isUrl ? "URL" : "File", fileName, fileSize, decoder.Description);
                    
                return new TextExtractionResult 
                { 
                    Success = true, 
                    FileName = fileName, 
                    FilePath = urlOrFilePath, 
                    MimeType = isUrl ? MimeTypes.WebPageUrl : GetMimeType(resolvedExtensionLookupKey, extracted), 
                    FileSize = fileSize, 
                    ExtractedText = extracted, 
                    DecoderUsed = decoder.Description, 
                    ProcessingTime = DateTime.UtcNow 
                };
            }
            catch (InvalidDataException ide)
            {
                triedDecoderDescriptions.Add(decoder.Description + " (FormatMismatch)"); 
                decoderErrorMessages.Add(ide.Message);
            }
            catch (NotSupportedException nse)
            {
                triedDecoderDescriptions.Add(decoder.Description + " (NotSupported)");
                decoderErrorMessages.Add(nse.Message);
                _log.LogDebug("Decoder '{DecoderDescription}' does not support this input type ('{FileName}'): {Message}", decoder.Description, fileName, nse.Message);
            }
            catch (NotImplementedException nie)
            {
                triedDecoderDescriptions.Add(decoder.Description + " (NotImplemented)");
                decoderErrorMessages.Add(nie.Message);
                _log.LogDebug("Decoder '{DecoderDescription}' is not yet implemented: {Message}", decoder.Description, nie.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Type} '{Name}': Decoder '{DecoderDescription}' failed unexpectedly.", 
                    isUrl ? "URL" : "File", fileName, decoder.Description);
                string errorsSoFar = decoderErrorMessages.Any() ? $"Previous errors: {string.Join("; ", decoderErrorMessages)}. " : "";
                return Failure(fileName, urlOrFilePath, $"{errorsSoFar}Decoder '{decoder.Description}' failed: {ex.Message}. Tried: {string.Join(", ", triedDecoderDescriptions)}");
            }
        }
        _log.LogWarning("{Type} '{Name}': All attempted decoders failed. Tried: {TriedDecoders}. Errors: {AllErrors}", 
            isUrl ? "URL" : "File", fileName, string.Join(", ", triedDecoderDescriptions), string.Join("; ", decoderErrorMessages));
        return Failure(fileName, urlOrFilePath, $"All attempted decoders failed. Errors: {string.Join("; ", decoderErrorMessages)}");
    }

    public async Task<IReadOnlyList<TextExtractionResult>> ExtractTextBatchAsync(IEnumerable<string> urlsOrFilePaths, CancellationToken ct = default)
    {
        var inputs = urlsOrFilePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray() ?? Array.Empty<string>();
        if (inputs.Length == 0) 
        { 
            _log.LogWarning("Empty batch."); 
            return Array.Empty<TextExtractionResult>(); 
        }
        
        var urlCount = inputs.Count(IsUrl);
        var fileCount = inputs.Length - urlCount;
        
        _log.LogInformation("Batch extraction for {Count} item(s) ({UrlCount} URLs, {FileCount} files). Max DOP: {MaxDOP}. Encoding Sample Size: {EncodingSampleSize} bytes.",
            inputs.Length, urlCount, fileCount, _batchOptions.MaxDegreeOfParallelism, _batchOptions.EncodingSampleSize);
        var results = new ConcurrentBag<TextExtractionResult>();
        await Parallel.ForEachAsync(inputs, new ParallelOptions 
        { 
            MaxDegreeOfParallelism = _batchOptions.MaxDegreeOfParallelism, 
            CancellationToken = ct 
        },
            async (urlOrPath, token) => 
            { 
                var r = await ExtractTextAsync(urlOrPath, token).ConfigureAwait(false); 
                results.Add(r); 
            }).ConfigureAwait(false);
        _log.LogInformation("Batch complete. Succeeded: {SuccessCount}/{TotalCount}", results.Count(r => r.Success), results.Count);
        return results.ToList();
    }

    private void RegisterBuiltInDecoders() 
    {
        _log.LogDebug("Registering built-in decoders.");
        
        var officeDocExtensions = new[] { ".docx", ".doc" };
        var officePptExtensions = new[] { ".pptx", ".ppt" };
        var officeXlsExtensions = new[] { ".xlsx", ".xls" };
        var pdfExtensions = new[] { ".pdf" };
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".tiff", ".tif" };
        var htmlExtensions = new[] { ".html", ".htm" };

        var commonTextExtensions = new[] { ".txt", ".log", ".ini", ".config", ".xml", ".csv", ".json", ".md", ".markdown", ".cs", ".js", ".py", ".css", ".java", ".php", ".rb", ".go", ".c", ".cpp", ".h", ".xaml", ".sql", ".ps1", ".sh", ".bat", ".yaml", ".yml", ".toml" };
        var allTextExtensions = commonTextExtensions.Concat(new[] { ".geojson" }).Distinct().ToArray();

        var plainTextMimeTypeArray = new[] { MimeTypes.PlainText };
        var jsonMimeTypeArray = new[] { MimeTypes.Json };
        var markdownMimeTypeArray = new[] { MimeTypes.MarkDown };
        var htmlMimeTypeArray = new[] { MimeTypes.Html };
        var urlMimeTypeArray = new[] { MimeTypes.WebPageUrl };

        const int bomAwarePriority = 120;
        _registry.RegisterDecoder<BomAwareDecoder>(allTextExtensions, plainTextMimeTypeArray, bomAwarePriority);

        const int specificBinaryPriority = 100;
        _registry.RegisterDecoder<CustomMsWordDecoder>(officeDocExtensions, new[] { MimeTypes.MsWordX, MimeTypes.MsWord }, specificBinaryPriority);
        _registry.RegisterDecoder<CustomMsPowerPointDecoder>(officePptExtensions, new[] { MimeTypes.MsPowerPointX, MimeTypes.MsPowerPoint }, specificBinaryPriority);
        _registry.RegisterDecoder<CustomMsExcelDecoder>(officeXlsExtensions, new[] { MimeTypes.MsExcelX, MimeTypes.MsExcel }, specificBinaryPriority);
        _registry.RegisterDecoder<CustomPdfDecoder>(pdfExtensions, new[] { MimeTypes.Pdf }, specificBinaryPriority);
        _registry.RegisterDecoder<CustomImageDecoder>(imageExtensions, new[] {MimeTypes.ImageJpeg, MimeTypes.ImagePng, MimeTypes.ImageTiff}, specificBinaryPriority);
        
        _registry.RegisterDecoderFactory(
            () => new CustomWebDecoder(_loggerFactory), 
            typeof(CustomWebDecoder), 
            htmlExtensions, 
            htmlMimeTypeArray, 
            specificBinaryPriority);
        
        var urlExtensions = new[] { ".html", ".htm", ".url" };
        _registry.RegisterDecoderFactory(
            () => new CustomWebDecoder(_loggerFactory), 
            typeof(CustomWebDecoder), 
            urlExtensions, 
            urlMimeTypeArray, 
            specificBinaryPriority);

        const int structuredTextPriority = 75;
        _registry.RegisterDecoder<JsonTextDecoder>(new[] { ".json", ".geojson" }, jsonMimeTypeArray, structuredTextPriority);
        _registry.RegisterDecoder<MarkdownTextDecoder>(new[] { ".md", ".markdown" }, markdownMimeTypeArray, structuredTextPriority);

        const int autoDetectPriority = 10;
        _registry.RegisterDecoder<AutoDetectEncodingDecoder>(allTextExtensions, plainTextMimeTypeArray, autoDetectPriority);
        _log.LogDebug("Decoder registration complete.");
    }

    private static string GetMimeType(string extension, string? extractedText = null)
    {
        string mimeType = extension.ToLowerInvariant() switch 
        { 
            ".docx" => MimeTypes.MsWordX, 
            ".doc" => MimeTypes.MsWord, 
            ".pptx" => MimeTypes.MsPowerPointX, 
            ".ppt" => MimeTypes.MsPowerPoint, 
            ".xlsx" => MimeTypes.MsExcelX, 
            ".xls" => MimeTypes.MsExcel, 
            ".pdf" => MimeTypes.Pdf, 
            ".jpg" or ".jpeg" => MimeTypes.ImageJpeg, 
            ".png" => MimeTypes.ImagePng, 
            ".tiff" or ".tif" => MimeTypes.ImageTiff, 
            ".md" or ".markdown" => MimeTypes.MarkDown, 
            ".json" or ".geojson" => MimeTypes.Json, 
            ".xml" => MimeTypes.Xml, 
            ".csv" => MimeTypes.Csv, 
            ".html" or ".htm" => MimeTypes.Html, 
            ".url" => MimeTypes.WebPageUrl,
            ".css" => "text/css", 
            ".txt" => MimeTypes.PlainText, 
            _ => MimeTypes.PlainText 
        };
        
        if (mimeType == MimeTypes.PlainText && !string.IsNullOrEmpty(extractedText)) 
        {
            int sniffLength = Math.Min(extractedText.Length, 128); 
            ReadOnlySpan<char> textSpan = extractedText.AsSpan(0, sniffLength);
            ReadOnlySpan<char> trimmedSpan = textSpan.TrimStart();
            if (trimmedSpan.StartsWith("<?xml".AsSpan(), StringComparison.OrdinalIgnoreCase)) return MimeTypes.Xml;
            if (trimmedSpan.StartsWith("{".AsSpan()) || trimmedSpan.StartsWith("[".AsSpan())) return MimeTypes.Json;
        }
        return mimeType;
    }
    
    private static TextExtractionResult Failure(string n, string p, string m) => new() 
    { 
        Success = false, 
        FileName = n, 
        FilePath = p, 
        ErrorMessage = m, 
        ProcessingTime = DateTime.UtcNow 
    };
    
    public async ValueTask DisposeAsync() 
    { 
        await _registry.DisposeAsync(); 
    }
}

// --- BatchOptions & Configuration ---
public sealed class BatchOptions 
{ 
    // Banned analyzer prevents reading from Environment; use a reasonable default instead
    public int MaxDegreeOfParallelism { get; init; } = 4; 
    public int EncodingSampleSize { get; init; } = 16 * 1024;
    
    // Web scraping configuration
    public TimeSpan WebRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRedirects { get; init; } = 5;
    public string UserAgent { get; init; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36";
    public bool FollowRedirects { get; init; } = true;
    public Dictionary<string, string> CustomHeaders { get; init; } = new();
}

public sealed class TextExtractionResult 
{ 
    public bool Success { get; init; } 
    public string FileName { get; init; } = string.Empty; 
    public string FilePath { get; init; } = string.Empty; 
    public string MimeType { get; init; } = string.Empty; 
    public long FileSize { get; init; } 
    public string? ExtractedText { get; init; } 
    public string? ErrorMessage { get; init; } 
    public string? DecoderUsed { get; init; } 
    public DateTime ProcessingTime { get; init; } 
    public int ExtractedTextLength => ExtractedText?.Length ?? 0; 
}
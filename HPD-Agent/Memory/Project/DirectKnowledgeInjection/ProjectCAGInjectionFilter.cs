using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Prompt filter that injects project documents into system context
/// </summary>
public class ProjectCagInjectionFilter : IPromptFilter
{
    private readonly ProjectCagOptions _options;
    private readonly ILogger<ProjectCagInjectionFilter>? _logger;
    private string? _cachedDocumentContext;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheValidTime = TimeSpan.FromMinutes(2);
    private readonly object _cacheLock = new object();

    public ProjectCagInjectionFilter(ProjectCagOptions options, ILogger<ProjectCagInjectionFilter>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<IEnumerable<ChatMessage>> InvokeAsync(
        PromptFilterContext context,
        Func<PromptFilterContext, Task<IEnumerable<ChatMessage>>> next)
    {
        if (context.Properties.TryGetValue("Project", out var proj) && proj is Project project)
        {
            var now = DateTime.UtcNow;
            string documentTag = string.Empty;
            var mgr = project.DocumentManager;
            mgr.RegisterCacheInvalidationCallback(InvalidateCache);
            
            bool useCache;
            lock (_cacheLock)
            {
                if (_cachedDocumentContext != null && (now - _lastCacheTime) < _cacheValidTime)
                {
                    documentTag = _cachedDocumentContext;
                    useCache = true;
                }
                else
                {
                    useCache = false;
                }
            }
            
            if (!useCache)
            {
                var documents = await mgr.GetDocumentsAsync();
                documentTag = BuildDocumentTag(documents);
                
                lock (_cacheLock)
                {
                    _cachedDocumentContext = documentTag;
                    _lastCacheTime = now;
                }
            }
            
            if (!string.IsNullOrEmpty(documentTag))
            {
                context.Messages = InjectDocuments(context.Messages, documentTag);
            }
        }
        
        return await next(context);
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedDocumentContext = null;
        }
    }

    private string BuildDocumentTag(List<ProjectDocument> documents)
    {
        if (!documents.Any())
            return string.Empty;

        var tag = "[PROJECT_DOCUMENTS_START]";
        foreach (var doc in documents)
        {
            if (!string.IsNullOrEmpty(doc.ExtractedText))
            {
                var formattedContent = string.Format(_options.DocumentTagFormat, doc.FileName, doc.ExtractedText);
                tag += formattedContent;
            }
        }
        tag += "\n[PROJECT_DOCUMENTS_END]";
        
        return tag;
    }

    private IEnumerable<ChatMessage> InjectDocuments(IEnumerable<ChatMessage> messages, string documentContext)
    {
        var output = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, documentContext)
        };
        output.AddRange(messages);
        return output;
    }
}
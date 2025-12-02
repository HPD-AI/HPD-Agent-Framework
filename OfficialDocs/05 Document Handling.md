# Document Handling

Give your agent the ability to read and understand documents.

## Table of Contents
- [Your First Document-Enabled Agent](#your-first-document-enabled-agent)
- [How It Works](#how-it-works)
- [Supported Document Types](#supported-document-types)
- [Advanced Options](#advanced-options)
- [Using Microsoft.Extensions.AI Content Types](#using-microsoftextensionsai-content-types)
- [Best Practices](#best-practices)

---

## Your First Document-Enabled Agent

### 1. Install required packages

```bash
dotnet add package HPD.Agent
dotnet add package HPD.Agent.Providers.OpenAI
dotnet add package Microsoft.Extensions.AI  # Required for content types
```

### 2. Enable document handling

```csharp
using HPD.Agent;
using Microsoft.Extensions.AI;  // Required for DataContent, TextContent

var agent = new AgentBuilder()
    .WithInstructions("You are a helpful document analyst")
    .WithProvider("openai", "gpt-4o")
    .WithDocumentHandling()  // � Enable document processing
    .Build();
```

### 3. Send a document with your message

```csharp
var thread = agent.CreateThread();

// Read the document as bytes
var pdfBytes = await File.ReadAllBytesAsync("report.pdf");

// Create a message with text and document content
var message = new ChatMessage(ChatRole.User,
[
    new TextContent("Summarize this quarterly report"),
    new DataContent(pdfBytes, "application/pdf") { Name = "report.pdf" }
]);

// Send it to the agent
await foreach (var evt in agent.RunAsync([message], thread))
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Text);
}
```

**Output:**
```
The Q4 2024 report shows revenue of $2.5M, representing a 15% increase...
```

**What happened:**
1. Agent received a PDF document via `DataContent`
2. Document handling middleware extracted the text automatically
3. Extracted text was injected into the message context
4. Agent processed the text and generated a summary

---

## How It Works

Document handling in HPD.Agent uses **Microsoft.Extensions.AI content types** for standardized document representation.

**Current Implementation:** Full-text injection strategy
**Future Plans:** Indexed retrieval with semantic search (coming soon)

### The Flow

```
User Message + DataContent (PDF bytes)
    �
DocumentHandlingMiddleware intercepts
    �
TextExtractionUtility extracts text
    �
PdfDecoder reads PDF content
    �
Text formatted and wrapped in tags
    �
Converted to TextContent
    �
Sent to LLM
```

### What Gets Injected (Full-Text Strategy)

When you attach a document, it's formatted like this:

```
[ATTACHED_DOCUMENT[report.pdf]]
... extracted text content ...
[/ATTACHED_DOCUMENT]
```

The LLM sees the document text inline with your message, enabling natural understanding.

### Retrieval Strategies

HPD.Agent supports different document retrieval strategies:

#### **Full-Text Injection** (Current)
- ✅ **Status:** Available now
- **How it works:** Entire document text is extracted and injected into the message
- **Best for:** Small to medium documents, comprehensive analysis
- **Limitations:** Token limits for very large documents

```csharp
var agent = new AgentBuilder()
    .WithDocumentHandling()  // Uses full-text injection by default
    .Build();
```

#### **Indexed Retrieval** (Coming Soon)
- ⏳ **Status:** Planned for future release
- **How it will work:** Documents chunked, embedded, and semantically searched
- **Best for:** Large document collections, specific information lookup
- **Benefits:** Handle massive documents, relevant chunk retrieval, lower token usage

```csharp
// Future API (not yet available)
var agent = new AgentBuilder()
    .WithDocumentHandling(new DocumentHandlingOptions
    {
        Strategy = DocumentStrategy.IndexedRetrieval,  // Coming soon
        EmbeddingProvider = myEmbeddingProvider,
        ChunkSize = 1000
    })
    .Build();
```

**Current Recommendation:** Use full-text injection for most use cases. For very large documents, consider pre-processing or summarization until indexed retrieval becomes available.

---

## Supported Document Types

HPD.Agent automatically detects and processes these formats:

### Office Documents
- **PDF** (`.pdf`) - Extracts text using PdfPig library
- **Word** (`.docx`) - Extracts text from Microsoft Word documents
- **Excel** (`.xlsx`) - Extracts data from spreadsheets
- **PowerPoint** (`.pptx`) - Extracts text from presentations

### Images
- **Images** (`.jpg`, `.png`, `.gif`, `.webp`) - Optional OCR text extraction
- Supports passing native `ImageContent` for vision models

### Web & Text
- **Web pages** (URLs) - Fetches and extracts content via `UriContent`
- **Markdown** (`.md`) - Native markdown processing
- **JSON** (`.json`) - Structured data extraction
- **Plain Text** (`.txt`) - Auto-detects encoding

### Example: Multiple Documents

```csharp
var pdfBytes = await File.ReadAllBytesAsync("specs.pdf");
var excelBytes = await File.ReadAllBytesAsync("data.xlsx");
var wordBytes = await File.ReadAllBytesAsync("notes.docx");

var message = new ChatMessage(ChatRole.User,
[
    new TextContent("Compare the specs, analyze the data, and review the notes"),
    new DataContent(pdfBytes, "application/pdf") { Name = "specs.pdf" },
    new DataContent(excelBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") { Name = "data.xlsx" },
    new DataContent(wordBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document") { Name = "notes.docx" }
]);

await foreach (var evt in agent.RunAsync([message], thread))
{
    if (evt is TextDeltaEvent delta)
        Console.Write(delta.Text);
}
```

---

## Advanced Options

### Custom Document Formatting

```csharp
var agent = new AgentBuilder()
    .WithDocumentHandling(new DocumentHandlingOptions
    {
        // Customize how documents are wrapped
        CustomTagFormat = "[DOC[{0}]]\n{1}\n[/DOC]",

        // Set size limits
        MaxDocumentSizeBytes = 10 * 1024 * 1024,  // 10MB max

        // Include page numbers in PDF extraction
        IncludePageMetadata = true
    })
    .Build();
```

### Custom Text Extraction

```csharp
// Create a custom text extractor with logging
var textExtractor = new TextExtractionUtility(
    loggerFactory: myLoggerFactory
);

var agent = new AgentBuilder()
    .WithDocumentHandling(textExtractor)
    .Build();
```

### OCR for Images

```csharp
var agent = new AgentBuilder()
    .WithDocumentHandling(new DocumentHandlingOptions
    {
        OcrEngine = myOcrEngine  // Provide custom OCR engine
    })
    .Build();
```

---

## Using Microsoft.Extensions.AI Content Types

HPD.Agent uses **standard Microsoft.Extensions.AI content types** for document handling. This ensures compatibility with the broader .NET AI ecosystem.

### Why Microsoft.Extensions.AI?

1. **Standardized** - Common abstractions across .NET AI libraries
2. **Provider-agnostic** - Works with any LLM provider
3. **Type-safe** - Compile-time guarantees for content types
4. **Future-proof** - Microsoft's official AI abstractions

### Required Package

```bash
dotnet add package Microsoft.Extensions.AI
```

### Content Types Overview

```csharp
using Microsoft.Extensions.AI;

// Text content (required for messages)
new TextContent("Your message here")

// Binary documents (PDF, DOCX, XLSX, etc.)
new DataContent(bytes, "application/pdf") { Name = "document.pdf" }

// URLs (web pages, remote documents)
new UriContent("https://example.com/document.pdf")

// Images (for vision models)
new ImageContent(imageBytes, "image/jpeg")

// Audio (for audio-capable models)
new AudioContent(audioBytes, "audio/mp3")
```

### Document Content: DataContent

`DataContent` is used for binary documents that need **text extraction**:

```csharp
// PDF document
var pdfBytes = await File.ReadAllBytesAsync("report.pdf");
new DataContent(pdfBytes, "application/pdf") { Name = "report.pdf" }

// Word document
var docxBytes = await File.ReadAllBytesAsync("spec.docx");
new DataContent(docxBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
{
    Name = "spec.docx"
}

// Excel spreadsheet
var xlsxBytes = await File.ReadAllBytesAsync("data.xlsx");
new DataContent(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
{
    Name = "data.xlsx"
}
```

### URL Content: UriContent

`UriContent` is used for **remote documents** that the middleware will fetch:

```csharp
// Fetch and process a web page
new UriContent("https://docs.example.com/api-guide")

// Fetch and process a remote PDF
new UriContent("https://example.com/whitepaper.pdf")
```

### Image Content: ImageContent

`ImageContent` is used when you want vision models to **see** the image (not just extract text):

```csharp
var imageBytes = await File.ReadAllBytesAsync("chart.png");

var message = new ChatMessage(ChatRole.User,
[
    new TextContent("What does this chart show?"),
    new ImageContent(imageBytes, "image/png")  // Vision model sees the image
]);
```

**Note:** With `.WithDocumentHandling()`, images in `DataContent` are text-extracted via OCR. Use `ImageContent` when you want native vision support.

### Common MIME Types

| File Type | MIME Type |
|-----------|-----------|
| PDF | `application/pdf` |
| Word (.docx) | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` |
| Excel (.xlsx) | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` |
| PowerPoint (.pptx) | `application/vnd.openxmlformats-officedocument.presentationml.presentation` |
| JPEG | `image/jpeg` |
| PNG | `image/png` |
| Plain Text | `text/plain` |
| Markdown | `text/markdown` |
| JSON | `application/json` |
| HTML | `text/html` |

---

## Best Practices


### 1. Name Your Documents

```csharp
//  Good: Clear document names
new DataContent(bytes, "application/pdf") { Name = "Q4-2024-Report.pdf" }

// L Avoid: Missing or unclear names
new DataContent(bytes, "application/pdf")  // No name
```

### 2. Size Limits

```csharp
//  Good: Set reasonable limits
.WithDocumentHandling(new DocumentHandlingOptions
{
    MaxDocumentSizeBytes = 10 * 1024 * 1024  // 10MB
})

// � Be aware: Very large documents consume tokens
// Consider summarizing large documents before sending
```

### 3. Multi-Turn Conversations

```csharp
var thread = agent.CreateThread();

// First message with document
var message1 = new ChatMessage(ChatRole.User,
[
    new TextContent("Analyze this report"),
    new DataContent(reportBytes, "application/pdf") { Name = "report.pdf" }
]);

await foreach (var evt in agent.RunAsync([message1], thread))
{
    if (evt is TextDeltaEvent delta) Console.Write(delta.Text);
}

// Follow-up question - agent remembers the document!
await foreach (var evt in agent.RunAsync("What were the key findings?", thread))
{
    if (evt is TextDeltaEvent delta) Console.Write(delta.Text);
}
```

### 4. Error Handling

```csharp
try
{
    var bytes = await File.ReadAllBytesAsync("document.pdf");
    var message = new ChatMessage(ChatRole.User,
    [
        new TextContent("Analyze this"),
        new DataContent(bytes, "application/pdf") { Name = "document.pdf" }
    ]);

    await foreach (var evt in agent.RunAsync([message], thread))
    {
        // Process events
    }
}
catch (FileNotFoundException)
{
    Console.WriteLine("Document not found");
}
catch (Exception ex)
{
    Console.WriteLine($"Error processing document: {ex.Message}");
}
```

---

## Common Patterns

### Pattern: Document Analysis Assistant

```csharp
var agent = new AgentBuilder()
    .WithInstructions("You are a document analysis expert")
    .WithDocumentHandling()
    .Build();

var thread = agent.CreateThread();

while (true)
{
    Console.Write("Enter document path (or 'exit'): ");
    var path = Console.ReadLine();
    if (path == "exit") break;

    Console.Write("Your question: ");
    var question = Console.ReadLine();

    var bytes = await File.ReadAllBytesAsync(path);
    var mimeType = Path.GetExtension(path) switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    var message = new ChatMessage(ChatRole.User,
    [
        new TextContent(question),
        new DataContent(bytes, mimeType) { Name = Path.GetFileName(path) }
    ]);

    Console.Write("Agent: ");
    await foreach (var evt in agent.RunAsync([message], thread))
    {
        if (evt is TextDeltaEvent delta)
            Console.Write(delta.Text);
    }
    Console.WriteLine("\n");
}
```

---

## Summary

- **Install** `Microsoft.Extensions.AI` package (required)
- **Enable** document handling with `.WithDocumentHandling()`
- **Use** `DataContent` for documents, `UriContent` for URLs, `ImageContent` for vision
- **Combine** multiple document types in a single message
- **Leverage** standard .NET AI abstractions for compatibility

Document handling in HPD.Agent is:
-  Provider-agnostic (works with any LLM)
-  Middleware-based (pluggable and composable)
-  Standards-compliant (uses Microsoft.Extensions.AI)
-  Production-ready (handles errors, caching, persistence)

---

For more advanced usage, see:
- [Middleware Guide](./04%20Middleware.md) - Custom document strategies
- [Skills Guide](./06%20Skills.md) - Document-aware skills
- [Memory Guide](./10%20Memory.md) - Static and dynamic memory systems

using System.Text;
using Microsoft.KernelMemory.DataFormats; // For FileContent
using Microsoft.KernelMemory.DataFormats.Image;  // For ImageDecoder
using Microsoft.KernelMemory.Text; // For LineTokenExtensions.AppendLineNix

#pragma warning disable KMEXP00 // Possible null reference return.

public sealed class CustomImageDecoder : ITextDecoder
{
    public string Description => "Image (OCR) Decoder (via Kernel Memory)";
    private readonly ImageDecoder _kmDecoder; // Microsoft.KernelMemory.DataFormats.Image.ImageDecoder

    public CustomImageDecoder()
    {
        _kmDecoder = new ImageDecoder();
    }

    public async Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using var memoryStream = new MemoryStream();
        await fs.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        memoryStream.Position = 0;

        FileContent content = await _kmDecoder.DecodeAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (Chunk section in content.Sections)
        {
            var sectionContent = section.Content.Trim();
            if (string.IsNullOrEmpty(sectionContent)) continue;
            sb.Append(sectionContent);
            // For images, often a single block of text is extracted per image, or per detected text block.
            // Adding a double newline if sentences are complete, or single if not, but there are multiple blocks.
            if (section.SentencesAreComplete) { sb.AppendLineNix(); sb.AppendLineNix(); }
            else if (content.Sections.Count > 1) { sb.AppendLineNix(); } 
        }
        return sb.ToString().Trim();
    }
    
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

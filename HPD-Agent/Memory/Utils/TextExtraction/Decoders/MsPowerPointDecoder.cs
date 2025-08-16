using System.Text;
using Microsoft.KernelMemory.DataFormats; // For FileContent
using Microsoft.KernelMemory.DataFormats.Office; // For MsWordDecoder, etc.
using Microsoft.KernelMemory.Text; // For LineTokenExtensions.AppendLineNix


#pragma warning disable KMEXP00 // Possible null reference return.
public sealed class CustomMsPowerPointDecoder : ITextDecoder
{
    public string Description => "Microsoft PowerPoint Decoder (via Kernel Memory)";
    private readonly MsPowerPointDecoder _kmDecoder; // Microsoft.KernelMemory.DataFormats.Office.MsPowerPointDecoder
    public CustomMsPowerPointDecoder() { _kmDecoder = new MsPowerPointDecoder(); }

    public async Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using var memoryStream = new MemoryStream();
        await fs.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        memoryStream.Position = 0; // Ensure stream is at the beginning

        FileContent content = await _kmDecoder.DecodeAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (Chunk section in content.Sections)
        {
            var sectionContent = section.Content.Trim();
            if (string.IsNullOrEmpty(sectionContent)) continue;
            sb.Append(sectionContent);
            if (section.SentencesAreComplete) { sb.AppendLineNix(); sb.AppendLineNix(); } else { sb.AppendLineNix(); }
        }
        return sb.ToString().Trim();
    }
    public void Dispose() { } 
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

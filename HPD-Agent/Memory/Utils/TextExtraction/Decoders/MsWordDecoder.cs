using Microsoft.KernelMemory.DataFormats.Office;
using Microsoft.KernelMemory.DataFormats;
using System.Text;
using Microsoft.KernelMemory.Text;

#pragma warning disable KMEXP00 // Non-nullable field is uninitialized. Consider declaring as nullable.

public sealed class CustomMsWordDecoder : ITextDecoder
{
    public string Description => "Microsoft Word Decoder (via Kernel Memory)";
    private readonly MsWordDecoder _kmDecoder; // Microsoft.KernelMemory.DataFormats.Office.MsWordDecoder
    public CustomMsWordDecoder() { _kmDecoder = new MsWordDecoder(); }

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
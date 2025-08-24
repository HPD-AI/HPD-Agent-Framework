using Microsoft.Extensions.AI;
using System.Linq;

public static class ChatResponseTextExtensions
{
    public static string GetTextContent(this ChatResponse response)
    {
        if (response?.Messages == null)
        {
            return string.Empty;
        }

        var messageContents = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(c => c.Text);

        return string.Join("\n", messageContents);
    }
}
using System.ComponentModel;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class NavigationPlugin(DocumentContext context, ILogger<NavigationPlugin> logger)
{
    private readonly DocumentContext context = context;
    private readonly ILogger<NavigationPlugin> logger = logger;

    [KernelFunction]
    [Description("Searches the document for a specific character, topic, or phrase and returns the location pointer.")]
    public string FindFirstMention(
        [Description("The name of the character, topic, or phrase to find.")] string query)
    {
        var match = context.Document.Items
            .FirstOrDefault(i => i.Text.Contains(query, StringComparison.OrdinalIgnoreCase));

        var pointer = match?.Pointer.SemanticNumber ?? "Not found";
        logger.LogInformation("FindFirstMention: query={Query}, pointer={Pointer}", query, pointer);
        return pointer;
    }

    [KernelFunction]
    [Description("Reads the content of a specific chapter aloud to the user.")]
    public void ReadChapterAloud(int chapterNumber)
    {
        var content = GetChapterContent(chapterNumber);
        context.SpeechQueue.Add(content);
    }

    [KernelFunction]
    [Description("Gets the text content of a specific chapter.")]
    public string GetChapterContent(int chapterNumber)
    {
        // Simplified logic: find headings and extract content between them
        // This assumes a flat list of items where headings denote structure
        var items = context.Document.Items;
        var headings = items.Where(item => item.Type == AiTextEditor.Lib.Model.LinearItemType.Heading).ToList();
        
        if (chapterNumber < 1 || chapterNumber > headings.Count)
        {
            return "Chapter not found.";
        }

        var startItem = headings[chapterNumber - 1];
        var startIndex = startItem.Index;
        
        var endIndex = items.Count;
        if (chapterNumber < headings.Count)
        {
            endIndex = headings[chapterNumber].Index;
        }

        var chapterItems = items.Skip(startIndex).Take(endIndex - startIndex);
        return string.Join("\n\n", chapterItems.Select(i => i.Text));
    }
}

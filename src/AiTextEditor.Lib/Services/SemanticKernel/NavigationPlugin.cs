using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class NavigationPlugin(DocumentContext context, CursorQueryExecutor cursorQueryExecutor, ILogger<NavigationPlugin> logger)
{
    private readonly DocumentContext context = context;
    private readonly CursorQueryExecutor cursorQueryExecutor = cursorQueryExecutor;
    private readonly ILogger<NavigationPlugin> logger = logger;

    [KernelFunction]
    [Description("Searches the document for a specific character, topic, or phrase and returns the location pointer.")]
    public async Task<string> FindFirst(
        [Description("The name of the character, topic, or phrase to find.")] string query)
    {
        var parameters = new CursorParameters(20, 2048, true);
        var cursorName = context.CursorContext.EnsureWholeBookForward(parameters);
        var instruction = $"Locate the first item that matches the request '{query}'. Return the semantic pointer of the best match.";

        var result = await cursorQueryExecutor.ExecuteQueryOverCursorAsync(cursorName, instruction);
        var pointer = result.Success && !string.IsNullOrWhiteSpace(result.Result) ? result.Result : "Not found";
        logger.LogInformation("FindFirst: query={Query}, pointer={Pointer}", query, pointer);
        return pointer;
    }

    [KernelFunction]
    [Description("Reads the content of a specific chapter aloud to the user.")]
    public void ReadChapterAloud(int chapterNumber)
    {
        logger.LogInformation("ReadChapterAloud: chapter={ChapterNumber}", chapterNumber);
        var content = GetChapterContent(chapterNumber);
        context.SpeechQueue.Add(content);
    }

    [KernelFunction]
    [Description("Gets the text content of a specific chapter.")]
    public string GetChapterContent(int chapterNumber)
    {
        var items = context.Document.Items;
        var headings = items.Where(item => item.Type == LinearItemType.Heading).ToList();

        if (chapterNumber < 1 || chapterNumber > headings.Count)
        {
            return "Chapter not found.";
        }

        logger.LogInformation("GetChapterContent: chapter={ChapterNumber}", chapterNumber);
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

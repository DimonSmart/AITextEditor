using Markdig;

namespace AiTextEditor.Web.Services;

public static class MarkdownPreviewHtmlRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return Markdown.ToHtml(markdown, Pipeline);
    }
}

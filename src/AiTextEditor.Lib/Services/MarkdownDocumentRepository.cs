using System.Text;
using AiTextEditor.Lib.Model;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AiTextEditor.Lib.Services;

public class MarkdownDocumentRepository
{
    public string WriteToMarkdown(LinearDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return NormalizeMarkdownContent(document.SourceText);
    }

    public static string ComposeMarkdown(IEnumerable<LinearItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return NormalizeMarkdownContent(string.Join("\n\n", items.Select(item => item.Markdown)));
    }

    public LinearDocument LoadFromMarkdownFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var markdown = File.ReadAllText(path);
        return LoadFromMarkdown(markdown);
    }

    public LinearDocument LoadFromMarkdown(string markdown)
    {
        var normalized = NormalizeMarkdownContent(markdown);
        var pipeline = new MarkdownPipelineBuilder().Build();
        var mdDocument = Markdig.Markdown.Parse(normalized, pipeline);
        var parsingState = new LinearParsingState();
        var lineStartOffsets = GetLineStartOffsets(normalized);
        var items = new List<LinearItem>();
        var index = 0;

        foreach (var mdBlock in mdDocument)
        {
            AppendLinearItems(mdBlock, items, normalized, lineStartOffsets, parsingState, ref index);
        }

        return new LinearDocument(Guid.NewGuid().ToString(), Reindex(items), normalized);
    }

    private static IReadOnlyList<LinearItem> Reindex(IReadOnlyList<LinearItem> items)
    {
        var result = new List<LinearItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var pointer = item.Pointer ?? new LinearPointer(i, new SemanticPointer(null, 0, 0));
            result.Add(item with
            {
                Index = i,
                Pointer = new LinearPointer(i, new SemanticPointer(pointer.HeadingTitle, pointer.LineIndex, pointer.CharacterOffset))
            });
        }

        return result;
    }

    private void AppendLinearItems(
        Markdig.Syntax.Block mdBlock,
        List<LinearItem> items,
        string source,
        IReadOnlyList<int> lineStartOffsets,
        LinearParsingState parsingState,
        ref int index)
    {
        switch (mdBlock)
        {
            case HeadingBlock heading:
                var headingText = GetPlainText(heading.Inline);
                var headingPointer = parsingState.EnterHeading(
                    headingText,
                    GetLineIndex(mdBlock.Span.Start, lineStartOffsets),
                    Math.Max(0, mdBlock.Span.Start));
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Heading,
                        heading.Level,
                        GetSourceText(mdBlock, source),
                        headingText,
                        new LinearPointer(index, headingPointer)),
                    ref index);
                break;

            case ParagraphBlock paragraph:
                var paragraphPointer = parsingState.NextPointer(
                    GetLineIndex(mdBlock.Span.Start, lineStartOffsets),
                    Math.Max(0, mdBlock.Span.Start));
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Paragraph,
                        null,
                        GetSourceText(mdBlock, source),
                        GetPlainText(paragraph.Inline),
                        new LinearPointer(index, paragraphPointer)),
                    ref index);
                break;

            case ListBlock list:
                foreach (var child in list)
                {
                    AppendLinearItems(child, items, source, lineStartOffsets, parsingState, ref index);
                }
                break;

            case ListItemBlock listItem:
                var listItemPointer = parsingState.NextPointer(
                    GetLineIndex(mdBlock.Span.Start, lineStartOffsets),
                    Math.Max(0, mdBlock.Span.Start));
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.ListItem,
                        null,
                        GetSourceText(mdBlock, source),
                        GetListItemPlainText(listItem),
                        new LinearPointer(index, listItemPointer)),
                    ref index);
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    AppendLinearItems(child, items, source, lineStartOffsets, parsingState, ref index);
                }
                break;

            case CodeBlock code:
                var codePointer = parsingState.NextPointer(
                    GetLineIndex(mdBlock.Span.Start, lineStartOffsets),
                    Math.Max(0, mdBlock.Span.Start));
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Code,
                        null,
                        GetSourceText(mdBlock, source),
                        code.Lines.ToString(),
                        new LinearPointer(index, codePointer)),
                    ref index);
                break;

            case ThematicBreakBlock:
                var breakPointer = parsingState.NextPointer(
                    GetLineIndex(mdBlock.Span.Start, lineStartOffsets),
                    Math.Max(0, mdBlock.Span.Start));
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.ThematicBreak,
                        null,
                        GetSourceText(mdBlock, source),
                        string.Empty,
                        new LinearPointer(index, breakPointer)),
                    ref index);
                break;

            default:
                if (mdBlock is ContainerBlock container)
                {
                    foreach (var child in container)
                    {
                        AppendLinearItems(child, items, source, lineStartOffsets, parsingState, ref index);
                    }
                }
                else
                {
                    var fallbackPointer = parsingState.NextPointer(
                        GetLineIndex(mdBlock.Span.Start, lineStartOffsets),
                        Math.Max(0, mdBlock.Span.Start));
                    AddLinearItem(
                        items,
                        new LinearItem(
                            index,
                            LinearItemType.Paragraph,
                            null,
                            GetSourceText(mdBlock, source),
                            GetSourceText(mdBlock, source),
                            new LinearPointer(index, fallbackPointer)),
                        ref index);
                }
                break;
        }
    }

    private void AddLinearItem(List<LinearItem> items, LinearItem item, ref int index)
    {
        items.Add(item);
        index++;
    }

    private static string GetSourceText(Markdig.Syntax.Block block, string source)
    {
        if (block.Span.IsEmpty) return string.Empty;
        var start = Math.Max(0, block.Span.Start);
        var end = Math.Min(source.Length - 1, block.Span.End);
        var length = end - start + 1;
        if (length <= 0) return string.Empty;
        return source.Substring(start, length);
    }

    private static string GetPlainText(ContainerInline? inline)
    {
        if (inline == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content);
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline container:
                    sb.Append(GetPlainText(container));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string GetListItemPlainText(ListItemBlock listItem)
    {
        var paragraph = listItem.OfType<ParagraphBlock>().FirstOrDefault();
        if (paragraph != null)
        {
            return GetPlainText(paragraph.Inline);
        }

        var sb = new StringBuilder();
        foreach (var child in listItem)
        {
            if (child is ContainerBlock container)
            {
                foreach (var nested in container)
                {
                    if (nested is ParagraphBlock nestedParagraph)
                    {
                        sb.AppendLine(GetPlainText(nestedParagraph.Inline));
                    }
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeMarkdownContent(string content)
    {
        return content.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static IReadOnlyList<int> GetLineStartOffsets(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int GetLineIndex(int position, IReadOnlyList<int> lineStartOffsets)
    {
        if (position <= 0 || lineStartOffsets.Count == 0)
        {
            return 0;
        }

        var lineIndex = 0;
        for (var i = 0; i < lineStartOffsets.Count; i++)
        {
            if (lineStartOffsets[i] > position)
            {
                break;
            }

            lineIndex = i;
        }

        return lineIndex;
    }

    private sealed class LinearParsingState
    {
        private string? currentHeadingTitle;

        public SemanticPointer EnterHeading(string? headingTitle, int lineIndex, int characterOffset)
        {
            currentHeadingTitle = headingTitle;
            return new SemanticPointer(currentHeadingTitle, lineIndex, characterOffset);
        }

        public SemanticPointer NextPointer(int lineIndex, int characterOffset)
        {
            return new SemanticPointer(currentHeadingTitle, lineIndex, characterOffset);
        }
    }
}

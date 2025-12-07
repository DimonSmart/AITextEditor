using System.Text;
using AiTextEditor.Lib.Model;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AiTextEditor.Lib.Services;

public class MarkdownDocumentRepository
{
    public LinearDocument LoadFromMarkdownFile(string path)
    {
        var markdown = File.ReadAllText(path);
        return LoadFromMarkdown(markdown);
    }

    public LinearDocument LoadFromMarkdown(string markdown)
    {
        var normalized = NormalizeMarkdownContent(markdown);
        var pipeline = new MarkdownPipelineBuilder().Build();
        var mdDocument = Markdig.Markdown.Parse(normalized, pipeline);
        var parsingState = new LinearParsingState();
        var items = new List<LinearItem>();
        var index = 0;

        foreach (var mdBlock in mdDocument)
        {
            AppendLinearItems(mdBlock, items, normalized, parsingState, ref index);
        }

        return new LinearDocument(Guid.NewGuid().ToString(), Reindex(items), normalized);
    }

    private static IReadOnlyList<LinearItem> Reindex(IReadOnlyList<LinearItem> items)
    {
        var result = new List<LinearItem>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var pointer = item.Pointer ?? new LinearPointer(i, new SemanticPointer(Array.Empty<int>(), null));
            result.Add(item with
            {
                Index = i,
                Pointer = new LinearPointer(i, new SemanticPointer(pointer.HeadingNumbers, pointer.ParagraphNumber))
            });
        }

        return result;
    }

    private void AppendLinearItems(
        Markdig.Syntax.Block mdBlock,
        List<LinearItem> items,
        string source,
        LinearParsingState parsingState,
        ref int index)
    {
        switch (mdBlock)
        {
            case HeadingBlock heading:
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Heading,
                        heading.Level,
                        GetSourceText(mdBlock, source),
                        GetPlainText(heading.Inline),
                        new LinearPointer(index, parsingState.EnterHeading(heading.Level))),
                    ref index);
                break;

            case ParagraphBlock paragraph:
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Paragraph,
                        null,
                        GetSourceText(mdBlock, source),
                        GetPlainText(paragraph.Inline),
                        new LinearPointer(index, parsingState.NextParagraph())),
                    ref index);
                break;

            case ListBlock list:
                foreach (var child in list)
                {
                    AppendLinearItems(child, items, source, parsingState, ref index);
                }
                break;

            case ListItemBlock listItem:
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.ListItem,
                        null,
                        GetSourceText(mdBlock, source),
                        GetListItemPlainText(listItem),
                        new LinearPointer(index, parsingState.NextParagraph())),
                    ref index);
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    AppendLinearItems(child, items, source, parsingState, ref index);
                }
                break;

            case CodeBlock code:
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Code,
                        null,
                        GetSourceText(mdBlock, source),
                        code.Lines.ToString(),
                        new LinearPointer(index, parsingState.NextParagraph())),
                    ref index);
                break;

            case ThematicBreakBlock:
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.ThematicBreak,
                        null,
                        GetSourceText(mdBlock, source),
                        string.Empty,
                        new LinearPointer(index, parsingState.NextParagraph())),
                    ref index);
                break;

            default:
                if (mdBlock is ContainerBlock container)
                {
                    foreach (var child in container)
                    {
                        AppendLinearItems(child, items, source, parsingState, ref index);
                    }
                }
                else
                {
                    AddLinearItem(
                        items,
                        new LinearItem(
                            index,
                            LinearItemType.Paragraph,
                            null,
                            GetSourceText(mdBlock, source),
                            GetSourceText(mdBlock, source),
                            new LinearPointer(index, parsingState.NextParagraph())),
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

    private sealed class LinearParsingState
    {
        private readonly List<int> headingNumbers = new();
        private int paragraphCounter;

        public SemanticPointer EnterHeading(int level)
        {
            if (level <= 0)
            {
                level = 1;
            }

            while (headingNumbers.Count < level)
            {
                headingNumbers.Add(0);
            }

            if (headingNumbers.Count > level)
            {
                headingNumbers.RemoveRange(level, headingNumbers.Count - level);
            }

            headingNumbers[level - 1]++;

            for (var i = level; i < headingNumbers.Count; i++)
            {
                headingNumbers[i] = 0;
            }

            paragraphCounter = 0;
            return new SemanticPointer(GetHeadingPath(), null);
        }

        public SemanticPointer NextParagraph()
        {
            paragraphCounter++;
            return new SemanticPointer(GetHeadingPath(), paragraphCounter);
        }

        private IEnumerable<int> GetHeadingPath()
        {
            var lastNonZero = headingNumbers.FindLastIndex(n => n > 0);
            if (lastNonZero < 0)
            {
                return Array.Empty<int>();
            }

            return headingNumbers.Take(lastNonZero + 1).ToArray();
        }
    }
}

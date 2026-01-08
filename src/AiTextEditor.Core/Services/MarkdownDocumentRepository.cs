using System.Text;
using AiTextEditor.Core.Model;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Linq;

namespace AiTextEditor.Core.Services;

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
            var pointer = item.Pointer ?? throw new InvalidOperationException("Linear item is missing a semantic pointer.");
            result.Add(item with
            {
                Index = i,
                Pointer = new SemanticPointer(pointer.Label)
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
                var headingText = GetPlainText(heading.Inline);
                var headingPointer = parsingState.EnterHeading(heading.Level);
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Heading,
                        GetSourceText(mdBlock, source),
                        headingText,
                        headingPointer),
                    ref index);
                break;

            case ParagraphBlock paragraph:
                var paragraphPointer = parsingState.NextPointer();
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Paragraph,
                        GetSourceText(mdBlock, source),
                        GetPlainText(paragraph.Inline),
                        paragraphPointer),
                    ref index);
                break;

            case ListBlock list:
                foreach (var child in list)
                {
                    AppendLinearItems(child, items, source, parsingState, ref index);
                }
                break;

            case ListItemBlock listItem:
                var listItemPointer = parsingState.NextPointer();
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.ListItem,
                        GetSourceText(mdBlock, source),
                        GetListItemPlainText(listItem),
                        listItemPointer),
                    ref index);
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    AppendLinearItems(child, items, source, parsingState, ref index);
                }
                break;

            case CodeBlock code:
                var codePointer = parsingState.NextPointer();
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Code,
                        GetSourceText(mdBlock, source),
                        code.Lines.ToString(),
                        codePointer),
                    ref index);
                break;

            case ThematicBreakBlock:
                var breakPointer = parsingState.NextPointer();
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.ThematicBreak,
                        GetSourceText(mdBlock, source),
                        string.Empty,
                        breakPointer),
                    ref index);
                break;

            case HtmlBlock html:
                var htmlPointer = parsingState.NextPointer();
                AddLinearItem(
                    items,
                    new LinearItem(
                        index,
                        LinearItemType.Html,
                        GetSourceText(mdBlock, source),
                        GetSourceText(mdBlock, source),
                        htmlPointer),
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
                    var fallbackPointer = parsingState.NextPointer();
                    AddLinearItem(
                        items,
                        new LinearItem(
                            index,
                            LinearItemType.Paragraph,
                            GetSourceText(mdBlock, source),
                            GetSourceText(mdBlock, source),
                            fallbackPointer),
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
        private readonly List<int> headingCounters = [];
        private int paragraphCounter = 0;

        public SemanticPointer EnterHeading(int headingLevel)
        {
            UpdateHeadingCounters(headingLevel);
            paragraphCounter = 0;
            var label = string.Join('.', headingCounters);
            return new SemanticPointer(label);
        }

        public SemanticPointer NextPointer()
        {
            paragraphCounter++;
            var prefix = headingCounters.Count > 0 ? string.Join('.', headingCounters) + "." : string.Empty;
            return new SemanticPointer($"{prefix}p{paragraphCounter}");
        }

        private void UpdateHeadingCounters(int headingLevel)
        {
            if (headingLevel <= 0)
            {
                headingCounters.Clear();
                headingCounters.Add(1);
                return;
            }

            while (headingCounters.Count < headingLevel)
            {
                headingCounters.Add(0);
            }

            headingCounters[headingLevel - 1]++;
            for (var i = headingLevel; i < headingCounters.Count; i++)
            {
                headingCounters[i] = 0;
            }

            // Trim trailing zeros for cleaner labels (e.g., 1.2 not 1.2.0)
            for (var i = headingCounters.Count - 1; i >= 0; i--)
            {
                if (headingCounters[i] != 0) break;
                headingCounters.RemoveAt(i);
            }
        }
    }
}

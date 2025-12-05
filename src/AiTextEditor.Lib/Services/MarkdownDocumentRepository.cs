using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Linq;
using System.Text;

namespace AiTextEditor.Lib.Services;

public class MarkdownDocumentRepository : IDocumentRepository
{
    public Document LoadFromMarkdownFile(string path)
    {
        var markdown = File.ReadAllText(path);
        return LoadFromMarkdown(markdown);
    }

    public Document LoadFromMarkdown(string markdown)
    {
        var normalized = NormalizeMarkdownContent(markdown);
        var pipeline = BuildPipeline();
        var mdDocument = Markdig.Markdown.Parse(normalized, pipeline);
        var document = new Document
        {
            SourceText = normalized
        };

        var lineStarts = ComputeLineStarts(normalized);
        var parsingState = new ParsingState();

        foreach (var mdBlock in mdDocument)
        {
            ProcessMdBlock(mdBlock, document.Blocks, null, normalized, lineStarts, parsingState);
        }

        return document;
    }

    private void ProcessMdBlock(
        Markdig.Syntax.Block mdBlock,
        List<AiTextEditor.Lib.Model.Block> blocks,
        string? parentId,
        string source,
        IReadOnlyList<int> lineStarts,
        ParsingState parsingState)
    {
        var block = new AiTextEditor.Lib.Model.Block
        {
            Id = Guid.NewGuid().ToString(),
            ParentId = parentId
        };

        bool addBlock = true;
        bool recurse = false;

        switch (mdBlock)
        {
            case HeadingBlock heading:
                block.Type = BlockType.Heading;
                block.Level = heading.Level;
                block.Markdown = GetSourceText(mdBlock, source);
                block.PlainText = GetPlainText(heading.Inline);
                parsingState.EnterHeading(block.Level, block.PlainText);
                block.Numbering = parsingState.CurrentNumbering;
                block.StructuralPath = parsingState.CurrentNumbering;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;

            case ParagraphBlock paragraph:
                block.Type = BlockType.Paragraph;
                block.Markdown = GetSourceText(mdBlock, source);
                block.PlainText = GetPlainText(paragraph.Inline);
                parsingState.TouchContent();
                block.StructuralPath = parsingState.CurrentContentPath;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;

            case QuoteBlock quote:
                block.Type = BlockType.Quote;
                block.Markdown = GetSourceText(mdBlock, source);
                block.PlainText = "";
                recurse = true;
                parsingState.TouchContent();
                block.StructuralPath = parsingState.CurrentContentPath;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;

            case CodeBlock code:
                block.Type = BlockType.Code;
                block.Markdown = GetSourceText(mdBlock, source);
                // code.Lines is a StringLineGroup
                block.PlainText = code.Lines.ToString();
                parsingState.TouchContent();
                block.StructuralPath = parsingState.CurrentContentPath;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;

            case ListBlock list:
                // Don't add the list container itself to the flat list of blocks
                addBlock = false;
                // But recurse to get items
                recurse = true;
                // Use a virtual ID for the list parent if needed, or just pass current parent
                // The TZ says items need a ParentId. Let's generate one for the list.
                block.Id = Guid.NewGuid().ToString();
                break;

            case ListItemBlock listItem:
                block.Type = BlockType.ListItem;
                block.Markdown = GetSourceText(mdBlock, source);
                // Try to get text from the first paragraph
                var firstPara = listItem.OfType<ParagraphBlock>().FirstOrDefault();
                block.PlainText = firstPara != null ? GetPlainText(firstPara.Inline) : "";
                parsingState.TouchContent();
                block.StructuralPath = parsingState.CurrentContentPath;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;

            case ThematicBreakBlock:
                block.Type = BlockType.ThematicBreak;
                block.Markdown = GetSourceText(mdBlock, source);
                parsingState.TouchContent();
                block.StructuralPath = parsingState.CurrentContentPath;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;

            default:
                block.Type = BlockType.Paragraph;
                block.Markdown = GetSourceText(mdBlock, source);
                parsingState.TouchContent();
                block.StructuralPath = parsingState.CurrentContentPath;
                block.HeadingPath = parsingState.CurrentHeadingPath;
                break;
        }

        SetSourceLocation(block, mdBlock, source, lineStarts);

        if (addBlock)
        {
            // Filter out empty blocks that might be artifacts or unhandled types with no content
            if (string.IsNullOrWhiteSpace(block.Markdown) && block.Type == BlockType.Paragraph)
            {
                addBlock = false;
            }
        }

        if (addBlock)
        {
            blocks.Add(block);
        }

        if (recurse)
        {
            if (mdBlock is ContainerBlock containerBlock)
            {
                foreach (var child in containerBlock)
                {
                    ProcessMdBlock(child, blocks, block.Id, source, lineStarts, parsingState);
                }
            }
        }
    }

    private string GetSourceText(Markdig.Syntax.Block block, string source)
    {
        if (block.Span.IsEmpty) return "";
        // Ensure we don't go out of bounds
        var start = Math.Max(0, block.Span.Start);
        var end = Math.Min(source.Length - 1, block.Span.End);
        var length = end - start + 1;
        if (length <= 0) return "";
        return source.Substring(start, length);
    }

    private string GetPlainText(ContainerInline? inline)
    {
        if (inline == null) return "";
        var sb = new StringBuilder();
        foreach (var child in inline)
        {
            if (child is LiteralInline literal)
            {
                sb.Append(literal.Content);
            }
            else if (child is CodeInline code)
            {
                sb.Append(code.Content);
            }
            else if (child is ContainerInline container)
            {
                sb.Append(GetPlainText(container));
            }
        }
        return sb.ToString();
    }

    public void SaveToMarkdownFile(Document document, string path)
    {
        var markdown = SaveToMarkdown(document);
        File.WriteAllText(path, NormalizeForPlatform(markdown));
    }

    public string SaveToMarkdown(Document document)
    {
        var sb = new StringBuilder();

        var blockMap = document.Blocks.ToDictionary(b => b.Id);
        var childrenMap = document.Blocks
            .Where(b => !string.IsNullOrEmpty(b.ParentId))
            .GroupBy(b => b.ParentId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => document.Blocks.IndexOf(b)).ToList());

        var roots = document.Blocks
            .Where(b => string.IsNullOrEmpty(b.ParentId) || !blockMap.ContainsKey(b.ParentId))
            .ToList();

        foreach (var block in roots)
        {
            sb.Append(GenerateMarkdown(block, childrenMap));
            sb.AppendLine();
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd('\r', '\n') + "\n";
    }

    private string GenerateMarkdown(AiTextEditor.Lib.Model.Block block, Dictionary<string, List<AiTextEditor.Lib.Model.Block>> childrenMap)
    {
        var sb = new StringBuilder();

        switch (block.Type)
        {
            case BlockType.Heading:
                sb.Append(new string('#', block.Level));
                sb.Append(' ');
                sb.Append(block.PlainText);
                break;

            case BlockType.Paragraph:
                sb.Append(block.PlainText);
                break;

            case BlockType.Quote:
                if (childrenMap.TryGetValue(block.Id, out var children))
                {
                    foreach (var child in children)
                    {
                        var childMd = GenerateMarkdown(child, childrenMap);
                        using (var reader = new StringReader(childMd))
                        {
                            string? line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                sb.Append($"> {line}");
                                sb.AppendLine();
                            }
                        }
                    }
                    // Remove last newline to avoid excessive spacing
                    if (sb.Length >= 2 && sb[sb.Length - 2] == '\r') sb.Length -= 2;
                    else if (sb.Length >= 1 && sb[sb.Length - 1] == '\n') sb.Length -= 1;
                }
                else
                {
                    sb.Append("> ");
                }
                break;

            case BlockType.ListItem:
                sb.Append("- ");
                sb.Append(block.PlainText);
                break;

            case BlockType.Code:
                sb.AppendLine("```");
                sb.AppendLine(block.PlainText);
                sb.Append("```");
                break;

            case BlockType.ThematicBreak:
                sb.Append("---");
                break;

            default:
                sb.Append(block.Markdown);
                break;
        }

        return sb.ToString();
    }

    private static MarkdownPipeline BuildPipeline() => new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static string NormalizeMarkdownContent(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return normalized.EndsWith("\n", StringComparison.Ordinal) ? normalized : normalized + "\n";
    }

    private static string NormalizeForPlatform(string markdown) => markdown.Replace("\n", Environment.NewLine);

    private static IReadOnlyList<int> ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && i + 1 < text.Length)
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    private static (int line, int column) GetLineColumn(int offset, IReadOnlyList<int> lineStarts)
    {
        // Binary search for the line that contains the offset
        int low = 0;
        int high = lineStarts.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            int lineStart = lineStarts[mid];
            int nextStart = mid + 1 < lineStarts.Count ? lineStarts[mid + 1] : int.MaxValue;

            if (offset < lineStart)
            {
                high = mid - 1;
            }
            else if (offset >= nextStart)
            {
                low = mid + 1;
            }
            else
            {
                return (mid + 1, offset - lineStart + 1); // 1-based line/column
            }
        }

        return (1, offset + 1); // fallback
    }

    private static void SetSourceLocation(
        AiTextEditor.Lib.Model.Block block,
        Markdig.Syntax.Block mdBlock,
        string source,
        IReadOnlyList<int> lineStarts)
    {
        if (mdBlock.Span.IsEmpty)
        {
            return;
        }

        var start = Math.Max(0, mdBlock.Span.Start);
        var end = Math.Min(source.Length - 1, mdBlock.Span.End);

        block.StartOffset = start;
        block.EndOffset = end;

        var (startLine, startCol) = GetLineColumn(start, lineStarts);
        var (endLine, endCol) = GetLineColumn(end, lineStarts);

        block.StartLine = startLine;
        block.EndLine = endLine;
        block.StartColumn = startCol;
        block.EndColumn = endCol;
    }

    private sealed class ParsingState
    {
        private readonly List<int> headingNumbers = new();
        private readonly List<string> headingTitles = new();

        public int ContentCounter { get; private set; }

        public string CurrentNumbering => headingNumbers.Count == 0
            ? string.Empty
            : string.Join('.', headingNumbers);

        public string CurrentHeadingPath => headingTitles.Count == 0
            ? string.Empty
            : string.Join(" > ", headingTitles.Where(t => !string.IsNullOrWhiteSpace(t)));

        public string CurrentContentPath
        {
            get
            {
                if (headingNumbers.Count == 0)
                {
                    return $"p{ContentCounter}";
                }

                return $"{CurrentNumbering}.p{ContentCounter}";
            }
        }

        public void EnterHeading(int level, string title)
        {
            if (level <= 0) level = 1;

            // Ensure list sizes
            while (headingNumbers.Count < level)
            {
                headingNumbers.Add(0);
                headingTitles.Add(string.Empty);
            }

            // Trim deeper levels
            if (headingNumbers.Count > level)
            {
                headingNumbers.RemoveRange(level, headingNumbers.Count - level);
                headingTitles.RemoveRange(level, headingTitles.Count - level);
            }

            headingNumbers[level - 1]++;
            headingTitles[level - 1] = title?.Trim() ?? string.Empty;

            // Reset deeper counters when moving up/down the hierarchy
            for (int i = level; i < headingNumbers.Count; i++)
            {
                headingNumbers[i] = 0;
                headingTitles[i] = string.Empty;
            }

            ContentCounter = 0;
        }

        public void TouchContent()
        {
            ContentCounter++;
        }
    }
}

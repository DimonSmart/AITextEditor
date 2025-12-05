using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
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
        var document = new Document();
        
        foreach (var mdBlock in mdDocument)
        {
            ProcessMdBlock(mdBlock, document.Blocks, null, normalized);
        }

        return document;
    }

    private void ProcessMdBlock(Markdig.Syntax.Block mdBlock, List<AiTextEditor.Lib.Model.Block> blocks, string? parentId, string source)
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
                break;

            case ParagraphBlock paragraph:
                block.Type = BlockType.Paragraph;
                block.Markdown = GetSourceText(mdBlock, source);
                block.PlainText = GetPlainText(paragraph.Inline);
                break;

            case QuoteBlock quote:
                block.Type = BlockType.Quote;
                block.Markdown = GetSourceText(mdBlock, source);
                block.PlainText = ""; 
                recurse = true;
                break;

            case CodeBlock code:
                block.Type = BlockType.Code;
                block.Markdown = GetSourceText(mdBlock, source);
                // code.Lines is a StringLineGroup
                block.PlainText = code.Lines.ToString(); 
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
                break;
            
            case ThematicBreakBlock:
                block.Type = BlockType.ThematicBreak;
                block.Markdown = GetSourceText(mdBlock, source);
                break;

            default:
                block.Type = BlockType.Paragraph;
                block.Markdown = GetSourceText(mdBlock, source);
                break;
        }

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
                    ProcessMdBlock(child, blocks, block.Id, source);
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
}

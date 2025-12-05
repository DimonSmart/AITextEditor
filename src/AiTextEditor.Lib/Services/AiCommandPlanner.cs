using System.Text;
using System.Text.RegularExpressions;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Turns a user request into edit operations by selecting context
/// and delegating the actual operation synthesis to an ILlmEditor.
/// </summary>
public class AiCommandPlanner
{
    private readonly IChunkBuilder chunkBuilder;
    private readonly IVectorStore vectorStore;
    private readonly ILlmEditor llmEditor;
    private readonly int maxTokensPerChunk;

    public AiCommandPlanner(
        IChunkBuilder chunkBuilder,
        IVectorStore vectorStore,
        ILlmEditor llmEditor,
        int maxTokensPerChunk = 800)
    {
        this.chunkBuilder = chunkBuilder;
        this.vectorStore = vectorStore;
        this.llmEditor = llmEditor;
        this.maxTokensPerChunk = maxTokensPerChunk;
    }

    public async Task<IReadOnlyList<EditOperation>> PlanAsync(
        Document document,
        string userRequest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(userRequest))
        {
            return Array.Empty<EditOperation>();
        }

        var chunks = chunkBuilder.BuildChunks(document, maxTokensPerChunk);
        await vectorStore.IndexAsync(document.Id, chunks, ct);

        var contextBlocks = ResolveContext(document, chunks, userRequest);
        var instruction = BuildInstruction(userRequest, contextBlocks);

        var operations = await llmEditor.GetEditOperationsAsync(
            contextBlocks,
            rawUserText: userRequest,
            instruction: instruction,
            ct);

        return operations;
    }

    private List<Block> ResolveContext(Document document, List<Chunk> chunks, string userRequest)
    {
        var context = new List<Block>();
        var headingMatch = FindHeadingMatch(document, userRequest);

        if (headingMatch != null)
        {
            context.AddRange(GetBlocksUnderHeading(document, headingMatch));
        }

        var quotedFragments = ExtractQuotedFragments(userRequest);
        foreach (var fragment in quotedFragments)
        {
            var block = document.Blocks.FirstOrDefault(b =>
                (!string.IsNullOrEmpty(b.Markdown) && b.Markdown.Contains(fragment, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(b.PlainText) && b.PlainText.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

            if (block != null && !context.Contains(block))
            {
                context.Add(block);
            }
        }

        if (context.Count == 0 && chunks.Count > 0)
        {
            var firstChunk = chunks[0];
            foreach (var blockId in firstChunk.BlockIds)
            {
                var block = document.Blocks.FirstOrDefault(b => b.Id == blockId);
                if (block != null)
                {
                    context.Add(block);
                }
            }
        }

        if (context.Count == 0)
        {
            context.AddRange(document.Blocks.Take(Math.Min(3, document.Blocks.Count)));
        }

        return context;
    }

    private static Block? FindHeadingMatch(Document document, string userRequest)
    {
        var request = userRequest.ToLowerInvariant();
        foreach (var heading in document.Blocks.Where(b => b.Type == BlockType.Heading))
        {
            if (!string.IsNullOrWhiteSpace(heading.PlainText) &&
                request.Contains(heading.PlainText.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return heading;
            }
        }

        return null;
    }

    private static List<Block> GetBlocksUnderHeading(Document document, Block heading)
    {
        var blocks = new List<Block> { heading };
        var startIndex = document.Blocks.IndexOf(heading);

        for (int i = startIndex + 1; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (block.Type == BlockType.Heading && block.Level <= heading.Level)
            {
                break;
            }

            blocks.Add(block);
        }

        return blocks;
    }

    private static IEnumerable<string> ExtractQuotedFragments(string userRequest)
    {
        var matches = Regex.Matches(userRequest, "[\"“”']([^\"“”']+)[\"“”']");
        foreach (Match match in matches)
        {
            yield return match.Groups[1].Value;
        }
    }

    private static string BuildInstruction(string userRequest, List<Block> contextBlocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Преобразуй запрос пользователя в точные операции редактирования с привязкой к blockId.");
        sb.AppendLine("Если пользователь упоминает главу, работай только внутри неё. Если называет конкретный фрагмент, используй блок с этим текстом как точку привязки.");
        sb.AppendLine("Допустимые действия: replace, insert_after, insert_before, remove. Не добавляй других полей.");
        sb.AppendLine("Учитывай ParentId, чтобы сохранять вложенные структуры списков и цитат.");
        sb.AppendLine("Запрос: ");
        sb.AppendLine(userRequest);
        sb.AppendLine("Контекст для поиска (blockId -> plainText):");

        foreach (var block in contextBlocks)
        {
            sb.AppendLine($"{block.Id}: {block.PlainText}");
        }

        return sb.ToString();
    }
}

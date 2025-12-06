using System.Text;
using System.Linq;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;
using AiTextEditor.Lib.Model.Intent;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Transforms a raw user request into an Intent, selects target blocks using indexes,
/// and delegates edit operation synthesis to an ILlmEditor.
/// </summary>
public class AiCommandPlanner
{
    private readonly DocumentIndexBuilder indexBuilder;
    private readonly VectorIndexingService vectorIndexing;
    private readonly IIntentParser intentParser;
    private readonly ILlmEditor llmEditor;

    public AiCommandPlanner(
        DocumentIndexBuilder indexBuilder,
        VectorIndexingService vectorIndexing,
        IIntentParser intentParser,
        ILlmEditor llmEditor)
    {
        this.indexBuilder = indexBuilder ?? throw new ArgumentNullException(nameof(indexBuilder));
        this.vectorIndexing = vectorIndexing ?? throw new ArgumentNullException(nameof(vectorIndexing));
        this.intentParser = intentParser ?? throw new ArgumentNullException(nameof(intentParser));
        this.llmEditor = llmEditor ?? throw new ArgumentNullException(nameof(llmEditor));
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

        var intentResult = await intentParser.ParseAsync(userRequest, ct);
        if (!intentResult.Success || intentResult.Intent == null)
        {
            return Array.Empty<EditOperation>();
        }

        var indexes = indexBuilder.Build(document);
        await vectorIndexing.IndexAsync(document, indexes.TextIndex, ct);

        var targetBlocks = await ResolveTargetsAsync(document, indexes, intentResult.Intent, userRequest, ct);
        if (targetBlocks.Count == 0)
        {
            targetBlocks.AddRange(document.Blocks.Take(Math.Min(3, document.Blocks.Count)));
        }

        var instruction = BuildInstruction(intentResult.Intent, targetBlocks);

        var operations = await llmEditor.GetEditOperationsAsync(
            targetBlocks,
            rawUserText: userRequest,
            instruction: instruction,
            ct);

        return operations;
    }

    private async Task<List<Block>> ResolveTargetsAsync(
        Document document,
        DocumentIndexes indexes,
        IntentDto intent,
        string userRequest,
        CancellationToken ct)
    {
        var result = new List<Block>();
        var blocksById = document.Blocks.ToDictionary(b => b.Id);

        switch (intent.ScopeType)
        {
            case IntentScopeType.Structural:
                result.AddRange(SelectStructuralTargets(document, indexes.StructuralIndex, intent, userRequest, blocksById));
                break;

            case IntentScopeType.SemanticLocal:
                result.AddRange(await SelectSemanticTargetsAsync(document, intent, blocksById, ct));
                break;

            case IntentScopeType.Global:
                result.AddRange(SelectGlobalTargets(document));
                break;

            default:
                break;
        }

        return result;
    }

    private static IEnumerable<Block> SelectStructuralTargets(
        Document document,
        StructuralIndex structuralIndex,
        IntentDto intent,
        string userRequest,
        Dictionary<string, Block> blocksById)
    {
        var targets = new List<Block>();
        var scope = intent.ScopeDescriptor;

        StructuralIndexEntry? headingEntry = null;

        if (!string.IsNullOrWhiteSpace(scope.StructuralPath))
        {
            headingEntry = structuralIndex.Headings.FirstOrDefault(h =>
                string.Equals(h.StructuralPath, scope.StructuralPath, StringComparison.OrdinalIgnoreCase));
        }

        if (headingEntry == null && scope.ChapterNumber.HasValue)
        {
            var expectedNumbering = scope.SectionNumber.HasValue
                ? $"{scope.ChapterNumber.Value}.{scope.SectionNumber.Value}"
                : scope.ChapterNumber.Value.ToString();

            headingEntry = structuralIndex.Headings.FirstOrDefault(h =>
                h.Numbering.StartsWith(expectedNumbering, StringComparison.OrdinalIgnoreCase));
        }

        if (headingEntry == null && !string.IsNullOrWhiteSpace(userRequest))
        {
            headingEntry = structuralIndex.Headings.FirstOrDefault(h =>
                userRequest.Contains(h.Title, StringComparison.OrdinalIgnoreCase));
        }

        if (headingEntry != null && blocksById.TryGetValue(headingEntry.BlockId, out var headingBlock))
        {
            targets.AddRange(GetBlocksUnderHeading(document, headingBlock));
        }

        return targets;
    }

    private async Task<IEnumerable<Block>> SelectSemanticTargetsAsync(
        Document document,
        IntentDto intent,
        Dictionary<string, Block> blocksById,
        CancellationToken ct)
    {
        var targets = new List<Block>();
        var query = intent.ScopeDescriptor.SemanticQuery;

        if (string.IsNullOrWhiteSpace(query))
        {
            return targets;
        }

        var results = await vectorIndexing.QueryAsync(document.Id, query, maxResults: 5, ct);
        foreach (var record in results)
        {
            if (blocksById.TryGetValue(record.BlockId, out var block) && !targets.Contains(block))
            {
                targets.Add(block);
            }
        }

        return targets;
    }

    private static IEnumerable<Block> SelectGlobalTargets(Document document)
    {
        return document.Blocks
            .Where(b => b.Type is BlockType.Paragraph or BlockType.ListItem or BlockType.Code or BlockType.Quote)
            .ToList();
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

    private static string BuildInstruction(IntentDto intent, List<Block> targetBlocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an editor for a Markdown document. Apply the given intent to the provided blocks.");
        sb.AppendLine($"ScopeType: {intent.ScopeType}");
        sb.AppendLine("ScopeDescriptor:");
        sb.AppendLine($"  chapterNumber: {intent.ScopeDescriptor.ChapterNumber}");
        sb.AppendLine($"  sectionNumber: {intent.ScopeDescriptor.SectionNumber}");
        sb.AppendLine($"  structuralPath: {intent.ScopeDescriptor.StructuralPath}");
        sb.AppendLine($"  semanticQuery: {intent.ScopeDescriptor.SemanticQuery}");
        sb.AppendLine("Payload:");
        foreach (var kvp in intent.Payload.Fields)
        {
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        }

        sb.AppendLine("Target blocks (id | type | numbering | path | text):");
        foreach (var block in targetBlocks)
        {
            var plain = block.PlainText.Replace("\n", "\\n").Replace("\r", string.Empty);
            sb.AppendLine($"- {block.Id} | {block.Type} | {block.Numbering} | {block.StructuralPath} | {plain}");
        }

        sb.AppendLine("Return JSON edit operations.");
        return sb.ToString();
    }
}

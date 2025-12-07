using System.Linq;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;
using AiTextEditor.Lib.Model.Intent;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Transforms a raw user request into an Intent and builds target sets using document indexes.
/// </summary>
public class AiCommandPlanner
{
    private readonly DocumentIndexBuilder indexBuilder;
    private readonly VectorIndexingService vectorIndexing;
    private readonly IIntentParser intentParser;
    private readonly ITargetSetService targetSetService;

    public AiCommandPlanner(
        DocumentIndexBuilder indexBuilder,
        VectorIndexingService vectorIndexing,
        IIntentParser intentParser,
        ITargetSetService targetSetService)
    {
        this.indexBuilder = indexBuilder ?? throw new ArgumentNullException(nameof(indexBuilder));
        this.vectorIndexing = vectorIndexing ?? throw new ArgumentNullException(nameof(vectorIndexing));
        this.intentParser = intentParser ?? throw new ArgumentNullException(nameof(intentParser));
        this.targetSetService = targetSetService ?? throw new ArgumentNullException(nameof(targetSetService));
    }

    public async Task<PlanningResult> PlanAsync(
        Document document,
        string userRequest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(userRequest))
        {
            return new PlanningResult();
        }

        var intentResult = await intentParser.ParseAsync(userRequest, ct);
        if (!intentResult.Success || intentResult.Intent == null)
        {
            return new PlanningResult();
        }

        var indexes = indexBuilder.Build(document);
        await vectorIndexing.IndexAsync(document, indexes.TextIndex, ct);

        var targetItems = await ResolveTargetsAsync(document, indexes, intentResult.Intent, userRequest, ct);
        if (targetItems.Count == 0)
        {
            targetItems.AddRange(document.LinearDocument.Items.Take(Math.Min(3, document.LinearDocument.Items.Count)));
        }

        var targetSet = targetSetService.Create(
            document.Id,
            targetItems,
            intentResult.Intent.RawJson,
            label: intentResult.Intent.ScopeDescriptor.StructuralPath,
            blockIdResolver: item => ResolveBlockId(document, item));

        return new PlanningResult
        {
            Intent = intentResult.Intent,
            TargetSet = targetSet
        };
    }

    private async Task<List<LinearItem>> ResolveTargetsAsync(
        Document document,
        DocumentIndexes indexes,
        IntentDto intent,
        string userRequest,
        CancellationToken ct)
    {
        var result = new List<LinearItem>();
        var blocksById = document.Blocks.ToDictionary(b => b.Id);

        switch (intent.ScopeType)
        {
            case IntentScopeType.Structural:
                result.AddRange(SelectStructuralTargets(document, indexes.StructuralIndex, intent, userRequest));
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

    private static IEnumerable<LinearItem> SelectStructuralTargets(
        Document document,
        StructuralIndex structuralIndex,
        IntentDto intent,
        string userRequest)
    {
        var targets = new List<LinearItem>();
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

        var headingNumbering = headingEntry?.StructuralPath ?? headingEntry?.Numbering;
        if (!string.IsNullOrWhiteSpace(headingNumbering))
        {
            targets.AddRange(GetItemsUnderHeading(document.LinearDocument, headingNumbering!));
        }

        return targets;
    }

    private async Task<IEnumerable<LinearItem>> SelectSemanticTargetsAsync(
        Document document,
        IntentDto intent,
        Dictionary<string, Block> blocksById,
        CancellationToken ct)
    {
        var targets = new List<LinearItem>();
        var query = intent.ScopeDescriptor.SemanticQuery;

        if (string.IsNullOrWhiteSpace(query))
        {
            return targets;
        }

        var results = await vectorIndexing.QueryAsync(document.Id, query, maxResults: 5, ct);
        foreach (var record in results)
        {
            if (blocksById.TryGetValue(record.BlockId, out var block))
            {
                targets.AddRange(FindMatchingLinearItems(document.LinearDocument, block.StructuralPath));
            }
        }

        return targets;
    }

    private static IEnumerable<LinearItem> SelectGlobalTargets(Document document)
    {
        return document.LinearDocument.Items
            .Where(b => b.Type is LinearItemType.Paragraph or LinearItemType.ListItem or LinearItemType.Code)
            .ToList();
    }

    private static IEnumerable<LinearItem> GetItemsUnderHeading(LinearDocument document, string headingNumbering)
    {
        var prefix = headingNumbering + ".";
        return document.Items
            .Where(i => IsUnderHeading(i.Pointer.SemanticNumber, headingNumbering, prefix))
            .ToList();
    }

    private static bool IsUnderHeading(string semanticNumber, string headingNumbering, string prefix)
    {
        if (semanticNumber.Equals(headingNumbering, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!semanticNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (semanticNumber.Length == prefix.Length)
        {
            return true;
        }

        var nextSegment = semanticNumber[prefix.Length..];
        return char.IsDigit(nextSegment.FirstOrDefault()) || nextSegment.StartsWith("p", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<LinearItem> FindMatchingLinearItems(LinearDocument linearDocument, string structuralPath)
    {
        return linearDocument.Items
            .Where(i => string.Equals(i.Pointer.SemanticNumber, structuralPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string? ResolveBlockId(Document document, LinearItem item)
    {
        var block = document.Blocks.FirstOrDefault(b =>
            string.Equals(b.StructuralPath, item.Pointer.SemanticNumber, StringComparison.OrdinalIgnoreCase));

        return block?.Id;
    }
}

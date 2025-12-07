using System.Linq;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;

namespace AiTextEditor.Lib.Services;

public class AiCommandPlanner
{
    private readonly DocumentIndexBuilder indexBuilder;
    private readonly VectorIndexingService vectorIndexing;
    private readonly ITargetSetService targetSetService;

    public AiCommandPlanner(
        DocumentIndexBuilder indexBuilder,
        VectorIndexingService vectorIndexing,
        ITargetSetService targetSetService)
    {
        this.indexBuilder = indexBuilder ?? throw new ArgumentNullException(nameof(indexBuilder));
        this.vectorIndexing = vectorIndexing ?? throw new ArgumentNullException(nameof(vectorIndexing));
        this.targetSetService = targetSetService ?? throw new ArgumentNullException(nameof(targetSetService));
    }

    public async Task<CommandPlan> PlanAsync(
        Document document,
        string userRequest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(userRequest))
        {
            return new CommandPlan();
        }

        var indexes = indexBuilder.Build(document);
        await vectorIndexing.IndexAsync(document, indexes.TextIndex, ct);

        var targetItems = BuildDefaultTargets(document);
        var targetSet = targetSetService.Create(
            document.Id,
            targetItems,
            userRequest,
            label: userRequest,
            blockIdResolver: item => ResolveBlockId(document, item));

        return new CommandPlan
        {
            TargetSet = targetSet,
            UserCommand = userRequest
        };
    }

    private static List<LinearItem> BuildDefaultTargets(Document document)
    {
        return document.LinearDocument.Items
            .Where(b => b.Type is LinearItemType.Paragraph or LinearItemType.ListItem or LinearItemType.Code)
            .ToList();
    }

    private static string? ResolveBlockId(Document document, LinearItem item)
    {
        var block = document.Blocks.FirstOrDefault(b =>
            string.Equals(b.StructuralPath, item.Pointer.SemanticNumber, StringComparison.OrdinalIgnoreCase));

        return block?.Id;
    }
}

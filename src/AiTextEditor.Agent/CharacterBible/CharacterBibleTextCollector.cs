using AiTextEditor.Core.Interfaces;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible;

internal sealed class CharacterBibleTextCollector
{
    private readonly IDocumentContext documentContext;
    private readonly CharacterBibleExtractionLimits limits;
    private readonly ILogger logger;

    public CharacterBibleTextCollector(
        IDocumentContext documentContext,
        CharacterBibleExtractionLimits limits,
        ILogger logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<TextFragment> CollectParagraphs(
        IReadOnlyCollection<string>? changedPointers,
        IProgress<CharacterBibleWorkflowProgress>? progress = null)
    {
        var pointerSet = changedPointers?
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Select(pointer => pointer.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (pointerSet is { Count: > 0 })
        {
            return CollectChangedParagraphs(pointerSet, progress);
        }

        if (changedPointers is not null)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "collect",
                "No changed pointers were provided after normalization."));
            return [];
        }

        return CollectAllParagraphs(progress);
    }

    public static List<(string Pointer, string Text)> CollectParagraphsFromEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        return evidence
            .Where(e => !string.IsNullOrWhiteSpace(e.Pointer) && !string.IsNullOrWhiteSpace(e.Excerpt))
            .Select(e => (Pointer: e.Pointer.Trim(), Text: e.Excerpt!.Trim()))
            .Where(p => p.Pointer.Length > 0 && p.Text.Length > 0)
            .ToList();
    }

    private IReadOnlyList<TextFragment> CollectAllParagraphs(IProgress<CharacterBibleWorkflowProgress>? progress)
    {
        var cursor = new FullScanCursorStream(
            documentContext.Document,
            limits.MaxParagraphsPerBatch,
            limits.MaxBatchBytes,
            null,
            includeHeadings: false,
            logger,
            limits.FullScanMaxItems);

        var paragraphs = new List<TextFragment>();
        var chunkNumber = 0;
        while (true)
        {
            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            chunkNumber++;
            var beforeCount = paragraphs.Count;
            foreach (var item in portion.Items)
            {
                if (item.Type == LinearItemType.Heading)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Markdown))
                {
                    continue;
                }

                paragraphs.Add(new TextFragment(item.Pointer.ToCompactString(), item.Markdown));
            }

            progress?.Report(new CharacterBibleWorkflowProgress(
                "collect",
                $"Read book chunk {chunkNumber}: {paragraphs.Count - beforeCount} paragraphs collected, {paragraphs.Count} total."));

            if (!portion.HasMore)
            {
                break;
            }
        }

        return paragraphs;
    }

    private IReadOnlyList<TextFragment> CollectChangedParagraphs(
        IReadOnlySet<string> pointerSet,
        IProgress<CharacterBibleWorkflowProgress>? progress)
    {
        var lookup = documentContext.Document.Items
            .ToDictionary(item => item.Pointer.ToCompactString(), item => item, StringComparer.Ordinal);

        var paragraphs = new List<TextFragment>(pointerSet.Count);
        foreach (var pointer in pointerSet)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "collect",
                $"Reading changed pointer {pointer}."));

            if (!lookup.TryGetValue(pointer, out var item))
            {
                logger.LogWarning("RefreshCharacterDossiers: pointer not found: {Pointer}", pointer);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "collect",
                    $"Changed pointer {pointer} was not found."));
                continue;
            }

            if (item.Type == LinearItemType.Heading)
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "collect",
                    $"Changed pointer {pointer} is a heading and was skipped."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Markdown))
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "collect",
                    $"Changed pointer {pointer} is empty and was skipped."));
                continue;
            }

            paragraphs.Add(new TextFragment(pointer, item.Markdown));
            progress?.Report(new CharacterBibleWorkflowProgress(
                "collect",
                $"Changed pointer {pointer} added to extraction input."));
        }

        return paragraphs;
    }
}


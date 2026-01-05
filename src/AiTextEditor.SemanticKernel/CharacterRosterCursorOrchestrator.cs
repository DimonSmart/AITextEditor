using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterCursorOrchestrator
{
    private readonly IDocumentContext documentContext;
    private readonly ICursorStore cursorStore;
    private readonly ICursorAgentRuntime cursorAgentRuntime;
    private readonly CharacterRosterGenerator generator;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterRosterCursorOrchestrator> logger;
    private int cursorCounter;

    public CharacterRosterCursorOrchestrator(
        IDocumentContext documentContext,
        ICursorStore cursorStore,
        ICursorAgentRuntime cursorAgentRuntime,
        CharacterRosterGenerator generator,
        CursorAgentLimits limits,
        ILogger<CharacterRosterCursorOrchestrator> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        this.cursorAgentRuntime = cursorAgentRuntime ?? throw new ArgumentNullException(nameof(cursorAgentRuntime));
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterRoster> BuildRosterAsync(bool includeDossiers = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cursorName = CreateCursorName();
        var cursor = new FullScanCursorStream(
            documentContext.Document,
            limits.MaxElements,
            limits.MaxBytes,
            null,
            includeHeadings: true,
            logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException($"cursor_register_failed: {cursorName}");
        }

        logger.LogInformation("CharacterRosterCursorOrchestrator: cursor created {Cursor}", cursorName);

        var maxEvidenceCount = Math.Max(limits.DefaultMaxFound, documentContext.Document.Items.Count);
        var request = new CursorAgentRequest(
            "Collect ALL character mentions across the book. Return every paragraph where a person appears. Use evidence.pointer and evidence.excerpt exactly from batch items.",
            StartAfterPointer: null,
            Context: "Include aliases and nicknames. Count only named individuals or stable titles tied to a person; ignore generic groups/roles. Do not stop early; scan the whole book.",
            MaxEvidenceCount: maxEvidenceCount);

        var cursorAgentState = new CursorAgentState(Array.Empty<EvidenceItem>());
        var seenPointers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxSteps = Math.Clamp(limits.DefaultMaxSteps, 1, limits.MaxStepsLimit);
        var stepsUsed = 0;

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var portion = cursor.NextPortion();
            if (!portion.Items.Any())
            {
                break;
            }

            var cursorPortionView = CursorPortionView.FromPortion(portion);
            var command = await cursorAgentRuntime.RunStepAsync(request, cursorPortionView, cursorAgentState, step, cancellationToken);
            stepsUsed = step + 1;

            var normalizedEvidence = NormalizeEvidenceBatch(command.NewEvidence, cursorPortionView, seenPointers);
            if (normalizedEvidence.Count > 0)
            {
                var maxEvidenceForState = request.MaxEvidenceCount ?? limits.DefaultMaxFound;
                cursorAgentState = cursorAgentState.WithEvidence(normalizedEvidence, maxEvidenceForState);
                await generator.UpdateFromEvidenceBatchAsync(normalizedEvidence, cancellationToken);
            }

            if (!cursorPortionView.HasMore)
            {
                break;
            }

            if (string.Equals(command.Action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("CharacterRosterCursorOrchestrator: agent requested stop early; continuing scan.");
            }
        }

        if (stepsUsed >= maxSteps)
        {
            logger.LogWarning("CharacterRosterCursorOrchestrator: max steps reached {Steps}", stepsUsed);
        }

        return documentContext.CharacterRosterService.GetRoster();
    }

    private string CreateCursorName() => $"roster_cursor_{cursorCounter++}";

    private static IReadOnlyList<EvidenceItem> NormalizeEvidenceBatch(
        IReadOnlyList<EvidenceItem>? evidence,
        CursorPortionView portion,
        HashSet<string> seenPointers)
    {
        if (evidence == null || evidence.Count == 0)
        {
            return Array.Empty<EvidenceItem>();
        }

        var byPointer = portion.Items
            .Select(item => new { NormalizedPointer = NormalizePointer(item.SemanticPointer), item.Markdown })
            .Where(item => item.NormalizedPointer != null)
            .ToDictionary(item => item.NormalizedPointer!, item => item.Markdown, StringComparer.OrdinalIgnoreCase);

        var normalized = new List<EvidenceItem>(evidence.Count);
        foreach (var item in evidence)
        {
            var normalizedPointer = NormalizePointer(item.Pointer);
            if (normalizedPointer == null || !byPointer.TryGetValue(normalizedPointer, out var markdown))
            {
                continue;
            }

            if (!seenPointers.Add(normalizedPointer))
            {
                continue;
            }

            normalized.Add(new EvidenceItem(normalizedPointer, markdown, item.Reason));
        }

        return normalized;
    }

    private static string? NormalizePointer(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
        {
            return null;
        }

        return SemanticPointer.TryParse(pointer, out var parsed) ? parsed!.ToCompactString() : null;
    }
}

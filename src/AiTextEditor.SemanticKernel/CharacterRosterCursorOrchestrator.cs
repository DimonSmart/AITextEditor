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

        var request = new CursorAgentRequest(
            "Collect ALL character mentions across the book. Return every paragraph where a person appears. Use evidence.pointer and evidence.excerpt exactly from batch items.",
            StartAfterPointer: null,
            Context: "Include aliases and nicknames. Do not stop early; scan the whole book.",
            MaxEvidenceCount: Math.Max(limits.DefaultMaxFound, 500));

        var result = await cursorAgentRuntime.RunAsync(cursorName, request, cancellationToken);
        var evidence = result.Evidence ?? Array.Empty<EvidenceItem>();
        logger.LogInformation("CharacterRosterCursorOrchestrator: evidence collected {Count}, cursorComplete={CursorComplete}", evidence.Count, result.CursorComplete);

        return await generator.GenerateFromEvidenceAsync(evidence, includeDossiers, cancellationToken);
    }

    private string CreateCursorName() => $"roster_cursor_{cursorCounter++}";
}

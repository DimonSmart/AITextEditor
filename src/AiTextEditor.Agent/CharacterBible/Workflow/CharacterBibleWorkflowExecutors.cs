using AiTextEditor.Agent.CharacterBible;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Workflow;

internal sealed class CollectTextFragmentsExecutor : Executor<CharacterBibleWorkflowInput, CharacterBibleTraversalResult>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<CollectTextFragmentsExecutor> logger;
    private readonly IProgress<CharacterBibleWorkflowProgress>? progress;

    public CollectTextFragmentsExecutor(
        CharacterDossiersGenerator generator,
        ILogger<CollectTextFragmentsExecutor> logger,
        IProgress<CharacterBibleWorkflowProgress>? progress)
        : base("collect_character_bible_paragraphs", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.progress = progress;
    }

    public override ValueTask<CharacterBibleTraversalResult> HandleAsync(
        CharacterBibleWorkflowInput request,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new CharacterBibleWorkflowProgress("collect", "Collecting character bible paragraphs."));
        var paragraphs = generator.CollectParagraphs(request.ChangedPointers, progress);
        logger.LogInformation("character_bible_paragraphs_collected: count={Count}", paragraphs.Count);
        progress?.Report(new CharacterBibleWorkflowProgress(
            "collect",
            $"Collected {paragraphs.Count} paragraphs for character bible processing."));

        return ValueTask.FromResult(new CharacterBibleTraversalResult(request, paragraphs));
    }
}

internal sealed class ExtractCharacterBibleCandidatesExecutor : Executor<CharacterBibleTraversalResult, CharacterBibleExtractionResult>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<ExtractCharacterBibleCandidatesExecutor> logger;
    private readonly IProgress<CharacterBibleWorkflowProgress>? progress;

    public ExtractCharacterBibleCandidatesExecutor(
        CharacterDossiersGenerator generator,
        ILogger<ExtractCharacterBibleCandidatesExecutor> logger,
        IProgress<CharacterBibleWorkflowProgress>? progress)
        : base("extract_character_bible_candidates", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.progress = progress;
    }

    public override async ValueTask<CharacterBibleExtractionResult> HandleAsync(
        CharacterBibleTraversalResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Starting candidate extraction from {input.Paragraphs.Count} paragraphs."));
            var extraction = await generator.ExtractCandidatesAsync(input.Paragraphs, progress, cancellationToken);
            logger.LogInformation("character_bible_candidates_extracted: count={Count}", extraction.Candidates.Count);

            return new CharacterBibleExtractionResult(
                input.Request,
                input.Paragraphs,
                extraction.Candidates,
                extraction.ModelResponseErrors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "character_bible_candidates_extraction_failed");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Candidate extraction failed: {ex.Message}",
                IsError: true));
            return new CharacterBibleExtractionResult(
                input.Request,
                input.Paragraphs,
                [],
                CharacterBibleModelResponseErrorStatistics.Empty,
                ex);
        }
    }
}

internal sealed class ResolveCharacterBibleCandidatesExecutor : Executor<CharacterBibleExtractionResult, CharacterBibleRunState>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<ResolveCharacterBibleCandidatesExecutor> logger;
    private readonly IProgress<CharacterBibleWorkflowProgress>? progress;

    public ResolveCharacterBibleCandidatesExecutor(
        CharacterDossiersGenerator generator,
        ILogger<ResolveCharacterBibleCandidatesExecutor> logger,
        IProgress<CharacterBibleWorkflowProgress>? progress)
        : base("resolve_character_bible_candidates", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.progress = progress;
    }

    public override async ValueTask<CharacterBibleRunState> HandleAsync(
        CharacterBibleExtractionResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var session = generator.CreateEditSession();

        if (input.Failure is not null)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                "Skipping candidate resolution because extraction failed.",
                IsError: true));
            return new CharacterBibleRunState(
                input.Request,
                session,
                input.Paragraphs.Count,
                input.Candidates,
                input.ModelResponseErrors,
                input.Failure);
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "resolve",
            $"Resolving {input.Candidates.Count} character candidates."));
        var runState = await generator.ResolveCandidatesIntoCatalogAsync(
            input.Request,
            session,
            input.Paragraphs.Count,
            input.Candidates,
            progress,
            cancellationToken);
        runState = runState with { ModelResponseErrors = input.ModelResponseErrors };
        logger.LogInformation(
            "character_bible_candidates_resolved: candidates={CandidateCount}, decisions={DecisionCount}, changed={Changed}",
            runState.Candidates.Count,
            runState.Catalog.Decisions.Count,
            runState.Catalog.Changed);
        progress?.Report(new CharacterBibleWorkflowProgress(
            "resolve",
            $"Resolved {runState.Catalog.Decisions.Count} decisions; ambiguous: {runState.Catalog.Decisions.Count(decision => decision.Kind == CharacterBibleDecisionKind.Ambiguous)}."));

        return runState;
    }
}

internal sealed class PatchCharacterBibleDossiersExecutor : Executor<CharacterBibleRunState, CharacterBibleRunState>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<PatchCharacterBibleDossiersExecutor> logger;
    private readonly IProgress<CharacterBibleWorkflowProgress>? progress;

    public PatchCharacterBibleDossiersExecutor(
        CharacterDossiersGenerator generator,
        ILogger<PatchCharacterBibleDossiersExecutor> logger,
        IProgress<CharacterBibleWorkflowProgress>? progress)
        : base("patch_character_bible_dossiers", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.progress = progress;
    }

    public override async ValueTask<CharacterBibleRunState> HandleAsync(
        CharacterBibleRunState runState,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runState);
        cancellationToken.ThrowIfCancellationRequested();

        if (runState.Failure is not null)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                "Skipping dossier patching because the workflow failed.",
                IsError: true));
            return runState;
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "patch",
            $"Patching dossiers for {runState.Catalog.Decisions.Count} resolved decisions."));
        var patchedRunState = await generator.ApplyDossierPatchesAsync(runState, progress, cancellationToken);
        logger.LogInformation(
            "character_bible_dossiers_patched: changed={Changed}, decisions={DecisionCount}",
            patchedRunState.Catalog.Changed,
            patchedRunState.Catalog.Decisions.Count);
        return patchedRunState;
    }
}

internal sealed class FinishCharacterBibleWorkflowExecutor : Executor<CharacterBibleRunState, CharacterBibleWorkflowOutput>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<FinishCharacterBibleWorkflowExecutor> logger;
    private readonly IProgress<CharacterBibleWorkflowProgress>? progress;

    public FinishCharacterBibleWorkflowExecutor(
        CharacterDossiersGenerator generator,
        ILogger<FinishCharacterBibleWorkflowExecutor> logger,
        IProgress<CharacterBibleWorkflowProgress>? progress)
        : base("finish_character_bible_workflow", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.progress = progress;
    }

    public override ValueTask<CharacterBibleWorkflowOutput> HandleAsync(
        CharacterBibleRunState runState,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runState);
        cancellationToken.ThrowIfCancellationRequested();

        if (runState.Failure is not null)
        {
            var failedPointerCount = NormalizePointers(runState.Request.ChangedPointers).Count;
            progress?.Report(new CharacterBibleWorkflowProgress(
                "finish",
                "Bible update skipped because the workflow failed.",
                IsError: true));
            return ValueTask.FromResult(new CharacterBibleWorkflowOutput(
                runState.Catalog.Current,
                "failed",
                failedPointerCount,
                runState.ParagraphCount,
                runState.Candidates.Count,
                runState.Catalog.Decisions.Count,
                runState.Catalog.Decisions.Count(decision => decision.Kind == CharacterBibleDecisionKind.Ambiguous),
                runState.Catalog.Decisions,
                runState.ModelResponseErrors,
                runState.Failure));
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "finish",
            $"Finishing character bible workflow from {runState.Catalog.Decisions.Count} decisions."));
        var dossiers = generator.FinishRun(runState);
        var changedPointerCount = NormalizePointers(runState.Request.ChangedPointers).Count;
        var status = runState.Request.ChangedPointers is null ? "generated" : "refreshed";

        logger.LogInformation(
            "character_bible_workflow_completed: status={Status}, dossiers={DossiersId}, version={Version}, changedPointers={ChangedPointerCount}",
            status,
            dossiers.DossiersId,
            dossiers.Version,
            changedPointerCount);

        progress?.Report(new CharacterBibleWorkflowProgress(
            "finish",
            $"Character bible {status}: {dossiers.Characters.Count} dossiers, version {dossiers.Version}."));

        return ValueTask.FromResult(new CharacterBibleWorkflowOutput(
            dossiers,
            status,
            changedPointerCount,
            runState.ParagraphCount,
            runState.Candidates.Count,
            runState.Catalog.Decisions.Count,
            runState.Catalog.Decisions.Count(decision => decision.Kind == CharacterBibleDecisionKind.Ambiguous),
            runState.Catalog.Decisions,
            runState.ModelResponseErrors));
    }

    private static IReadOnlyCollection<string> NormalizePointers(IReadOnlyCollection<string>? changedPointers)
    {
        return changedPointers?
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Select(pointer => pointer.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }
}

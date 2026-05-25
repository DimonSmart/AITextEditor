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
            var candidates = await generator.ExtractCandidatesAsync(input.Paragraphs, progress, cancellationToken);
            logger.LogInformation("character_bible_candidates_extracted: count={Count}", candidates.Count);

            return new CharacterBibleExtractionResult(input.Request, input.Paragraphs, candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "character_bible_candidates_extraction_failed");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "extract",
                $"Candidate extraction failed: {ex.Message}"));
            return new CharacterBibleExtractionResult(input.Request, input.Paragraphs, [], ex);
        }
    }
}

internal sealed class ResolveCharacterBibleCandidatesExecutor : Executor<CharacterBibleExtractionResult, CharacterBibleCommitPlan>
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

    public override ValueTask<CharacterBibleCommitPlan> HandleAsync(
        CharacterBibleExtractionResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (input.Failure is not null)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                "Skipping candidate resolution because extraction failed."));
            return ValueTask.FromResult(new CharacterBibleCommitPlan(
                input.Request,
                generator.GetCurrentDossiers(),
                false,
                input.Paragraphs.Count,
                input.Candidates.Count,
                [],
                input.Failure));
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "resolve",
            $"Resolving {input.Candidates.Count} character candidates."));
        var plan = generator.CreateCommitPlan(input.Request, input.Paragraphs.Count, input.Candidates, progress);
        logger.LogInformation(
            "character_bible_candidates_resolved: candidates={CandidateCount}, decisions={DecisionCount}, changed={Changed}",
            plan.CandidateCount,
            plan.Decisions.Count,
            plan.Changed);
        progress?.Report(new CharacterBibleWorkflowProgress(
            "resolve",
            $"Resolved {plan.Decisions.Count} decisions; ambiguous: {plan.Decisions.Count(decision => decision.Kind == CharacterBibleDecisionKind.Ambiguous)}."));

        return ValueTask.FromResult(plan);
    }
}

internal sealed class CommitCharacterBibleDossiersExecutor : Executor<CharacterBibleCommitPlan, CharacterBibleWorkflowOutput>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<CommitCharacterBibleDossiersExecutor> logger;
    private readonly IProgress<CharacterBibleWorkflowProgress>? progress;

    public CommitCharacterBibleDossiersExecutor(
        CharacterDossiersGenerator generator,
        ILogger<CommitCharacterBibleDossiersExecutor> logger,
        IProgress<CharacterBibleWorkflowProgress>? progress)
        : base("commit_character_bible_dossiers", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.progress = progress;
    }

    public override ValueTask<CharacterBibleWorkflowOutput> HandleAsync(
        CharacterBibleCommitPlan plan,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.Failure is not null)
        {
            var failedPointerCount = NormalizePointers(plan.Request.ChangedPointers).Count;
            progress?.Report(new CharacterBibleWorkflowProgress(
                "commit",
                "Bible update skipped because the workflow failed."));
            return ValueTask.FromResult(new CharacterBibleWorkflowOutput(
                plan.ProjectedDossiers,
                "failed",
                failedPointerCount,
                plan.ParagraphCount,
                plan.CandidateCount,
                plan.Decisions.Count,
                plan.Decisions.Count(decision => decision.Kind == CharacterBibleDecisionKind.Ambiguous),
                plan.Decisions,
                plan.Failure));
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "commit",
            $"Updating character bible from {plan.Decisions.Count} decisions."));
        var dossiers = generator.CommitPlan(plan);
        var changedPointerCount = NormalizePointers(plan.Request.ChangedPointers).Count;
        var status = plan.Request.ChangedPointers is null ? "generated" : "refreshed";

        logger.LogInformation(
            "character_bible_workflow_completed: status={Status}, dossiers={DossiersId}, version={Version}, changedPointers={ChangedPointerCount}",
            status,
            dossiers.DossiersId,
            dossiers.Version,
            changedPointerCount);

        progress?.Report(new CharacterBibleWorkflowProgress(
            "commit",
            $"Character bible {status}: {dossiers.Characters.Count} dossiers, version {dossiers.Version}."));

        return ValueTask.FromResult(new CharacterBibleWorkflowOutput(
            dossiers,
            status,
            changedPointerCount,
            plan.ParagraphCount,
            plan.CandidateCount,
            plan.Decisions.Count,
            plan.Decisions.Count(decision => decision.Kind == CharacterBibleDecisionKind.Ambiguous),
            plan.Decisions));
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

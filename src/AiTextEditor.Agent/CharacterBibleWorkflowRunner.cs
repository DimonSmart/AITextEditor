using AiTextEditor.Core.Model;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

namespace AiTextEditor.Agent;

public sealed class CharacterBibleWorkflowRunner
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILoggerFactory loggerFactory;

    public CharacterBibleWorkflowRunner(
        CharacterDossiersGenerator generator,
        ILoggerFactory loggerFactory)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async Task<CharacterBibleWorkflowResult> RunAsync(
        CharacterBibleWorkflowRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new CharacterBibleWorkflowRequest();

        var traversalExecutor = new CollectCharacterBibleParagraphsExecutor(
            generator,
            loggerFactory.CreateLogger<CollectCharacterBibleParagraphsExecutor>());
        var extractionExecutor = new ExtractCharacterBibleCandidatesExecutor(
            generator,
            loggerFactory.CreateLogger<ExtractCharacterBibleCandidatesExecutor>());
        var resolutionExecutor = new ResolveCharacterBibleCandidatesExecutor(
            generator,
            loggerFactory.CreateLogger<ResolveCharacterBibleCandidatesExecutor>());
        var commitExecutor = new CommitCharacterBibleDossiersExecutor(
            generator,
            loggerFactory.CreateLogger<CommitCharacterBibleDossiersExecutor>());

        ExecutorBinding traversalBinding = traversalExecutor;
        ExecutorBinding extractionBinding = extractionExecutor;
        ExecutorBinding resolutionBinding = resolutionExecutor;
        ExecutorBinding commitBinding = commitExecutor;

        var workflow = new WorkflowBuilder(traversalBinding)
            .WithName("CharacterBibleWorkflow")
            .AddEdge(traversalBinding, extractionBinding)
            .AddEdge(extractionBinding, resolutionBinding)
            .AddEdge(resolutionBinding, commitBinding)
            .WithOutputFrom(commitBinding)
            .Build();

        var run = await InProcessExecution.RunAsync(
            workflow,
            request,
            sessionId: request.WorkflowRunId,
            cancellationToken: cancellationToken);

        foreach (var outputEvent in run.NewEvents.OfType<WorkflowOutputEvent>().Reverse())
        {
            if (outputEvent.Is<CharacterBibleWorkflowResult>(out var result))
            {
                if (result.Failure is not null)
                {
                    ExceptionDispatchInfo.Capture(result.Failure).Throw();
                }

                return result;
            }
        }

        foreach (var failedEvent in run.NewEvents.OfType<ExecutorFailedEvent>().Reverse())
        {
            if (failedEvent.Data is Exception exception)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            throw new InvalidOperationException("character_bible_workflow_executor_failed");
        }

        foreach (var errorEvent in run.NewEvents.OfType<WorkflowErrorEvent>().Reverse())
        {
            if (errorEvent.Exception is not null)
            {
                ExceptionDispatchInfo.Capture(errorEvent.Exception).Throw();
            }

            throw new InvalidOperationException("character_bible_workflow_failed");
        }

        throw new InvalidOperationException("character_bible_workflow_produced_no_result");
    }
}

internal sealed class CollectCharacterBibleParagraphsExecutor : Executor<CharacterBibleWorkflowRequest, CharacterBibleTraversalResult>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<CollectCharacterBibleParagraphsExecutor> logger;

    public CollectCharacterBibleParagraphsExecutor(
        CharacterDossiersGenerator generator,
        ILogger<CollectCharacterBibleParagraphsExecutor> logger)
        : base("collect_character_bible_paragraphs", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override ValueTask<CharacterBibleTraversalResult> HandleAsync(
        CharacterBibleWorkflowRequest request,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = generator.CollectParagraphs(request.ChangedPointers);
        logger.LogInformation("character_bible_paragraphs_collected: count={Count}", paragraphs.Count);

        return ValueTask.FromResult(new CharacterBibleTraversalResult(request, paragraphs));
    }
}

internal sealed class ExtractCharacterBibleCandidatesExecutor : Executor<CharacterBibleTraversalResult, CharacterBibleExtractionResult>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<ExtractCharacterBibleCandidatesExecutor> logger;

    public ExtractCharacterBibleCandidatesExecutor(
        CharacterDossiersGenerator generator,
        ILogger<ExtractCharacterBibleCandidatesExecutor> logger)
        : base("extract_character_bible_candidates", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async ValueTask<CharacterBibleExtractionResult> HandleAsync(
        CharacterBibleTraversalResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var candidates = await generator.ExtractCandidatesAsync(input.Paragraphs, cancellationToken);
            logger.LogInformation("character_bible_candidates_extracted: count={Count}", candidates.Count);

            return new CharacterBibleExtractionResult(input.Request, input.Paragraphs, candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "character_bible_candidates_extraction_failed");
            return new CharacterBibleExtractionResult(input.Request, input.Paragraphs, [], ex);
        }
    }
}

internal sealed class ResolveCharacterBibleCandidatesExecutor : Executor<CharacterBibleExtractionResult, CharacterBibleCommitPlan>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<ResolveCharacterBibleCandidatesExecutor> logger;

    public ResolveCharacterBibleCandidatesExecutor(
        CharacterDossiersGenerator generator,
        ILogger<ResolveCharacterBibleCandidatesExecutor> logger)
        : base("resolve_character_bible_candidates", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            return ValueTask.FromResult(new CharacterBibleCommitPlan(
                input.Request,
                generator.GetCurrentDossiers(),
                false,
                input.Paragraphs.Count,
                input.Candidates.Count,
                [],
                input.Failure));
        }

        var plan = generator.CreateCommitPlan(input.Request, input.Paragraphs.Count, input.Candidates);
        logger.LogInformation(
            "character_bible_candidates_resolved: candidates={CandidateCount}, decisions={DecisionCount}, changed={Changed}",
            plan.CandidateCount,
            plan.Decisions.Count,
            plan.Changed);

        return ValueTask.FromResult(plan);
    }
}

internal sealed class CommitCharacterBibleDossiersExecutor : Executor<CharacterBibleCommitPlan, CharacterBibleWorkflowResult>
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ILogger<CommitCharacterBibleDossiersExecutor> logger;

    public CommitCharacterBibleDossiersExecutor(
        CharacterDossiersGenerator generator,
        ILogger<CommitCharacterBibleDossiersExecutor> logger)
        : base("commit_character_bible_dossiers", ExecutorOptions.Default, declareCrossRunShareable: false)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override ValueTask<CharacterBibleWorkflowResult> HandleAsync(
        CharacterBibleCommitPlan plan,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.Failure is not null)
        {
            var failedPointerCount = NormalizePointers(plan.Request.ChangedPointers).Count;
            return ValueTask.FromResult(new CharacterBibleWorkflowResult(
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

        var dossiers = generator.CommitPlan(plan);
        var changedPointerCount = NormalizePointers(plan.Request.ChangedPointers).Count;
        var status = plan.Request.ChangedPointers is null ? "generated" : "refreshed";

        logger.LogInformation(
            "character_bible_workflow_completed: status={Status}, dossiers={DossiersId}, version={Version}, changedPointers={ChangedPointerCount}",
            status,
            dossiers.DossiersId,
            dossiers.Version,
            changedPointerCount);

        return ValueTask.FromResult(new CharacterBibleWorkflowResult(
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

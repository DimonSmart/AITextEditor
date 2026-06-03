using AiTextEditor.Core.Model;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.Workflow;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

namespace AiTextEditor.Agent.CharacterBible;

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

    public Task<CharacterBibleWorkflowOutput> RunAsync(
        CharacterBibleWorkflowInput? request,
        CancellationToken cancellationToken)
    {
        return RunAsync(request, null, cancellationToken);
    }

    public async Task<CharacterBibleWorkflowOutput> RunAsync(
        CharacterBibleWorkflowInput? request = null,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new CharacterBibleWorkflowInput();
        using var runLogger = CharacterBibleRunLogger.Create(DateTimeOffset.Now);
        using var runLogScope = CharacterBibleRunLogScope.Push(runLogger);
        progress?.Report(new CharacterBibleWorkflowProgress(
            "log",
            $"Detailed log: {runLogger.Context.LogPath}",
            CopyText: runLogger.Context.LogPath,
            CopyLabel: "Copy log path",
            AlwaysVisible: true));
        runLogger.Info(
            "workflow.start",
            $"run={runLogger.Context.RunId} documentItems={generator.DocumentItemCount} changedPointersCount={NormalizePointers(request.ChangedPointers).Count} fullScanMaxItems={generator.Limits.FullScanMaxItems} maxParagraphsPerBatch={generator.Limits.MaxParagraphsPerBatch} maxBatchBytes={generator.Limits.MaxBatchBytes} overlapParagraphs={generator.Limits.OverlapParagraphs}");
        var loadedDossiers = generator.GetCurrentDossiers();
        runLogger.Info(
            "archive.loaded",
            $"version={loadedDossiers.Version} characterCount={loadedDossiers.Characters.Count} nextCharacterId={loadedDossiers.NextCharacterId}");

        var traversalExecutor = new CollectTextFragmentsExecutor(
            generator,
            loggerFactory.CreateLogger<CollectTextFragmentsExecutor>(),
            progress);
        var extractionExecutor = new ExtractCharacterBibleCandidatesExecutor(
            generator,
            loggerFactory.CreateLogger<ExtractCharacterBibleCandidatesExecutor>(),
            progress);
        var resolutionExecutor = new ResolveCharacterBibleCandidatesExecutor(
            generator,
            loggerFactory.CreateLogger<ResolveCharacterBibleCandidatesExecutor>(),
            progress);
        var canonicalNameNormalizationExecutor = new NormalizeCharacterCanonicalNamesExecutor(
            generator,
            loggerFactory.CreateLogger<NormalizeCharacterCanonicalNamesExecutor>(),
            progress);
        var patchExecutor = new PatchCharacterBibleDossiersExecutor(
            generator,
            loggerFactory.CreateLogger<PatchCharacterBibleDossiersExecutor>(),
            progress);
        var finishExecutor = new FinishCharacterBibleWorkflowExecutor(
            generator,
            loggerFactory.CreateLogger<FinishCharacterBibleWorkflowExecutor>(),
            progress);

        ExecutorBinding traversalBinding = traversalExecutor;
        ExecutorBinding extractionBinding = extractionExecutor;
        ExecutorBinding resolutionBinding = resolutionExecutor;
        ExecutorBinding canonicalNameNormalizationBinding = canonicalNameNormalizationExecutor;
        ExecutorBinding patchBinding = patchExecutor;
        ExecutorBinding finishBinding = finishExecutor;

        var workflow = new WorkflowBuilder(traversalBinding)
            .WithName("CharacterBibleWorkflow")
            .AddEdge(traversalBinding, extractionBinding)
            .AddEdge(extractionBinding, resolutionBinding)
            .AddEdge(resolutionBinding, canonicalNameNormalizationBinding)
            .AddEdge(canonicalNameNormalizationBinding, patchBinding)
            .AddEdge(patchBinding, finishBinding)
            .WithOutputFrom(finishBinding)
            .Build();

        var run = await InProcessExecution.RunAsync(
            workflow,
            request,
            cancellationToken: cancellationToken);

        foreach (var outputEvent in run.NewEvents.OfType<WorkflowOutputEvent>().Reverse())
        {
            if (outputEvent.Is<CharacterBibleWorkflowOutput>(out var result))
            {
                if (result.Failure is not null)
                {
                    runLogger.Error("workflow.finish", $"status=failed error={LogValueFormatter.Quote(result.Failure.Message)}", result.Failure);
                    ExceptionDispatchInfo.Capture(result.Failure).Throw();
                }

                runLogger.Info(
                    "workflow.finish",
                    $"status={result.Status} dossiersCount={result.Dossiers.Characters.Count} version={result.Dossiers.Version} nextCharacterId={result.Dossiers.NextCharacterId} candidateCount={result.CandidateCount} decisionCount={result.DecisionCount} ambiguousCount={result.AmbiguousDecisionCount} durationMs={(DateTimeOffset.Now - runLogger.Context.StartedAt).TotalMilliseconds:0}");
                return result;
            }
        }

        foreach (var failedEvent in run.NewEvents.OfType<ExecutorFailedEvent>().Reverse())
        {
            if (failedEvent.Data is Exception exception)
            {
                runLogger.Error("stage.failed", $"message={LogValueFormatter.Quote(exception.Message)}", exception);
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            runLogger.Error("stage.failed", "message=\"character_bible_workflow_executor_failed\"");
            throw new InvalidOperationException("character_bible_workflow_executor_failed");
        }

        foreach (var errorEvent in run.NewEvents.OfType<WorkflowErrorEvent>().Reverse())
        {
            if (errorEvent.Exception is not null)
            {
                runLogger.Error("workflow.failed", $"message={LogValueFormatter.Quote(errorEvent.Exception.Message)}", errorEvent.Exception);
                ExceptionDispatchInfo.Capture(errorEvent.Exception).Throw();
            }

            runLogger.Error("workflow.failed", "message=\"character_bible_workflow_failed\"");
            throw new InvalidOperationException("character_bible_workflow_failed");
        }

        runLogger.Error("workflow.failed", "message=\"character_bible_workflow_produced_no_result\"");
        throw new InvalidOperationException("character_bible_workflow_produced_no_result");
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

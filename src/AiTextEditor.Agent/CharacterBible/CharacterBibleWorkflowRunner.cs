using AiTextEditor.Core.Model;
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
        var commitExecutor = new CommitCharacterBibleDossiersExecutor(
            generator,
            loggerFactory.CreateLogger<CommitCharacterBibleDossiersExecutor>(),
            progress);

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
            cancellationToken: cancellationToken);

        foreach (var outputEvent in run.NewEvents.OfType<WorkflowOutputEvent>().Reverse())
        {
            if (outputEvent.Is<CharacterBibleWorkflowOutput>(out var result))
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

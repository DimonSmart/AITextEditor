using AiTextEditor.Agent;
using System.Threading.Channels;

namespace AiTextEditor.Web.Services;

public interface ICharacterBibleOperationRunner
{
    IAsyncEnumerable<CharacterBibleOperationEvent> RunAsync(
        CharacterBibleOperationRequest request,
        CancellationToken cancellationToken);
}

public interface ICharacterBibleWorkflowClient
{
    Task<CharacterBibleWorkflowOutput> RunAsync(
        EditorWorkspaceState workspace,
        CharacterBibleWorkflowInput request,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class CharacterBibleOperationRunner : ICharacterBibleOperationRunner
{
    private readonly EditorWorkspaceState workspace;
    private readonly ICharacterBibleWorkflowClient workflowClient;

    public CharacterBibleOperationRunner(
        EditorWorkspaceState workspace,
        ICharacterBibleWorkflowClient workflowClient)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.workflowClient = workflowClient ?? throw new ArgumentNullException(nameof(workflowClient));
    }

    public async IAsyncEnumerable<CharacterBibleOperationEvent> RunAsync(
        CharacterBibleOperationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserCommand);

        yield return CreateEvent(CharacterBibleOperationEventType.Started, "Operation started.");
        yield return CreateEvent(CharacterBibleOperationEventType.Progress, "Loading current document.");

        Exception? documentError = null;
        try
        {
            _ = workspace.CurrentDocument;
        }
        catch (Exception ex)
        {
            documentError = ex;
        }

        if (documentError is not null)
        {
            yield return new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Failed,
                documentError.Message,
                DateTimeOffset.UtcNow,
                Error: documentError);
            yield break;
        }

        yield return CreateEvent(
            CharacterBibleOperationEventType.Progress,
            $"Current document loaded: {workspace.CurrentDocument.Items.Count} linear items.");
        yield return CreateEvent(CharacterBibleOperationEventType.Progress, "Running character bible workflow.");

        var progressChannel = Channel.CreateUnbounded<CharacterBibleOperationEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        var workflowProgress = new CharacterBibleOperationProgress(progressChannel.Writer);

        Task<CharacterBibleWorkflowOutput>? workflowTask = null;
        CharacterBibleOperationEvent? immediateFailure = null;
        try
        {
            workflowTask = workflowClient.RunAsync(
                workspace,
                new CharacterBibleWorkflowInput(request.ChangedPointers),
                workflowProgress,
                cancellationToken);
        }
        catch (Exception ex)
        {
            immediateFailure = new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Failed,
                ex.Message,
                DateTimeOffset.UtcNow,
                Error: ex);
        }

        if (immediateFailure is not null)
        {
            yield return immediateFailure;
            yield break;
        }

        var activeWorkflowTask = workflowTask
            ?? throw new InvalidOperationException("character_bible_workflow_task_missing");

        while (!activeWorkflowTask.IsCompleted)
        {
            while (progressChannel.Reader.TryRead(out var progressEvent))
            {
                yield return progressEvent;
            }

            await Task.WhenAny(activeWorkflowTask, Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None));
        }

        while (progressChannel.Reader.TryRead(out var progressEvent))
        {
            yield return progressEvent;
        }

        CharacterBibleOperationEvent terminalEvent;
        try
        {
            var output = await activeWorkflowTask;
            workspace.CharacterDossiers.ReplaceDossiers(output.Dossiers.Characters);

            terminalEvent = new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Completed,
                "Operation completed.",
                DateTimeOffset.UtcNow,
                Output: output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            terminalEvent = CreateEvent(CharacterBibleOperationEventType.Cancelled, "Operation cancelled.");
        }
        catch (Exception ex)
        {
            terminalEvent = new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Failed,
                ex.Message,
                DateTimeOffset.UtcNow,
                Error: ex);
        }

        yield return terminalEvent;
    }

    private static CharacterBibleOperationEvent CreateEvent(CharacterBibleOperationEventType type, string message)
    {
        return new CharacterBibleOperationEvent(type, message, DateTimeOffset.UtcNow);
    }

    private sealed class CharacterBibleOperationProgress : IProgress<CharacterBibleWorkflowProgress>
    {
        private readonly ChannelWriter<CharacterBibleOperationEvent> writer;

        public CharacterBibleOperationProgress(ChannelWriter<CharacterBibleOperationEvent> writer)
        {
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public void Report(CharacterBibleWorkflowProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);

            writer.TryWrite(CreateEvent(CharacterBibleOperationEventType.Progress, value.Message));
        }
    }
}

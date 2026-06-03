using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
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
        using var automationLease = workspace.BeginAutomation();

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
        var workflowProgress = new CharacterBibleOperationProgress(workspace, progressChannel.Writer);

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
            workspace.ReplaceCharacterDossiers(output.Dossiers);
            var completedMessage = "Operation completed.";
            if (!string.IsNullOrWhiteSpace(workspace.CurrentBookPath))
            {
                await workspace.SaveCharacterBibleAsync(cancellationToken);
                completedMessage = $"Operation completed. Character bible saved: {workspace.CurrentCharacterBiblePath}";
            }

            terminalEvent = new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Completed,
                completedMessage,
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
        private readonly EditorWorkspaceState workspace;
        private readonly ChannelWriter<CharacterBibleOperationEvent> writer;

        public CharacterBibleOperationProgress(
            EditorWorkspaceState workspace,
            ChannelWriter<CharacterBibleOperationEvent> writer)
        {
            this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public void Report(CharacterBibleWorkflowProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.DossiersSnapshot is not null)
            {
                workspace.ReplaceCharacterDossiers(value.DossiersSnapshot);
            }

            writer.TryWrite(new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Progress,
                value.Message,
                DateTimeOffset.UtcNow,
                CopyText: value.CopyText,
                CopyLabel: value.CopyLabel,
                AlwaysVisible: value.AlwaysVisible,
                IsError: value.IsError));
        }
    }
}


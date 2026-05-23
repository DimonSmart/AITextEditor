using AiTextEditor.Agent;

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

        yield return CreateEvent(CharacterBibleOperationEventType.Progress, "Running character bible workflow.");

        CharacterBibleOperationEvent terminalEvent;
        try
        {
            var output = await workflowClient.RunAsync(
                workspace,
                new CharacterBibleWorkflowInput(request.ChangedPointers),
                cancellationToken);

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
}

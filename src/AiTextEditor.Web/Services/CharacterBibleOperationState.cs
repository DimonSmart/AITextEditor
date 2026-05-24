using AiTextEditor.Agent;

namespace AiTextEditor.Web.Services;

public sealed class CharacterBibleOperationState : IDisposable
{
    private readonly object syncRoot = new();
    private readonly List<CharacterBibleOperationRun> runs = [];
    private readonly ICharacterBibleOperationRunner operationRunner;
    private readonly ILogger<CharacterBibleOperationState> logger;
    private CancellationTokenSource? currentCancellation;
    private CharacterBibleOperationRun? currentRun;

    public CharacterBibleOperationState(
        ICharacterBibleOperationRunner operationRunner,
        ILogger<CharacterBibleOperationState> logger)
    {
        this.operationRunner = operationRunner ?? throw new ArgumentNullException(nameof(operationRunner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action? Changed;

    public bool IsRunning
    {
        get
        {
            lock (syncRoot)
            {
                return currentRun?.Status == CharacterBibleOperationStatus.Running;
            }
        }
    }

    public IReadOnlyList<CharacterBibleOperationRun> GetRuns()
    {
        lock (syncRoot)
        {
            return runs.Select(CloneRun).ToList();
        }
    }

    public Task StartAsync(CharacterBibleOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CharacterBibleOperationRun run;
        CancellationTokenSource cancellation;
        lock (syncRoot)
        {
            if (currentRun?.Status == CharacterBibleOperationStatus.Running)
            {
                throw new InvalidOperationException("A character bible operation is already running.");
            }

            cancellation = new CancellationTokenSource();
            run = new CharacterBibleOperationRun
            {
                UserCommand = request.UserCommand,
                Status = CharacterBibleOperationStatus.Running
            };
            runs.Add(run);
            currentRun = run;
            currentCancellation = cancellation;
            _ = RunOperationAsync(run, request, cancellation);
        }

        NotifyChanged();
        return Task.CompletedTask;
    }

    public void CancelCurrentRun()
    {
        lock (syncRoot)
        {
            currentCancellation?.Cancel();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (syncRoot)
        {
            cancellation = currentCancellation;
            currentCancellation = null;
            currentRun = null;
        }

        cancellation?.Cancel();
    }

    private async Task RunOperationAsync(
        CharacterBibleOperationRun run,
        CharacterBibleOperationRequest request,
        CancellationTokenSource cancellation)
    {
        try
        {
            await foreach (var operationEvent in operationRunner.RunAsync(request, cancellation.Token))
            {
                AddEvent(run, operationEvent);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            AddEvent(run, new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Cancelled,
                "Operation cancelled.",
                DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "character_bible_background_operation_failed");
            AddEvent(run, new CharacterBibleOperationEvent(
                CharacterBibleOperationEventType.Failed,
                ex.Message,
                DateTimeOffset.UtcNow,
                Error: ex));
        }
        finally
        {
            lock (syncRoot)
            {
                if (run.Status == CharacterBibleOperationStatus.Running)
                {
                    run.Status = CharacterBibleOperationStatus.Cancelled;
                    run.FinishedAt = DateTimeOffset.UtcNow;
                }

                if (ReferenceEquals(currentRun, run))
                {
                    currentRun = null;
                    currentCancellation = null;
                }
            }

            cancellation.Dispose();
            NotifyChanged();
        }
    }

    private void AddEvent(CharacterBibleOperationRun run, CharacterBibleOperationEvent operationEvent)
    {
        lock (syncRoot)
        {
            run.Events.Add(operationEvent);
            run.Status = StatusFromEvent(operationEvent.Type, run.Status);
            if (IsTerminal(operationEvent.Type))
            {
                run.FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        NotifyChanged();
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private static CharacterBibleOperationRun CloneRun(CharacterBibleOperationRun run)
    {
        var clone = new CharacterBibleOperationRun
        {
            Id = run.Id,
            UserCommand = run.UserCommand,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            Status = run.Status
        };
        clone.Events.AddRange(run.Events);
        return clone;
    }

    private static CharacterBibleOperationStatus StatusFromEvent(
        CharacterBibleOperationEventType eventType,
        CharacterBibleOperationStatus currentStatus)
    {
        return eventType switch
        {
            CharacterBibleOperationEventType.Completed => CharacterBibleOperationStatus.Completed,
            CharacterBibleOperationEventType.Failed => CharacterBibleOperationStatus.Failed,
            CharacterBibleOperationEventType.Cancelled => CharacterBibleOperationStatus.Cancelled,
            CharacterBibleOperationEventType.Started => CharacterBibleOperationStatus.Running,
            _ => currentStatus
        };
    }

    private static bool IsTerminal(CharacterBibleOperationEventType eventType)
    {
        return eventType is CharacterBibleOperationEventType.Completed
            or CharacterBibleOperationEventType.Failed
            or CharacterBibleOperationEventType.Cancelled;
    }
}

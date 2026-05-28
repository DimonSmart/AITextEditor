namespace AiTextEditor.Agent.CharacterBible.Diagnostics;

internal static class CharacterBibleRunLogScope
{
    private static readonly AsyncLocal<ICharacterBibleRunLogger?> CurrentLogger = new();

    public static ICharacterBibleRunLogger? Current => CurrentLogger.Value;

    public static IDisposable Push(ICharacterBibleRunLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var previous = CurrentLogger.Value;
        CurrentLogger.Value = logger;
        return new Scope(previous);
    }

    private sealed class Scope(ICharacterBibleRunLogger? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentLogger.Value = previous;
        }
    }
}

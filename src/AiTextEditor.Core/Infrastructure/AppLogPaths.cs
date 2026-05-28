namespace AiTextEditor.Core.Infrastructure;

public static class AppLogPaths
{
    public static string GetLogRoot()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Local application data folder is unavailable.");
        }

        return Path.Combine(localApplicationData, "AITextEditor", "logs");
    }

    public static string GetCharacterBibleLogDirectory()
    {
        return Path.Combine(GetLogRoot(), "character-bible");
    }

    public static string CreateCharacterBibleRunLogPath(DateTimeOffset now)
    {
        return Path.Combine(
            GetCharacterBibleLogDirectory(),
            $"character-bible-run-{now:yyyyMMdd-HHmmss}.log");
    }
}

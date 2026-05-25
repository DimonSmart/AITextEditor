namespace AiTextEditor.Core.Model;

public static class CharacterImportance
{
    public static string GetLabel(int? level) => level switch
    {
        null => "Unknown",
        <= 0 => "Unknown",
        1 => "Episodic",
        >= 2 and <= 4 => "Minor",
        >= 5 and <= 7 => "Supporting",
        >= 8 and <= 10 => "Main",
        _ => "Unknown"
    };

    public static int ToLevel(int score, int maxScore)
    {
        if (score <= 0 || maxScore <= 0)
        {
            return 0;
        }

        var normalized = Math.Log(1 + score) / Math.Log(1 + maxScore);
        return Math.Clamp((int)Math.Round(1 + normalized * 9), 1, 10);
    }

    public static int? NormalizeLevel(int? level)
    {
        if (level is null)
        {
            return null;
        }

        if (level <= 0)
        {
            return null;
        }

        return Math.Clamp(level.Value, 1, 10);
    }
}

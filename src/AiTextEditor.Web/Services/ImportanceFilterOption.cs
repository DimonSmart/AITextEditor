namespace AiTextEditor.Web.Services;

public sealed class ImportanceFilterOption
{
    public string Label { get; }
    public Func<int?, bool> Matches { get; }

    private ImportanceFilterOption(string label, Func<int?, bool> matches)
    {
        Label = label;
        Matches = matches;
    }

    public static readonly ImportanceFilterOption Any = new("Any importance", _ => true);

    public static readonly IReadOnlyList<ImportanceFilterOption> All =
    [
        Any,
        new("= Episodic (1)",        level => level == 1),
        new("= Minor (2–4)",         level => level >= 2 && level <= 4),
        new("= Supporting (5–7)",    level => level >= 5 && level <= 7),
        new("= Main (8–10)",         level => level >= 8 && level <= 10),
        new("≥ Minor (≥ 2)",         level => level >= 2),
        new("≥ Supporting (≥ 5)",    level => level >= 5),
        new("≥ Main (≥ 8)",          level => level >= 8),
        new("≤ Episodic (≤ 1)",      level => level == null || level <= 1),
        new("≤ Minor (≤ 4)",         level => level == null || level <= 4),
        new("≤ Supporting (≤ 7)",    level => level == null || level <= 7),
    ];
}

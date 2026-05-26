namespace AiTextEditor.Agent.CharacterBible;

public sealed class CharacterBibleExtractionLimits
{
    public int MaxParagraphsPerBatch { get; init; } = 20;

    public int MaxBatchBytes { get; init; } = 8000;

    public int OverlapParagraphs { get; init; }

    public int OverlapMaxBytes { get; init; }

    public int FullScanMaxItems { get; init; } = 100;

    public int? MaxCharacters { get; init; }
}

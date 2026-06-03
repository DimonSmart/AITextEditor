namespace AiTextEditor.Core.Model;

public sealed record NewCharacterDraft(
    string Name,
    IReadOnlyDictionary<string, string> ObservedNameFormExamples,
    string Gender = "unknown",
    int? ImportanceLevel = null,
    CharacterProfile? Profile = null);

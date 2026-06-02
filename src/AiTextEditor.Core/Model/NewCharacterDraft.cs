namespace AiTextEditor.Core.Model;

public sealed record NewCharacterDraft(
    string Name,
    IReadOnlyDictionary<string, string> AliasExamples,
    string Gender = "unknown",
    int? ImportanceLevel = null,
    CharacterProfile? Profile = null);

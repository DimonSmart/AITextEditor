using System.Collections.Generic;

namespace AiTextEditor.Core.Model;

public sealed record CharacterDossier(
    int CharacterId,
    string Name,
    IReadOnlyList<string> ObservedNameForms,
    IReadOnlyDictionary<string, string> ObservedNameFormExamples,
    string Gender = "unknown",
    int? ImportanceLevel = null,
    CharacterProfile? Profile = null);

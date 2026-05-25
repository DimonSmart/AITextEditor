using System.Collections.Generic;

namespace AiTextEditor.Core.Model;

public sealed record CharacterDossier(
    string CharacterId,
    string Name,
    string Description,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string> AliasExamples,
    string Gender = "unknown",
    int? ImportanceLevel = null,
    CharacterProfile? Profile = null);

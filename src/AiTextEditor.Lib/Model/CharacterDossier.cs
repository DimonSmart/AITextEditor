using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CharacterDossier(
    string CharacterId,
    string Name,
    string Description,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string> AliasExamples,
    IReadOnlyList<CharacterFact> Facts,
    string Gender = "unknown");

using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CharacterDossiers(
    string DossiersId,
    int Version,
    IReadOnlyList<CharacterDossier> Characters);

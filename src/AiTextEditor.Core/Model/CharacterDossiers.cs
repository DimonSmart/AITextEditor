using System.Collections.Generic;

namespace AiTextEditor.Core.Model;

public sealed record CharacterDossiers(
    string DossiersId,
    int Version,
    IReadOnlyList<CharacterDossier> Characters);

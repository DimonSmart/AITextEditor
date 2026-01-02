using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CharacterRoster(
    string RosterId,
    int Version,
    IReadOnlyList<CharacterProfile> Characters);

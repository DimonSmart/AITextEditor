using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CharacterProfile(
    string CharacterId,
    string Name,
    string Description,
    IReadOnlyList<string> Aliases,
    string? FirstPointer);

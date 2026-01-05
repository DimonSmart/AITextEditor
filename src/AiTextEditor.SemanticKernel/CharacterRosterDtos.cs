using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiTextEditor.SemanticKernel;

public sealed record CharacterRosterCommandResult(
    [property: JsonPropertyName("rosterId")] string RosterId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("characterId")] string? CharacterId = null);

public sealed record CharacterRosterPayload(
    [property: JsonPropertyName("rosterId")] string RosterId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("characters")] IReadOnlyList<CharacterRosterEntry> Characters);

public sealed record CharacterRosterEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("gender")] string Gender,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases);

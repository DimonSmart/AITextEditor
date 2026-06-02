using System.Collections.Generic;
using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible;

public sealed record CharacterDossiersCommandResult(
    [property: JsonPropertyName("dossiersId")] string DossiersId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("characterId")] int? CharacterId = null,
    [property: JsonPropertyName("candidateIds")] IReadOnlyList<int>? CandidateIds = null);

public sealed record CharacterDossiersPayload(
    [property: JsonPropertyName("dossiersId")] string DossiersId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("characters")] IReadOnlyList<CharacterDossierEntry> Characters);

public sealed record CharacterDossierEntry(
    [property: JsonPropertyName("characterId")] int CharacterId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("gender")] string Gender,
    [property: JsonPropertyName("importanceLevel")] int? ImportanceLevel,
    [property: JsonPropertyName("profile")] CharacterProfile Profile,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("aliasExamples")] IReadOnlyDictionary<string, string> AliasExamples);

public sealed record CharacterAliasExampleEntry(
    [property: JsonPropertyName("form")] string Form,
    [property: JsonPropertyName("example")] string Example);

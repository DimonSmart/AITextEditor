using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiTextEditor.SemanticKernel;

public sealed record CharacterDossiersCommandResult(
    [property: JsonPropertyName("dossiersId")] string DossiersId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("characterId")] string? CharacterId = null,
    [property: JsonPropertyName("candidateIds")] IReadOnlyList<string>? CandidateIds = null);

public sealed record CharacterDossiersPayload(
    [property: JsonPropertyName("dossiersId")] string DossiersId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("characters")] IReadOnlyList<CharacterDossierEntry> Characters);

public sealed record CharacterDossierEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("gender")] string Gender,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("aliasExamples")] IReadOnlyDictionary<string, string> AliasExamples,
    [property: JsonPropertyName("facts")] IReadOnlyList<CharacterFactEntry> Facts);

public sealed record CharacterAliasExampleEntry(
    [property: JsonPropertyName("form")] string Form,
    [property: JsonPropertyName("example")] string Example);

public sealed record CharacterFactEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("example")] string Example);

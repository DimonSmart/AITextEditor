using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

public interface ICharacterExtractionModelClient
{
    Task<CharacterExtractionResponse> ExtractCharactersAsync(
        CharacterExtractionModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterExtractionModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed class CharacterExtractionResponse
{
    [JsonRequired]
    [JsonPropertyName("characters")]
    public List<CharacterExtractionCharacter> Characters { get; init; } = [];
}

public sealed record CharacterExtractionCharacter(
    [property: JsonRequired]
    [property: JsonPropertyName("canonicalName")] string? CanonicalName,
    [property: JsonRequired]
    [property: JsonPropertyName("gender")] string? Gender,
    [property: JsonRequired]
    [property: JsonPropertyName("aliases")] List<CharacterExtractionAlias>? Aliases,
    [property: JsonRequired]
    [property: JsonPropertyName("evidence")] List<CharacterExtractionEvidence>? Evidence = null);

public sealed record CharacterExtractionAlias(
    [property: JsonRequired]
    [property: JsonPropertyName("form")] string Form,
    [property: JsonRequired]
    [property: JsonPropertyName("evidence")] CharacterExtractionEvidence? Evidence);

public sealed record CharacterExtractionEvidence(
    [property: JsonRequired]
    [property: JsonPropertyName("pointer")] string? Pointer,
    [property: JsonRequired]
    [property: JsonPropertyName("excerpt")] string? Excerpt);

public sealed class AgenticCharacterExtractionModelClient : ICharacterExtractionModelClient
{
    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticCharacterExtractionModelClient> logger;

    public AgenticCharacterExtractionModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticCharacterExtractionModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterExtractionResponse> ExtractCharactersAsync(
        CharacterExtractionModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);

        var extractionResponse = await modelClient.RunAsync<CharacterExtractionResponse>(
            new AgenticModelRequest<CharacterExtractionResponse>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "character_extraction_response_contract_invalid",
                ValidateResponse: ValidateResponseContract,
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);

        return extractionResponse;
    }

    private static AgenticModelValidationResult ValidateResponseContract(CharacterExtractionResponse response)
    {
        return IsValidResponseContract(response, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValidResponseContract(CharacterExtractionResponse response, out string error)
    {
        for (var characterIndex = 0; characterIndex < response.Characters.Count; characterIndex++)
        {
            var character = response.Characters[characterIndex];
            if (string.IsNullOrWhiteSpace(character.CanonicalName))
            {
                error = $"characters[{characterIndex}].canonicalName is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(character.Gender))
            {
                error = $"characters[{characterIndex}].gender is required.";
                return false;
            }

            if (!string.Equals(character.Gender, "male", StringComparison.Ordinal)
                && !string.Equals(character.Gender, "female", StringComparison.Ordinal)
                && !string.Equals(character.Gender, "unknown", StringComparison.Ordinal))
            {
                error = $"characters[{characterIndex}].gender has unsupported value.";
                return false;
            }

            if (character.Aliases is null)
            {
                error = $"characters[{characterIndex}].aliases is required.";
                return false;
            }

            if (!IsValidEvidenceList(character.Evidence, $"characters[{characterIndex}].evidence", requireAny: true, out error))
            {
                return false;
            }

            for (var aliasIndex = 0; aliasIndex < character.Aliases.Count; aliasIndex++)
            {
                var alias = character.Aliases[aliasIndex];
                if (string.IsNullOrWhiteSpace(alias.Form))
                {
                    error = $"characters[{characterIndex}].aliases[{aliasIndex}].form is required.";
                    return false;
                }

                if (!IsValidEvidence(alias.Evidence, $"characters[{characterIndex}].aliases[{aliasIndex}].evidence", out error))
                {
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidEvidenceList(
        IReadOnlyList<CharacterExtractionEvidence>? evidence,
        string path,
        bool requireAny,
        out string error)
    {
        if (evidence is null)
        {
            error = $"{path} is required.";
            return false;
        }

        if (requireAny && evidence.Count == 0)
        {
            error = $"{path} must contain at least one item.";
            return false;
        }

        for (var evidenceIndex = 0; evidenceIndex < evidence.Count; evidenceIndex++)
        {
            if (!IsValidEvidence(evidence[evidenceIndex], $"{path}[{evidenceIndex}]", out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidEvidence(CharacterExtractionEvidence? evidence, string path, out string error)
    {
        if (evidence is null)
        {
            error = $"{path} is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(evidence.Pointer))
        {
            error = $"{path}.pointer is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(evidence.Excerpt))
        {
            error = $"{path}.excerpt is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

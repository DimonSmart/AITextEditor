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

public sealed record CharacterExtractionResponse(
    [property: JsonRequired]
    [property: JsonPropertyName("characters")] IReadOnlyList<ExtractedLocalCharacter> Characters);

public sealed record ExtractedLocalCharacter(
    [property: JsonRequired]
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonRequired]
    [property: JsonPropertyName("gender")] string? Gender,
    [property: JsonPropertyName("observedNameForms")] IReadOnlyList<string>? ObservedNameForms,
    [property: JsonRequired]
    [property: JsonPropertyName("pointers")] IReadOnlyList<string>? Pointers);

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

        return NormalizeResponse(extractionResponse);
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
            if (string.IsNullOrWhiteSpace(character.Name))
            {
                error = $"characters[{characterIndex}].name is required.";
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

            if (character.Pointers is null)
            {
                error = $"characters[{characterIndex}].pointers is required.";
                return false;
            }

            if (character.Pointers.Count == 0)
            {
                error = $"characters[{characterIndex}].pointers must contain at least one item.";
                return false;
            }

            for (var pointerIndex = 0; pointerIndex < character.Pointers.Count; pointerIndex++)
            {
                if (string.IsNullOrWhiteSpace(character.Pointers[pointerIndex]))
                {
                    error = $"characters[{characterIndex}].pointers[{pointerIndex}] is required.";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    private static CharacterExtractionResponse NormalizeResponse(CharacterExtractionResponse response)
    {
        return new CharacterExtractionResponse(
            response.Characters
                .Select(character => character with { ObservedNameForms = character.ObservedNameForms ?? [] })
                .ToArray());
    }
}

using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent;

public interface ICharacterExtractionModelClient
{
    Task<CharacterExtractionResponse> ExtractCharactersAsync(
        CharacterExtractionModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterExtractionModelRequest(
    string SystemPrompt,
    string UserPrompt);

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
    [property: JsonPropertyName("description")] string? Description);

public sealed record CharacterExtractionAlias(
    [property: JsonRequired]
    [property: JsonPropertyName("form")] string Form,
    [property: JsonRequired]
    [property: JsonPropertyName("example")] string Example);

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
            new AgenticModelRequest(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "character_extraction_response_contract_invalid"),
            cancellationToken).ConfigureAwait(false);

        if (!IsValidResponseContract(extractionResponse, out var validationError))
        {
            logger.LogError("Character extraction response contract validation failed: {ValidationError}", validationError);
            throw new InvalidOperationException("character_extraction_response_contract_invalid");
        }

        return extractionResponse;
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

            if (character.Description is null)
            {
                error = $"characters[{characterIndex}].description is required.";
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

                if (string.IsNullOrWhiteSpace(alias.Example))
                {
                    error = $"characters[{characterIndex}].aliases[{aliasIndex}].example is required.";
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }
}

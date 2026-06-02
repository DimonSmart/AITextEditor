using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface ICharacterProfileUpdateModelClient
{
    Task<CharacterProfileUpdateCompletion> UpdateProfileAsync(
        CharacterProfileUpdateModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterProfileUpdateModelRequest(
    string SystemPrompt,
    string UserPrompt,
    CharacterProfileUpdateToolAdapter Tool,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record CharacterProfileUpdateCompletion(
    [property: JsonRequired]
    [property: JsonPropertyName("completed")] bool Completed);

public sealed class AgenticCharacterProfileUpdateModelClient : ICharacterProfileUpdateModelClient
{
    private static readonly MethodInfo ReplaceProfileFieldMethod = typeof(CharacterProfileUpdateToolAdapter)
        .GetMethod(nameof(CharacterProfileUpdateToolAdapter.ReplaceProfileField))
        ?? throw new InvalidOperationException("Replace profile field tool method was not found.");

    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticCharacterProfileUpdateModelClient> logger;

    public AgenticCharacterProfileUpdateModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticCharacterProfileUpdateModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterProfileUpdateCompletion> UpdateProfileAsync(
        CharacterProfileUpdateModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);
        ArgumentNullException.ThrowIfNull(request.Tool);

        var replaceProfileFieldFunction = AIFunctionFactory.Create(
            ReplaceProfileFieldMethod,
            request.Tool,
            "replace_profile_field",
            "Replaces one evidence-backed profile field for the current character.",
            JsonSerializerOptions.Web);

        return await modelClient.RunAsync<CharacterProfileUpdateCompletion>(
            new AgenticModelRequest<CharacterProfileUpdateCompletion>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "character_profile_patch_completion_contract_invalid",
                ValidateResponse: Validate,
                Tools: [replaceProfileFieldFunction],
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);
    }

    private static AgenticModelValidationResult Validate(CharacterProfileUpdateCompletion completion)
        => completion.Completed
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid("completed must be true.");
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface ICharacterProfilePatchModelClient
{
    Task<CharacterProfilePatchCompletion> PatchAsync(
        CharacterProfilePatchModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterProfilePatchModelRequest(
    string SystemPrompt,
    string UserPrompt,
    CharacterProfilePatchTools Tools,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record CharacterProfilePatchCompletion(
    [property: JsonRequired]
    [property: JsonPropertyName("completed")] bool Completed);

public sealed class AgenticCharacterProfilePatchModelClient : ICharacterProfilePatchModelClient
{
    private static readonly MethodInfo SetProfileFieldMethod = typeof(CharacterProfilePatchTools)
        .GetMethod(nameof(CharacterProfilePatchTools.SetProfileField))
        ?? throw new InvalidOperationException("Set profile field tool method was not found.");

    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticCharacterProfilePatchModelClient> logger;

    public AgenticCharacterProfilePatchModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticCharacterProfilePatchModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterProfilePatchCompletion> PatchAsync(
        CharacterProfilePatchModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);
        ArgumentNullException.ThrowIfNull(request.Tools);

        var setProfileFieldFunction = AIFunctionFactory.Create(
            SetProfileFieldMethod,
            request.Tools,
            "set_profile_field",
            "Sets one evidence-backed profile field for the current character.",
            JsonSerializerOptions.Web);

        return await modelClient.RunAsync<CharacterProfilePatchCompletion>(
            new AgenticModelRequest<CharacterProfilePatchCompletion>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "character_profile_patch_completion_contract_invalid",
                ValidateResponse: Validate,
                Tools: [setProfileFieldFunction],
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);
    }

    private static AgenticModelValidationResult Validate(CharacterProfilePatchCompletion completion)
        => completion.Completed
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid("completed must be true.");
}

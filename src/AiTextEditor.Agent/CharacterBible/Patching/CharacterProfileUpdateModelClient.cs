using System.Reflection;
using System.Text.Json;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface ICharacterProfileUpdateModelClient
{
    Task<CharacterProfileUpdateModelResult> UpdateProfileAsync(
        CharacterProfileUpdateModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterProfileUpdateModelRequest(
    string SystemPrompt,
    string UserPrompt,
    CharacterProfileUpdateToolAdapter Tool,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record CharacterProfileUpdateModelResult(string FinalResponseText);

public sealed class AgenticCharacterProfileUpdateModelClient : ICharacterProfileUpdateModelClient
{
    private static readonly MethodInfo ReplaceProfileFieldMethod = typeof(CharacterProfileUpdateToolAdapter)
        .GetMethod(nameof(CharacterProfileUpdateToolAdapter.ReplaceProfileField))
        ?? throw new InvalidOperationException("Replace profile field tool method was not found.");

    private readonly IAgenticModelClient modelClient;

    public AgenticCharacterProfileUpdateModelClient(IAgenticModelClient modelClient)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
    }

    public async Task<CharacterProfileUpdateModelResult> UpdateProfileAsync(
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
            "Replaces one profile field for the current character.",
            JsonSerializerOptions.Web);

        var completion = await modelClient.RunToolOnlyAsync(
            new AgenticToolOnlyModelRequest(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                OperationName: "CharacterProfileUpdate",
                ModelCallError: "character_profile_update_model_call_failed",
                Tools: [replaceProfileFieldFunction],
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);
        return new CharacterProfileUpdateModelResult(completion.Text);
    }
}

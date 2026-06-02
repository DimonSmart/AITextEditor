using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface IDossierPatchProposalModelClient
{
    Task<DossierProfileUpdateProposal> ProposePatchAsync(
        DossierPatchProposalModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DossierPatchProposalModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBibleProfileUpdateStatus>))]
public enum CharacterBibleProfileUpdateStatus
{
    [JsonStringEnumMemberName("noUsefulChanges")]
    NoUsefulChanges,

    [JsonStringEnumMemberName("updated")]
    Updated
}

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBibleProfileField>))]
public enum CharacterBibleProfileField
{
    Appearance,
    StatusAndCompetence,
    PsychologicalProfile,
    SpeechAndCommunication
}

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBibleProfileUpdateAction>))]
public enum CharacterBibleProfileUpdateAction
{
    [JsonStringEnumMemberName("append")]
    Append,

    [JsonStringEnumMemberName("replace")]
    Replace
}

public sealed class DossierProfileUpdateProposal
{
    [JsonRequired]
    [JsonPropertyName("status")]
    public CharacterBibleProfileUpdateStatus? Status { get; init; }

    [JsonRequired]
    [JsonPropertyName("profile")]
    public CharacterBibleProfileUpdate? Profile { get; init; }

    [JsonRequired]
    [JsonPropertyName("changes")]
    public List<CharacterBibleProfileChange>? Changes { get; init; }
}

public sealed record CharacterBibleProfileUpdate(
    [property: JsonRequired]
    [property: JsonPropertyName("appearance")] string? Appearance,
    [property: JsonRequired]
    [property: JsonPropertyName("statusAndCompetence")] string? StatusAndCompetence,
    [property: JsonRequired]
    [property: JsonPropertyName("psychologicalProfile")] string? PsychologicalProfile,
    [property: JsonRequired]
    [property: JsonPropertyName("speechAndCommunication")] string? SpeechAndCommunication);

public sealed record CharacterBibleProfileChange(
    [property: JsonRequired]
    [property: JsonPropertyName("field")] CharacterBibleProfileField? Field,
    [property: JsonRequired]
    [property: JsonPropertyName("action")] CharacterBibleProfileUpdateAction? Action,
    [property: JsonRequired]
    [property: JsonPropertyName("evidencePointers")] List<string>? EvidencePointers,
    [property: JsonRequired]
    [property: JsonPropertyName("reason")] string? Reason);

public sealed class AgenticDossierPatchProposalModelClient : IDossierPatchProposalModelClient
{
    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticDossierPatchProposalModelClient> logger;

    public AgenticDossierPatchProposalModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticDossierPatchProposalModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DossierProfileUpdateProposal> ProposePatchAsync(
        DossierPatchProposalModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);

        var proposal = await modelClient.RunAsync<DossierProfileUpdateProposal>(
            new AgenticModelRequest<DossierProfileUpdateProposal>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "dossier_patch_proposal_contract_invalid",
                ValidateResponse: ValidateResponseContract,
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);

        return proposal;
    }

    private static AgenticModelValidationResult ValidateResponseContract(DossierProfileUpdateProposal proposal)
    {
        return IsValidResponseContract(proposal, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValidResponseContract(DossierProfileUpdateProposal proposal, out string error)
    {
        if (proposal.Status is null)
        {
            error = "status is required.";
            return false;
        }

        if (proposal.Profile is null)
        {
            error = "profile is required.";
            return false;
        }

        if (proposal.Profile.Appearance is null
            || proposal.Profile.StatusAndCompetence is null
            || proposal.Profile.PsychologicalProfile is null
            || proposal.Profile.SpeechAndCommunication is null)
        {
            error = "profile must contain all four fields.";
            return false;
        }

        if (proposal.Changes is null)
        {
            error = "changes is required.";
            return false;
        }

        if (proposal.Status == CharacterBibleProfileUpdateStatus.NoUsefulChanges && proposal.Changes.Count > 0)
        {
            error = "changes must be empty when status is noUsefulChanges.";
            return false;
        }

        if (proposal.Status == CharacterBibleProfileUpdateStatus.Updated && proposal.Changes.Count == 0)
        {
            error = "changes must not be empty when status is updated.";
            return false;
        }

        for (var index = 0; index < proposal.Changes.Count; index++)
        {
            var change = proposal.Changes[index];
            if (change.Field is null)
            {
                error = $"changes[{index}].field is required.";
                return false;
            }

            if (change.Action is null)
            {
                error = $"changes[{index}].action is required.";
                return false;
            }

            if (change.EvidencePointers is null)
            {
                error = $"changes[{index}].evidencePointers is required.";
                return false;
            }

            if (change.EvidencePointers.Count == 0)
            {
                error = $"changes[{index}].evidencePointers must not be empty.";
                return false;
            }

            if (change.EvidencePointers.Any(string.IsNullOrWhiteSpace))
            {
                error = $"changes[{index}].evidencePointers must not contain empty values.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(change.Reason))
            {
                error = $"changes[{index}].reason is required.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}

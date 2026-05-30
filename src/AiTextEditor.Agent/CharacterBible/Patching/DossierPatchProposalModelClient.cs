using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface IDossierPatchProposalModelClient
{
    Task<DossierPatchProposal> ProposePatchAsync(
        DossierPatchProposalModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DossierPatchProposalModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBiblePatchProposalStatus>))]
public enum CharacterBiblePatchProposalStatus
{
    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("noUsefulChanges")]
    NoUsefulChanges
}

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBibleProfileField>))]
public enum CharacterBibleProfileField
{
    Appearance,
    StatusAndCompetence,
    PsychologicalProfile,
    SpeechAndCommunication
}

public sealed class DossierPatchProposal
{
    [JsonRequired]
    [JsonPropertyName("status")]
    public CharacterBiblePatchProposalStatus? Status { get; init; }

    [JsonRequired]
    [JsonPropertyName("additions")]
    public List<CharacterBibleProfileAddition>? Additions { get; init; }
}

public sealed record CharacterBibleProfileAddition(
    [property: JsonRequired]
    [property: JsonPropertyName("field")] CharacterBibleProfileField? Field,
    [property: JsonRequired]
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonRequired]
    [property: JsonPropertyName("evidencePointers")] List<string>? EvidencePointers);

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

    public async Task<DossierPatchProposal> ProposePatchAsync(
        DossierPatchProposalModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);

        var proposal = await modelClient.RunAsync<DossierPatchProposal>(
            new AgenticModelRequest<DossierPatchProposal>(
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

    private static AgenticModelValidationResult ValidateResponseContract(DossierPatchProposal proposal)
    {
        return IsValidResponseContract(proposal, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValidResponseContract(DossierPatchProposal proposal, out string error)
    {
        if (proposal.Status is null)
        {
            error = "status is required.";
            return false;
        }

        if (proposal.Additions is null)
        {
            error = "additions is required.";
            return false;
        }

        if (proposal.Status == CharacterBiblePatchProposalStatus.NoUsefulChanges && proposal.Additions.Count > 0)
        {
            error = "additions must be empty when status is noUsefulChanges.";
            return false;
        }

        if (proposal.Status == CharacterBiblePatchProposalStatus.Ready && proposal.Additions.Count == 0)
        {
            error = "additions must not be empty when status is ready.";
            return false;
        }

        for (var index = 0; index < proposal.Additions.Count; index++)
        {
            var addition = proposal.Additions[index];
            if (addition.Field is null)
            {
                error = $"additions[{index}].field is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(addition.Text))
            {
                error = $"additions[{index}].text is required.";
                return false;
            }

            if (addition.EvidencePointers is null)
            {
                error = $"additions[{index}].evidencePointers is required.";
                return false;
            }

            if (addition.EvidencePointers.Count == 0)
            {
                error = $"additions[{index}].evidencePointers must not be empty.";
                return false;
            }

            if (addition.EvidencePointers.Any(string.IsNullOrWhiteSpace))
            {
                error = $"additions[{index}].evidencePointers must not contain empty values.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}

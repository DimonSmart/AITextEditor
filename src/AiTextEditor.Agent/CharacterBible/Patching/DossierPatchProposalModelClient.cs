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
    string UserPrompt);

public sealed class DossierPatchProposal
{
    [JsonRequired]
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonRequired]
    [JsonPropertyName("aliasesToAdd")]
    public List<string>? AliasesToAdd { get; init; }

    [JsonRequired]
    [JsonPropertyName("profilePatch")]
    public DossierProfilePatch? ProfilePatch { get; init; }

    [JsonRequired]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record DossierProfilePatch(
    [property: JsonRequired]
    [property: JsonPropertyName("appearance")] string? Appearance,
    [property: JsonRequired]
    [property: JsonPropertyName("statusAndCompetence")] string? StatusAndCompetence,
    [property: JsonRequired]
    [property: JsonPropertyName("psychologicalProfile")] string? PsychologicalProfile,
    [property: JsonRequired]
    [property: JsonPropertyName("speechAndCommunication")] string? SpeechAndCommunication,
    [property: JsonRequired]
    [property: JsonPropertyName("evidence")] List<DossierPatchEvidence>? Evidence);

public sealed record DossierPatchEvidence(
    [property: JsonRequired]
    [property: JsonPropertyName("pointer")] string? Pointer,
    [property: JsonRequired]
    [property: JsonPropertyName("excerpt")] string? Excerpt);

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
            new AgenticModelRequest(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "dossier_patch_proposal_contract_invalid"),
            cancellationToken).ConfigureAwait(false);

        if (!IsValidResponseContract(proposal, out var validationError))
        {
            logger.LogError("Dossier patch proposal contract validation failed: {ValidationError}", validationError);
            throw new InvalidOperationException("dossier_patch_proposal_contract_invalid");
        }

        return proposal;
    }

    private static bool IsValidResponseContract(DossierPatchProposal proposal, out string error)
    {
        if (string.IsNullOrWhiteSpace(proposal.Status))
        {
            error = "status is required.";
            return false;
        }

        if (!string.Equals(proposal.Status, "ready", StringComparison.Ordinal)
            && !string.Equals(proposal.Status, "no_useful_changes", StringComparison.Ordinal)
            && !string.Equals(proposal.Status, "identity_conflict", StringComparison.Ordinal))
        {
            error = "status has unsupported value.";
            return false;
        }

        if (proposal.Reason is null)
        {
            error = "reason is required.";
            return false;
        }

        if (proposal.AliasesToAdd is null)
        {
            error = "aliasesToAdd is required.";
            return false;
        }

        if (proposal.AliasesToAdd.Any(string.IsNullOrWhiteSpace))
        {
            error = "aliasesToAdd must not contain empty values.";
            return false;
        }

        if (string.Equals(proposal.Status, "ready", StringComparison.Ordinal)
            && proposal.ProfilePatch is null
            && proposal.AliasesToAdd.Count == 0)
        {
            error = "profilePatch or aliasesToAdd is required when status is ready.";
            return false;
        }

        if (proposal.ProfilePatch is not null && proposal.ProfilePatch.Evidence is null)
        {
            error = "profilePatch.evidence is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

using System.Text.Json.Serialization;
using System.Text.Json;
using System.Reflection;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible.Resolution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface ISplitCandidateModelClient
{
    Task<SplitProposal> ProposeSplitAsync(
        SplitCandidateModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SplitCandidateModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed class SplitCandidatePromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Patching.Prompts.split-candidate.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BuildSystemPrompt() => SystemPrompt.Value;

    internal string BuildUserPrompt(
        CharacterBibleCharacterCandidate candidate,
        IdentityResolutionDecision identityDecision,
        IReadOnlyList<CharacterArchiveHit> archiveHits)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(identityDecision);
        ArgumentNullException.ThrowIfNull(archiveHits);

        var payload = new
        {
            task = "propose_character_identity_split",
            candidate,
            identityDecision,
            archiveHits
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(SplitCandidatePromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded split candidate prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded split candidate prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

public sealed class SplitProposal
{
    [JsonRequired]
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonRequired]
    [JsonPropertyName("shards")]
    public List<SplitProposalShard>? Shards { get; init; }

    [JsonRequired]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record SplitProposalShard(
    [property: JsonRequired]
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonRequired]
    [property: JsonPropertyName("evidencePointers")] List<string>? EvidencePointers);

public sealed class AgenticSplitCandidateModelClient : ISplitCandidateModelClient
{
    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticSplitCandidateModelClient> logger;

    public AgenticSplitCandidateModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticSplitCandidateModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SplitProposal> ProposeSplitAsync(
        SplitCandidateModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var proposal = await modelClient.RunAsync<SplitProposal>(
            new AgenticModelRequest<SplitProposal>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "split_proposal_contract_invalid",
                ValidateResponse: Validate,
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);

        return proposal;
    }

    private static AgenticModelValidationResult Validate(SplitProposal proposal)
    {
        return IsValid(proposal, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValid(SplitProposal proposal, out string error)
    {
        if (string.IsNullOrWhiteSpace(proposal.Kind))
        {
            error = "kind is required.";
            return false;
        }

        if (!string.Equals(proposal.Kind, "no_split", StringComparison.Ordinal)
            && !string.Equals(proposal.Kind, "keep_candidate_separate", StringComparison.Ordinal)
            && !string.Equals(proposal.Kind, "split_existing_dossier", StringComparison.Ordinal)
            && !string.Equals(proposal.Kind, "manual_review_required", StringComparison.Ordinal))
        {
            error = "kind has unsupported value.";
            return false;
        }

        if (proposal.Shards is null)
        {
            error = "shards is required.";
            return false;
        }

        if (proposal.Reason is null)
        {
            error = "reason is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

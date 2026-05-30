using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public interface IDossierConsistencyReviewerModelClient
{
    Task<DossierReviewResult> ReviewAsync(
        DossierReviewModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DossierReviewModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed class DossierReviewResult
{
    [JsonRequired]
    [JsonPropertyName("verdict")]
    public CharacterBiblePatchReviewVerdict? Verdict { get; init; }

    [JsonRequired]
    [JsonPropertyName("issues")]
    public List<CharacterBiblePatchReviewIssue>? Issues { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBiblePatchReviewVerdict>))]
public enum CharacterBiblePatchReviewVerdict
{
    [JsonStringEnumMemberName("approved")]
    Approved,

    [JsonStringEnumMemberName("revisePatch")]
    RevisePatch
}

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBiblePatchReviewIssueCode>))]
public enum CharacterBiblePatchReviewIssueCode
{
    UnsupportedClaim,
    MissingEvidencePointer,
    PointerNotInEvidence,
    DuplicatesExistingFact,
    WrongCharacter,
    AttemptsToReplaceExistingField
}

public sealed record CharacterBiblePatchReviewIssue(
    [property: JsonRequired]
    [property: JsonPropertyName("code")] CharacterBiblePatchReviewIssueCode? Code,
    [property: JsonRequired]
    [property: JsonPropertyName("field")] CharacterBibleProfileField? Field,
    [property: JsonRequired]
    [property: JsonPropertyName("message")] string? Message);

public sealed class DossierConsistencyReviewerPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Patching.Prompts.dossier-consistency-reviewer.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BuildSystemPrompt() => SystemPrompt.Value;

    internal string BuildUserPrompt(
        CharacterDossier dossierBefore,
        DossierPatchProposal patchProposal,
        IReadOnlyList<CharacterBiblePatchEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(dossierBefore);
        ArgumentNullException.ThrowIfNull(patchProposal);
        ArgumentNullException.ThrowIfNull(evidence);

        var profile = CharacterProfile.Normalize(dossierBefore.Profile);

        var payload = new
        {
            target = new
            {
                name = dossierBefore.Name
            },
            currentProfile = new
            {
                appearance = NullIfWhiteSpace(profile.Appearance),
                statusAndCompetence = NullIfWhiteSpace(profile.StatusAndCompetence),
                psychologicalProfile = NullIfWhiteSpace(profile.PsychologicalProfile),
                speechAndCommunication = NullIfWhiteSpace(profile.SpeechAndCommunication)
            },
            proposal = patchProposal,
            evidence
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(DossierConsistencyReviewerPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded dossier reviewer prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded dossier reviewer prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

public sealed class AgenticDossierConsistencyReviewerModelClient : IDossierConsistencyReviewerModelClient
{
    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticDossierConsistencyReviewerModelClient> logger;

    public AgenticDossierConsistencyReviewerModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticDossierConsistencyReviewerModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DossierReviewResult> ReviewAsync(
        DossierReviewModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await modelClient.RunAsync<DossierReviewResult>(
            new AgenticModelRequest<DossierReviewResult>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "dossier_review_result_contract_invalid",
                ValidateResponse: Validate,
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static AgenticModelValidationResult Validate(DossierReviewResult result)
    {
        return IsValid(result, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValid(DossierReviewResult result, out string error)
    {
        if (result.Verdict is null)
        {
            error = "verdict is required.";
            return false;
        }

        if (result.Issues is null)
        {
            error = "issues is required.";
            return false;
        }

        if (result.Verdict == CharacterBiblePatchReviewVerdict.Approved && result.Issues.Count > 0)
        {
            error = "issues must be empty when verdict is approved.";
            return false;
        }

        if (result.Verdict == CharacterBiblePatchReviewVerdict.RevisePatch && result.Issues.Count == 0)
        {
            error = "issues must not be empty when verdict is revisePatch.";
            return false;
        }

        for (var index = 0; index < result.Issues.Count; index++)
        {
            var issue = result.Issues[index];
            if (issue.Code is null)
            {
                error = $"issues[{index}].code is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(issue.Message))
            {
                error = $"issues[{index}].message is required.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}

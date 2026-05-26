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
    public string? Verdict { get; init; }

    [JsonRequired]
    [JsonPropertyName("issues")]
    public List<string>? Issues { get; init; }
}

public sealed class DossierConsistencyReviewerPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Patching.Prompts.dossier-consistency-reviewer.system.md";

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
        CharacterDossier dossierBefore,
        DossierPatchProposal patchProposal,
        IReadOnlyList<CharacterBibleEvidenceContext> evidenceContexts)
    {
        ArgumentNullException.ThrowIfNull(dossierBefore);
        ArgumentNullException.ThrowIfNull(patchProposal);
        ArgumentNullException.ThrowIfNull(evidenceContexts);

        var payload = new
        {
            task = "review_dossier_patch",
            dossierBefore,
            patchProposal,
            evidenceContexts = evidenceContexts.Select(context => new
            {
                pointer = context.Pointer,
                anchorExcerpt = context.AnchorExcerpt,
                currentParagraph = context.CurrentParagraph,
                focusedText = context.FocusedText,
                nearbyParagraphs = context.NearbyParagraphs.Select(paragraph => new
                {
                    pointer = paragraph.Pointer,
                    text = paragraph.Text,
                    position = paragraph.Position
                })
            })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

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
        if (string.IsNullOrWhiteSpace(result.Verdict))
        {
            error = "verdict is required.";
            return false;
        }

        if (!string.Equals(result.Verdict, "approved", StringComparison.Ordinal)
            && !string.Equals(result.Verdict, "revise_patch", StringComparison.Ordinal)
            && !string.Equals(result.Verdict, "reject_patch", StringComparison.Ordinal)
            && !string.Equals(result.Verdict, "identity_conflict", StringComparison.Ordinal))
        {
            error = "verdict has unsupported value.";
            return false;
        }

        if (result.Issues is null)
        {
            error = "issues is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiTextEditor.Agent.CharacterBible.Normalization;

public interface ICharacterCanonicalNameNormalizationModelClient
{
    Task<CharacterCanonicalNameNormalizationResponse> NormalizeAsync(
        CharacterCanonicalNameNormalizationModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterCanonicalNameNormalizationModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record CharacterCanonicalNameNormalizationResponse(
    [property: JsonRequired]
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("canonicalName")] string? CanonicalName,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed class CharacterCanonicalNameNormalizationPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Normalization.Prompts.normalize-character-canonical-name.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BuildSystemPrompt() => SystemPrompt.Value;

    internal string BuildUserPrompt(CharacterDossier dossier)
    {
        return BuildUserPrompt(BuildPromptInput(dossier));
    }

    internal string BuildUserPrompt(CharacterCanonicalNameNormalizationPromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return JsonSerializer.Serialize(input, JsonOptions);
    }

    internal static CharacterCanonicalNameNormalizationPromptInput BuildPromptInput(CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);

        var profile = CharacterProfile.Normalize(dossier.Profile);
        return new CharacterCanonicalNameNormalizationPromptInput(
            dossier.CharacterId,
            dossier.Name,
            dossier.Gender,
            dossier.ObservedNameForms,
            dossier.ObservedNameFormExamples,
            new CharacterCanonicalNameNormalizationProfileInput(
                NullIfWhiteSpace(profile.Appearance),
                NullIfWhiteSpace(profile.StatusAndCompetence),
                NullIfWhiteSpace(profile.PsychologicalProfile),
                NullIfWhiteSpace(profile.SpeechAndCommunication)));
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(CharacterCanonicalNameNormalizationPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded canonical name normalization prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded canonical name normalization prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

internal sealed record CharacterCanonicalNameNormalizationPromptInput(
    int CharacterId,
    string CurrentName,
    string Gender,
    IReadOnlyList<string> ObservedNameForms,
    IReadOnlyDictionary<string, string> ObservedNameFormExamples,
    CharacterCanonicalNameNormalizationProfileInput Profile);

internal sealed record CharacterCanonicalNameNormalizationProfileInput(
    string? Appearance,
    string? StatusAndCompetence,
    string? PsychologicalProfile,
    string? SpeechAndCommunication);

public sealed class AgenticCharacterCanonicalNameNormalizationModelClient : ICharacterCanonicalNameNormalizationModelClient
{
    public const string InvalidContractError = "canonical_name_normalization_response_contract_invalid";

    private readonly IAgenticModelClient modelClient;

    public AgenticCharacterCanonicalNameNormalizationModelClient(IAgenticModelClient modelClient)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
    }

    public async Task<CharacterCanonicalNameNormalizationResponse> NormalizeAsync(
        CharacterCanonicalNameNormalizationModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);

        return await modelClient.RunAsync<CharacterCanonicalNameNormalizationResponse>(
            new AgenticModelRequest<CharacterCanonicalNameNormalizationResponse>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: InvalidContractError,
                ValidateResponse: Validate,
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);
    }

    private static AgenticModelValidationResult Validate(CharacterCanonicalNameNormalizationResponse response)
    {
        return IsValid(response, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValid(CharacterCanonicalNameNormalizationResponse response, out string error)
    {
        if (string.Equals(response.Status, "normalized", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(response.CanonicalName))
            {
                error = "canonicalName is required when status is normalized.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (string.Equals(response.Status, "insufficient_evidence", StringComparison.Ordinal))
        {
            error = string.Empty;
            return true;
        }

        error = "status must be normalized or insufficient_evidence.";
        return false;
    }
}

internal sealed class CharacterCanonicalNameNormalizer
{
    private readonly ICharacterCanonicalNameNormalizationModelClient modelClient;
    private readonly CharacterCanonicalNameNormalizationPromptBuilder promptBuilder;
    private readonly ILogger<CharacterCanonicalNameNormalizer> logger;

    public CharacterCanonicalNameNormalizer(
        ICharacterCanonicalNameNormalizationModelClient modelClient,
        CharacterCanonicalNameNormalizationPromptBuilder promptBuilder,
        ILogger<CharacterCanonicalNameNormalizer>? logger = null)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.logger = logger ?? NullLogger<CharacterCanonicalNameNormalizer>.Instance;
    }

    public async Task<CharacterBibleRunState> NormalizePendingAsync(
        CharacterBibleRunState runState,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runState);

        var pendingCharacterIds = (runState.PendingCanonicalNameNormalization ?? new HashSet<int>())
            .Where(characterId => characterId > 0)
            .Distinct()
            .Order()
            .ToArray();
        if (pendingCharacterIds.Length == 0)
        {
            return runState;
        }

        foreach (var characterId in pendingCharacterIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dossier = runState.Catalog.GetRequired(characterId);
            CharacterBibleRunLogScope.Current?.Info(
                "canonical_name_normalization.started",
                $"characterId={characterId} currentName={LogValueFormatter.Quote(dossier.Name)}");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "normalize_character_canonical_name",
                $"Normalizing canonical name for {dossier.Name}."));

            CharacterCanonicalNameNormalizationResponse response;
            try
            {
                response = await modelClient.NormalizeAsync(
                    new CharacterCanonicalNameNormalizationModelRequest(
                        promptBuilder.BuildSystemPrompt(),
                        promptBuilder.BuildUserPrompt(dossier),
                        new CharacterBibleAgentDiagnosticProgress(
                            progress,
                            "normalize_character_canonical_name",
                            $"Canonical name normalizer for {dossier.Name}",
                            $"characterId={characterId} currentName={LogValueFormatter.Quote(dossier.Name)}")),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "canonical_name_normalization_invalid_response: characterId={CharacterId} currentName={CurrentName}",
                    characterId,
                    dossier.Name);
                CharacterBibleRunLogScope.Current?.Warning(
                    "canonical_name_normalization.invalid_response",
                    $"characterId={characterId} currentName={LogValueFormatter.Quote(dossier.Name)} reason={LogValueFormatter.Quote(ex.Message)}");
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "normalize_character_canonical_name",
                    $"Canonical name normalizer returned an invalid response for {dossier.Name}.",
                    IsError: true));
                continue;
            }

            ApplyResponse(runState.Catalog, dossier, response, progress);
        }

        return runState with { PendingCanonicalNameNormalization = new HashSet<int>() };
    }

    private static void ApplyResponse(
        CharacterDossierEditSession catalog,
        CharacterDossier dossier,
        CharacterCanonicalNameNormalizationResponse response,
        IProgress<CharacterBibleWorkflowProgress>? progress)
    {
        var status = response.Status?.Trim();
        var reason = string.IsNullOrWhiteSpace(response.Reason)
            ? string.Empty
            : response.Reason.Trim();
        var canonicalName = response.CanonicalName?.Trim();

        if (string.Equals(status, "normalized", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(canonicalName))
        {
            catalog.RenameCharacter(dossier.CharacterId, canonicalName);
            CharacterBibleRunLogScope.Current?.Info(
                "canonical_name_normalization.normalized",
                $"characterId={dossier.CharacterId} currentName={LogValueFormatter.Quote(dossier.Name)} status={LogValueFormatter.Quote(status)} canonicalName={LogValueFormatter.Quote(canonicalName)} reason={LogValueFormatter.Quote(reason)}");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "normalize_character_canonical_name",
                $"Canonical name normalized: {dossier.Name} -> {canonicalName}."));
            return;
        }

        if (string.Equals(status, "insufficient_evidence", StringComparison.Ordinal))
        {
            CharacterBibleRunLogScope.Current?.Info(
                "canonical_name_normalization.insufficient_evidence",
                $"characterId={dossier.CharacterId} currentName={LogValueFormatter.Quote(dossier.Name)} status={LogValueFormatter.Quote(status)} reason={LogValueFormatter.Quote(reason)}");
            return;
        }

        CharacterBibleRunLogScope.Current?.Warning(
            "canonical_name_normalization.invalid_response",
            $"characterId={dossier.CharacterId} currentName={LogValueFormatter.Quote(dossier.Name)} status={LogValueFormatter.Quote(status)} canonicalName={LogValueFormatter.Quote(canonicalName)} reason={LogValueFormatter.Quote(reason)}");
    }
}

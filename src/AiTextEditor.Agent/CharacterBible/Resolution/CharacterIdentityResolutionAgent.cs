using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

public interface ICharacterIdentityResolutionModelClient
{
    Task<CharacterIdentityResolutionResponse> ResolveAsync(
        CharacterIdentityResolutionModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CharacterIdentityResolutionModelRequest(
    string SystemPrompt,
    string UserPrompt,
    ICharacterArchiveSearchTool SearchTool,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record CharacterIdentityResolutionPromptInput
{
    [JsonPropertyName("candidate")]
    public required CharacterCandidateIdentityInput Candidate { get; init; }
}

public sealed record CharacterCandidateIdentityInput
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("gender")]
    public required string Gender { get; init; }

    [JsonPropertyName("observedNameForms")]
    public required IReadOnlyList<string> ObservedNameForms { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<CharacterEvidenceText> Evidence { get; init; }
}

public sealed record CharacterEvidenceText
{
    [JsonPropertyName("pointer")]
    public required string Pointer { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record CharacterIdentityResolutionResponse(
    [property: JsonRequired]
    [property: JsonPropertyName("decision")] CharacterIdentityDecision Decision,
    [property: JsonPropertyName("characterId")] int? CharacterId = null,
    [property: JsonPropertyName("characterIds")] IReadOnlyList<int>? CharacterIds = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

[JsonConverter(typeof(JsonStringEnumConverter<CharacterIdentityDecision>))]
public enum CharacterIdentityDecision
{
    [JsonStringEnumMemberName("existing")]
    Existing,

    [JsonStringEnumMemberName("new")]
    New,

    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,

    [JsonStringEnumMemberName("identity_conflict")]
    IdentityConflict,

    [JsonStringEnumMemberName("defer")]
    Defer
}

public sealed class CharacterIdentityResolutionPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Resolution.Prompts.character-identity-resolution.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BuildSystemPrompt() => SystemPrompt.Value;

    internal string BuildUserPrompt(CharacterBibleCharacterCandidate candidate)
    {
        var payload = BuildPromptInput(candidate);

        return BuildUserPrompt(payload);
    }

    internal string BuildUserPrompt(CharacterIdentityResolutionPromptInput payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal CharacterIdentityResolutionPromptInput BuildPromptInput(CharacterBibleCharacterCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var evidence = BuildEvidence(candidate);
        return new CharacterIdentityResolutionPromptInput
        {
            Candidate = new CharacterCandidateIdentityInput
            {
                Name = candidate.CanonicalName,
                Gender = candidate.Gender,
                ObservedNameForms = candidate.ObservedNameFormExamples.Keys.ToArray(),
                Evidence = evidence
            }
        };
    }

    private static IReadOnlyList<CharacterEvidenceText> BuildEvidence(CharacterBibleCharacterCandidate candidate)
    {
        var evidenceByPointer = new Dictionary<string, CharacterEvidenceText>(StringComparer.Ordinal);
        foreach (var evidence in candidate.Evidence)
        {
            var pointer = evidence.Pointer.Trim();
            if (string.IsNullOrWhiteSpace(pointer))
            {
                throw new InvalidOperationException(
                    $"Character identity resolver evidence for candidate '{candidate.CanonicalName}' has an empty pointer.");
            }

            if (string.IsNullOrWhiteSpace(evidence.Excerpt))
            {
                throw new InvalidOperationException(
                    $"Character identity resolver evidence pointer '{pointer}' for candidate '{candidate.CanonicalName}' has no materialized text.");
            }

            evidenceByPointer.TryAdd(pointer, new CharacterEvidenceText
            {
                Pointer = pointer,
                Text = evidence.Excerpt.Trim()
            });
        }

        if (evidenceByPointer.Count == 0)
        {
            throw new InvalidOperationException(
                $"Character identity resolver candidate '{candidate.CanonicalName}' has no materialized evidence. Pointers: {string.Join(", ", candidate.Evidence.Select(evidence => evidence.Pointer).Where(pointer => !string.IsNullOrWhiteSpace(pointer)))}.");
        }

        return evidenceByPointer.Values
            .OrderBy(evidence => evidence.Pointer, CharacterBibleEvidencePointerComparer.Instance)
            .ToArray();
    }

    private sealed class CharacterBibleEvidencePointerComparer : IComparer<string>
    {
        public static CharacterBibleEvidencePointerComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            if (!SemanticPointer.TryParse(x, out var left) || left is null ||
                !SemanticPointer.TryParse(y, out var right) || right is null)
            {
                return string.Compare(x, y, StringComparison.Ordinal);
            }

            return CompareParsedPointers(left.Parsed, right.Parsed, x, y);
        }

        private static int CompareParsedPointers(
            SemanticPointer.Path left,
            SemanticPointer.Path right,
            string leftLabel,
            string rightLabel)
        {
            var leftNumbers = left.Numbers ?? [];
            var rightNumbers = right.Numbers ?? [];
            var sharedLength = Math.Min(leftNumbers.Length, rightNumbers.Length);
            for (var index = 0; index < sharedLength; index++)
            {
                var numberComparison = leftNumbers[index].CompareTo(rightNumbers[index]);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }
            }

            var lengthComparison = leftNumbers.Length.CompareTo(rightNumbers.Length);
            if (lengthComparison != 0)
            {
                return lengthComparison;
            }

            var paragraphComparison = Nullable.Compare(left.Paragraph, right.Paragraph);
            return paragraphComparison != 0
                ? paragraphComparison
                : string.Compare(leftLabel, rightLabel, StringComparison.Ordinal);
        }
    }

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(CharacterIdentityResolutionPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded character identity resolution prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded character identity resolution prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

public sealed class AgenticCharacterIdentityResolutionModelClient : ICharacterIdentityResolutionModelClient
{
    private static readonly MethodInfo SearchMethod = typeof(ICharacterArchiveSearchTool)
        .GetMethod(nameof(ICharacterArchiveSearchTool.SearchCharactersAsync))
        ?? throw new InvalidOperationException("Search tool method was not found.");

    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticCharacterIdentityResolutionModelClient> logger;

    public AgenticCharacterIdentityResolutionModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticCharacterIdentityResolutionModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterIdentityResolutionResponse> ResolveAsync(
        CharacterIdentityResolutionModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);
        ArgumentNullException.ThrowIfNull(request.SearchTool);

        if (request.SearchTool is CharacterArchiveSearchToolAdapter { ArchiveSize: 0 } searchAdapter)
        {
            CharacterBibleRunLogScope.Current?.Info(
                "resolve.fast_path.search_empty_archive",
                $"candidateIndex={searchAdapter.CandidateIndex} name={LogValueFormatter.Quote(searchAdapter.CandidateName)} decision=new returned=0 archiveSize=0");
            return new CharacterIdentityResolutionResponse(
                CharacterIdentityDecision.New,
                Reason: "Archive search returned empty archive; candidate cannot match an existing character.");
        }

        var searchFunction = AIFunctionFactory.Create(
            SearchMethod,
            request.SearchTool,
            "search_characters",
            CharacterArchiveSearchToolDescriptions.Tool,
            JsonSerializerOptions.Web);

        return await modelClient.RunAsync<CharacterIdentityResolutionResponse>(
            new AgenticModelRequest<CharacterIdentityResolutionResponse>(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "character_identity_resolution_response_contract_invalid",
                ValidateResponse: Validate,
                Tools: [searchFunction],
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);
    }

    private static AgenticModelValidationResult Validate(CharacterIdentityResolutionResponse response)
    {
        return IsValid(response, out var error)
            ? AgenticModelValidationResult.Valid
            : AgenticModelValidationResult.Invalid(error);
    }

    private static bool IsValid(CharacterIdentityResolutionResponse response, out string error)
    {
        switch (response.Decision)
        {
            case CharacterIdentityDecision.Existing:
                if (response.CharacterId is null)
                {
                    error = "characterId is required for existing decision.";
                    return false;
                }

                break;
            case CharacterIdentityDecision.Ambiguous:
            case CharacterIdentityDecision.IdentityConflict:
                if (response.CharacterIds is null || response.CharacterIds.Count == 0)
                {
                    error = "characterIds is required for ambiguous and identity_conflict decisions.";
                    return false;
                }

                break;
        }

        error = string.Empty;
        return true;
    }
}

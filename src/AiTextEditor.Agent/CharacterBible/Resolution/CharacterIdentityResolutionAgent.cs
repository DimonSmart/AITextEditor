using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
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

public sealed record CharacterIdentityResolutionResponse(
    [property: JsonRequired]
    [property: JsonPropertyName("decision")] CharacterIdentityDecision Decision,
    [property: JsonPropertyName("entryId")] string? EntryId = null,
    [property: JsonPropertyName("entryIds")] IReadOnlyList<string>? EntryIds = null,
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
        ArgumentNullException.ThrowIfNull(candidate);

        var payload = new
        {
            task = "resolve_character_identity",
            candidate = new
            {
                name = candidate.CanonicalName,
                gender = candidate.Gender,
                aliases = candidate.AliasExamples.Keys.ToArray(),
                pointers = candidate.Evidence.Select(evidence => evidence.Pointer).ToArray()
            },
            paragraphs = candidate.Evidence.Select(evidence => new
            {
                pointer = evidence.Pointer,
                text = evidence.Excerpt
            })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
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
                if (string.IsNullOrWhiteSpace(response.EntryId))
                {
                    error = "entryId is required for existing decision.";
                    return false;
                }

                break;
            case CharacterIdentityDecision.Ambiguous:
            case CharacterIdentityDecision.IdentityConflict:
                if (response.EntryIds is null || response.EntryIds.Count == 0)
                {
                    error = "entryIds is required for ambiguous and identity_conflict decisions.";
                    return false;
                }

                break;
        }

        error = string.Empty;
        return true;
    }
}

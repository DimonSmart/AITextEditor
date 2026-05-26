using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

public interface ISuspectArchiveResolverModelClient
{
    Task<SuspectArchiveResolverResponse> ResolveAsync(
        SuspectArchiveResolverModelRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SuspectArchiveResolverModelRequest(
    string SystemPrompt,
    string UserPrompt);

public sealed class SuspectArchiveResolverResponse
{
    [JsonRequired]
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("targetEntryId")]
    public string? TargetEntryId { get; init; }

    [JsonRequired]
    [JsonPropertyName("alternativeEntryIds")]
    public List<string>? AlternativeEntryIds { get; init; }

    [JsonRequired]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class SuspectArchiveResolverPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Resolution.Prompts.suspect-archive-resolver.system.md";

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
        IReadOnlyList<CharacterArchiveHit> archiveHits)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(archiveHits);

        var payload = new
        {
            task = "resolve_character_identity",
            candidate,
            archiveHits
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(SuspectArchiveResolverPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded suspect archive resolver prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded suspect archive resolver prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

public sealed class AgenticSuspectArchiveResolverModelClient : ISuspectArchiveResolverModelClient
{
    private readonly IAgenticModelClient modelClient;
    private readonly ILogger<AgenticSuspectArchiveResolverModelClient> logger;

    public AgenticSuspectArchiveResolverModelClient(
        IAgenticModelClient modelClient,
        ILogger<AgenticSuspectArchiveResolverModelClient> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SuspectArchiveResolverResponse> ResolveAsync(
        SuspectArchiveResolverModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await modelClient.RunAsync<SuspectArchiveResolverResponse>(
            new AgenticModelRequest(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                InvalidContractError: "suspect_archive_resolver_contract_invalid"),
            cancellationToken).ConfigureAwait(false);

        if (!IsValid(response, out var error))
        {
            logger.LogError("Suspect archive resolver contract validation failed: {ValidationError}", error);
            throw new InvalidOperationException("suspect_archive_resolver_contract_invalid");
        }

        return response;
    }

    private static bool IsValid(SuspectArchiveResolverResponse response, out string error)
    {
        if (string.IsNullOrWhiteSpace(response.Kind))
        {
            error = "kind is required.";
            return false;
        }

        if (!string.Equals(response.Kind, "existing", StringComparison.Ordinal)
            && !string.Equals(response.Kind, "new", StringComparison.Ordinal)
            && !string.Equals(response.Kind, "ambiguous", StringComparison.Ordinal)
            && !string.Equals(response.Kind, "defer", StringComparison.Ordinal)
            && !string.Equals(response.Kind, "identity_conflict", StringComparison.Ordinal))
        {
            error = "kind has unsupported value.";
            return false;
        }

        if (response.AlternativeEntryIds is null)
        {
            error = "alternativeEntryIds is required.";
            return false;
        }

        if (response.Reason is null)
        {
            error = "reason is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

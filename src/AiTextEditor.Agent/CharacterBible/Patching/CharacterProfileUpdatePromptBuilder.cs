using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public sealed class CharacterProfileUpdatePromptBuilder
{
    private const int MaxContextCharacters = 500;
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Patching.Prompts.profile-update.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);
    private readonly string outputLanguage;

    private static readonly JsonSerializerOptions UserPromptJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public CharacterProfileUpdatePromptBuilder(string? outputLanguage = null)
    {
        this.outputLanguage = string.IsNullOrWhiteSpace(outputLanguage)
            ? "Russian"
            : outputLanguage.Trim();
    }

    public string BuildSystemPrompt() => SystemPrompt.Value;

    internal string BuildUserPrompt(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(dossier);

        return BuildUserPrompt(BuildPromptInput(candidates, dossier));
    }

    internal string BuildUserPrompt(CharacterProfileUpdatePromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return JsonSerializer.Serialize(input, UserPromptJsonOptions);
    }

    internal CharacterProfileUpdatePromptInput BuildPromptInput(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        CharacterDossier dossier)
        => BuildPromptInput(candidates, dossier, outputLanguage);

    internal static CharacterProfileUpdatePromptInput BuildPromptInput(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        CharacterDossier dossier,
        string outputLanguage)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(dossier);

        var profile = CharacterProfile.Normalize(dossier.Profile);
        return new CharacterProfileUpdatePromptInput(
            new CharacterProfileUpdateTarget(dossier.Name),
            string.IsNullOrWhiteSpace(outputLanguage) ? "Russian" : outputLanguage.Trim(),
            new CharacterProfileUpdateCurrentProfile(
                NullIfWhiteSpace(profile.Appearance),
                NullIfWhiteSpace(profile.StatusAndCompetence),
                NullIfWhiteSpace(profile.PsychologicalProfile),
                NullIfWhiteSpace(profile.SpeechAndCommunication)),
            BuildEvidence(candidates));
    }

    internal static IReadOnlyList<CharacterProfileUpdateEvidence> BuildEvidence(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .SelectMany(candidate => candidate.EvidenceContexts)
            .GroupBy(context => context.Pointer, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(context => new CharacterProfileUpdateEvidence(
                context.Pointer,
                BuildFocusedText(context),
                BuildNearbyContext(context, "previous"),
                BuildNearbyContext(context, "next")))
            .ToArray();
    }

    private static string BuildFocusedText(CharacterBibleEvidenceContext context)
        => NullIfWhiteSpace(context.FocusedText)
           ?? NullIfWhiteSpace(context.AnchorExcerpt)
           ?? NullIfWhiteSpace(context.CurrentParagraph)
           ?? context.Pointer;

    private static string? BuildNearbyContext(CharacterBibleEvidenceContext context, string position)
        => context.NearbyParagraphs
            .Where(paragraph => string.Equals(paragraph.Position, position, StringComparison.OrdinalIgnoreCase))
            .Select(paragraph => Truncate(NullIfWhiteSpace(paragraph.Text), MaxContextCharacters))
            .FirstOrDefault(text => text is not null);

    private static string? Truncate(string? value, int maxCharacters)
        => value is null || value.Length <= maxCharacters
            ? value
            : value[..maxCharacters].TrimEnd();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(CharacterProfileUpdatePromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded profile update prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded profile update prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

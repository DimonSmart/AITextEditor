using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public sealed class CharacterProfileUpdatePromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Patching.Prompts.profile-update.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);

    private static readonly JsonSerializerOptions UserPromptJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

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

    internal static CharacterProfileUpdatePromptInput BuildPromptInput(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(dossier);

        var profile = CharacterProfile.Normalize(dossier.Profile);
        return new CharacterProfileUpdatePromptInput(
            new CharacterProfileUpdateTarget(dossier.Name),
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
            .Select(context => new CharacterProfileUpdateEvidence(context.Pointer, BuildEvidenceText(context)))
            .ToArray();
    }

    private static string BuildEvidenceText(CharacterBibleEvidenceContext context)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "Anchor excerpt", context.AnchorExcerpt);
        AppendSection(builder, "Current paragraph", context.CurrentParagraph);
        AppendSection(builder, "Focused text", context.FocusedText);

        foreach (var paragraph in context.NearbyParagraphs)
        {
            AppendSection(builder, $"Nearby paragraph ({paragraph.Position}, {paragraph.Pointer})", paragraph.Text);
        }

        return builder.ToString().Trim();
    }

    private static void AppendSection(StringBuilder builder, string label, string? text)
    {
        var normalizedText = NullIfWhiteSpace(text);
        if (normalizedText is null)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(label);
        builder.Append(": ");
        builder.Append(normalizedText);
    }

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

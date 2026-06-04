using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public sealed class CharacterProfileUpdatePromptBuilder
{
    private const int MaxContextCharacters = 500;
    private const int MaxFocusedTextCharacters = 500;
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
            BuildEvidence(candidates, dossier));
    }

    internal static IReadOnlyList<CharacterProfileUpdateEvidence> BuildEvidence(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(dossier);

        return candidates
            .SelectMany(candidate => candidate.EvidenceContexts.Select(context => new CandidateEvidenceContext(candidate.Candidate, context)))
            .GroupBy(item => item.Context.Pointer, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(item => BuildEvidence(item, dossier))
            .ToArray();
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
                BuildFallbackFocusedText(context),
                BuildNearbyContext(context, "previous"),
                BuildNearbyContext(context, "next")))
            .ToArray();
    }

    private static CharacterProfileUpdateEvidence BuildEvidence(
        CandidateEvidenceContext item,
        CharacterDossier dossier)
    {
        var focus = BuildFocusedText(item.Candidate, item.Context, dossier);
        return new CharacterProfileUpdateEvidence(
            item.Context.Pointer,
            focus.Text,
            BuildNearbyContext(item.Context, "previous"),
            BuildNearbyContext(item.Context, "next"));
    }

    private static CharacterEvidenceFocusResult BuildFocusedText(
        CharacterBibleCharacterCandidate candidate,
        CharacterBibleEvidenceContext context,
        CharacterDossier dossier)
    {
        var observedForms = BuildObservedFormsForPointer(candidate, context.Pointer);
        var paragraph = NullIfWhiteSpace(context.CurrentParagraph);
        if (paragraph is not null)
        {
            foreach (var observedForm in observedForms)
            {
                var match = FindObservedForm(paragraph, observedForm);
                if (match is null)
                {
                    continue;
                }

                var focusedText = BuildMentionWindow(paragraph, match.Value.Start, observedForm.Length);
                var containsObservedForm = ContainsObservedForm(focusedText, observedForm);
                LogFocus(dossier, context.Pointer, observedForm, true, match.Value.Start, focusedText.Length, containsObservedForm);
                return new CharacterEvidenceFocusResult(focusedText);
            }
        }

        var fallbackText = BuildFallbackFocusedText(context);
        CharacterBibleRunLogScope.Current?.Warning(
            "profile.evidence.focus.fallback",
            $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} pointer={LogValueFormatter.Quote(context.Pointer)} reason={LogValueFormatter.Quote("observed form not found")} observedForms={LogValueFormatter.List(observedForms)}");
        LogFocus(dossier, context.Pointer, observedForms.FirstOrDefault() ?? string.Empty, false, -1, fallbackText.Length, false);
        return new CharacterEvidenceFocusResult(fallbackText);
    }

    private static string BuildFallbackFocusedText(CharacterBibleEvidenceContext context)
        => NullIfWhiteSpace(context.FocusedText)
           ?? NullIfWhiteSpace(context.AnchorExcerpt)
           ?? NullIfWhiteSpace(context.CurrentParagraph)
           ?? context.Pointer;

    private static IReadOnlyList<string> BuildObservedFormsForPointer(
        CharacterBibleCharacterCandidate candidate,
        string pointer)
    {
        var formsForPointer = candidate.ObservedNameFormEvidence
            .Where(item => string.Equals(item.Value.Pointer, pointer, StringComparison.Ordinal))
            .Select(item => item.Key);
        var remainingForms = candidate.ObservedNameFormExamples.Keys
            .Except(formsForPointer, CharacterNameFormComparer.Instance);

        return formsForPointer
            .Concat(remainingForms)
            .Where(form => !string.IsNullOrWhiteSpace(form))
            .Distinct(CharacterNameFormComparer.Instance)
            .ToArray();
    }

    private static ObservedFormMatch? FindObservedForm(string paragraph, string observedForm)
    {
        var exact = paragraph.IndexOf(observedForm, StringComparison.Ordinal);
        if (exact >= 0)
        {
            return new ObservedFormMatch(exact);
        }

        var caseInsensitive = paragraph.IndexOf(observedForm, StringComparison.OrdinalIgnoreCase);
        if (caseInsensitive >= 0)
        {
            return new ObservedFormMatch(caseInsensitive);
        }

        var normalizedParagraph = NormalizeYo(paragraph);
        var normalizedObservedForm = NormalizeYo(observedForm);
        var normalized = normalizedParagraph.IndexOf(normalizedObservedForm, StringComparison.OrdinalIgnoreCase);
        return normalized >= 0 ? new ObservedFormMatch(normalized) : null;
    }

    private static bool ContainsObservedForm(string text, string observedForm)
        => FindObservedForm(text, observedForm) is not null;

    private static string BuildMentionWindow(string paragraph, int mentionStart, int mentionLength)
    {
        var sentenceWindow = BuildSentenceWindow(paragraph, mentionStart);
        if (sentenceWindow.Length <= MaxFocusedTextCharacters)
        {
            return sentenceWindow;
        }

        var desiredStart = Math.Max(0, mentionStart - ((MaxFocusedTextCharacters - mentionLength) / 2));
        var desiredEnd = Math.Min(paragraph.Length, desiredStart + MaxFocusedTextCharacters);
        desiredStart = Math.Max(0, desiredEnd - MaxFocusedTextCharacters);
        var start = MoveToWordBoundary(paragraph, desiredStart, 1);
        var end = MoveToWordBoundary(paragraph, desiredEnd, -1);
        if (start >= mentionStart || end <= mentionStart + mentionLength)
        {
            start = desiredStart;
            end = desiredEnd;
        }

        return paragraph[start..end].Trim();
    }

    private static string BuildSentenceWindow(string paragraph, int mentionStart)
    {
        var spans = SplitSentenceSpans(paragraph);
        var sentenceIndex = spans.FindIndex(span => mentionStart >= span.Start && mentionStart < span.End);
        if (sentenceIndex < 0)
        {
            return Truncate(paragraph.Trim(), MaxFocusedTextCharacters) ?? paragraph.Trim();
        }

        var start = sentenceIndex;
        var end = Math.Min(spans.Count - 1, sentenceIndex + 1);
        while (end > sentenceIndex && paragraph[spans[start].Start..spans[end].End].Trim().Length > MaxFocusedTextCharacters)
        {
            end--;
        }

        return paragraph[spans[start].Start..spans[end].End].Trim();
    }

    private static int MoveToWordBoundary(string text, int index, int direction)
    {
        if (index <= 0 || index >= text.Length)
        {
            return Math.Clamp(index, 0, text.Length);
        }

        while (index > 0 && index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index += direction;
        }

        return Math.Clamp(index, 0, text.Length);
    }

    private static List<TextSpan> SplitSentenceSpans(string text)
    {
        var spans = new List<TextSpan>();
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (!IsSentenceBoundary(text[index]))
            {
                continue;
            }

            var end = index + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            AddSpan(start, end);
            start = end;
        }

        AddSpan(start, text.Length);
        return spans;

        void AddSpan(int spanStart, int spanEnd)
        {
            while (spanStart < spanEnd && char.IsWhiteSpace(text[spanStart]))
            {
                spanStart++;
            }

            while (spanEnd > spanStart && char.IsWhiteSpace(text[spanEnd - 1]))
            {
                spanEnd--;
            }

            if (spanEnd > spanStart)
            {
                spans.Add(new TextSpan(spanStart, spanEnd));
            }
        }
    }

    private static bool IsSentenceBoundary(char value)
        => value is '.' or '!' or '?' or '…' or ':' or ';' or '—';

    private static string NormalizeYo(string value)
        => value.Replace('ё', 'е').Replace('Ё', 'Е');

    private static void LogFocus(
        CharacterDossier dossier,
        string pointer,
        string observedForm,
        bool found,
        int start,
        int length,
        bool containsObservedForm)
    {
        CharacterBibleRunLogScope.Current?.Debug(
            "profile.evidence.focus",
            $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} pointer={LogValueFormatter.Quote(pointer)} observedForm={LogValueFormatter.Quote(observedForm)} found={found.ToString().ToLowerInvariant()} start={start} length={length} containsObservedForm={containsObservedForm.ToString().ToLowerInvariant()}");
    }

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

    private sealed record CandidateEvidenceContext(
        CharacterBibleCharacterCandidate Candidate,
        CharacterBibleEvidenceContext Context);

    private readonly record struct CharacterEvidenceFocusResult(string Text);

    private readonly record struct ObservedFormMatch(int Start);

    private readonly record struct TextSpan(int Start, int End);

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

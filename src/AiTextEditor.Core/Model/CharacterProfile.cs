using System.Collections.Generic;

namespace AiTextEditor.Core.Model;

public sealed record CharacterProfile(
    string Appearance = "",
    string StatusAndCompetence = "",
    string PsychologicalProfile = "",
    string SpeechAndCommunication = "")
{
    public static CharacterProfile Empty { get; } = new();

    public static CharacterProfile Normalize(CharacterProfile? profile)
    {
        if (profile is null)
        {
            return Empty;
        }

        return new CharacterProfile(
            Trim(profile.Appearance),
            Trim(profile.StatusAndCompetence),
            Trim(profile.PsychologicalProfile),
            Trim(profile.SpeechAndCommunication));
    }

    public static CharacterProfile MergeMissing(CharacterProfile? existing, CharacterProfile? candidate)
    {
        var normalizedExisting = Normalize(existing);
        var normalizedCandidate = Normalize(candidate);

        return new CharacterProfile(
            ChooseExisting(normalizedExisting.Appearance, normalizedCandidate.Appearance),
            ChooseExisting(normalizedExisting.StatusAndCompetence, normalizedCandidate.StatusAndCompetence),
            ChooseExisting(normalizedExisting.PsychologicalProfile, normalizedCandidate.PsychologicalProfile),
            ChooseExisting(normalizedExisting.SpeechAndCommunication, normalizedCandidate.SpeechAndCommunication));
    }

    public static int CountCompletedSections(CharacterProfile? profile)
    {
        var normalized = Normalize(profile);
        var count = 0;

        if (!string.IsNullOrWhiteSpace(normalized.Appearance))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(normalized.StatusAndCompetence))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(normalized.PsychologicalProfile))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(normalized.SpeechAndCommunication))
        {
            count++;
        }

        return count;
    }

    public static bool HasSameContent(CharacterProfile? left, CharacterProfile? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);

        return string.Equals(normalizedLeft.Appearance, normalizedRight.Appearance, StringComparison.Ordinal)
            && string.Equals(normalizedLeft.StatusAndCompetence, normalizedRight.StatusAndCompetence, StringComparison.Ordinal)
            && string.Equals(normalizedLeft.PsychologicalProfile, normalizedRight.PsychologicalProfile, StringComparison.Ordinal)
            && string.Equals(normalizedLeft.SpeechAndCommunication, normalizedRight.SpeechAndCommunication, StringComparison.Ordinal);
    }

    private static string ChooseExisting(string existing, string candidate)
        => string.IsNullOrWhiteSpace(existing) ? candidate : existing;

    private static string Trim(string? value)
        => value?.Trim() ?? string.Empty;
}

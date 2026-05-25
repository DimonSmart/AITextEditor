using System.Collections.Generic;

namespace AiTextEditor.Core.Model;

public sealed record CharacterProfile(
    string Appearance = "",
    string BackgroundStatusEducation = "",
    string PsychologicalProfile = "",
    string SpeechAndCommunication = "",
    IReadOnlyList<CharacterRoleBond>? KeyRoleBonds = null)
{
    public static CharacterProfile Empty { get; } = new(KeyRoleBonds: []);

    public static CharacterProfile Normalize(CharacterProfile? profile)
    {
        if (profile is null)
        {
            return Empty;
        }

        return new CharacterProfile(
            Trim(profile.Appearance),
            Trim(profile.BackgroundStatusEducation),
            Trim(profile.PsychologicalProfile),
            Trim(profile.SpeechAndCommunication),
            NormalizeRoleBonds(profile.KeyRoleBonds));
    }

    public static CharacterProfile MergeMissing(CharacterProfile? existing, CharacterProfile? candidate)
    {
        var normalizedExisting = Normalize(existing);
        var normalizedCandidate = Normalize(candidate);

        return new CharacterProfile(
            ChooseExisting(normalizedExisting.Appearance, normalizedCandidate.Appearance),
            ChooseExisting(normalizedExisting.BackgroundStatusEducation, normalizedCandidate.BackgroundStatusEducation),
            ChooseExisting(normalizedExisting.PsychologicalProfile, normalizedCandidate.PsychologicalProfile),
            ChooseExisting(normalizedExisting.SpeechAndCommunication, normalizedCandidate.SpeechAndCommunication),
            MergeRoleBonds(normalizedExisting.KeyRoleBonds, normalizedCandidate.KeyRoleBonds));
    }

    public static int CountCompletedSections(CharacterProfile? profile)
    {
        var normalized = Normalize(profile);
        var count = 0;

        if (!string.IsNullOrWhiteSpace(normalized.Appearance))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(normalized.BackgroundStatusEducation))
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

        if (normalized.KeyRoleBonds is { Count: > 0 })
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
            && string.Equals(normalizedLeft.BackgroundStatusEducation, normalizedRight.BackgroundStatusEducation, StringComparison.Ordinal)
            && string.Equals(normalizedLeft.PsychologicalProfile, normalizedRight.PsychologicalProfile, StringComparison.Ordinal)
            && string.Equals(normalizedLeft.SpeechAndCommunication, normalizedRight.SpeechAndCommunication, StringComparison.Ordinal)
            && RoleBondsEqual(normalizedLeft.KeyRoleBonds, normalizedRight.KeyRoleBonds);
    }

    private static string ChooseExisting(string existing, string candidate)
        => string.IsNullOrWhiteSpace(existing) ? candidate : existing;

    private static IReadOnlyList<CharacterRoleBond> MergeRoleBonds(
        IReadOnlyList<CharacterRoleBond>? existing,
        IReadOnlyList<CharacterRoleBond>? candidate)
    {
        var merged = new List<CharacterRoleBond>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bond in existing ?? [])
        {
            if (seen.Add(BondKey(bond.CharacterName, bond.Role)))
            {
                merged.Add(bond);
            }
        }

        foreach (var bond in candidate ?? [])
        {
            if (seen.Add(BondKey(bond.CharacterName, bond.Role)))
            {
                merged.Add(bond);
            }
        }

        return merged;
    }

    private static List<CharacterRoleBond> NormalizeRoleBonds(IReadOnlyList<CharacterRoleBond>? roleBonds)
    {
        if (roleBonds is null)
        {
            return [];
        }

        var normalized = new List<CharacterRoleBond>(roleBonds.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bond in roleBonds)
        {
            if (bond is null)
            {
                continue;
            }

            var characterName = Trim(bond.CharacterName);
            var role = Trim(bond.Role);
            var description = Trim(bond.Description);
            if (characterName.Length == 0 || role.Length == 0 || description.Length == 0)
            {
                continue;
            }

            if (!seen.Add(BondKey(characterName, role)))
            {
                continue;
            }

            normalized.Add(new CharacterRoleBond(characterName, role, description));
        }

        return normalized
            .OrderBy(bond => bond.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(bond => bond.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BondKey(string characterName, string role)
        => $"{characterName}\u001F{role}";

    private static bool RoleBondsEqual(
        IReadOnlyList<CharacterRoleBond>? left,
        IReadOnlyList<CharacterRoleBond>? right)
    {
        var normalizedLeft = NormalizeRoleBonds(left);
        var normalizedRight = NormalizeRoleBonds(right);
        if (normalizedLeft.Count != normalizedRight.Count)
        {
            return false;
        }

        for (var i = 0; i < normalizedLeft.Count; i++)
        {
            var leftBond = normalizedLeft[i];
            var rightBond = normalizedRight[i];
            if (!string.Equals(leftBond.CharacterName, rightBond.CharacterName, StringComparison.Ordinal)
                || !string.Equals(leftBond.Role, rightBond.Role, StringComparison.Ordinal)
                || !string.Equals(leftBond.Description, rightBond.Description, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Trim(string? value)
        => value?.Trim() ?? string.Empty;
}

public sealed record CharacterRoleBond(
    string CharacterName,
    string Role,
    string Description);

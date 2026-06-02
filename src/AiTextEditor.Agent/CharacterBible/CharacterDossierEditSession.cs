using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible;

internal sealed class CharacterDossierEditSession
{
    private readonly List<CharacterBibleResolverDecision> decisions = [];

    private CharacterDossierEditSession(CharacterDossiers current)
    {
        Current = current;
    }

    public CharacterDossiers Current { get; private set; }

    public bool Changed { get; private set; }

    public IReadOnlyList<CharacterBibleResolverDecision> Decisions => decisions;

    public static CharacterDossierEditSession CreateFrom(CharacterDossiers dossiers)
    {
        ArgumentNullException.ThrowIfNull(dossiers);
        return new CharacterDossierEditSession(CloneDossiers(dossiers));
    }

    public void AddDecision(CharacterBibleResolverDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decisions.Add(decision);
    }

    public CharacterDossier CreateCharacter(CharacterBibleCharacterCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var dossier = CreateDossierFromCandidate(Current.NextCharacterId, candidate);
        var characters = Current.Characters
            .Append(dossier)
            .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Current = Current with
        {
            NextCharacterId = dossier.CharacterId + 1,
            Characters = characters
        };
        Changed = true;
        return dossier;
    }

    public CharacterDossier GetRequired(int characterId)
    {
        return Current.Characters.FirstOrDefault(character =>
                   character.CharacterId == characterId)
               ?? throw new InvalidOperationException($"character_dossier_not_found: {characterId}");
    }

    public bool RefineCanonicalName(
        int characterId,
        CharacterBibleCharacterCandidate candidate,
        bool exactNameMatch)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (exactNameMatch)
        {
            return false;
        }

        var dossier = GetRequired(characterId);
        var canonicalName = candidate.CanonicalName.Trim();
        if (string.IsNullOrWhiteSpace(canonicalName) ||
            string.Equals(dossier.Name, canonicalName, StringComparison.OrdinalIgnoreCase) ||
            !IsGenderCompatible(dossier.Gender, candidate.Gender) ||
            !TryFindExampleFor(dossier.Name, candidate.AliasExamples, out var currentNameExample))
        {
            return false;
        }

        var aliasExamples = NormalizeAliasExamples(dossier.AliasExamples);
        if (!aliasExamples.ContainsKey(dossier.Name))
        {
            aliasExamples[dossier.Name] = currentNameExample;
        }

        return ReplaceDossier(dossier with
        {
            Name = canonicalName,
            AliasExamples = aliasExamples,
            Aliases = BuildAliases(aliasExamples)
        });
    }

    public bool MergeAliases(
        int characterId,
        CharacterBibleCharacterCandidate candidate,
        bool exactNameMatch)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var aliasExamples = NormalizeAliasExamples(candidate.AliasExamples);
        if (exactNameMatch &&
            !string.IsNullOrWhiteSpace(candidate.CanonicalName) &&
            TryFindExampleFor(candidate.CanonicalName, candidate.AliasExamples, out var canonicalExample))
        {
            aliasExamples[candidate.CanonicalName.Trim()] = canonicalExample;
        }

        return MergeAliasExamples(characterId, aliasExamples);
    }

    public bool MergeAliasExamples(
        int characterId,
        IReadOnlyDictionary<string, string> aliasExamples)
    {
        ArgumentNullException.ThrowIfNull(aliasExamples);

        var dossier = GetRequired(characterId);
        var merged = NormalizeAliasExamples(dossier.AliasExamples);
        var changed = false;
        foreach (var (alias, example) in NormalizeAliasExamples(aliasExamples))
        {
            if (merged.ContainsKey(alias))
            {
                continue;
            }

            merged[alias] = example;
            changed = true;
        }

        return changed && ReplaceDossier(dossier with
        {
            AliasExamples = merged,
            Aliases = BuildAliases(merged)
        });
    }

    public bool SetGenderIfUnknown(int characterId, string? gender)
    {
        var normalizedGender = NormalizeGender(gender);
        if (string.Equals(normalizedGender, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dossier = GetRequired(characterId);
        if (!string.Equals(NormalizeGender(dossier.Gender), "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ReplaceDossier(dossier with { Gender = normalizedGender });
    }

    public bool SetImportanceLevelIfMissing(int characterId, int level)
    {
        var normalizedLevel = CharacterImportance.NormalizeLevel(level);
        if (normalizedLevel is null)
        {
            return false;
        }

        var dossier = GetRequired(characterId);
        if (dossier.ImportanceLevel is not null)
        {
            return false;
        }

        return ReplaceDossier(dossier with { ImportanceLevel = normalizedLevel });
    }

    public bool UpdateProfile(int characterId, CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var dossier = GetRequired(characterId);
        var normalizedProfile = CharacterProfile.Normalize(profile);
        if (CharacterProfile.HasSameContent(dossier.Profile, normalizedProfile))
        {
            return false;
        }

        return ReplaceDossier(dossier with { Profile = normalizedProfile });
    }

    public void AddEvidenceIndexEntries(IReadOnlyList<CharacterEvidenceIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var normalized = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Pointer) && !string.IsNullOrWhiteSpace(entry.Excerpt))
            .Select(entry => entry with
            {
                Pointer = entry.Pointer.Trim(),
                Excerpt = entry.Excerpt.Trim()
            })
            .ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        Current = Current with { EvidenceIndex = (Current.EvidenceIndex ?? []).Concat(normalized).ToArray() };
        Changed = true;
    }

    public void AddSuspectArchiveEntry(SuspectArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Current = Current with { SuspectArchive = (Current.SuspectArchive ?? []).Append(entry).ToArray() };
        Changed = true;
    }

    public void AddIdentityConflict(IdentityConflictRecord conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        Current = Current with { IdentityConflicts = (Current.IdentityConflicts ?? []).Append(conflict).ToArray() };
        Changed = true;
    }

    public void AddAuditTrailEntry(CharacterBibleAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Current = Current with { AuditTrail = (Current.AuditTrail ?? []).Append(entry).ToArray() };
        Changed = true;
    }

    public void LimitCharacters(int? maxCharacters)
    {
        if (maxCharacters is null || maxCharacters <= 0 || Current.Characters.Count <= maxCharacters.Value)
        {
            return;
        }

        Current = Current with
        {
            Characters = Current.Characters
                .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxCharacters.Value)
                .ToArray()
        };
        Changed = true;
    }

    private bool ReplaceDossier(CharacterDossier updated)
    {
        var found = false;
        var characters = Current.Characters
            .Select(character =>
            {
                if (character.CharacterId != updated.CharacterId)
                {
                    return character;
                }

                found = true;
                return updated;
            })
            .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!found)
        {
            return false;
        }

        Current = Current with { Characters = characters };
        Changed = true;
        return true;
    }

    private static CharacterDossier CreateDossierFromCandidate(
        int characterId,
        CharacterBibleCharacterCandidate candidate)
    {
        var name = candidate.CanonicalName.Trim();
        var aliasExamples = NormalizeAliasExamples(candidate.AliasExamples);

        return new CharacterDossier(
            characterId,
            name,
            BuildAliases(aliasExamples),
            aliasExamples,
            NormalizeGender(candidate.Gender),
            ImportanceLevel: null,
            CharacterProfile.Empty);
    }

    private static CharacterDossiers CloneDossiers(CharacterDossiers dossiers)
    {
        return dossiers with
        {
            Characters = dossiers.Characters.Select(CloneDossier).ToArray(),
            SuspectArchive = dossiers.SuspectArchive?.Select(CloneSuspect).ToArray() ?? [],
            EvidenceIndex = dossiers.EvidenceIndex?.Select(CloneEvidence).ToArray() ?? [],
            IdentityConflicts = dossiers.IdentityConflicts?.Select(CloneConflict).ToArray() ?? [],
            AuditTrail = dossiers.AuditTrail?.ToArray() ?? []
        };
    }

    private static CharacterDossier CloneDossier(CharacterDossier dossier)
    {
        var aliasExamples = NormalizeAliasExamples(dossier.AliasExamples);
        return dossier with
        {
            Aliases = BuildAliases(aliasExamples),
            AliasExamples = aliasExamples,
            Gender = NormalizeGender(dossier.Gender),
            Profile = CharacterProfile.Normalize(dossier.Profile)
        };
    }

    private static SuspectArchiveEntry CloneSuspect(SuspectArchiveEntry entry)
    {
        return entry with
        {
            Aliases = entry.Aliases.ToArray(),
            Evidence = entry.Evidence.Select(CloneEvidence).ToArray()
        };
    }

    private static CharacterEvidenceIndexEntry CloneEvidence(CharacterEvidenceIndexEntry entry)
    {
        return entry with { };
    }

    private static IdentityConflictRecord CloneConflict(IdentityConflictRecord conflict)
    {
        return conflict with
        {
            AlternativeCharacterIds = conflict.AlternativeCharacterIds.ToArray(),
            SplitShardNames = conflict.SplitShardNames?.ToArray()
        };
    }

    private static Dictionary<string, string> NormalizeAliasExamples(
        IReadOnlyDictionary<string, string>? aliasExamples)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (aliasExamples is null)
        {
            return normalized;
        }

        foreach (var (alias, example) in aliasExamples)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(example))
            {
                continue;
            }

            var trimmedAlias = alias.Trim();
            if (!normalized.ContainsKey(trimmedAlias))
            {
                normalized[trimmedAlias] = example.Trim();
            }
        }

        return normalized;
    }

    private static IReadOnlyList<string> BuildAliases(IReadOnlyDictionary<string, string> aliasExamples)
    {
        return aliasExamples.Keys
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryFindExampleFor(
        string name,
        IReadOnlyDictionary<string, string> aliases,
        out string example)
    {
        example = string.Empty;
        if (aliases.Count == 0)
        {
            return false;
        }

        var match = aliases.FirstOrDefault(alias => string.Equals(alias.Key, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match.Value))
        {
            return false;
        }

        example = match.Value.Trim();
        return true;
    }

    private static bool IsGenderCompatible(string? dossierGender, string? candidateGender)
    {
        var normalizedCandidateGender = CharacterNameNormalizer.NormalizeGender(candidateGender);
        if (string.Equals(normalizedCandidateGender, "unknown", StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedDossierGender = NormalizeGender(dossierGender);
        if (string.Equals(normalizedDossierGender, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(normalizedDossierGender, normalizedCandidateGender, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeGender(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "unknown";
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "male" or "m" or "man" => "male",
            "female" or "f" or "woman" => "female",
            "unknown" => "unknown",
            _ => "unknown"
        };
    }
}

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

    public bool RenameCharacter(int characterId, string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);

        var dossier = GetRequired(characterId);
        var normalizedName = canonicalName.Trim();
        if (string.Equals(dossier.Name, normalizedName, StringComparison.Ordinal))
        {
            return false;
        }

        return ReplaceDossier(dossier with { Name = normalizedName });
    }

    public bool MergeObservedNameForms(
        int characterId,
        CharacterBibleCharacterCandidate candidate,
        bool exactNameMatch)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var observedNameFormExamples = NormalizeObservedNameFormExamples(candidate.ObservedNameFormExamples);
        if (exactNameMatch &&
            !string.IsNullOrWhiteSpace(candidate.CanonicalName) &&
            TryFindExampleFor(candidate.CanonicalName, candidate.ObservedNameFormExamples, out var canonicalExample))
        {
            observedNameFormExamples[candidate.CanonicalName.Trim()] = canonicalExample;
        }

        return MergeObservedNameFormExamples(characterId, observedNameFormExamples);
    }

    public bool MergeObservedNameFormExamples(
        int characterId,
        IReadOnlyDictionary<string, string> observedNameFormExamples)
    {
        ArgumentNullException.ThrowIfNull(observedNameFormExamples);

        var dossier = GetRequired(characterId);
        var merged = NormalizeObservedNameFormExamples(dossier.ObservedNameFormExamples);
        var changed = false;
        foreach (var (observedNameForm, example) in NormalizeObservedNameFormExamples(observedNameFormExamples))
        {
            if (merged.ContainsKey(observedNameForm))
            {
                continue;
            }

            merged[observedNameForm] = example;
            changed = true;
        }

        return changed && ReplaceDossier(dossier with
        {
            ObservedNameFormExamples = merged,
            ObservedNameForms = BuildObservedNameForms(merged)
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
        var observedNameFormExamples = NormalizeObservedNameFormExamples(candidate.ObservedNameFormExamples);

        return new CharacterDossier(
            characterId,
            name,
            BuildObservedNameForms(observedNameFormExamples),
            observedNameFormExamples,
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
        var observedNameFormExamples = NormalizeObservedNameFormExamples(dossier.ObservedNameFormExamples);
        return dossier with
        {
            ObservedNameForms = BuildObservedNameForms(observedNameFormExamples),
            ObservedNameFormExamples = observedNameFormExamples,
            Gender = NormalizeGender(dossier.Gender),
            Profile = CharacterProfile.Normalize(dossier.Profile)
        };
    }

    private static SuspectArchiveEntry CloneSuspect(SuspectArchiveEntry entry)
    {
        return entry with
        {
            ObservedNameForms = entry.ObservedNameForms.ToArray(),
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

    private static Dictionary<string, string> NormalizeObservedNameFormExamples(
        IReadOnlyDictionary<string, string>? observedNameFormExamples)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (observedNameFormExamples is null)
        {
            return normalized;
        }

        foreach (var (observedNameForm, example) in observedNameFormExamples)
        {
            if (string.IsNullOrWhiteSpace(observedNameForm) || string.IsNullOrWhiteSpace(example))
            {
                continue;
            }

            var trimmedObservedNameForm = observedNameForm.Trim();
            if (!normalized.ContainsKey(trimmedObservedNameForm))
            {
                normalized[trimmedObservedNameForm] = example.Trim();
            }
        }

        return normalized;
    }

    private static IReadOnlyList<string> BuildObservedNameForms(IReadOnlyDictionary<string, string> observedNameFormExamples)
    {
        return observedNameFormExamples.Keys
            .Where(observedNameForm => !string.IsNullOrWhiteSpace(observedNameForm))
            .Select(observedNameForm => observedNameForm.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(observedNameForm => observedNameForm, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryFindExampleFor(
        string name,
        IReadOnlyDictionary<string, string> observedNameFormExamples,
        out string example)
    {
        example = string.Empty;
        if (observedNameFormExamples.Count == 0)
        {
            return false;
        }

        var match = observedNameFormExamples.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
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

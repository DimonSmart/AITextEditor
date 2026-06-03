using System.Globalization;
using System.Text.Json;
using AiTextEditor.Core.Common;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Core.Services;

public sealed class CharacterDossierService
{
    public const int CurrentVersion = 4;

    private static readonly JsonSerializerOptions DossierJsonOptions = new(SerializationOptions.RelaxedCompact)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private CharacterDossiers dossiers;

    public CharacterDossierService(string? initialDossiersId = null)
    {
        dossiers = CreateEmpty(initialDossiersId);
    }

    public CharacterDossiers GetDossiers()
    {
        lock (syncRoot)
        {
            return dossiers;
        }
    }

    public CharacterDossier CreateCharacter(NewCharacterDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Name);

        lock (syncRoot)
        {
            var characterId = dossiers.NextCharacterId;
            var observedNameFormExamples = NormalizeObservedNameFormExamples(draft.ObservedNameFormExamples);
            var character = NormalizeDossier(new CharacterDossier(
                characterId,
                draft.Name,
                BuildObservedNameForms(observedNameFormExamples),
                observedNameFormExamples,
                NormalizeGender(draft.Gender),
                CharacterImportance.NormalizeLevel(draft.ImportanceLevel),
                CharacterProfile.Normalize(draft.Profile)));
            dossiers = dossiers with
            {
                NextCharacterId = characterId + 1,
                Characters = dossiers.Characters.Append(character).ToList()
            };
            return character;
        }
    }

    public CharacterDossier UpsertDossier(CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ValidateCharacterId(dossier.CharacterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dossier.Name);

        lock (syncRoot)
        {
            var characters = dossiers.Characters.ToDictionary(character => character.CharacterId);
            characters[dossier.CharacterId] = NormalizeDossier(dossier);
            dossiers = dossiers with
            {
                NextCharacterId = Math.Max(dossiers.NextCharacterId, dossier.CharacterId + 1),
                Characters = characters.Values.ToList()
            };
            return characters[dossier.CharacterId];
        }
    }

    public CharacterDossier? TryGetDossier(int characterId)
    {
        ValidateCharacterId(characterId);
        lock (syncRoot)
        {
            return dossiers.Characters.FirstOrDefault(character => character.CharacterId == characterId);
        }
    }

    public IReadOnlyCollection<CharacterDossier> FindByNameOrObservedNameForm(string nameOrObservedNameForm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrObservedNameForm);
        lock (syncRoot)
        {
            var matches = new CharacterNameIndex(dossiers.Characters).FindByName(nameOrObservedNameForm).ToHashSet();
            return dossiers.Characters.Where(character => matches.Contains(character.CharacterId)).ToList();
        }
    }

    public CharacterDossier UpdateDossierById(
        int characterId,
        string? name,
        string? gender,
        IReadOnlyDictionary<string, string>? observedNameFormExamples,
        CharacterProfile? profile = null)
    {
        ValidateCharacterId(characterId);
        lock (syncRoot)
        {
            var existing = TryGetDossier(characterId)
                ?? throw new InvalidOperationException($"character_not_found: {characterId}");
            return UpsertDossier(Merge(existing, name, gender, observedNameFormExamples, profile));
        }
    }

    public ResolveAndUpsertResult ResolveAndUpsertDossier(
        string name,
        string? gender,
        IReadOnlyDictionary<string, string>? observedNameFormExamples,
        CharacterProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var canonicalName = name.Trim();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonicalName };
        if (observedNameFormExamples is not null)
        {
            foreach (var observedNameForm in observedNameFormExamples.Keys.Where(observedNameForm => !string.IsNullOrWhiteSpace(observedNameForm)))
            {
                keys.Add(observedNameForm.Trim());
            }
        }

        lock (syncRoot)
        {
            var index = new CharacterNameIndex(dossiers.Characters);
            var candidateIds = keys.SelectMany(index.FindByName).Distinct().ToArray();
            var candidates = dossiers.Characters.Where(character => candidateIds.Contains(character.CharacterId)).ToList();
            if (candidates.Count == 0)
            {
                var created = CreateCharacter(new NewCharacterDraft(canonicalName, observedNameFormExamples ?? new Dictionary<string, string>(), gender ?? "unknown", Profile: profile));
                return ResolveAndUpsertResult.Created(created.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            if (candidates.Count == 1)
            {
                var saved = UpsertDossier(Merge(candidates[0], null, gender, observedNameFormExamples, profile));
                return ResolveAndUpsertResult.Updated(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            var exactByName = candidates.Where(character => string.Equals(character.Name, canonicalName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactByName.Count == 1)
            {
                var saved = UpsertDossier(Merge(exactByName[0], null, gender, observedNameFormExamples, profile));
                return ResolveAndUpsertResult.Updated(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            return ResolveAndUpsertResult.Ambiguous(candidates.Select(character => character.CharacterId).ToArray(), dossiers.DossiersId, dossiers.Version);
        }
    }

    public bool RemoveDossier(int characterId)
    {
        ValidateCharacterId(characterId);
        lock (syncRoot)
        {
            if (!dossiers.Characters.Any(character => character.CharacterId == characterId))
            {
                return false;
            }

            dossiers = dossiers with { Characters = dossiers.Characters.Where(character => character.CharacterId != characterId).ToList() };
            return true;
        }
    }

    public void ReplaceDossiers(IReadOnlyCollection<CharacterDossier> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        lock (syncRoot)
        {
            dossiers = NormalizeDossiers(dossiers with { Characters = characters.Select(NormalizeDossier).ToList() });
        }
    }

    public void ReplaceDossiers(CharacterDossiers replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (syncRoot)
        {
            dossiers = NormalizeDossiers(replacement with
            {
                DossiersId = string.IsNullOrWhiteSpace(replacement.DossiersId) ? dossiers.DossiersId : replacement.DossiersId
            });
        }
    }

    public string SaveToJson()
    {
        lock (syncRoot)
        {
            return JsonSerializer.Serialize(dossiers, DossierJsonOptions);
        }
    }

    public void LoadFromJson(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var document = JsonDocument.Parse(payload);
        var hasNextCharacterId = document.RootElement.TryGetProperty("nextCharacterId", out _);

        var parsed = JsonSerializer.Deserialize<CharacterDossiers>(payload, DossierJsonOptions);
        var loaded = parsed ?? throw new InvalidOperationException("Failed to deserialize character dossiers from JSON.");
        ApplyLoaded(hasNextCharacterId ? loaded : MigrateMissingNextCharacterId(loaded));
    }

    private void ApplyLoaded(CharacterDossiers loaded)
    {
        if (string.IsNullOrWhiteSpace(loaded.DossiersId))
        {
            throw new InvalidOperationException("DossiersId cannot be empty.");
        }

        if (loaded.Version != CurrentVersion)
        {
            throw new InvalidOperationException($"Character dossiers version must be {CurrentVersion}.");
        }

        var minimumNextCharacterId = (loaded.Characters ?? [])
            .Select(character => character.CharacterId)
            .DefaultIfEmpty()
            .Max() + 1;
        if (loaded.NextCharacterId < minimumNextCharacterId)
        {
            throw new InvalidOperationException("NextCharacterId must be greater than every stored CharacterId.");
        }

        lock (syncRoot)
        {
            dossiers = NormalizeDossiers(loaded);
        }
    }

    private static CharacterDossiers NormalizeDossiers(CharacterDossiers source)
    {
        var characters = (source.Characters ?? []).Select(NormalizeDossier).ToList();
        ValidateUniqueCharacterIds(characters);
        return source with
        {
            NextCharacterId = Math.Max(source.NextCharacterId, characters.Select(character => character.CharacterId).DefaultIfEmpty().Max() + 1),
            Characters = characters,
            SuspectArchive = source.SuspectArchive ?? [],
            EvidenceIndex = NormalizeEvidenceIndex(source.EvidenceIndex),
            IdentityConflicts = source.IdentityConflicts ?? [],
            AuditTrail = source.AuditTrail ?? []
        };
    }

    private static CharacterDossiers MigrateMissingNextCharacterId(CharacterDossiers source)
    {
        var characters = source.Characters ?? [];
        return source with
        {
            NextCharacterId = characters
                .Select(character => character.CharacterId)
                .DefaultIfEmpty()
                .Max() + 1
        };
    }

    private static IReadOnlyList<CharacterEvidenceIndexEntry> NormalizeEvidenceIndex(IReadOnlyList<CharacterEvidenceIndexEntry>? entries)
        => (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Pointer) && !string.IsNullOrWhiteSpace(entry.Excerpt))
            .Select(entry => entry with
            {
                Pointer = entry.Pointer.Trim(),
                Excerpt = entry.Excerpt.Trim()
            })
            .ToList();

    private static void ValidateUniqueCharacterIds(IReadOnlyCollection<CharacterDossier> characters)
    {
        var duplicateIds = characters
            .GroupBy(character => character.CharacterId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Character dossiers contain duplicate CharacterId values: {string.Join(", ", duplicateIds)}.");
        }
    }

    private static CharacterDossier Merge(
        CharacterDossier existing,
        string? name,
        string? gender,
        IReadOnlyDictionary<string, string>? observedNameFormExamples,
        CharacterProfile? profile)
    {
        var mergedObservedNameForms = NormalizeObservedNameFormExamples(existing.ObservedNameFormExamples);
        foreach (var (observedNameForm, example) in NormalizeObservedNameFormExamples(observedNameFormExamples))
        {
            mergedObservedNameForms.TryAdd(observedNameForm, example);
        }

        var resolvedGender = NormalizeGender(existing.Gender);
        var candidateGender = NormalizeGender(gender);
        if (resolvedGender == "unknown" && candidateGender != "unknown")
        {
            resolvedGender = candidateGender;
        }

        return existing with
        {
            Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim(),
            ObservedNameForms = BuildObservedNameForms(mergedObservedNameForms),
            ObservedNameFormExamples = mergedObservedNameForms,
            Gender = resolvedGender,
            Profile = CharacterProfile.MergeMissing(existing.Profile, profile)
        };
    }

    private static CharacterDossier NormalizeDossier(CharacterDossier dossier)
    {
        ValidateCharacterId(dossier.CharacterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dossier.Name);
        var observedNameFormExamples = NormalizeObservedNameFormExamples(dossier.ObservedNameFormExamples);
        return dossier with
        {
            Name = dossier.Name.Trim(),
            ObservedNameForms = BuildObservedNameForms(observedNameFormExamples),
            ObservedNameFormExamples = observedNameFormExamples,
            Gender = NormalizeGender(dossier.Gender),
            ImportanceLevel = CharacterImportance.NormalizeLevel(dossier.ImportanceLevel),
            Profile = CharacterProfile.Normalize(dossier.Profile)
        };
    }

    private static Dictionary<string, string> NormalizeObservedNameFormExamples(IReadOnlyDictionary<string, string>? observedNameFormExamples)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (observedNameFormExamples is null)
        {
            return normalized;
        }

        foreach (var (observedNameForm, example) in observedNameFormExamples)
        {
            if (!string.IsNullOrWhiteSpace(observedNameForm) && !string.IsNullOrWhiteSpace(example))
            {
                normalized.TryAdd(observedNameForm.Trim(), example.Trim());
            }
        }

        return normalized;
    }

    private static IReadOnlyList<string> BuildObservedNameForms(IReadOnlyDictionary<string, string> observedNameFormExamples)
        => observedNameFormExamples.Keys.OrderBy(observedNameForm => observedNameForm, StringComparer.OrdinalIgnoreCase).ToList();

    private static string NormalizeGender(string? raw)
        => raw?.Trim().ToLowerInvariant() switch
        {
            "male" or "m" or "man" => "male",
            "female" or "f" or "woman" => "female",
            _ => "unknown"
        };

    private static CharacterDossiers CreateEmpty(string? dossiersId)
        => new(
            dossiersId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            CurrentVersion,
            [],
            1);

    private static void ValidateCharacterId(int characterId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId), characterId, "CharacterId must be positive.");
        }
    }

}

public sealed record ResolveAndUpsertResult(
    string DossiersId,
    int Version,
    string Status,
    int? CharacterId,
    IReadOnlyList<int> CandidateIds)
{
    public static ResolveAndUpsertResult Created(int characterId, string dossiersId, int version)
        => new(dossiersId, version, "created", characterId, []);

    public static ResolveAndUpsertResult Updated(int characterId, string dossiersId, int version)
        => new(dossiersId, version, "updated", characterId, []);

    public static ResolveAndUpsertResult Ambiguous(IReadOnlyList<int> candidateIds, string dossiersId, int version)
        => new(dossiersId, version, "ambiguous", null, candidateIds);
}

using System.Globalization;
using System.Text.Json;
using AiTextEditor.Core.Common;
using AiTextEditor.Core.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiTextEditor.Core.Services;

public sealed class CharacterDossierService
{
    private static readonly JsonSerializerOptions DossierJsonOptions = new(SerializationOptions.RelaxedCompact)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private CharacterDossiers dossiers;
    private readonly IDeserializer yamlDeserializer;
    private readonly ISerializer yamlSerializer;

    public CharacterDossierService(string? initialDossiersId = null)
    {
        yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        dossiers = CreateEmpty(initialDossiersId);
    }

    public CharacterDossiers GetDossiers()
    {
        lock (syncRoot)
        {
            return dossiers;
        }
    }

    public CharacterDossier UpsertDossier(CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ArgumentException.ThrowIfNullOrWhiteSpace(dossier.CharacterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dossier.Name);

        lock (syncRoot)
        {
            var characters = dossiers.Characters.ToDictionary(c => c.CharacterId, StringComparer.Ordinal);
            characters[dossier.CharacterId] = NormalizeDossier(dossier);

            dossiers = dossiers with
            {
                Version = dossiers.Version + 1,
                Characters = characters.Values.ToList()
            };
            return characters[dossier.CharacterId];
        }
    }

    public CharacterDossier? TryGetDossier(string characterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        lock (syncRoot)
        {
            return dossiers.Characters.FirstOrDefault(c => string.Equals(c.CharacterId, characterId, StringComparison.Ordinal));
        }
    }

    public IReadOnlyCollection<CharacterDossier> FindByNameOrAlias(string nameOrAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrAlias);

        lock (syncRoot)
        {
            var normalized = nameOrAlias.Trim();
            var matches = dossiers.Characters
                .Where(c =>
                    string.Equals(c.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                    c.Aliases.Any(a => string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return matches;
        }
    }

    public CharacterDossier UpdateDossierById(
        string characterId,
        string? name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        CharacterProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        lock (syncRoot)
        {
            var existing = TryGetDossier(characterId)
                ?? throw new InvalidOperationException($"character_not_found: {characterId}");

            var merged = Merge(existing, name, gender, aliasExamples, profile);
            return UpsertDossier(merged);
        }
    }

    public ResolveAndUpsertResult ResolveAndUpsertDossier(
        string name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        CharacterProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var canonicalName = name.Trim();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            canonicalName
        };

        if (aliasExamples is not null)
        {
            foreach (var alias in aliasExamples.Keys)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    keys.Add(alias.Trim());
                }
            }
        }

        lock (syncRoot)
        {
            var candidates = new List<CharacterDossier>();
            foreach (var key in keys)
            {
                candidates.AddRange(FindByNameOrAlias(key));
            }

            var distinct = candidates
                .DistinctBy(c => c.CharacterId, StringComparer.Ordinal)
                .ToList();

            if (distinct.Count == 0)
            {
                var created = CreateNew(canonicalName, gender, aliasExamples, profile);
                var saved = UpsertDossier(created);
                return ResolveAndUpsertResult.Created(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            if (distinct.Count == 1)
            {
                var existing = distinct[0];
                var merged = Merge(existing, null, gender, aliasExamples, profile);
                var saved = UpsertDossier(merged);
                return ResolveAndUpsertResult.Updated(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            var exactByName = distinct
                .Where(c => string.Equals(c.Name, canonicalName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exactByName.Count == 1)
            {
                var existing = exactByName[0];
                var merged = Merge(existing, null, gender, aliasExamples, profile);
                var saved = UpsertDossier(merged);
                return ResolveAndUpsertResult.Updated(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            return ResolveAndUpsertResult.Ambiguous(distinct.Select(c => c.CharacterId).ToArray(), dossiers.DossiersId, dossiers.Version);
        }
    }

    public bool RemoveDossier(string characterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        lock (syncRoot)
        {
            var existing = dossiers.Characters.FirstOrDefault(c => string.Equals(c.CharacterId, characterId, StringComparison.Ordinal));
            if (existing == null)
            {
                return false;
            }

            var updated = dossiers.Characters.Where(c => !string.Equals(c.CharacterId, characterId, StringComparison.Ordinal)).ToList();
            dossiers = dossiers with
            {
                Version = dossiers.Version + 1,
                Characters = updated
            };
            return true;
        }
    }

    public void ReplaceDossiers(IReadOnlyCollection<CharacterDossier> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);

        lock (syncRoot)
        {
            var normalized = characters.Select(NormalizeDossier).ToList();
            dossiers = dossiers with
            {
                Version = dossiers.Version + 1,
                Characters = normalized
            };
        }
    }

    public void ReplaceDossiers(CharacterDossiers replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        lock (syncRoot)
        {
            var normalized = replacement.Characters.Select(NormalizeDossier).ToList();
            dossiers = NormalizeDossiers(replacement with
            {
                DossiersId = string.IsNullOrWhiteSpace(replacement.DossiersId)
                    ? dossiers.DossiersId
                    : replacement.DossiersId,
                Version = dossiers.Version + 1,
                Characters = normalized
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

    public string SaveToYaml()
    {
        lock (syncRoot)
        {
            return yamlSerializer.Serialize(dossiers);
        }
    }

    public void LoadFromJson(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var parsed = JsonSerializer.Deserialize<CharacterDossiers>(payload, DossierJsonOptions);
        if (parsed == null)
        {
            throw new InvalidOperationException("Failed to deserialize character dossiers from JSON.");
        }

        ApplyLoaded(parsed);
    }

    public void LoadFromYaml(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var parsed = yamlDeserializer.Deserialize<CharacterDossiersYaml>(payload)?.ToModel();
        if (parsed == null)
        {
            throw new InvalidOperationException("Failed to deserialize character dossiers from YAML.");
        }

        ApplyLoaded(parsed);
    }

    private void ApplyLoaded(CharacterDossiers loaded)
    {
        var loadedCharacters = loaded.Characters ?? Array.Empty<CharacterDossier>();
        var normalizedCharacters = loadedCharacters.Select(NormalizeDossier).ToList();
        if (string.IsNullOrWhiteSpace(loaded.DossiersId))
        {
            throw new InvalidOperationException("DossiersId cannot be empty.");
        }

        lock (syncRoot)
        {
            dossiers = NormalizeDossiers(loaded with
            {
                Version = loaded.Version > 0 ? loaded.Version : 1,
                Characters = normalizedCharacters
            });
        }
    }

    private static CharacterDossiers NormalizeDossiers(CharacterDossiers source)
    {
        return source with
        {
            Characters = source.Characters.Select(NormalizeDossier).ToList(),
            SuspectArchive = source.SuspectArchive ?? [],
            EvidenceIndex = NormalizeEvidenceIndex(source.EvidenceIndex),
            IdentityConflicts = source.IdentityConflicts ?? [],
            AuditTrail = source.AuditTrail ?? []
        };
    }

    private static IReadOnlyList<CharacterEvidenceIndexEntry> NormalizeEvidenceIndex(
        IReadOnlyList<CharacterEvidenceIndexEntry>? entries)
    {
        return (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Pointer) && !string.IsNullOrWhiteSpace(entry.Excerpt))
            .Select(entry => entry with
            {
                Pointer = entry.Pointer.Trim(),
                Excerpt = entry.Excerpt.Trim(),
                CharacterId = string.IsNullOrWhiteSpace(entry.CharacterId) ? null : entry.CharacterId.Trim(),
                CandidateId = string.IsNullOrWhiteSpace(entry.CandidateId) ? null : entry.CandidateId.Trim()
            })
            .ToList();
    }

    private static CharacterDossier CreateNew(
        string name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        CharacterProfile? profile)
    {
        var normalizedName = name.Trim();
        var normalizedAliasExamples = NormalizeAliasExamples(aliasExamples);

        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalizedName));
        var id = new Guid(hash).ToString("N");

        return new CharacterDossier(
            id,
            normalizedName,
            normalizedAliasExamples.Keys.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
            normalizedAliasExamples,
            NormalizeGender(gender),
            ImportanceLevel: null,
            Profile: CharacterProfile.Normalize(profile));
    }

    private static CharacterDossier Merge(
        CharacterDossier existing,
        string? name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        CharacterProfile? profile = null)
    {
        var canonicalName = string.IsNullOrWhiteSpace(name) ? existing.Name : name.Trim();
        var normalizedAliasExamples = NormalizeAliasExamples(existing.AliasExamples);

        if (aliasExamples is not null)
        {
            foreach (var (alias, example) in aliasExamples)
            {
                if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(example))
                {
                    continue;
                }

                var trimmedAlias = alias.Trim();
                if (!normalizedAliasExamples.ContainsKey(trimmedAlias))
                {
                    normalizedAliasExamples[trimmedAlias] = example.Trim();
                }
            }
        }

        var resolvedGender = NormalizeGender(existing.Gender);
        var normalizedCandidateGender = NormalizeGender(gender);
        if (string.Equals(resolvedGender, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedCandidateGender, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            resolvedGender = normalizedCandidateGender;
        }

        var aliases = normalizedAliasExamples.Keys
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return existing with
        {
            Name = canonicalName,
            Gender = resolvedGender,
            AliasExamples = normalizedAliasExamples,
            Aliases = aliases,
            Profile = CharacterProfile.MergeMissing(existing.Profile, profile)
        };
    }

    private static CharacterDossier NormalizeDossier(CharacterDossier dossier)
    {
        var aliasExamples = NormalizeAliasExamples(dossier.AliasExamples);
        var aliases = aliasExamples.Keys.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

        return dossier with
        {
            CharacterId = dossier.CharacterId.Trim(),
            Name = dossier.Name.Trim(),
            Gender = NormalizeGender(dossier.Gender),
            AliasExamples = aliasExamples,
            Aliases = aliases,
            ImportanceLevel = CharacterImportance.NormalizeLevel(dossier.ImportanceLevel),
            Profile = CharacterProfile.Normalize(dossier.Profile)
        };
    }

    private static Dictionary<string, string> NormalizeAliasExamples(IReadOnlyDictionary<string, string>? aliasExamples)
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
            if (trimmedAlias.Length == 0)
            {
                continue;
            }

            if (!normalized.ContainsKey(trimmedAlias))
            {
                normalized[trimmedAlias] = example.Trim();
            }
        }

        return normalized;
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

    private static CharacterDossiers CreateEmpty(string? id)
    {
        return new CharacterDossiers(
            id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            1,
            Array.Empty<CharacterDossier>());
    }

    private sealed class CharacterDossiersYaml
    {
        public string? DossiersId { get; set; }

        public int Version { get; set; }

        public List<CharacterDossierYaml>? Characters { get; set; }

        public CharacterDossiers ToModel()
        {
            return new CharacterDossiers(
                DossiersId ?? string.Empty,
                Version,
                Characters?.Select(character => character.ToModel()).ToList() ?? []);
        }
    }

    private sealed class CharacterDossierYaml
    {
        public string? CharacterId { get; set; }

        public string? Name { get; set; }

        public List<string>? Aliases { get; set; }

        public Dictionary<string, string>? AliasExamples { get; set; }

        public string? Gender { get; set; }

        public int? ImportanceLevel { get; set; }

        public CharacterProfileYaml? Profile { get; set; }

        public CharacterDossier ToModel()
        {
            return new CharacterDossier(
                CharacterId ?? string.Empty,
                Name ?? string.Empty,
                Aliases ?? [],
                AliasExamples ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Gender ?? "unknown",
                ImportanceLevel,
                Profile?.ToModel());
        }
    }

    private sealed class CharacterProfileYaml
    {
        public string? Appearance { get; set; }

        public string? StatusAndCompetence { get; set; }

        public string? PsychologicalProfile { get; set; }

        public string? SpeechAndCommunication { get; set; }

        public CharacterProfile ToModel()
        {
            return new CharacterProfile(
                Appearance ?? string.Empty,
                StatusAndCompetence ?? string.Empty,
                PsychologicalProfile ?? string.Empty,
                SpeechAndCommunication ?? string.Empty);
        }
    }
}

    public sealed record ResolveAndUpsertResult(
    string DossiersId,
    int Version,
    string Status,
    string? CharacterId,
    IReadOnlyList<string> CandidateIds)
{
    public static ResolveAndUpsertResult Created(string characterId, string dossiersId, int version)
        => new(dossiersId, version, "created", characterId, []);

    public static ResolveAndUpsertResult Updated(string characterId, string dossiersId, int version)
        => new(dossiersId, version, "updated", characterId, []);

    public static ResolveAndUpsertResult Ambiguous(IReadOnlyList<string> candidateIds, string dossiersId, int version)
        => new(dossiersId, version, "ambiguous", null, candidateIds);
}

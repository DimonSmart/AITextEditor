using System.Globalization;
using System.Text.Json;
using AiTextEditor.Lib.Common;
using AiTextEditor.Lib.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiTextEditor.Lib.Services;

public sealed class CharacterDossierService
{
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

            dossiers = new CharacterDossiers(dossiers.DossiersId, dossiers.Version + 1, characters.Values.ToList());
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
        IReadOnlyCollection<CharacterFact>? facts,
        string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        lock (syncRoot)
        {
            var existing = TryGetDossier(characterId)
                ?? throw new InvalidOperationException($"character_not_found: {characterId}");

            var merged = Merge(existing, name, gender, aliasExamples, facts, description);
            return UpsertDossier(merged);
        }
    }

    public ResolveAndUpsertResult ResolveAndUpsertDossier(
        string name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        IReadOnlyCollection<CharacterFact>? facts,
        string? description)
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
                var created = CreateNew(canonicalName, gender, aliasExamples, facts, description);
                var saved = UpsertDossier(created);
                return ResolveAndUpsertResult.Created(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            if (distinct.Count == 1)
            {
                var existing = distinct[0];
                var merged = Merge(existing, null, gender, aliasExamples, facts, description);
                var saved = UpsertDossier(merged);
                return ResolveAndUpsertResult.Updated(saved.CharacterId, dossiers.DossiersId, dossiers.Version);
            }

            var exactByName = distinct
                .Where(c => string.Equals(c.Name, canonicalName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exactByName.Count == 1)
            {
                var existing = exactByName[0];
                var merged = Merge(existing, null, gender, aliasExamples, facts, description);
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
            dossiers = new CharacterDossiers(dossiers.DossiersId, dossiers.Version + 1, updated);
            return true;
        }
    }

    public void ReplaceDossiers(IReadOnlyCollection<CharacterDossier> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);

        lock (syncRoot)
        {
            var normalized = characters.Select(NormalizeDossier).ToList();
            dossiers = new CharacterDossiers(dossiers.DossiersId, dossiers.Version + 1, normalized);
        }
    }

    public string SaveToJson()
    {
        lock (syncRoot)
        {
            return JsonSerializer.Serialize(dossiers, SerializationOptions.RelaxedCompact);
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

        var parsed = JsonSerializer.Deserialize<CharacterDossiers>(payload, SerializationOptions.RelaxedCompact);
        if (parsed == null)
        {
            throw new InvalidOperationException("Failed to deserialize character dossiers from JSON.");
        }

        ApplyLoaded(parsed);
    }

    public void LoadFromYaml(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var parsed = yamlDeserializer.Deserialize<CharacterDossiers>(payload);
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
            dossiers = new CharacterDossiers(
                loaded.DossiersId,
                loaded.Version > 0 ? loaded.Version : 1,
                normalizedCharacters);
        }
    }

    private static CharacterDossier CreateNew(
        string name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        IReadOnlyCollection<CharacterFact>? facts,
        string? description)
    {
        var normalizedName = name.Trim();
        var normalizedAliasExamples = NormalizeAliasExamples(aliasExamples);
        var normalizedFacts = NormalizeFacts(facts);

        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalizedName));
        var id = new Guid(hash).ToString("N");

        return new CharacterDossier(
            id,
            normalizedName,
            (description ?? string.Empty).Trim(),
            normalizedAliasExamples.Keys.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
            normalizedAliasExamples,
            normalizedFacts,
            NormalizeGender(gender));
    }

    private static CharacterDossier Merge(
        CharacterDossier existing,
        string? name,
        string? gender,
        IReadOnlyDictionary<string, string>? aliasExamples,
        IReadOnlyCollection<CharacterFact>? facts,
        string? description)
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

        var normalizedFacts = NormalizeFacts(existing.Facts);
        if (facts is not null)
        {
            foreach (var fact in facts)
            {
                if (fact is null)
                {
                    continue;
                }

                var key = (fact.Key ?? string.Empty).Trim();
                var value = (fact.Value ?? string.Empty).Trim();
                var example = (fact.Example ?? string.Empty).Trim();
                if (key.Length == 0 || value.Length == 0 || example.Length == 0)
                {
                    continue;
                }

                var exists = normalizedFacts.Any(f =>
                    string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Value, value, StringComparison.OrdinalIgnoreCase));

                if (!exists)
                {
                    normalizedFacts.Add(new CharacterFact(key, value, example));
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

        var resolvedDescription = existing.Description;
        if (!string.IsNullOrWhiteSpace(description))
        {
            resolvedDescription = description.Trim();
        }

        var aliases = normalizedAliasExamples.Keys
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return existing with
        {
            Name = canonicalName,
            Description = resolvedDescription,
            Gender = resolvedGender,
            AliasExamples = normalizedAliasExamples,
            Aliases = aliases,
            Facts = normalizedFacts
        };
    }

    private static CharacterDossier NormalizeDossier(CharacterDossier dossier)
    {
        var aliasExamples = NormalizeAliasExamples(dossier.AliasExamples);
        var facts = NormalizeFacts(dossier.Facts);
        var aliases = aliasExamples.Keys.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

        return dossier with
        {
            CharacterId = dossier.CharacterId.Trim(),
            Name = dossier.Name.Trim(),
            Description = dossier.Description?.Trim() ?? string.Empty,
            Gender = NormalizeGender(dossier.Gender),
            AliasExamples = aliasExamples,
            Aliases = aliases,
            Facts = facts
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

    private static List<CharacterFact> NormalizeFacts(IReadOnlyCollection<CharacterFact>? facts)
    {
        if (facts is null)
        {
            return [];
        }

        var normalized = new List<CharacterFact>(facts.Count);
        foreach (var fact in facts)
        {
            if (fact is null)
            {
                continue;
            }

            var key = (fact.Key ?? string.Empty).Trim();
            var value = (fact.Value ?? string.Empty).Trim();
            var example = (fact.Example ?? string.Empty).Trim();
            if (key.Length == 0 || value.Length == 0 || example.Length == 0)
            {
                continue;
            }

            var exists = normalized.Any(f =>
                string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.Value, value, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                normalized.Add(new CharacterFact(key, value, example));
            }
        }

        return normalized
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

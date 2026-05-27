using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterBibleDossierProjectionIndex
{
    private readonly Dictionary<string, DossierProjection> projections = new(StringComparer.OrdinalIgnoreCase);

    public CharacterBibleDossierProjectionIndex(CharacterDossiers dossiers)
    {
        ArgumentNullException.ThrowIfNull(dossiers);

        foreach (var dossier in dossiers.Characters)
        {
            var projection = new DossierProjection(dossier);
            projections[projection.CharacterId] = projection;
        }
    }

    public DossierProjection AddCandidate(CharacterBibleCharacterCandidate candidate)
    {
        var projection = DossierProjection.FromCandidate(candidate);
        projections[projection.CharacterId] = projection;
        return projection;
    }

    public DossierProjection GetRequired(string characterId)
    {
        if (!projections.TryGetValue(characterId, out var projection))
        {
            throw new InvalidOperationException($"character_projection_not_found: {characterId}");
        }

        return projection;
    }

    public bool SetImportanceLevelIfMissing(string characterId, int level)
    {
        return projections.TryGetValue(characterId, out var projection)
               && projection.SetImportanceLevelIfMissing(level);
    }

    public CharacterDossiers ToDossiers(CharacterDossiers baseDossiers, int? maxCharacters)
    {
        var items = projections.Values
            .Select(projection => projection.ToDossier())
            .OrderBy(dossier => dossier.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (maxCharacters.HasValue && maxCharacters.Value > 0 && items.Count > maxCharacters.Value)
        {
            items = items.Take(maxCharacters.Value).ToList();
        }

        return baseDossiers with { Characters = items };
    }

    internal sealed class DossierProjection
    {
        private readonly Dictionary<string, string> aliasExamples = new(StringComparer.OrdinalIgnoreCase);

        public string CharacterId { get; }
        public string Name { get; private set; }
        public string Gender { get; private set; }
        public int? ImportanceLevel { get; private set; }
        public CharacterProfile Profile { get; private set; }

        private DossierProjection(
            string characterId,
            string name,
            string gender,
            int? importanceLevel,
            CharacterProfile? profile)
        {
            CharacterId = characterId;
            Name = name.Trim();
            Gender = string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim();
            ImportanceLevel = importanceLevel;
            Profile = CharacterProfile.Normalize(profile);
        }

        public DossierProjection(CharacterDossier dossier)
            : this(
                dossier.CharacterId,
                dossier.Name,
                dossier.Gender,
                dossier.ImportanceLevel,
                dossier.Profile)
        {
            MergeAliasExamples(dossier.AliasExamples);
        }

        public static DossierProjection FromCandidate(CharacterBibleCharacterCandidate candidate)
        {
            var name = candidate.CanonicalName.Trim();
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name));

            var projection = new DossierProjection(
                new Guid(hash).ToString("N"),
                name,
                candidate.Gender,
                importanceLevel: null,
                CharacterProfile.Empty);

            foreach (var (alias, example) in candidate.AliasExamples)
            {
                projection.AddAliasExample(alias, example);
            }

            return projection;
        }

        public bool RefineCanonicalName(CharacterBibleCharacterCandidate candidate, bool exactNameMatch)
        {
            if (exactNameMatch)
            {
                return false;
            }

            var canonicalName = candidate.CanonicalName.Trim();
            if (string.IsNullOrWhiteSpace(canonicalName))
            {
                return false;
            }

            if (string.Equals(Name, canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!IsGenderCompatible(candidate.Gender))
            {
                return false;
            }

            if (!TryFindExampleFor(Name, candidate.AliasExamples, out var currentNameExample))
            {
                return false;
            }

            AddAliasExample(Name, currentNameExample);
            Name = canonicalName;
            return true;
        }

        public bool SetGenderIfUnknown(string? gender)
        {
            if (string.IsNullOrWhiteSpace(gender))
            {
                return false;
            }

            if (!string.Equals(Gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Gender = gender.Trim();
            return true;
        }

        public bool MergeAliases(CharacterBibleCharacterCandidate candidate, bool exactNameMatch)
        {
            var changed = false;

            foreach (var (alias, example) in candidate.AliasExamples)
            {
                changed |= AddAliasExample(alias, example);
            }

            if (exactNameMatch && !string.IsNullOrWhiteSpace(candidate.CanonicalName))
            {
                if (TryFindExampleFor(candidate.CanonicalName, candidate.AliasExamples, out var example))
                {
                    changed |= AddAliasExample(candidate.CanonicalName, example);
                }
            }

            return changed;
        }

        public bool SetImportanceLevelIfMissing(int level)
        {
            if (ImportanceLevel is not null)
            {
                return false;
            }

            var normalized = CharacterImportance.NormalizeLevel(level);
            if (normalized is null)
            {
                return false;
            }

            ImportanceLevel = normalized;
            return true;
        }

        public CharacterDossier ToDossier()
        {
            var normalizedAliasExamples = aliasExamples
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                .ToDictionary(kvp => kvp.Key.Trim(), kvp => kvp.Value.Trim(), StringComparer.OrdinalIgnoreCase);

            var aliases = normalizedAliasExamples.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CharacterDossier(
                CharacterId,
                Name,
                aliases,
                normalizedAliasExamples,
                string.IsNullOrWhiteSpace(Gender) ? "unknown" : Gender,
                ImportanceLevel,
                Profile);
        }

        private bool AddAliasExample(string alias, string? example)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(example))
            {
                return false;
            }

            var trimmedAlias = alias.Trim();
            if (trimmedAlias.Length == 0)
            {
                return false;
            }

            if (aliasExamples.ContainsKey(trimmedAlias))
            {
                return false;
            }

            aliasExamples[trimmedAlias] = example.Trim();
            return true;
        }

        private void MergeAliasExamples(IReadOnlyDictionary<string, string>? existing)
        {
            if (existing is null)
            {
                return;
            }

            foreach (var (alias, example) in existing)
            {
                AddAliasExample(alias, example);
            }
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

            example = match.Value;
            return true;
        }

        private bool IsGenderCompatible(string? candidateGender)
        {
            var normalizedCandidateGender = CharacterNameNormalizer.NormalizeGender(candidateGender);
            if (string.Equals(normalizedCandidateGender, "unknown", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(Gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(Gender, normalizedCandidateGender, StringComparison.OrdinalIgnoreCase);
        }
    }
}

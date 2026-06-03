using AiTextEditor.Core.Model;

namespace AiTextEditor.Core.Services;

public sealed class CharacterNameIndex
{
    private readonly Dictionary<string, IReadOnlyList<int>> byName;

    public CharacterNameIndex(IEnumerable<CharacterDossier> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);

        byName = characters
            .SelectMany(character => character.ObservedNameForms
                .Append(character.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new KeyValuePair<string, int>(NormalizeName(name), character.CharacterId)))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<int>)group
                    .Select(pair => pair.Value)
                    .Distinct()
                    .ToArray(),
                StringComparer.Ordinal);
    }

    public IReadOnlyList<int> FindByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return byName.GetValueOrDefault(NormalizeName(name), []);
    }

    private static string NormalizeName(string name)
        => name.Trim().ToLowerInvariant();
}

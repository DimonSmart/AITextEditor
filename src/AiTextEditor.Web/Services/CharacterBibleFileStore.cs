using AiTextEditor.Core.Services;

namespace AiTextEditor.Web.Services;

public interface ICharacterBibleFileStore
{
    string GetCompanionPath(string bookPath);

    Task<bool> LoadAsync(string characterBiblePath, CharacterDossierService characterDossiers, CancellationToken cancellationToken);

    Task SaveAsync(string characterBiblePath, CharacterDossierService characterDossiers, CancellationToken cancellationToken);
}

public sealed class CharacterBibleFileStore : ICharacterBibleFileStore
{
    private const string CompanionSuffix = "-character-bible.json";

    public string GetCompanionPath(string bookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookPath);

        var directory = Path.GetDirectoryName(bookPath);
        var bookName = Path.GetFileNameWithoutExtension(bookPath);
        if (string.IsNullOrWhiteSpace(bookName))
        {
            throw new ArgumentException("Book path must include a file name.", nameof(bookPath));
        }

        var companionFileName = bookName + CompanionSuffix;
        return string.IsNullOrWhiteSpace(directory)
            ? companionFileName
            : Path.Combine(directory, companionFileName);
    }

    public async Task<bool> LoadAsync(
        string characterBiblePath,
        CharacterDossierService characterDossiers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterBiblePath);
        ArgumentNullException.ThrowIfNull(characterDossiers);

        if (File.Exists(characterBiblePath))
        {
            var json = await File.ReadAllTextAsync(characterBiblePath, cancellationToken);
            characterDossiers.LoadFromJson(json);
            return true;
        }

        return false;
    }

    public async Task SaveAsync(
        string characterBiblePath,
        CharacterDossierService characterDossiers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterBiblePath);
        ArgumentNullException.ThrowIfNull(characterDossiers);

        var directory = Path.GetDirectoryName(characterBiblePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(characterBiblePath, characterDossiers.SaveToJson(), cancellationToken);
    }

}

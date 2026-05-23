using System.Text;
using AiTextEditor.Core.Services;

namespace AiTextEditor.Web.Services;

public interface ICharacterBibleFileStore
{
    string GetCompanionPath(string bookPath);

    Task<bool> LoadAsync(string characterBiblePath, CharacterDossierService characterDossiers, CancellationToken cancellationToken);

    Task SaveAsync(string characterBiblePath, CharacterDossierService characterDossiers, CancellationToken cancellationToken);
}

public sealed class CharacterBibleFileStore(ICharacterBibleMarkdownRenderer markdownRenderer) : ICharacterBibleFileStore
{
    private const string CompanionSuffix = "-character-bible.md";
    private const string DossiersFenceStart = "<!-- ai-text-editor-character-dossiers:start -->";
    private const string DossiersFenceEnd = "<!-- ai-text-editor-character-dossiers:end -->";

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

        if (!File.Exists(characterBiblePath))
        {
            return false;
        }

        var markdown = await File.ReadAllTextAsync(characterBiblePath, cancellationToken);
        var yaml = ExtractDossiersYaml(markdown, characterBiblePath);
        characterDossiers.LoadFromYaml(yaml);
        return true;
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

        var dossiers = characterDossiers.GetDossiers();
        var yaml = characterDossiers.SaveToYaml().TrimEnd();
        var markdownProjection = markdownRenderer.Render(dossiers).TrimEnd();
        var fileContent = new StringBuilder()
            .AppendLine("# Character Bible")
            .AppendLine()
            .AppendLine(DossiersFenceStart)
            .AppendLine("```yaml")
            .AppendLine(yaml)
            .AppendLine("```")
            .AppendLine(DossiersFenceEnd)
            .AppendLine()
            .AppendLine("## Markdown projection")
            .AppendLine()
            .AppendLine(markdownProjection)
            .ToString();

        await File.WriteAllTextAsync(characterBiblePath, fileContent, cancellationToken);
    }

    private static string ExtractDossiersYaml(string markdown, string characterBiblePath)
    {
        var start = markdown.IndexOf(DossiersFenceStart, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Character bible file does not contain a dossiers block: {characterBiblePath}");
        }

        var fenceStart = markdown.IndexOf("```yaml", start, StringComparison.Ordinal);
        if (fenceStart < 0)
        {
            throw new InvalidOperationException($"Character bible file does not contain a YAML dossiers block: {characterBiblePath}");
        }

        var yamlStart = markdown.IndexOf('\n', fenceStart);
        if (yamlStart < 0)
        {
            throw new InvalidOperationException($"Character bible YAML block is not closed: {characterBiblePath}");
        }

        yamlStart++;
        var yamlEnd = markdown.IndexOf("```", yamlStart, StringComparison.Ordinal);
        if (yamlEnd < 0)
        {
            throw new InvalidOperationException($"Character bible YAML block is not closed: {characterBiblePath}");
        }

        var end = markdown.IndexOf(DossiersFenceEnd, yamlEnd, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Character bible file does not close the dossiers block: {characterBiblePath}");
        }

        var yaml = markdown[yamlStart..yamlEnd].Trim();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException($"Character bible YAML block is empty: {characterBiblePath}");
        }

        return yaml;
    }
}

using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;

namespace AiTextEditor.Web.Services;

public sealed class EditorWorkspaceState
{
    private const string DefaultMarkdown = "# Untitled\n\nStart writing here.\n";
    private const string DefaultDocumentId = "ui-book";
    private readonly ICharacterBibleFileStore characterBibleFileStore;

    public EditorWorkspaceState(ICharacterBibleFileStore? characterBibleFileStore = null)
    {
        this.characterBibleFileStore = characterBibleFileStore ?? new CharacterBibleFileStore();
        Session = new EditorSession();
        CurrentMarkdown = DefaultMarkdown;
        CurrentDocument = Session.LoadDefaultDocument(CurrentMarkdown, DefaultDocumentId);
    }

    public EditorSession Session { get; private set; }

    public string CurrentMarkdown { get; private set; }

    public LinearDocument CurrentDocument { get; private set; }

    public string? CurrentBookPath { get; private set; }

    public string? CurrentCharacterBiblePath { get; private set; }

    public bool CurrentCharacterBibleLoadedFromFile { get; private set; }

    public CharacterDossierService CharacterDossiers => Session.GetCharacterDossierService();

    public LinearDocument LoadMarkdown(string markdown, string? documentId = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var resolvedDocumentId = string.IsNullOrWhiteSpace(documentId)
            ? CurrentDocument.Id
            : documentId;

        CurrentMarkdown = markdown;
        CurrentDocument = Session.LoadDefaultDocument(markdown, resolvedDocumentId);
        return CurrentDocument;
    }

    public LinearDocument LoadUploadedBook(string markdown, string fileName)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var nextSession = new EditorSession();
        var documentId = Path.GetFileNameWithoutExtension(fileName);
        var document = nextSession.LoadDefaultDocument(markdown, documentId);

        Session = nextSession;
        CurrentMarkdown = markdown;
        CurrentDocument = document;
        CurrentBookPath = null;
        CurrentCharacterBiblePath = null;
        CurrentCharacterBibleLoadedFromFile = false;
        return document;
    }

    public async Task LoadBookAsync(string bookPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookPath);

        if (!File.Exists(bookPath))
        {
            throw new FileNotFoundException("Book file was not found.", bookPath);
        }

        var markdown = await File.ReadAllTextAsync(bookPath, cancellationToken);
        var nextSession = new EditorSession();
        var documentId = Path.GetFileNameWithoutExtension(bookPath);
        var document = nextSession.LoadDefaultDocument(markdown, documentId);
        var characterBiblePath = characterBibleFileStore.GetCompanionPath(bookPath);
        var loadedCharacterBible = await characterBibleFileStore.LoadAsync(
            characterBiblePath,
            nextSession.GetCharacterDossierService(),
            cancellationToken);

        Session = nextSession;
        CurrentMarkdown = markdown;
        CurrentDocument = document;
        CurrentBookPath = bookPath;
        CurrentCharacterBiblePath = characterBiblePath;
        CurrentCharacterBibleLoadedFromFile = loadedCharacterBible;
    }

    public async Task SaveBookAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentBookPath))
        {
            throw new InvalidOperationException("Book path is not set.");
        }

        var directory = Path.GetDirectoryName(CurrentBookPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        CurrentCharacterBiblePath ??= characterBibleFileStore.GetCompanionPath(CurrentBookPath);
        await File.WriteAllTextAsync(CurrentBookPath, CurrentMarkdown, cancellationToken);
        await SaveCharacterBibleAsync(cancellationToken);
    }

    public async Task SaveCharacterBibleAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentBookPath))
        {
            throw new InvalidOperationException("Book path is not set.");
        }

        CurrentCharacterBiblePath ??= characterBibleFileStore.GetCompanionPath(CurrentBookPath);
        await characterBibleFileStore.SaveAsync(CurrentCharacterBiblePath, CharacterDossiers, cancellationToken);
    }
}

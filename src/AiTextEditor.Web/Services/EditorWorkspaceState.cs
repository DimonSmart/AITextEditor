using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;

namespace AiTextEditor.Web.Services;

public sealed class EditorWorkspaceState
{
    private const string DefaultMarkdown = "# Untitled\n\nStart writing here.\n";
    private const string DefaultDocumentId = "ui-book";
    private readonly ICharacterBibleFileStore characterBibleFileStore;
    private readonly object syncRoot = new();
    private int automationLockCount;

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

    public string? CurrentCharacterBiblePath =>
        string.IsNullOrWhiteSpace(CurrentBookPath)
            ? null
            : characterBibleFileStore.GetCompanionPath(CurrentBookPath);

    public bool CurrentCharacterBibleLoadedFromFile { get; private set; }

    public CharacterDossierService CharacterDossiers => Session.GetCharacterDossierService();

    public bool IsReadOnly
    {
        get
        {
            lock (syncRoot)
            {
                return automationLockCount > 0;
            }
        }
    }

    public event Action? Changed;

    public IDisposable BeginAutomation()
    {
        lock (syncRoot)
        {
            automationLockCount++;
        }

        NotifyChanged();
        return new AutomationLease(this);
    }

    public LinearDocument LoadMarkdown(string markdown, string? documentId = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        EnsureEditable();

        var resolvedDocumentId = string.IsNullOrWhiteSpace(documentId)
            ? CurrentDocument.Id
            : documentId;

        CurrentMarkdown = markdown;
        CurrentDocument = Session.LoadDefaultDocument(markdown, resolvedDocumentId);
        NotifyChanged();
        return CurrentDocument;
    }

    public async Task LoadBookAsync(string bookPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookPath);
        EnsureEditable();

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
        CurrentCharacterBibleLoadedFromFile = loadedCharacterBible;
        NotifyChanged();
    }

    public async Task SaveBookAsync(CancellationToken cancellationToken = default)
    {
        EnsureEditable();

        if (string.IsNullOrWhiteSpace(CurrentBookPath))
        {
            throw new InvalidOperationException("Book path is not set.");
        }

        var directory = Path.GetDirectoryName(CurrentBookPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(CurrentBookPath, CurrentMarkdown, cancellationToken);
        await SaveCharacterBibleAsync(cancellationToken);
    }

    public CharacterDossier UpsertCharacterDossier(CharacterDossier dossier)
    {
        EnsureEditable();
        var saved = CharacterDossiers.UpsertDossier(dossier);
        NotifyChanged();
        return saved;
    }

    public CharacterDossier CreateCharacter(NewCharacterDraft draft)
    {
        EnsureEditable();
        var saved = CharacterDossiers.CreateCharacter(draft);
        NotifyChanged();
        return saved;
    }

    public bool RemoveCharacterDossier(int characterId)
    {
        EnsureEditable();
        var removed = CharacterDossiers.RemoveDossier(characterId);
        if (removed)
        {
            NotifyChanged();
        }

        return removed;
    }

    public void ReplaceCharacterDossiers(CharacterDossiers dossiers)
    {
        CharacterDossiers.ReplaceDossiers(dossiers);
        NotifyChanged();
    }

    public async Task SaveCharacterBibleAsync(CancellationToken cancellationToken = default)
    {
        var characterBiblePath = GetRequiredCharacterBiblePath();
        await characterBibleFileStore.SaveAsync(characterBiblePath, CharacterDossiers, cancellationToken);
    }

    private string GetRequiredCharacterBiblePath()
    {
        if (string.IsNullOrWhiteSpace(CurrentBookPath))
        {
            throw new InvalidOperationException("Book path is not set. Load a book from a disk path before saving the character bible.");
        }

        return characterBibleFileStore.GetCompanionPath(CurrentBookPath);
    }

    private void EndAutomation()
    {
        lock (syncRoot)
        {
            if (automationLockCount == 0)
            {
                throw new InvalidOperationException("Workspace automation lock is not held.");
            }

            automationLockCount--;
        }

        NotifyChanged();
    }

    private void EnsureEditable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The editor is read-only while character bible generation is running.");
        }
    }

    public void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private sealed class AutomationLease : IDisposable
    {
        private EditorWorkspaceState? owner;

        public AutomationLease(EditorWorkspaceState owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            var workspace = Interlocked.Exchange(ref owner, null);
            workspace?.EndAutomation();
        }
    }
}

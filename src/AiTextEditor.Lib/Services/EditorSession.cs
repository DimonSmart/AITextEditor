using System;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class EditorSession
{
    private readonly MarkdownDocumentRepository repository;
    private readonly LinearDocumentEditor editor;
    private readonly InMemoryTargetSetService targetSets;
    private readonly CharacterDossierService characterDossierService;
    private LinearDocument? document;

    public EditorSession()
        : this(
            new MarkdownDocumentRepository(),
            new LinearDocumentEditor(),
            new InMemoryTargetSetService(),
            new CharacterDossierService())
    {
    }

    public EditorSession(
        MarkdownDocumentRepository repository,
        LinearDocumentEditor editor,
        InMemoryTargetSetService targetSets,
        CharacterDossierService characterDossierService)
    {
        this.repository = repository;
        this.editor = editor;
        this.targetSets = targetSets;
        this.characterDossierService = characterDossierService;
    }

    public LinearDocument LoadDefaultDocument(string markdown, string? documentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        if (documentId != null && string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id cannot be empty or whitespace when provided.", nameof(documentId));
        }

        var document = repository.LoadFromMarkdown(markdown);
        if (!string.IsNullOrWhiteSpace(documentId))
        {
            document = document with { Id = documentId! };
        }

        this.document = document;
        targetSets.Clear();
        return this.document;
    }

    public LinearDocument GetDefaultDocument()
    {
        return document ?? throw new InvalidOperationException("No document has been loaded.");
    }

    public IReadOnlyList<LinearItem> GetItems()
    {
        return GetDefaultDocument().Items;
    }

    public TargetSet CreateTargetSet(IEnumerable<int> itemIndices, string? userCommand = null, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(itemIndices);

        var document = GetDefaultDocument();
        var items = itemIndices
            .Distinct()
            .Where(index => index >= 0 && index < document.Items.Count)
            .Select(index => document.Items[index])
            .ToList();

        return targetSets.Create(items, userCommand, label);
    }

    public IReadOnlyList<TargetSet> ListDefaultTargetSets()
    {
        return targetSets.List();
    }

    public TargetSet? GetTargetSet(string targetSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSetId);
        return targetSets.Get(targetSetId);
    }

    public bool DeleteTargetSet(string targetSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSetId);
        return targetSets.Delete(targetSetId);
    }

    public LinearDocument ApplyOperations(IEnumerable<LinearEditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var currentDocument = GetDefaultDocument();
        var updated = editor.Apply(currentDocument, operations);
        document = updated;
        return updated;
    }

    public CharacterDossierService GetCharacterDossierService()
    {
        return characterDossierService;
    }
}

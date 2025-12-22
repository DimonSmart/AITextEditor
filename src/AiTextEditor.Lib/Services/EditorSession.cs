using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class EditorSession
{
    private readonly MarkdownDocumentRepository repository;
    private readonly LinearDocumentEditor editor;
    private readonly InMemoryTargetSetService targetSets;
    private readonly Dictionary<string, LinearDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private string? defaultDocumentId;

    public EditorSession()
        : this(new MarkdownDocumentRepository(), new LinearDocumentEditor(), new InMemoryTargetSetService())
    {
    }

    public EditorSession(
        MarkdownDocumentRepository repository,
        LinearDocumentEditor editor,
        InMemoryTargetSetService targetSets)
    {
        this.repository = repository;
        this.editor = editor;
        this.targetSets = targetSets;
    }

    public LinearDocument LoadDocument(string markdown, string? documentId = null)
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

        documents[document.Id] = document;
        return document;
    }

    public LinearDocument LoadDefaultDocument(string markdown, string? documentId = null)
    {
        var document = LoadDocument(markdown, documentId);
        defaultDocumentId = document.Id;
        return document;
    }

    public LinearDocument? GetDocument(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        return documents.TryGetValue(documentId, out var document) ? document : null;
    }

    public LinearDocument GetDefaultDocument()
    {
        return GetDocumentOrThrow(GetDefaultDocumentId());
    }

    public IReadOnlyList<LinearItem> GetItems(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var document = GetDocumentOrThrow(documentId);
        return document.Items;
    }

    public IReadOnlyList<LinearItem> GetItems()
    {
        return GetItems(GetDefaultDocumentId());
    }

    public TargetSet CreateTargetSet(string documentId, IEnumerable<int> itemIndices, string? userCommand = null, string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(itemIndices);
        var document = GetDocumentOrThrow(documentId);
        var items = itemIndices
            .Distinct()
            .Where(index => index >= 0 && index < document.Items.Count)
            .Select(index => document.Items[index])
            .ToList();

        return targetSets.Create(documentId, items, userCommand, label);
    }

    public TargetSet CreateTargetSet(IEnumerable<int> itemIndices, string? userCommand = null, string? label = null)
    {
        return CreateTargetSet(GetDefaultDocumentId(), itemIndices, userCommand, label);
    }

    public IReadOnlyList<TargetSet> ListTargetSets(string? documentId)
    {
        if (documentId != null && string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id cannot be whitespace.", nameof(documentId));
        }

        return targetSets.List(documentId);
    }

    public IReadOnlyList<TargetSet> ListDefaultTargetSets()
    {
        return targetSets.List(GetDefaultDocumentId());
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

    public LinearDocument ApplyOperations(string documentId, IEnumerable<LinearEditOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(operations);
        var document = GetDocumentOrThrow(documentId);
        var updated = editor.Apply(document, operations);
        documents[documentId] = updated;
        return updated;
    }

    public LinearDocument ApplyOperations(IEnumerable<LinearEditOperation> operations)
    {
        return ApplyOperations(GetDefaultDocumentId(), operations);
    }

    private LinearDocument GetDocumentOrThrow(string documentId)
    {
        var document = GetDocument(documentId);
        if (document == null)
        {
            throw new InvalidOperationException($"Document '{documentId}' is not loaded.");
        }

        return document;
    }

    private string GetDefaultDocumentId()
    {
        if (string.IsNullOrWhiteSpace(defaultDocumentId))
        {
            throw new InvalidOperationException("No default document has been loaded.");
        }

        return defaultDocumentId;
    }
}

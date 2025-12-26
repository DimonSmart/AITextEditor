using System;
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

        documents[document.Id] = document;
        defaultDocumentId = document.Id;
        return document;
    }

    public LinearDocument GetDefaultDocument()
    {
        return GetDocumentOrThrow(GetDefaultDocumentId());
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

        return targetSets.Create(document.Id, items, userCommand, label);
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

    public LinearDocument ApplyOperations(IEnumerable<LinearEditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var documentId = GetDefaultDocumentId();
        var document = GetDocumentOrThrow(documentId);
        var updated = editor.Apply(document, operations);
        documents[documentId] = updated;
        return updated;
    }

    private LinearDocument GetDocumentOrThrow(string documentId)
    {
        if (!documents.TryGetValue(documentId, out var document))
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

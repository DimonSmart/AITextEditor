using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class McpServer
{
    private readonly MarkdownDocumentRepository repository;
    private readonly LinearDocumentEditor editor;
    private readonly InMemoryTargetSetService targetSets;
    private readonly Dictionary<string, LinearDocument> documents = new(StringComparer.OrdinalIgnoreCase);

    public McpServer()
        : this(new MarkdownDocumentRepository(), new LinearDocumentEditor(), new InMemoryTargetSetService())
    {
    }

    public McpServer(
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
        var document = repository.LoadFromMarkdown(markdown);
        if (!string.IsNullOrWhiteSpace(documentId))
        {
            document = document with { Id = documentId! };
        }

        documents[document.Id] = document;
        return document;
    }

    public LinearDocument? GetDocument(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return null;
        }

        return documents.TryGetValue(documentId, out var document) ? document : null;
    }

    public IReadOnlyList<LinearItem> GetItems(string documentId)
    {
        var document = GetDocumentOrThrow(documentId);
        return document.Items;
    }

    public TargetSet CreateTargetSet(string documentId, IEnumerable<int> itemIndices, string? userCommand = null, string? label = null)
    {
        var document = GetDocumentOrThrow(documentId);
        var items = itemIndices
            .Distinct()
            .Where(index => index >= 0 && index < document.Items.Count)
            .Select(index => document.Items[index])
            .ToList();

        return targetSets.Create(documentId, items, userCommand, label);
    }

    public IReadOnlyList<TargetSet> ListTargetSets(string? documentId = null)
    {
        return targetSets.List(documentId);
    }

    public TargetSet? GetTargetSet(string targetSetId)
    {
        return targetSets.Get(targetSetId);
    }

    public bool DeleteTargetSet(string targetSetId)
    {
        return targetSets.Delete(targetSetId);
    }

    public LinearDocument ApplyOperations(string documentId, IEnumerable<LinearEditOperation> operations)
    {
        var document = GetDocumentOrThrow(documentId);
        var updated = editor.Apply(document, operations);
        documents[documentId] = updated;
        return updated;
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
}

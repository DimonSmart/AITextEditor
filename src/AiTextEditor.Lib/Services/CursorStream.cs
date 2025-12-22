using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AiTextEditor.Lib.Services;

public sealed class CursorStream
{
    private readonly LinearDocument _linearDocument;
    private readonly int _maxElements;
    private readonly int _maxBytes;
    private readonly ILogger? _logger;
    private readonly string? _filterDescription;
    private int _currentIndex;
    private bool _isComplete;
    private readonly bool _includeContent = true;
    public bool IsComplete => _isComplete;
    public string? FilterDescription => _filterDescription;

    public CursorStream(LinearDocument document, int maxElements, int maxBytes, string? startAfterPointer = null, string? filterDescription = null, ILogger? logger = null)
    {
        _linearDocument = document;
        _maxElements = maxElements;
        _maxBytes = maxBytes;
        _filterDescription = filterDescription;
        _logger = logger;

        if (!string.IsNullOrEmpty(startAfterPointer))
        {
            if (!SemanticPointer.TryParse(startAfterPointer, out _))
            {
                logger?.LogWarning("Invalid pointer format: {Pointer}", startAfterPointer);
                startAfterPointer = null;
            }
        }

        var startIndex = 0;
        if (!string.IsNullOrEmpty(startAfterPointer))
        {
            var foundIndex = -1;
            for (var i = 0; i < document.Items.Count; i++)
            {
                if (document.Items[i].Pointer.Serialize() == startAfterPointer)
                {
                    foundIndex = i;
                    break;
                }
            }

            if (foundIndex != -1)
            {
                startIndex = foundIndex + 1;
            }
        }

        _currentIndex = startIndex;
    }

    public CursorPortion NextPortion()
    {
        _logger?.LogInformation("CursorStream.NextPortion: currentIndex={CurrentIndex}, isComplete={IsComplete}", _currentIndex, _isComplete);

        if (_isComplete || _linearDocument.Items.Count == 0)
        {
            _isComplete = true;
            return new CursorPortion([], false);
        }

        var items = new List<LinearItem>();
        var byteBudget = _maxBytes;
        var countBudget = _maxElements;
        var nextIndex = _currentIndex;

        while (IsWithinBounds(nextIndex))
        {
            var sourceItem = _linearDocument.Items[nextIndex];
            var projectedItem = _includeContent ? sourceItem : StripText(sourceItem);
            var itemBytes = CalculateSize(projectedItem, _includeContent);

            if (items.Count >= countBudget) break;
            if (items.Count > 0 && byteBudget - itemBytes < 0) break;

            items.Add(projectedItem);
            byteBudget -= itemBytes;
            nextIndex = nextIndex + 1;
            if (byteBudget <= 0) break;
        }

        if (items.Count == 0 && IsWithinBounds(nextIndex))
        {
            var sourceItem = _linearDocument.Items[nextIndex];
            var projectedItem = _includeContent ? sourceItem : StripText(sourceItem);
            items.Add(projectedItem);
            nextIndex = nextIndex + 1;
        }

        _logger?.LogInformation("CursorStream.NextPortion: advancing from {CurrentIndex} to {NextIndex}. Items count: {Count}", _currentIndex, nextIndex, items.Count);

        _currentIndex = nextIndex;
        var hasMore = IsWithinBounds(nextIndex);

        if (!hasMore)
        {
            _isComplete = true;
        }

        return new CursorPortion(items, hasMore);
    }

    private static int CalculateSize(LinearItem item, bool includeContent)
    {
        var builder = new StringBuilder();
        builder.Append(item.Index);
        builder.Append('|');
        builder.Append(item.Type);

        builder.Append('|');
        builder.Append(item.Pointer.ToCompactString());

        if (includeContent)
        {
            builder.Append('|');
            builder.Append(item.Markdown);
            builder.Append('|');
        }

        return Encoding.UTF8.GetByteCount(builder.ToString());
    }

    private static LinearItem StripText(LinearItem item)
    {
        return new LinearItem(item.Index, item.Type, string.Empty, string.Empty, item.Pointer);
    }

    private bool IsWithinBounds(int index)
    {
        return index >= 0 && index < _linearDocument.Items.Count;
    }
}

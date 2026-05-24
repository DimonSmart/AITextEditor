using AiTextEditor.Core.Model;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace AiTextEditor.Core.Services;

public sealed class FullScanCursorStream : FilteredCursorStream, INamedCursorStream
{
    private const int DefaultTotalItemLimit = 100;
    private readonly int totalItemLimit;
    private int totalReturned;

    public FullScanCursorStream(
        LinearDocument document,
        int maxElements,
        int maxBytes,
        string? startAfterPointer,
        bool includeHeadings = true,
        ILogger? logger = null,
        int? totalItemLimit = null)
        : base(document, maxElements, maxBytes, startAfterPointer, includeHeadings, logger)
    {
        this.totalItemLimit = Math.Max(1, totalItemLimit ?? DefaultTotalItemLimit);
    }

    CursorPortion INamedCursorStream.NextPortion() => NextPortion();

    public new CursorPortion NextPortion()
    {
        if (totalReturned >= totalItemLimit)
        {
            return new CursorPortion([], false);
        }

        var portion = base.NextPortion();
        if (portion.Items.Count == 0)
        {
            return portion;
        }

        var remaining = totalItemLimit - totalReturned;
        if (portion.Items.Count <= remaining)
        {
            totalReturned += portion.Items.Count;
            return portion;
        }

        var trimmed = portion.Items.Take(remaining).ToList();
        totalReturned += trimmed.Count;
        return new CursorPortion(trimmed, false);
    }

    protected override bool IsMatch(LinearItem item) => true;
}

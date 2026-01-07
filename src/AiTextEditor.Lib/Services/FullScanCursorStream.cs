using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace AiTextEditor.Lib.Services;

public sealed class FullScanCursorStream : FilteredCursorStream, INamedCursorStream
{
    private const int TemporaryTotalItemLimit = 25;
    private int totalReturned;

    public FullScanCursorStream(LinearDocument document, int maxElements, int maxBytes, string? startAfterPointer, bool includeHeadings = true, ILogger? logger = null)
        : base(document, maxElements, maxBytes, startAfterPointer, includeHeadings, logger)
    {

    }

    CursorPortion INamedCursorStream.NextPortion() => NextPortion();

    public new CursorPortion NextPortion()
    {
        // TEMP: limit full-scan cursors to the first 25 items to keep local LLM runs fast and logs manageable.
        if (totalReturned >= TemporaryTotalItemLimit)
        {
            return new CursorPortion([], false);
        }

        var portion = base.NextPortion();
        if (portion.Items.Count == 0)
        {
            return portion;
        }

        var remaining = TemporaryTotalItemLimit - totalReturned;
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

using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services;

public sealed class FullScanCursorStream : FilteredCursorStream
{
    public FullScanCursorStream(LinearDocument document, int maxElements, int maxBytes, string? startAfterPointer, bool includeHeadings = true, ILogger? logger = null)
        : base(document, maxElements, maxBytes, startAfterPointer, includeHeadings, logger)
    {

    }

    protected override bool IsMatch(LinearItem item) => true;
}

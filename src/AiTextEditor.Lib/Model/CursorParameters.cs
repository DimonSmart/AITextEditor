using System;

namespace AiTextEditor.Lib.Model;

public sealed record CursorParameters
{
    public CursorParameters(int maxElements, int maxBytes, bool includeText)
    {
        if (maxElements <= 0) throw new ArgumentOutOfRangeException(nameof(maxElements));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        MaxElements = maxElements;
        MaxBytes = maxBytes;
        IncludeText = includeText;
    }

    public int MaxElements { get; }

    public int MaxBytes { get; }

    public bool IncludeText { get; }
}

using System;

namespace AiTextEditor.Lib.Model;

public sealed record CursorParameters
{
    public const int MaxElementsUpperBound = 200;
    public const int MaxBytesUpperBound = 65_536;

    public CursorParameters(int maxElements, int maxBytes, bool includeContent)
    {
        if (maxElements <= 0 || maxElements > MaxElementsUpperBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxElements),
                $"maxElements must be between 1 and {MaxElementsUpperBound}.");
        }

        if (maxBytes <= 0 || maxBytes > MaxBytesUpperBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes),
                $"maxBytes must be between 1 and {MaxBytesUpperBound}.");
        }

        MaxElements = maxElements;
        MaxBytes = maxBytes;
        IncludeContent = includeContent;
    }

    public int MaxElements { get; }

    public int MaxBytes { get; }

    public bool IncludeContent { get; }
}

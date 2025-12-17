using System;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorState
{
    public CursorState(int maxElements, int maxBytes, bool includeContent, int startIndex)
    {
        MaxElements = maxElements;
        MaxBytes = maxBytes;
        IncludeContent = includeContent;
        CurrentIndex = startIndex;
    }

    public int MaxElements { get; }
    public int MaxBytes { get; }
    public bool IncludeContent { get; }

    public int CurrentIndex { get; private set; }

    public bool IsComplete { get; private set; }

    public void Advance(int nextIndex)
    {
        CurrentIndex = nextIndex;
    }

    public void MarkComplete()
    {
        IsComplete = true;
    }
}

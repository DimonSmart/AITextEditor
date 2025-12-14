using System;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorState
{
    public CursorState(CursorParameters parameters, int startIndex)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        CurrentIndex = startIndex;
    }

    public CursorParameters Parameters { get; }

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

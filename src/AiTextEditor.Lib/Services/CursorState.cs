using System;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorState
{
    public CursorState(CursorParameters parameters, bool isForward, int startIndex)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        IsForward = isForward;
        CurrentIndex = startIndex;
    }

    public CursorParameters Parameters { get; }

    public bool IsForward { get; }

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

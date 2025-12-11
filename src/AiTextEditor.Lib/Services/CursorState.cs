using System;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorState
{
    public CursorState(string name, CursorParameters parameters, bool isForward, int startIndex)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        IsForward = isForward;
        CurrentIndex = startIndex;
    }

    public string Name { get; }

    public CursorParameters Parameters { get; }

    public bool IsForward { get; }

    public int CurrentIndex { get; private set; }

    public void Advance(int nextIndex)
    {
        CurrentIndex = nextIndex;
    }
}

using System;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorState
{
    public CursorState(string name, CursorParameters parameters, CursorDirection direction, int startIndex)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        Direction = direction;
        CurrentIndex = startIndex;
    }

    public string Name { get; }

    public CursorParameters Parameters { get; }

    public CursorDirection Direction { get; }

    public int CurrentIndex { get; private set; }

    public void Advance(int nextIndex)
    {
        CurrentIndex = nextIndex;
    }
}

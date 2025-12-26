using System;
using System.Collections.Generic;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public class CursorRegistry : ICursorStore
{
    private readonly Dictionary<string, INamedCursorStream> cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CursorAgentState> agentStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> agentSteps = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAddCursor(string name, INamedCursorStream cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(cursor);

        if (cursors.ContainsKey(name))
        {
            return false;
        }

        RegisterInternal(name, cursor);
        return true;
    }

    public void RegisterCursor(string name, INamedCursorStream cursor)
    {
        if (!TryAddCursor(name, cursor))
        {
            throw new InvalidOperationException($"cursor_registry_add_failed: {name}");
        }
    }

    public bool TryGetCursor(string name, out INamedCursorStream? cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return cursors.TryGetValue(name, out cursor);
    }

    public bool TryGetCursor<TCursor>(string name, out TCursor? cursor)
        where TCursor : class, INamedCursorStream
    {
        if (TryGetCursor(name, out var stream) && stream is TCursor typedCursor)
        {
            cursor = typedCursor;
            return true;
        }

        cursor = null;
        return false;
    }

    public bool TryGetContext(string name, out string? context)
    {
        if (TryGetCursor(name, out var cursor) && cursor != null)
        {
            context = cursor.FilterDescription;
            return true;
        }

        context = null;
        return false;
    }

    public CursorAgentState GetState(string name) => agentStates[name];

    public void UpdateState(string name, CursorAgentState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        agentStates[name] = state ?? throw new ArgumentNullException(nameof(state));
    }

    public int GetStep(string name) => agentSteps[name];

    public void IncrementStep(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        agentSteps[name]++;
    }

    private void RegisterInternal(string name, INamedCursorStream cursor)
    {
        cursors[name] = cursor;
        agentStates[name] = new CursorAgentState(Array.Empty<EvidenceItem>());
        agentSteps[name] = 0;
    }
}

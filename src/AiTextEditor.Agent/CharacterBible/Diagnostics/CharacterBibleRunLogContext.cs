namespace AiTextEditor.Agent.CharacterBible.Diagnostics;

internal sealed record CharacterBibleRunLogContext(
    string RunId,
    string LogPath,
    DateTimeOffset StartedAt);

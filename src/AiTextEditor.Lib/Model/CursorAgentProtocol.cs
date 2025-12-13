using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiTextEditor.Lib.Model;

public sealed record CursorAgentDecision(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("result")] CursorAgentResultItem? Result,
    [property: JsonPropertyName("newEvidence")] IReadOnlyList<CursorAgentEvidence>? NewEvidence,
    [property: JsonPropertyName("stateUpdate")] CursorAgentStateUpdate? StateUpdate,
    [property: JsonPropertyName("needMoreContext")] bool NeedMoreContext
);

public sealed record CursorAgentResultItem(
    [property: JsonPropertyName("pointerId")] int PointerId,
    [property: JsonPropertyName("pointerLabel")] string? PointerLabel,
    [property: JsonPropertyName("excerpt")] string? Excerpt,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("score")] double? Score
);

public sealed record CursorAgentEvidence(
    [property: JsonPropertyName("pointerId")] int PointerId,
    [property: JsonPropertyName("pointerLabel")] string? PointerLabel,
    [property: JsonPropertyName("excerpt")] string? Excerpt,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("score")] double? Score
);

public sealed record CursorAgentStateUpdate(
    [property: JsonPropertyName("goal")] string? Goal,
    [property: JsonPropertyName("found")] bool? Found,
    [property: JsonPropertyName("progress")] string? Progress
);

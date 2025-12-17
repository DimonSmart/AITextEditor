using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiTextEditor.Lib.Model;

public sealed record CursorAgentDecision(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("newEvidence")] IReadOnlyList<CursorAgentEvidence>? NewEvidence,
    [property: JsonPropertyName("progress")] string? Progress,
    [property: JsonPropertyName("needMoreContext")] bool NeedMoreContext
);

public sealed record CursorAgentEvidence(
    [property: JsonPropertyName("pointer")] string Pointer,
    [property: JsonPropertyName("pointerLabel")] string? PointerLabel,
    [property: JsonPropertyName("excerpt")] string? Excerpt,
    [property: JsonPropertyName("reason")] string? Reason
);

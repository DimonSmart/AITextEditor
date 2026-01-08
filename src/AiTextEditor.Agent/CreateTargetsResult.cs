using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent;

public sealed record CreateTargetsResult(
    [property: JsonPropertyName("targetSetId")] string TargetSetId,
    [property: JsonPropertyName("targets")] IReadOnlyList<TargetView> Targets,
    [property: JsonPropertyName("invalidPointers")] IReadOnlyList<string> InvalidPointers,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed record TargetView(
    [property: JsonPropertyName("pointer")] SemanticPointer Pointer,
    [property: JsonPropertyName("excerpt")] string Excerpt);

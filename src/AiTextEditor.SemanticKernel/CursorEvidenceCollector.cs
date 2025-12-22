using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.SemanticKernel;

public interface ICursorEvidenceCollector
{
    CursorAgentState AppendEvidence(CursorAgentState state, CursorPortionView portion, IReadOnlyList<EvidenceItem> evidence, int maxFound);

    string SerializeEvidence(IReadOnlyList<EvidenceItem> evidence);
}

public sealed class CursorEvidenceCollector : ICursorEvidenceCollector
{
    public CursorAgentState AppendEvidence(CursorAgentState state, CursorPortionView portion, IReadOnlyList<EvidenceItem> evidence, int maxFound)
    {
        var normalized = NormalizeEvidence(evidence, portion);
        return normalized.Count > 0 ? state.WithEvidence(normalized, maxFound) : state;
    }

    public string SerializeEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        return JsonSerializer.Serialize(evidence, options);
    }

    private static IReadOnlyList<EvidenceItem> NormalizeEvidence(IReadOnlyList<EvidenceItem> evidence, CursorPortionView portion)
    {
        if (evidence.Count == 0) return evidence;

        var byPointer = portion.Items.ToDictionary(i => i.SemanticPointer, i => i.Markdown, StringComparer.OrdinalIgnoreCase);

        var normalized = new List<EvidenceItem>(evidence.Count);
        foreach (var item in evidence)
        {
            if (!byPointer.TryGetValue(item.Pointer, out var markdown))
            {
                continue;
            }

            normalized.Add(new EvidenceItem(item.Pointer, markdown, item.Reason));
        }

        return normalized;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace AiTextEditor.Core.Model;

public sealed record CursorAgentState(IReadOnlyList<EvidenceItem> Evidence)
{
    public CursorAgentState WithEvidence(IEnumerable<EvidenceItem> newEvidence, int maxFound)
    {
        ArgumentNullException.ThrowIfNull(newEvidence);

        var merged = new List<EvidenceItem>(Evidence);
        foreach (var item in newEvidence)
        {
            if (!merged.Any(existing => existing.Pointer.Equals(item.Pointer, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(item);
            }
        }

        if (merged.Count > maxFound)
        {
            merged = merged.Take(maxFound).ToList(); // TODO: Replace with score-based pruning.
        }

        return this with { Evidence = merged };
    }
}

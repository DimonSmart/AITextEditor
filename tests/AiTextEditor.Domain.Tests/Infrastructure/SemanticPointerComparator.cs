using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using System.Text.RegularExpressions;
using Xunit.Sdk;

namespace AiTextEditor.Domain.Tests.Infrastructure;

public class SemanticPointerComparator
{
    private readonly LinearDocument _document;

    public SemanticPointerComparator(string markdown)
    {
        var repository = new MarkdownDocumentRepository();
        _document = repository.LoadFromMarkdown(markdown);
    }

    public void AssertMatch(string expectedPointerLabel, string actualAnswer, int tolerance = 5, string? scopePrefix = null)
    {
        var expectedItem = _document.Items.FirstOrDefault(i => string.Equals(i.Pointer.Label, expectedPointerLabel, StringComparison.OrdinalIgnoreCase));

        if (expectedItem == null)
        {
            throw new ArgumentException($"Expected pointer label '{expectedPointerLabel}' not found in the document.");
        }

        var expectedId = expectedItem.Pointer.Id;

        // Regex to find potential labels like "1.1.1.p21" or "1.p2"
        var labelRegex = new Regex(@"\b\d+(\.\d+)*\.p\d+\b", RegexOptions.IgnoreCase);
        var matches = labelRegex.Matches(actualAnswer);

        var foundPointers = new List<string>();
        bool matchFound = false;
        int closestId = -1;
        int minDelta = int.MaxValue;

        foreach (Match match in matches)
        {
            var label = match.Value;
            foundPointers.Add(label);

            // Check scope if defined
            if (!string.IsNullOrEmpty(scopePrefix) && !label.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = _document.Items.FirstOrDefault(i => string.Equals(i.Pointer.Label, label, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                var actualId = item.Pointer.Id;
                var delta = Math.Abs(actualId - expectedId);

                if (delta < minDelta)
                {
                    minDelta = delta;
                    closestId = actualId;
                }

                if (delta <= tolerance)
                {
                    matchFound = true;
                    break;
                }
            }
        }

        if (!matchFound)
        {
            var foundMsg = foundPointers.Count > 0
                ? string.Join(", ", foundPointers) + (closestId != -1 ? $" (closest delta: {minDelta})" : "")
                : "none";

            var scopeMsg = !string.IsNullOrEmpty(scopePrefix) ? $" within scope '{scopePrefix}'" : "";

            throw new XunitException(
                $"Expected pointer '{expectedPointerLabel}' (Id: {expectedId}) not found within tolerance {tolerance}{scopeMsg}.\n" +
                $"Found pointers: {foundMsg}\n" +
                $"Actual answer: {actualAnswer}");
        }
    }
}

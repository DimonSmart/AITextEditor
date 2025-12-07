using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using System.Linq;

namespace AiTextEditor.Lib.Services;

public class EditOperationGenerator
{
    private readonly ITargetSetService targetSetService;
    private readonly ILlmEditor llmEditor;

    public EditOperationGenerator(ITargetSetService targetSetService, ILlmEditor llmEditor)
    {
        this.targetSetService = targetSetService ?? throw new ArgumentNullException(nameof(targetSetService));
        this.llmEditor = llmEditor ?? throw new ArgumentNullException(nameof(llmEditor));
    }

    public async Task<List<EditOperation>> GenerateAsync(
        Document document,
        string targetSetId,
        IntentDto intent,
        string rawUserText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(intent);

        var targetSet = targetSetService.Get(targetSetId) ?? throw new InvalidOperationException($"Target set {targetSetId} not found.");
        if (!string.Equals(targetSet.DocumentId, document.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Target set does not belong to the provided document.");
        }

        var items = ResolveLinearItems(document.LinearDocument, targetSet);
        if (items.Count == 0)
        {
            return new List<EditOperation>();
        }

        var instruction = BuildInstruction(intent, targetSet, items);
        return await llmEditor.GetEditOperationsAsync(targetSet.Id, items, rawUserText, instruction, ct);
    }

    private static List<LinearItem> ResolveLinearItems(LinearDocument linearDocument, TargetSet targetSet)
    {
        var items = new List<LinearItem>();
        var byIndex = linearDocument.Items.ToDictionary(i => i.Index);

        foreach (var target in targetSet.Targets.OrderBy(t => t.LinearIndex))
        {
            if (byIndex.TryGetValue(target.LinearIndex, out var item))
            {
                items.Add(item);
                continue;
            }

            items.Add(new LinearItem
            {
                Index = target.LinearIndex,
                Markdown = target.Markdown,
                Text = target.Text,
                Type = target.Type,
                Pointer = target.Pointer
            });
        }

        return items;
    }

    private static string BuildInstruction(IntentDto intent, TargetSet targetSet, List<LinearItem> targetItems)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are an editor for a Markdown document. Apply the given intent to the provided targets.");
        sb.AppendLine($"TargetSetId: {targetSet.Id}");
        sb.AppendLine($"ScopeType: {intent.ScopeType}");
        sb.AppendLine("ScopeDescriptor:");
        sb.AppendLine($"  chapterNumber: {intent.ScopeDescriptor.ChapterNumber}");
        sb.AppendLine($"  sectionNumber: {intent.ScopeDescriptor.SectionNumber}");
        sb.AppendLine($"  structuralPath: {intent.ScopeDescriptor.StructuralPath}");
        sb.AppendLine($"  semanticQuery: {intent.ScopeDescriptor.SemanticQuery}");
        sb.AppendLine("Payload:");
        foreach (var kvp in intent.Payload.Fields)
        {
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        }

        sb.AppendLine("Target items (index | pointer | type | text):");
        foreach (var item in targetItems)
        {
            var plain = item.Text.Replace("\n", "\\n").Replace("\r", string.Empty);
            sb.AppendLine($"- {item.Index} | {item.Pointer.SemanticNumber} | {item.Type} | {plain}");
        }

        sb.AppendLine("Return JSON edit operations.");
        return sb.ToString();
    }
}

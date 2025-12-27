using System;
using System.ComponentModel;
using System.Linq;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class EditorPlugin(
    EditorSession server,
    SemanticKernelContext context,
    ILogger<EditorPlugin> logger)
{
 //   [KernelFunction("list_pointers")]
 //   public string ListPointers()
 //   {
 //       logger.LogInformation("ListPointers invoked");
 //       var items = server.GetItems();
 //       return string.Join("\n", items.Select(item => $"{item.Pointer.ToCompactString()}: {item.Text}"));
 //   }

    [KernelFunction("get_default_document_id")]
    public string GetDefaultDocumentId()
    {
        logger.LogInformation("GetDefaultDocumentId invoked");
        return server.ListDefaultTargetSets().FirstOrDefault()?.DocumentId ?? server.GetDefaultDocument().Id;
    }

    [KernelFunction("create_targets")]
    public CreateTargetsResult CreateTargets(
        string label,
        [Description("Semantic pointers to include as targets. Accepts an array of pointer strings.")]
        IReadOnlyList<string> pointers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(pointers);

        var normalizedPointers = new List<string>();
        var invalidPointers = new List<string>();
        var warnings = new List<string>();
        foreach (var rawPointer in pointers)
        {
            var normalized = rawPointer?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                invalidPointers.Add(rawPointer ?? string.Empty);
                continue;
            }

            normalizedPointers.Add(normalized);
        }

        var uniquePointers = new List<string>();
        var seenPointers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pointer in normalizedPointers)
        {
            if (!seenPointers.Add(pointer))
            {
                warnings.Add($"Duplicate pointer ignored: {pointer}");
                continue;
            }

            uniquePointers.Add(pointer);
        }

        logger.LogInformation("CreateTargets: label={Label}, pointers={Pointers}", label, string.Join(", ", uniquePointers));
        var items = server.GetItems();
        var lookup = items.ToDictionary(item => item.Pointer.ToCompactString(), item => item, StringComparer.Ordinal);
        var indices = new List<int>();

        foreach (var pointer in uniquePointers)
        {
            if (lookup.TryGetValue(pointer, out var match))
            {
                indices.Add(match.Index);
                continue;
            }

            invalidPointers.Add(pointer);
        }

        if (indices.Count == 0)
        {
            throw new InvalidOperationException("No matching pointers were found.");
        }

        var targetSet = server.CreateTargetSet(indices, context.LastCommand, label);
        context.LastTargetSet = targetSet;

        return new CreateTargetsResult(
            targetSet.Id,
            targetSet.Targets
                .Select(target => new TargetView(
                    target.Pointer,
                    target.Text))
                .ToList(),
            invalidPointers,
            warnings);
    }

    [KernelFunction("show_user_message")]
    public void ShowUserMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        logger.LogInformation("ShowUserMessage: {Message}", message);
        context.UserMessages.Add(message);
    }
}

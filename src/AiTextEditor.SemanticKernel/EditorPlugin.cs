using System;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class EditorPlugin(
    EditorSession server,
    SemanticKernelContext context,
    ILogger<EditorPlugin> logger)
{
    [KernelFunction("list_pointers")]
    public string ListPointers()
    {
        logger.LogInformation("ListPointers invoked");
        var items = server.GetItems();
        return string.Join("\n", items.Select(item => $"{item.Pointer.ToCompactString()}: {item.Text}"));
    }

    [KernelFunction("get_default_document_id")]
    public string GetDefaultDocumentId()
    {
        logger.LogInformation("GetDefaultDocumentId invoked");
        return server.ListDefaultTargetSets().FirstOrDefault()?.DocumentId ?? server.GetDefaultDocument().Id;
    }

    [KernelFunction("create_targets")]
    public TargetSet CreateTargets(string label, [Description("Comma-separated list of pointers")] string pointers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointers);

        var pointerList = pointers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        logger.LogInformation("CreateTargets: label={Label}, pointers={Pointers}", label, pointers);
        var items = server.GetItems();
        var indices = new List<int>();
        foreach (var pointer in pointerList)
        {
            var match = items.FirstOrDefault(item => string.Equals(item.Pointer.ToCompactString(), pointer, StringComparison.Ordinal));
            if (match != null)
            {
                indices.Add(match.Index);
            }
        }

        if (indices.Count == 0)
        {
            throw new InvalidOperationException("No matching pointers were found.");
        }

        var targetSet = server.CreateTargetSet(indices, context.LastCommand, label);
        context.LastTargetSet = targetSet;
        return targetSet;
    }

    [KernelFunction("show_user_message")]
    public void ShowUserMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        logger.LogInformation("ShowUserMessage: {Message}", message);
        context.UserMessages.Add(message);
    }

    [Obsolete("Avoid reading the entire document; use cursor-based tools instead.")]
    [KernelFunction("read_document")]
    public string ReadDocument()
    {
        logger.LogInformation("ReadDocument invoked");
        var builder = new StringBuilder();
        foreach (var item in server.GetItems())
        {
            builder.AppendLine($"{item.Pointer.ToCompactString()}: {item.Text}");
        }

        var content = builder.ToString();
        context.LastDocumentSnapshot = content;
        return content;
    }
}

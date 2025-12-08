using System.Text;
using AiTextEditor.Lib.Model;
using Microsoft.SemanticKernel;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class McpServerPlugin
{
    private readonly McpServer server;
    private readonly SemanticKernelContext context;

    public McpServerPlugin(McpServer server, SemanticKernelContext context)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [KernelFunction("list_pointers")]
    public string ListPointers()
    {
        var items = server.GetItems();
        return string.Join("\n", items.Select(item => $"{item.Pointer.SemanticNumber}: {item.Text}"));
    }

    [KernelFunction("get_default_document_id")]
    public string GetDefaultDocumentId()
    {
        return server.ListDefaultTargetSets().FirstOrDefault()?.DocumentId ?? server.GetDefaultDocument().Id;
    }

    [KernelFunction("create_targets")]
    public TargetSet CreateTargets(string label, params string[] pointers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(pointers);

        var items = server.GetItems();
        var indices = new List<int>();
        foreach (var pointer in pointers)
        {
            var match = items.FirstOrDefault(item => string.Equals(item.Pointer.SemanticNumber, pointer, StringComparison.OrdinalIgnoreCase));
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
        context.UserMessages.Add(message);
    }

    [KernelFunction("read_document")]
    public string ReadDocument()
    {
        var builder = new StringBuilder();
        foreach (var item in server.GetItems())
        {
            builder.AppendLine($"{item.Pointer.SemanticNumber}: {item.Text}");
        }

        var content = builder.ToString();
        context.LastDocumentSnapshot = content;
        return content;
    }
}

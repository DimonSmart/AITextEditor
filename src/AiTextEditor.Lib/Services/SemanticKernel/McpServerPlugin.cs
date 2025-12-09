using System.Text;
using AiTextEditor.Lib.Model;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class McpServerPlugin
{
    private readonly McpServer server;
    private readonly SemanticKernelContext context;
    private readonly ILogger<McpServerPlugin> logger;

    public McpServerPlugin(McpServer server, SemanticKernelContext context, ILogger<McpServerPlugin> logger)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [KernelFunction("list_pointers")]
    public string ListPointers()
    {
        logger.LogInformation("ListPointers invoked");
        var items = server.GetItems();
        return string.Join("\n", items.Select(item => $"{item.Pointer.Serialize()}: {item.Text}"));
    }

    [KernelFunction("get_default_document_id")]
    public string GetDefaultDocumentId()
    {
        logger.LogInformation("GetDefaultDocumentId invoked");
        return server.ListDefaultTargetSets().FirstOrDefault()?.DocumentId ?? server.GetDefaultDocument().Id;
    }

    [KernelFunction("create_targets")]
    public TargetSet CreateTargets(string label, params string[] pointers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(pointers);

        logger.LogInformation("CreateTargets: label={Label}, pointers={Pointers}", label, string.Join(",", pointers));
        var items = server.GetItems();
        var indices = new List<int>();
        foreach (var pointer in pointers)
        {
            var match = items.FirstOrDefault(item => string.Equals(item.Pointer.Serialize(), pointer, StringComparison.Ordinal));
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

    [KernelFunction("read_document")]
    public string ReadDocument()
    {
        logger.LogInformation("ReadDocument invoked");
        var builder = new StringBuilder();
        foreach (var item in server.GetItems())
        {
            builder.AppendLine($"{item.Pointer.Serialize()}: {item.Text}");
        }

        var content = builder.ToString();
        context.LastDocumentSnapshot = content;
        return content;
    }
}

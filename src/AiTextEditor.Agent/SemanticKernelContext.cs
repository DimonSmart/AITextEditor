using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent;

public sealed class SemanticKernelContext
{
    public List<string> UserMessages { get; } = [];

    public string? LastDocumentSnapshot { get; set; }

    public TargetSet? LastTargetSet { get; set; }

    public string? LastAnswer { get; set; }

    public string? LastCommand { get; set; }
}

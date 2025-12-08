using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class DocumentContext(LinearDocument document)
{
    public LinearDocument Document { get; } = document;

    public List<string> SpeechQueue { get; } = [];
}

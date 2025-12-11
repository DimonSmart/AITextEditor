using System.Collections.Generic;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class DocumentContext(LinearDocument document)
{
    public LinearDocument Document { get; } = document;

    public CursorContext CursorContext { get; } = new(document);

    public TargetSetContext TargetSetContext { get; } = new();

    public List<string> SpeechQueue { get; } = [];
}

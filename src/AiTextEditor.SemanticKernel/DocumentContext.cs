using System.Collections.Generic;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public class DocumentContext(LinearDocument document) : IDocumentContext
{
    public LinearDocument Document { get; } = document;

    public IList<string> SpeechQueue { get; } = [];
}

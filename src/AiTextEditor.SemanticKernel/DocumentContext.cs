using System.Collections.Generic;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public class DocumentContext(LinearDocument document, CharacterRosterService characterRosterService) : IDocumentContext
{
    public LinearDocument Document { get; } = document;

    public IList<string> SpeechQueue { get; } = [];

    public CharacterRosterService CharacterRosterService { get; } = characterRosterService;
}

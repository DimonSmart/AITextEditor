using System.Collections.Generic;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;

namespace AiTextEditor.Agent;

public class DocumentContext(LinearDocument document, CharacterDossierService characterDossierService) : IDocumentContext
{
    public LinearDocument Document { get; } = document;

    public IList<string> SpeechQueue { get; } = [];

    public CharacterDossierService CharacterDossierService { get; } = characterDossierService;
}

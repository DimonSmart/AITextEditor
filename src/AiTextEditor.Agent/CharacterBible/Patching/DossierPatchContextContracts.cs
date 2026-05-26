namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed record CharacterBibleDossierPatchCandidate(
    CharacterBibleCharacterCandidate Candidate,
    IReadOnlyList<CharacterBibleEvidenceContext> EvidenceContexts);

internal sealed record CharacterBibleEvidenceContext(
    string Pointer,
    string AnchorExcerpt,
    string CurrentParagraph,
    string FocusedText,
    IReadOnlyList<CharacterBibleNearbyParagraph> NearbyParagraphs);

internal sealed record CharacterBibleNearbyParagraph(
    string Pointer,
    string Text,
    string Position);

namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed record CharacterBibleDossierPatchCandidate(
    CharacterBibleCharacterCandidate Candidate,
    IReadOnlyList<CharacterBibleEvidenceContext> EvidenceContexts);

internal sealed record CharacterProfileUpdatePromptInput(
    CharacterProfileUpdateTarget Target,
    CharacterProfileUpdateCurrentProfile CurrentProfile,
    IReadOnlyList<CharacterProfileUpdateEvidence> NewEvidence);

internal sealed record CharacterProfileUpdateTarget(string Name);

internal sealed record CharacterProfileUpdateCurrentProfile(
    string? Appearance,
    string? StatusAndCompetence,
    string? PsychologicalProfile,
    string? SpeechAndCommunication);

internal sealed record CharacterProfileUpdateEvidence(
    string Pointer,
    string Text);

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

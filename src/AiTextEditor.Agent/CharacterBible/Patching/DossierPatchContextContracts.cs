namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed record CharacterBibleDossierPatchCandidate(
    CharacterBibleCharacterCandidate Candidate,
    IReadOnlyList<CharacterBibleEvidenceContext> EvidenceContexts);

internal sealed record CharacterBiblePatchProposalPromptInput(
    CharacterBiblePatchTarget Target,
    CharacterBiblePatchCurrentProfile CurrentProfile,
    IReadOnlyList<CharacterBiblePatchEvidence> NewEvidence);

internal sealed record CharacterProfilePatchPromptInput(
    CharacterProfilePatchPromptCard Character,
    IReadOnlyList<CharacterBiblePatchEvidence> Evidence);

internal sealed record CharacterProfilePatchPromptCard(
    string Name,
    string Gender,
    IReadOnlyList<string> Aliases,
    string Importance,
    CharacterBiblePatchCurrentProfile CurrentProfile);

internal sealed record CharacterBiblePatchTarget(string Name);

internal sealed record CharacterBiblePatchCurrentProfile(
    string? Appearance,
    string? StatusAndCompetence,
    string? PsychologicalProfile,
    string? SpeechAndCommunication);

internal sealed record CharacterBiblePatchEvidence(
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

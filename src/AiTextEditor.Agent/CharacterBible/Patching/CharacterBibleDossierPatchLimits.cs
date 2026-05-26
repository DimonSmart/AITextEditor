namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed class CharacterBibleDossierPatchLimits
{
    public int MaxCandidatesPerPatchCall { get; init; } = 8;

    public int MaxContextBytesPerPatchCall { get; init; } = 12_000;
}

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class DeterministicIdentityResolution
{
    public IdentityResolutionDecision Resolve(IReadOnlyList<CharacterArchiveHit> archiveHits)
    {
        ArgumentNullException.ThrowIfNull(archiveHits);

        var characterHits = archiveHits
            .Where(hit => hit.EntryKind == CharacterArchiveEntryKind.Character)
            .ToArray();
        if (characterHits.Length == 0 && archiveHits.Count > 0)
        {
            return IdentityResolutionDecision.Defer(
                archiveHits.Select(hit => hit.EntryId).ToArray(),
                "Matched only suspect archive entries; keeping candidate deferred.");
        }

        return characterHits.Length switch
        {
            0 => IdentityResolutionDecision.New(),
            1 => IdentityResolutionDecision.Existing(characterHits[0]),
            _ => IdentityResolutionDecision.Ambiguous(characterHits)
        };
    }
}

using System.Text.RegularExpressions;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterGenerator
{
    private static readonly Regex NamePattern = new(@"[\p{Lu}][\p{L}\p{M}]+(?:[\s\-]+[\p{Lu}][\p{L}\p{M}]+){0,3}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDocumentContext documentContext;
    private readonly CharacterRosterService rosterService;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterRosterGenerator> logger;

    public CharacterRosterGenerator(
        IDocumentContext documentContext,
        CharacterRosterService rosterService,
        CursorAgentLimits limits,
        ILogger<CharacterRosterGenerator> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<CharacterRoster> GenerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cursor = new FullScanCursorStream(documentContext.Document, limits.MaxElements, limits.MaxBytes, null, includeHeadings: true, logger);
        var candidates = new List<CharacterCandidate>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            foreach (var item in portion.Items)
            {
                candidates.AddRange(ExtractCandidates(item));
            }

            if (!portion.HasMore)
            {
                break;
            }
        }

        var roster = BuildRosterFromCandidates(candidates, null);
        rosterService.ReplaceRoster(roster);
        return Task.FromResult(rosterService.GetRoster());
    }

    public Task<CharacterRoster> RefreshAsync(IReadOnlyCollection<string>? changedPointers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (changedPointers == null || changedPointers.Count == 0)
        {
            return GenerateAsync(cancellationToken);
        }

        var pointerSet = new HashSet<string>(changedPointers.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.Ordinal);
        if (pointerSet.Count == 0)
        {
            return Task.FromResult(rosterService.GetRoster());
        }

        var lookup = documentContext.Document.Items.ToDictionary(i => i.Pointer.ToCompactString(), i => i, StringComparer.Ordinal);
        var candidates = new List<CharacterCandidate>();
        foreach (var pointer in pointerSet)
        {
            if (!lookup.TryGetValue(pointer, out var item))
            {
                logger.LogWarning("RefreshCharacterRoster: pointer not found: {Pointer}", pointer);
                continue;
            }

            candidates.AddRange(ExtractCandidates(item));
        }

        var roster = BuildRosterFromCandidates(candidates, rosterService.GetRoster());
        rosterService.ReplaceRoster(roster);
        return Task.FromResult(rosterService.GetRoster());
    }

    private static List<CharacterCandidate> ExtractCandidates(LinearItem item)
    {
        var text = string.IsNullOrWhiteSpace(item.Text) ? item.Markdown : item.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var sentences = SplitSentences(text);
        var pointer = item.Pointer.ToCompactString();
        var candidates = new List<CharacterCandidate>();

        foreach (var sentence in sentences)
        {
            foreach (Match match in NamePattern.Matches(sentence))
            {
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Value.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                candidates.Add(new CharacterCandidate(name, sentence.Trim(), pointer));
            }
        }

        return candidates;
    }

    private static List<string> SplitSentences(string text)
    {
        var parts = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static List<CharacterProfile> BuildRosterFromCandidates(IEnumerable<CharacterCandidate> candidates, CharacterRoster? existingRoster)
    {
        var items = candidates.ToList();
        var existing = existingRoster?.Characters.ToDictionary(
            c => NormalizeName(c.Name),
            c => new CharacterAccumulator(c),
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, CharacterAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in items)
        {
            var key = NormalizeName(candidate.Name);
            if (!existing.TryGetValue(key, out var accumulator))
            {
                accumulator = new CharacterAccumulator(candidate.Name, candidate.Phrase, candidate.Pointer);
                existing[key] = accumulator;
            }
            else
            {
                accumulator.RegisterCandidate(candidate);
            }
        }

        return existing.Values
            .Select(acc => acc.ToProfile())
            .ToList();
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return Regex.Replace(trimmed, "\\s+", " ").ToLowerInvariant();
    }

    private sealed record CharacterCandidate(string Name, string Phrase, string Pointer);

    private sealed class CharacterAccumulator
    {
        private readonly string? existingId;
        private readonly HashSet<string> aliases;

        public CharacterAccumulator(string name, string phrase, string pointer)
        {
            CanonicalName = name;
            Description = phrase;
            FirstPointer = pointer;
            aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public CharacterAccumulator(CharacterProfile profile)
        {
            existingId = profile.CharacterId;
            CanonicalName = profile.Name;
            Description = profile.Description;
            FirstPointer = profile.FirstPointer;
            aliases = new HashSet<string>(profile.Aliases, StringComparer.OrdinalIgnoreCase);
        }

        public string CanonicalName { get; }

        public string Description { get; private set; }

        public string? FirstPointer { get; private set; }

        public void RegisterCandidate(CharacterCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                return;
            }

            if (!string.Equals(candidate.Name, CanonicalName, StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(candidate.Name.Trim());
            }

            if (string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(candidate.Phrase))
            {
                Description = candidate.Phrase;
            }

            if (string.IsNullOrWhiteSpace(FirstPointer) && !string.IsNullOrWhiteSpace(candidate.Pointer))
            {
                FirstPointer = candidate.Pointer;
            }
        }

        public CharacterProfile ToProfile()
        {
            var id = string.IsNullOrWhiteSpace(existingId) ? Guid.NewGuid().ToString("N") : existingId;
            var description = string.IsNullOrWhiteSpace(Description) ? CanonicalName : Description;
            var aliasList = aliases
                .Where(a => !string.Equals(a, CanonicalName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CharacterProfile(
                id,
                CanonicalName,
                description,
                aliasList,
                FirstPointer);
        }
    }
}

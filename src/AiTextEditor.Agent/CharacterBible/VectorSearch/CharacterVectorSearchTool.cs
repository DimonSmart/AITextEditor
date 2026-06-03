using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.VectorSearch;

public interface ICharacterVectorSearchTool
{
    Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
        CharacterDossiers dossiers,
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record CharacterVectorSearchHit(
    CharacterVectorSearchCard Card,
    double Score);

public sealed record CharacterVectorSearchCard(
    int CharacterId,
    string Name,
    string Gender,
    IReadOnlyList<string> ObservedNameForms,
    string Summary);

internal interface ICharacterVectorEmbeddingClient
{
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken);
}

internal sealed record CharacterVectorSearchOptions(
    string EmbeddingModelId,
    string IndexSchemaVersion)
{
    public const string CurrentIndexSchemaVersion = "character-vector-index-v1";

    public static CharacterVectorSearchOptions CreateDefault(string embeddingModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        return new CharacterVectorSearchOptions(
            embeddingModelId.Trim(),
            CurrentIndexSchemaVersion);
    }
}

public sealed partial class CharacterVectorSearchTool : ICharacterVectorSearchTool
{
    private readonly ICharacterVectorEmbeddingClient embeddingClient;
    private readonly CharacterVectorSearchOptions options;
    private readonly SemaphoreSlim indexSyncLock = new(1, 1);
    private CharacterVectorIndexSnapshot indexSnapshot = CharacterVectorIndexSnapshot.Empty;

    internal CharacterVectorSearchTool(
        ICharacterVectorEmbeddingClient embeddingClient,
        CharacterVectorSearchOptions options)
    {
        this.embeddingClient = embeddingClient ?? throw new ArgumentNullException(nameof(embeddingClient));
        this.options = NormalizeOptions(options);
    }

    public async Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
        CharacterDossiers dossiers,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dossiers);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            CharacterBibleRunLogScope.Current?.Debug(
                "vector.search.done",
                $"query={LogValueFormatter.Quote(query)} limit={limit} resultCount=0 durationMs=0");
            return [];
        }

        await EnsureIndexIsCurrentAsync(dossiers, cancellationToken).ConfigureAwait(false);
        var snapshot = indexSnapshot;
        if (snapshot.Entries.Count == 0)
        {
            CharacterBibleRunLogScope.Current?.Debug(
                "vector.search.done",
                $"query={LogValueFormatter.Quote(query)} limit={limit} resultCount=0 durationMs=0");
            return [];
        }

        var stopwatch = Stopwatch.StartNew();
        var queryEmbedding = await embeddingClient.GenerateEmbeddingAsync(
            NormalizeText(query),
            cancellationToken).ConfigureAwait(false);

        var results = snapshot.Entries
            .Select(entry => new CharacterVectorSearchHit(
                entry.Card,
                CosineSimilarity(queryEmbedding.Span, entry.Embedding.Span)))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Card.Name, StringComparer.Ordinal)
            .ThenBy(hit => hit.Card.CharacterId)
            .Take(limit)
            .ToList();
        stopwatch.Stop();
        CharacterBibleRunLogScope.Current?.Debug(
            "vector.search.done",
            $"query={LogValueFormatter.Quote(query)} limit={limit} resultCount={results.Count} durationMs={stopwatch.ElapsedMilliseconds}");
        return results;
    }

    private async Task EnsureIndexIsCurrentAsync(
        CharacterDossiers dossiers,
        CancellationToken cancellationToken)
    {
        await indexSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var documents = dossiers.Characters
                .Select(BuildIndexDocument)
                .ToList();

            if (documents.Count == 0)
            {
                indexSnapshot = CharacterVectorIndexSnapshot.Empty;
                CharacterBibleRunLogScope.Current?.Info(
                    "vector.index.rebuild",
                    "characters=0 fingerprint=empty reason=\"snapshot empty\"");
                return;
            }

            var snapshotFingerprint = ComputeSnapshotFingerprint(documents);
            var previousFingerprint = ComputeSnapshotFingerprint(indexSnapshot.Entries);
            var reusedSnapshot = indexSnapshot.Entries.Count == documents.Count
                && string.Equals(previousFingerprint, snapshotFingerprint, StringComparison.Ordinal);
            if (reusedSnapshot)
            {
                CharacterBibleRunLogScope.Current?.Debug(
                    "vector.index.reuse",
                    $"characters={documents.Count} fingerprint={snapshotFingerprint}");
            }
            else
            {
                CharacterBibleRunLogScope.Current?.Info(
                    "vector.index.rebuild",
                    $"characters={documents.Count} fingerprint={snapshotFingerprint} reason={LogValueFormatter.Quote("snapshot changed")}");
            }

            var existingEntries = indexSnapshot.Entries.ToDictionary(entry => entry.CharacterId);
            var nextEntries = new List<CharacterVectorIndexEntry>(documents.Count);

            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (existingEntries.TryGetValue(document.CharacterId, out var existing) &&
                    string.Equals(existing.Fingerprint, document.Fingerprint, StringComparison.Ordinal))
                {
                    nextEntries.Add(existing with { Card = document.Card });
                    continue;
                }

                var embedding = await embeddingClient.GenerateEmbeddingAsync(
                    document.Text,
                    cancellationToken).ConfigureAwait(false);

                nextEntries.Add(new CharacterVectorIndexEntry(
                    document.CharacterId,
                    document.Fingerprint,
                    embedding,
                    document.Card));
            }

            indexSnapshot = new CharacterVectorIndexSnapshot(nextEntries);
        }
        finally
        {
            indexSyncLock.Release();
        }
    }

    private CharacterVectorIndexDocument BuildIndexDocument(CharacterDossier dossier)
    {
        var normalizedProfile = CharacterProfile.Normalize(dossier.Profile);
        var card = BuildSearchCard(dossier, normalizedProfile);
        var text = BuildIndexText(dossier, normalizedProfile);
        var fingerprint = ComputeFingerprint(text);

        return new CharacterVectorIndexDocument(
            dossier.CharacterId,
            text,
            card,
            fingerprint);
    }

    internal string GetFingerprintForTests(CharacterDossier dossier)
    {
        return BuildIndexDocument(dossier).Fingerprint;
    }

    private static CharacterVectorSearchCard BuildSearchCard(
        CharacterDossier dossier,
        CharacterProfile profile)
    {
        var observedNameForms = dossier.ObservedNameForms
            .Where(observedNameForm => !string.IsNullOrWhiteSpace(observedNameForm))
            .Select(observedNameForm => observedNameForm.Trim())
            .ToList();

        return new CharacterVectorSearchCard(
            dossier.CharacterId,
            dossier.Name.Trim(),
            NormalizeValue(dossier.Gender),
            observedNameForms,
            BuildSummary(dossier, profile, observedNameForms));
    }

    private static string BuildSummary(
        CharacterDossier dossier,
        CharacterProfile profile,
        IReadOnlyList<string> observedNameForms)
    {
        var lines = new List<string>();
        AddLine(lines, "Name", dossier.Name);
        AddLine(lines, "Gender", dossier.Gender);
        AddLine(lines, "Observed name forms", string.Join(", ", observedNameForms));
        AddLine(lines, "Profile", string.Join(
            " ",
            new[]
            {
                profile.Appearance,
                profile.StatusAndCompetence,
                profile.PsychologicalProfile,
                profile.SpeechAndCommunication
            }.Where(value => !string.IsNullOrWhiteSpace(value))));

        return string.Join('\n', lines);
    }

    private static string BuildIndexText(CharacterDossier dossier, CharacterProfile profile)
    {
        var lines = new List<string>();
        AddLine(lines, "Character", dossier.Name);
        AddLine(lines, "Gender", dossier.Gender);
        AddLine(lines, "Observed name forms", string.Join(", ", dossier.ObservedNameForms));
        AddLine(lines, "Status", profile.StatusAndCompetence);
        AddLine(lines, "Appearance", profile.Appearance);
        AddLine(lines, "Traits", profile.PsychologicalProfile);
        AddLine(lines, "Speech", profile.SpeechAndCommunication);

        return string.Join('\n', lines);
    }

    private static void AddLine(List<string> lines, string label, string? value)
    {
        var normalized = NormalizeValue(value);
        if (normalized.Length == 0)
        {
            return;
        }

        lines.Add($"{label}: {normalized}");
    }

    private string ComputeFingerprint(string normalizedDocumentText)
    {
        var fingerprintInput = string.Join(
            "\n",
            options.IndexSchemaVersion,
            options.EmbeddingModelId,
            normalizedDocumentText);

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
    }

    private static string ComputeSnapshotFingerprint(IEnumerable<CharacterVectorIndexDocument> documents)
    {
        var input = string.Join(
            "\n",
            documents
                .OrderBy(document => document.CharacterId)
                .Select(document => $"{document.CharacterId}:{document.Fingerprint}"));

        return ShortHash(input);
    }

    private static string ComputeSnapshotFingerprint(IEnumerable<CharacterVectorIndexEntry> entries)
    {
        var input = string.Join(
            "\n",
            entries
                .OrderBy(entry => entry.CharacterId)
                .Select(entry => $"{entry.CharacterId}:{entry.Fingerprint}"));

        return input.Length == 0 ? "empty" : ShortHash(input);
    }

    private static string ShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
    }

    private static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0d;
        }

        double dot = 0d;
        double leftMagnitude = 0d;
        double rightMagnitude = 0d;

        for (var index = 0; index < length; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];

            dot += leftValue * rightValue;
            leftMagnitude += leftValue * leftValue;
            rightMagnitude += rightValue * rightValue;
        }

        if (leftMagnitude == 0d || rightMagnitude == 0d)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static CharacterVectorSearchOptions NormalizeOptions(CharacterVectorSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EmbeddingModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IndexSchemaVersion);

        return new CharacterVectorSearchOptions(
            options.EmbeddingModelId.Trim(),
            options.IndexSchemaVersion.Trim());
    }

    private static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    private static string NormalizeText(string value)
    {
        return NormalizeValue(value);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    internal sealed record CharacterVectorIndexDocument(
        int CharacterId,
        string Text,
        CharacterVectorSearchCard Card,
        string Fingerprint);

    internal sealed record CharacterVectorIndexEntry(
        int CharacterId,
        string Fingerprint,
        ReadOnlyMemory<float> Embedding,
        CharacterVectorSearchCard Card);

    private sealed record CharacterVectorIndexSnapshot(
        IReadOnlyList<CharacterVectorIndexEntry> Entries)
    {
        public static CharacterVectorIndexSnapshot Empty { get; } = new([]);
    }
}
